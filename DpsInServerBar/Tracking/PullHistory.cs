using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace DpsInServerBar.Tracking;

/// <summary>Pull history persisted as JSON next to the plugin configuration. Game thread only, except the file write.</summary>
public sealed class PullHistory
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string path;
    private readonly IPluginLog log;
    private readonly object writeLock = new();
    private readonly List<PullRecord> pulls = [];
    private int writtenRevision = -1;

    public PullHistory(string directory, IPluginLog log)
    {
        path = Path.Combine(directory, "history.json");
        this.log = log;
        Load();
    }

    /// <summary>Most recent first.</summary>
    public IReadOnlyList<PullRecord> Pulls => pulls;

    /// <summary>Incremented on every change, so windows can cache derived data.</summary>
    public int Revision { get; private set; }

    public void Add(PullRecord pull, int maxPulls)
    {
        pulls.Insert(0, pull);
        if (maxPulls > 0 && pulls.Count > maxPulls)
            pulls.RemoveRange(maxPulls, pulls.Count - maxPulls);
        Changed();
    }

    public void Remove(Guid id)
    {
        pulls.RemoveAll(p => p.Id == id);
        Changed();
    }

    public void Clear()
    {
        pulls.Clear();
        Changed();
    }

    /// <summary>Call after mutating a record in place (e.g. its outcome).</summary>
    public void Changed()
    {
        Revision++;
        SaveAsync();
    }

    /// <summary>Best value of a metric among the kills of the same encounter, excluding the given pull.</summary>
    public double? BestOf(string title, Func<PullRecord, double?> metric, Guid exclude) =>
        string.IsNullOrEmpty(title)
            ? null
            : pulls.Where(p => p.Id != exclude && p.Outcome == PullOutcome.Kill
                               && p.Title.Equals(title, StringComparison.OrdinalIgnoreCase))
                   .Select(metric)
                   .Where(v => v is > 0)
                   .Max();

    /// <summary>
    /// Best personal DPS among the kills of the same encounter, excluding the given pull. Wipes are ignored:
    /// comparing with a prog pull that ended early says nothing.
    /// </summary>
    public PullRecord? BestFor(string title, Guid? exclude = null) =>
        string.IsNullOrEmpty(title)
            ? null
            : pulls.Where(p => p.Id != exclude && p.Outcome == PullOutcome.Kill
                               && p.Title.Equals(title, StringComparison.OrdinalIgnoreCase) && p.YourDps > 0)
                   .MaxBy(p => p.YourDps);

    private void Load()
    {
        try
        {
            if (!File.Exists(path))
                return;

            var loaded = JsonSerializer.Deserialize<List<PullRecord>>(File.ReadAllText(path), JsonOptions);
            if (loaded is not null)
                pulls.AddRange(loaded);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Could not read the pull history, starting with an empty one");
        }
    }

    private void SaveAsync()
    {
        // Records are not mutated after being stored except for their outcome, so a shallow copy is enough.
        var copy = pulls.ToList();
        var revision = Revision;
        Task.Run(() =>
        {
            try
            {
                var json = JsonSerializer.Serialize(copy, JsonOptions);
                lock (writeLock)
                {
                    // Saves can finish out of order; never overwrite a newer state with an older one.
                    if (revision < writtenRevision)
                        return;

                    var tmp = path + ".tmp";
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, path, overwrite: true);
                    writtenRevision = revision;
                }
            }
            catch (Exception ex)
            {
                log.Error(ex, "Could not save the pull history");
            }
        });
    }
}
