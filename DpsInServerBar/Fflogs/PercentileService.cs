using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace DpsInServerBar.Fflogs;

/// <summary>rDPS values at given percentiles for one encounter, difficulty and job.</summary>
public sealed class Breakpoints
{
    public DateTime FetchedUtc { get; set; }
    public int Count { get; set; }

    /// <summary>(amount, percentile), sorted by amount ascending.</summary>
    public List<double[]> Points { get; set; } = [];
}

/// <summary>
/// Estimates the FFLogs percentile of an rDPS value. Percentile breakpoints come from the encounter's character
/// rankings (best parse per character, rDPS), fetched in the background and cached for a day; values in between
/// are interpolated. Public methods are called from the game thread and never block.
/// </summary>
public sealed class PercentileService : IDisposable
{
    private static readonly double[] TargetPercentiles = [99, 95, 90, 75, 50, 25, 10, 1];
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(24);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(2);

    private readonly FflogsApi api = new();
    private readonly Configuration config;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly string cachePath;
    private readonly CancellationTokenSource cts = new();
    private readonly object cacheLock = new();

    private readonly ConcurrentDictionary<string, Breakpoints> breakpoints = new();
    private readonly ConcurrentDictionary<string, DateTime> inFlightOrFailed = new();
    private volatile List<EncounterEntry>? catalog;
    private DateTime catalogFailedUtc = DateTime.MinValue;
    private int catalogLoading;

    public PercentileService(Configuration config, IDataManager dataManager, string configDirectory, IPluginLog log)
    {
        this.config = config;
        this.dataManager = dataManager;
        this.log = log;
        cachePath = Path.Combine(configDirectory, "fflogs-cache.json");
        LoadCache();
    }

    /// <summary>Last problem met, for the settings window.</summary>
    public string? LastError { get; private set; }

    public bool HasCredentials => config.FflogsClientId.Length > 0 && config.FflogsClientSecret.Length > 0;

    public void Dispose()
    {
        cts.Cancel();
        api.Dispose();
        cts.Dispose();
    }

    /// <summary>Estimated percentile of <paramref name="rdps"/>, or null while unknown (data is then fetched).</summary>
    public double? Estimate(uint territory, uint bossNameId, string job, double rdps)
    {
        if (!config.ShowPercentile || !HasCredentials || rdps <= 0)
            return null;

        var encounter = ResolveEncounter(territory, bossNameId);
        var spec = FflogsNames.Spec(job);
        if (encounter is null || spec is null)
            return null;

        var key = $"{encounter.Value.Id}|{encounter.Value.Difficulty}|{spec}";
        if (breakpoints.TryGetValue(key, out var bp) && DateTime.UtcNow - bp.FetchedUtc < CacheDuration)
            return Interpolate(bp, rdps);

        RequestBreakpoints(key, encounter.Value.Id, encounter.Value.Difficulty, spec);
        return bp is null ? null : Interpolate(bp, rdps); // stale data is better than nothing while refreshing
    }

    /// <summary>The FFLogs encounter used for the current fight, for display in the settings.</summary>
    public string Describe(uint territory, uint bossNameId)
    {
        if (!HasCredentials)
            return "No FFLogs credentials.";
        if (catalog is null)
            return "Loading the FFLogs encounter list...";

        var encounter = ResolveEncounter(territory, bossNameId);
        return encounter is { } e
            ? $"{e.Name} (id {e.Id}, {(e.Difficulty == 101 ? "Savage" : "Normal/Extreme/Ultimate")})"
            : $"No FFLogs encounter matches \"{EnglishBossName(bossNameId)}\". Set the id by hand.";
    }

    public string EnglishBossName(uint bossNameId) =>
        bossNameId != 0 && dataManager.GetExcelSheet<BNpcName>(ClientLanguage.English).TryGetRow(bossNameId, out var row)
            ? row.Singular.ExtractText()
            : string.Empty;

