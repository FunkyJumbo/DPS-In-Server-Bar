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
        cachePath = Path.Combine(configDirectory, "fflogs-cache-v2.json");
        // The first cache only held the top 100 characters (taken for the whole ranking): never reused.
        try { File.Delete(Path.Combine(configDirectory, "fflogs-cache.json")); } catch (IOException) { }
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
            return Loc.T("No FFLogs credentials.");
        if (catalog is null)
            return Loc.T("Loading the FFLogs encounter list...");

        var encounter = ResolveEncounter(territory, bossNameId);
        return encounter is { } e
            ? $"{e.Name} (id {e.Id}, {(e.Difficulty == 101 ? "Savage" : "Normal/Extreme/Ultimate")})"
            : Loc.F("No FFLogs encounter matches \"{0}\". Set the id by hand.", EnglishBossName(bossNameId));
    }

    public string EnglishBossName(uint bossNameId) =>
        bossNameId != 0 && dataManager.GetExcelSheet<BNpcName>(ClientLanguage.English).TryGetRow(bossNameId, out var row)
            ? row.Singular.ExtractText()
            : string.Empty;

    /// <summary>Result of the last "Test connection": null while none (or running), then (success, message).</summary>
    public (bool Ok, string Message)? TestResult { get; private set; }

    public bool Testing { get; private set; }

    /// <summary>Checks the credentials with a tiny query (the API rate limit), in the background.</summary>
    public void TestConnection()
    {
        if (Testing)
            return;

        Testing = true;
        TestResult = null;
        var id = config.FflogsClientId;
        var secret = config.FflogsClientSecret;
        Task.Run(async () =>
        {
            try
            {
                var data = await api.QueryAsync(id, secret, "{ rateLimitData { limitPerHour pointsSpentThisHour } }", cts.Token)
                                    .ConfigureAwait(false);
                var limit = data["rateLimitData"]?["limitPerHour"]?.ToString();
                var spent = double.TryParse(data["rateLimitData"]?["pointsSpentThisHour"]?.ToString(),
                                            System.Globalization.NumberStyles.Float,
                                            System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 0;
                TestResult = (true, Loc.F("Connected. API points used this hour: {0:0} / {1}.", spent, limit));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                TestResult = (false, e.Message);
            }
            finally
            {
                Testing = false;
            }
        });
    }

    /// <summary>Drops the cache, e.g. after changing credentials.</summary>
    public void Reset()
    {
        TestResult = null;
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

        // Pages already fetched: amounts (best first) and whether more pages follow.
        var fetched = new Dictionary<int, (List<double> Amounts, bool HasMore)>();

        async Task FetchPages(IEnumerable<int> wanted)
        {
            var pages = wanted.Where(p => !fetched.ContainsKey(p)).Distinct().ToList();
            if (pages.Count == 0)
                return;

            var aliases = string.Join(" ", pages.Select(p => $"p{p}: {Rankings(p)}"));
            var data = await api.QueryAsync(id, secret, $"{{ worldData {{ encounter(id: {encounterId}) {{ {aliases} }} }} }}", cts.Token)
                                .ConfigureAwait(false);
            foreach (var p in pages)
            {
                var node = RankingsNode(data, $"p{p}");
                fetched[p] = (Amounts(node), node?["hasMorePages"]?.GetValue<bool>() ?? false);
            }
        }

        async Task<(List<double> Amounts, bool HasMore)> Page(int page)
        {
            await FetchPages([page]).ConfigureAwait(false);
            return fetched[page];
        }

        var first = await Page(1).ConfigureAwait(false);
        if (first.Amounts.Count == 0)
            throw new InvalidOperationException($"No {spec} rankings on FFLogs for this encounter yet.");
        var perPage = first.Amounts.Count;

        // The API gives no total: find the last page (doubling, then halving), each page holding perPage characters.
        var last = 1;
        if (first.HasMore)
        {
            var low = 1; // known to have more pages after it
            var high = 2;
            while (true)
            {
                var (amounts, hasMore) = await Page(high).ConfigureAwait(false);
                if (amounts.Count == 0)
                    break;           // past the end: the last page is between low and high
                if (!hasMore)
                {
                    low = high;      // high is the last page
                    break;
                }

                low = high;
                high *= 2;
                if (high > 1 << 16)
                    break;
            }

            while (low < high && !(fetched.TryGetValue(low, out var l) && !l.HasMore))
            {
                var mid = (low + high + 1) / 2;
                var (amounts, hasMore) = await Page(mid).ConfigureAwait(false);
                if (amounts.Count == 0)
                    high = mid - 1;
                else if (hasMore)
                    low = mid;
                else
                    low = high = mid;
            }

            last = low;
        }

        var count = (last - 1) * perPage + (await Page(last).ConfigureAwait(false)).Amounts.Count;
        var ranks = TargetPercentiles
            .Select(p => (Percentile: p, Rank: Math.Clamp((int)Math.Floor(count * (1 - p / 100)) + 1, 1, count)))
            .ToList();

        // Every page still needed, in a single request.
        await FetchPages(ranks.Select(r => (r.Rank - 1) / perPage + 1)).ConfigureAwait(false);

        var points = new List<double[]> { new[] { first.Amounts[0], 100.0 } };
        foreach (var (percentile, rank) in ranks)
        {
            var amounts = fetched[(rank - 1) / perPage + 1].Amounts;
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
