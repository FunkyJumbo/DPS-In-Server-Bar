using System;
using System.Collections.Generic;
using System.Linq;

namespace DpsInServerBar.Native;

/// <summary>Healing done by a party member in a pull, and how much of it went over full HP.</summary>
/// <param name="Sources">Yours only: per action (or HoT), most healing first.</param>
public sealed record HealerOverheal(string Player, string Job, bool IsYou, long Healing, long Overheal, HealSource[]? Sources = null)
{
    public double Share => Healing > 0 ? (double)Overheal / Healing : 0;
}

/// <summary>Healing and overheal of one of your heal actions (or HoTs) in a pull.</summary>
public sealed record HealSource(string Name, uint Icon, bool IsHot, long Healing, long Overheal)
{
    public double Share => Healing > 0 ? (double)Overheal / Healing : 0;
}

/// <summary>
/// Heals done by the party (pets folded into their owner) and their overheal: the part of each heal above the
/// HP the target was missing when it landed. Game thread only.
/// </summary>
public sealed class HealLog
{
    private readonly Dictionary<uint, Entry> byOwner = new();

    // Heals landed on a target since the last frame: the HP read from the game does not include them yet.
    private readonly Dictionary<uint, long> healedThisFrame = new();

    private bool wasInCombat;
    private int revision;
    private int publishedRevision = -1;
    private IReadOnlyList<HealerOverheal> published = [];

    /// <summary>Healing and overheal of each party member who healed, in the fight in progress or the last one.</summary>
    public IReadOnlyList<HealerOverheal> Current
    {
        get
        {
            if (publishedRevision != revision)
            {
                publishedRevision = revision;
                published = byOwner.Values.Where(e => e.Healing > 0)
                                   .Select(e => new HealerOverheal(e.Name, e.Job, e.IsYou, e.Healing, e.Overheal, e.IsYou ? Describe(e.Sources) : null))
                                   .OrderByDescending(e => e.Healing)
                                   .ToList();
            }

            return published;
        }
    }

    public void Tick(bool inCombat)
    {
        if (inCombat && !wasInCombat)
        {
            byOwner.Clear();
            revision++;
        }

        wasInCombat = inCombat;
        healedThisFrame.Clear();
    }

    /// <summary>A heal (or HoT tick) from a party member landed on a target missing <paramref name="missingHp"/> HP.</summary>
    /// <param name="sourceId">The heal action, or the HoT status when <paramref name="isHot"/>.</param>
    public void OnHeal(uint ownerId, string name, string job, bool isYou, uint targetId, long amount, long missingHp, uint sourceId = 0, bool isHot = false)
    {
        if (!wasInCombat || amount <= 0)
            return;

        var already = healedThisFrame.GetValueOrDefault(targetId);
        var missing = Math.Max(0, missingHp - already);
        var over = Math.Max(0, amount - missing);
        healedThisFrame[targetId] = already + amount;

        if (!byOwner.TryGetValue(ownerId, out var entry))
        {
            entry = new Entry(name, job.ToUpperInvariant(), isYou);
            byOwner[ownerId] = entry;
        }

        entry.Healing += amount;
        entry.Overheal += over;
        if (isYou && sourceId != 0)
        {
            var (h, o) = entry.Sources.GetValueOrDefault((isHot, sourceId));
            entry.Sources[(isHot, sourceId)] = (h + amount, o + over);
        }

        revision++;
    }

    private static HealSource[] Describe(Dictionary<(bool IsHot, uint Id), (long Healing, long Overheal)> sources) =>
        sources.Select(s => (Key: s.Key, Info: Name(s.Key.IsHot, s.Key.Id), s.Value))
               .GroupBy(s => (s.Info.Name, s.Key.IsHot)) // versions of a heal with the same name
               .Select(g => new HealSource(g.Key.Name, g.First().Info.Icon, g.Key.IsHot,
                                           g.Sum(s => s.Value.Healing), g.Sum(s => s.Value.Overheal)))
               .OrderByDescending(s => s.Healing)
               .ToArray();

    private static readonly Dictionary<(bool, uint), (string Name, uint Icon)> NameCache = new();

    // Name and icon of an action or a status.
    private static (string Name, uint Icon) Name(bool isHot, uint id)
    {
        if (NameCache.TryGetValue((isHot, id), out var known))
            return known;

        (string, uint) info;
        if (isHot)
        {
            info = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Status>().TryGetRow(id, out var status)
                       ? (status.Name.ExtractText(), status.Icon)
                       : ($"#{id}", 0u);
        }
        else
        {
            info = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().TryGetRow(id, out var action)
                       ? (action.Name.ExtractText(), (uint)action.Icon)
                       : ($"#{id}", 0u);
        }

        NameCache[(isHot, id)] = info;
        return info;
    }

    private sealed class Entry(string name, string job, bool isYou)
    {
        public string Name { get; } = name;
        public string Job { get; } = job;
        public bool IsYou { get; } = isYou;
        public long Healing { get; set; }
        public long Overheal { get; set; }
        public Dictionary<(bool IsHot, uint Id), (long Healing, long Overheal)> Sources { get; } = new();
    }
}