    /// <summary>Drops the cache, e.g. after changing credentials.</summary>
    public void Reset()
    {
        breakpoints.Clear();
        inFlightOrFailed.Clear();
        catalog = null;
        catalogFailedUtc = DateTime.MinValue;
        LastError = null;
    }

    private (int Id, string Name, int Difficulty)? ResolveEncounter(uint territory, uint bossNameId)
    {
        var difficulty = IsSavage(territory) ? 101 : 100;

        if (config.FflogsEncounterOverrides.TryGetValue(territory, out var forced) && forced > 0)
            return (forced, $"#{forced}", difficulty);

        var list = catalog;
        if (list is null)
        {
            LoadCatalog();
            return null;
        }

        var name = EnglishBossName(bossNameId);
        if (name.Length == 0)
            return null;

        // Exact name first, then partial; current (non frozen) zones before legacy ones.
        var match = list.Where(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Frozen).FirstOrDefault()
                    ?? list.Where(e => e.Name.Contains(name, StringComparison.OrdinalIgnoreCase)
                                       || name.Contains(e.Name, StringComparison.OrdinalIgnoreCase))
                           .OrderBy(e => e.Frozen).FirstOrDefault();

        return match is null ? null : (match.Id, match.Name, difficulty);
    }

    private bool IsSavage(uint territory) =>
        dataManager.GetExcelSheet<TerritoryType>(ClientLanguage.English).TryGetRow(territory, out var row)
        && row.ContentFinderCondition.ValueNullable?.Name.ExtractText().Contains("(Savage)", StringComparison.OrdinalIgnoreCase) == true;

    private static double Interpolate(Breakpoints bp, double value)
    {
        var points = bp.Points;
        if (points.Count == 0)
            return 0;

        if (value <= points[0][0])
            return Math.Max(0, points[0][1] * value / points[0][0]);
        if (value >= points[^1][0])
            return points[^1][1];

        for (var i = 1; i < points.Count; i++)
        {
            if (value > points[i][0])
                continue;
            var (a0, p0, a1, p1) = (points[i - 1][0], points[i - 1][1], points[i][0], points[i][1]);
            return p0 + (p1 - p0) * (value - a0) / Math.Max(1e-6, a1 - a0);
        }

        return points[^1][1];
    }

    private void LoadCatalog()
    {
        if (DateTime.UtcNow - catalogFailedUtc < RetryDelay || Interlocked.Exchange(ref catalogLoading, 1) == 1)
            return;

        var (id, secret) = (config.FflogsClientId, config.FflogsClientSecret);
        Task.Run(async () =>
        {
            try
            {
                const string query = "{ worldData { expansions { id zones { id name frozen encounters { id name } } } } }";
                var data = await api.QueryAsync(id, secret, query, cts.Token).ConfigureAwait(false);

                var entries = new List<EncounterEntry>();
                foreach (var expansion in data["worldData"]?["expansions"]?.AsArray() ?? [])
                {
                    foreach (var zone in expansion?["zones"]?.AsArray() ?? [])
                    {
                        var frozen = zone?["frozen"]?.GetValue<bool>() ?? false;
                        foreach (var enc in zone?["encounters"]?.AsArray() ?? [])
                        {
                            if (enc?["id"] is { } encId && enc["name"]?.ToString() is { } encName)
                                entries.Add(new EncounterEntry(encId.GetValue<int>(), encName, frozen));
                        }
                    }
                }

                catalog = entries;
                LastError = null;
            }
            catch (Exception ex)
            {
                catalogFailedUtc = DateTime.UtcNow;
                LastError = ex.Message;
                log.Warning($"FFLogs encounter list: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref catalogLoading, 0);
            }
        });
    }

    private void RequestBreakpoints(string key, int encounterId, int difficulty, string spec)
    {
        if (inFlightOrFailed.TryGetValue(key, out var since) && DateTime.UtcNow - since < RetryDelay)
            return;
        inFlightOrFailed[key] = DateTime.UtcNow;

        var (id, secret) = (config.FflogsClientId, config.FflogsClientSecret);
        Task.Run(async () =>
        {
            try
            {
                breakpoints[key] = await FetchBreakpointsAsync(id, secret, encounterId, difficulty, spec).ConfigureAwait(false);
                inFlightOrFailed.TryRemove(key, out _);
                LastError = null;
                SaveCache();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                log.Warning($"FFLogs rankings for {spec} on {encounterId}: {ex.Message}");
            }
        });
    }

    private async Task<Breakpoints> FetchBreakpointsAsync(string id, string secret, int encounterId, int difficulty, string spec)
    {
        string Rankings(int page) =>
            $"characterRankings(className: \"{spec}\", specName: \"{spec}\", metric: rdps, difficulty: {difficulty}, page: {page})";

        // Page 1 gives the total number of ranked characters and the top value.
        var first = await api.QueryAsync(id, secret, $"{{ worldData {{ encounter(id: {encounterId}) {{ p1: {Rankings(1)} }} }} }}", cts.Token)
                             .ConfigureAwait(false);
        var page1 = RankingsNode(first, "p1");
        var count = page1?["count"]?.GetValue<int>() ?? 0;
        var top = Amounts(page1);
        if (count == 0 || top.Count == 0)
            throw new InvalidOperationException($"No {spec} rankings on FFLogs for this encounter yet.");

        var perPage = top.Count;
        var ranks = TargetPercentiles
            .Select(p => (Percentile: p, Rank: Math.Clamp((int)Math.Floor(count * (1 - p / 100)) + 1, 1, count)))
            .ToList();

        // Fetch every other needed page in a single request, using aliases.
        var pages = ranks.Select(r => (r.Rank - 1) / perPage + 1).Where(p => p > 1).Distinct().ToList();
        JsonNode? others = null;
        if (pages.Count > 0)
        {
            var aliases = string.Join(" ", pages.Select(p => $"p{p}: {Rankings(p)}"));
            others = await api.QueryAsync(id, secret, $"{{ worldData {{ encounter(id: {encounterId}) {{ {aliases} }} }} }}", cts.Token)
                              .ConfigureAwait(false);
        }

        var points = new List<double[]> { new[] { top[0], 100.0 } };
        foreach (var (percentile, rank) in ranks)
        {
            var page = (rank - 1) / perPage + 1;
            var amounts = page == 1 ? top : Amounts(RankingsNode(others, $"p{page}"));
            var index = (rank - 1) % perPage;
            if (index < amounts.Count)
                points.Add([amounts[index], percentile]);
        }

        return new Breakpoints
        {
            FetchedUtc = DateTime.UtcNow,
            Count = count,
            Points = points.OrderBy(p => p[0]).ToList(),
        };
    }

    private static JsonNode? RankingsNode(JsonNode? data, string alias)
    {
        var node = data?["worldData"]?["encounter"]?[alias];
        // The field is a JSON scalar: depending on the server it comes as an object or as a JSON string.
        return node is JsonValue v && v.TryGetValue<string>(out var s) ? JsonNode.Parse(s) : node;
    }

    private static List<double> Amounts(JsonNode? rankings) =>
        rankings?["rankings"]?.AsArray()
                .Select(r => r?["amount"]?.GetValue<double>() ?? 0)
                .Where(a => a > 0)
                .ToList() ?? [];

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(cachePath))
                return;
            var cached = JsonSerializer.Deserialize<Dictionary<string, Breakpoints>>(File.ReadAllText(cachePath));
            foreach (var (k, v) in cached ?? [])
                breakpoints[k] = v;
        }
        catch (Exception ex)
        {
            log.Warning($"Could not read the FFLogs cache: {ex.Message}");
        }
    }

    private void SaveCache()
    {
        try
        {
            var json = JsonSerializer.Serialize(breakpoints.ToDictionary(kv => kv.Key, kv => kv.Value));
            lock (cacheLock)
                File.WriteAllText(cachePath, json);
        }
        catch (Exception ex)
        {
            log.Warning($"Could not save the FFLogs cache: {ex.Message}");
        }
    }

    private sealed record EncounterEntry(int Id, string Name, bool Frozen);
}
