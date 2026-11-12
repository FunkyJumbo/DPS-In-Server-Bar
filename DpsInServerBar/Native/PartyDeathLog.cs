using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using DpsInServerBar.Tracking;

namespace DpsInServerBar.Native;

/// <summary>
/// The deaths of every party member in the fight in progress (or the last one), with their cause: one
/// <see cref="DeathRecorder"/> per member, yours being the one that also names your own death. Game thread only.
/// </summary>
public sealed class PartyDeathLog
{
    // A death this soon after combat ended (the whole party died, closing the fight) still belongs to it.
    private static readonly TimeSpan AfterCombat = TimeSpan.FromSeconds(5);
    private const int MaxDeaths = 200;

    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly IObjectTable objectTable;
    private readonly DeathRecorder yours;
    private readonly Dictionary<uint, Member> members = new();
    private readonly HashSet<uint> present = [];
    private readonly List<Entry> deaths = [];
    private bool wasInCombat;
    private DateTime combatEndUtc = DateTime.MinValue;
    private int revision;
    private (int Revision, DateTime StartUtc) publishedKey = (-1, default);
    private IReadOnlyList<PartyDeath> published = [];
    private int announced;

    public PartyDeathLog(IDataManager dataManager, IPluginLog log, IObjectTable objectTable, DeathRecorder yours)
    {
        this.dataManager = dataManager;
        this.log = log;
        this.objectTable = objectTable;
        this.yours = yours;
    }

    /// <summary>The recorder of a party member, null for anyone else.</summary>
    public DeathRecorder? For(uint entityId) => members.TryGetValue(entityId, out var m) ? m.Recorder : null;

    /// <summary>The deaths of the current (or last) fight, oldest first, timed from <paramref name="startUtc"/>.</summary>
    public IReadOnlyList<PartyDeath> Since(DateTime startUtc)
    {
        if (publishedKey != (revision, startUtc))
        {
            publishedKey = (revision, startUtc);
            published = deaths.Where(e => e.Info.TimeUtc >= startUtc.AddSeconds(-2))
                              .Select(e => new PartyDeath(Math.Max(0, (e.Info.TimeUtc - startUtc).TotalSeconds), e.Player, e.Job, e.IsYou,
                                                          e.Info.Ability, e.Info.Source, e.Info.Damage, e.Info.HpBefore, e.Info.Recap.ToList()))
                              .ToList();
        }

        return published;
    }

    /// <summary>
    /// The deaths not handed out yet, once they are at least <paramref name="settle"/> old (a body still falling
    /// refines its cause just after the death). Each death is handed out once.
    /// </summary>
    public List<(string Player, string Job, bool IsYou, DeathInfo Info)> TakeNew(TimeSpan settle)
    {
        var result = new List<(string, string, bool, DeathInfo)>();
        var now = DateTime.UtcNow;
        while (announced < deaths.Count && now - deaths[announced].Info.TimeUtc >= settle)
        {
            var e = deaths[announced++];
            result.Add((e.Player, e.Job, e.IsYou, e.Info));
        }

        return result;
    }

    /// <summary>Called every frame with the current party (you included).</summary>
    public void Tick(IEnumerable<(uint Id, string Name, string Job, bool IsYou)> party, bool inCombat, Func<uint, string> nameOf)
    {
        var now = DateTime.UtcNow;
        if (inCombat && !wasInCombat)
        {
            deaths.Clear();
            announced = 0;
            revision++;
        }
        else if (!inCombat && wasInCombat)
        {
            combatEndUtc = now;
        }

        wasInCombat = inCombat;

        present.Clear();
        foreach (var (id, name, job, isYou) in party)
        {
            present.Add(id);
            if (!members.TryGetValue(id, out var member) || member.IsYou != isYou)
            {
                var recorder = isYou ? yours : new DeathRecorder(dataManager, log, logDetails: false);
                members[id] = member = new Member(recorder) { Seen = recorder.LastDeath };
            }

            member.Name = name;
            member.Job = job;
            member.IsYou = isYou;
            member.Recorder.Observe(objectTable.SearchByEntityId(id) as IBattleChara, nameOf);
            Collect(member, now, inCombat);
        }

        foreach (var gone in members.Keys.Where(id => !present.Contains(id)).ToList())
            members.Remove(gone);
    }

    /// <summary>A new death of this member goes in the list; a refined one (body still falling) replaces its entry.</summary>
    private void Collect(Member member, DateTime now, bool inCombat)
    {
        var last = member.Recorder.LastDeath;
        if (last is null || ReferenceEquals(last, member.Seen))
            return;

        var refined = member.Seen is { } seen && seen.TimeUtc == last.TimeUtc;
        member.Seen = last;
        if (refined)
        {
            var index = deaths.FindIndex(e => e.Player == member.Name && e.Info.TimeUtc == last.TimeUtc);
            if (index >= 0)
            {
                deaths[index] = deaths[index] with { Info = last };
                revision++;
            }

            return;
        }

        if ((!inCombat && now - combatEndUtc > AfterCombat) || deaths.Count >= MaxDeaths)
            return;

        deaths.Add(new Entry(member.Name, member.Job, member.IsYou, last));
        revision++;
    }

    private sealed record Entry(string Player, string Job, bool IsYou, DeathInfo Info);

    private sealed class Member(DeathRecorder recorder)
    {
        public DeathRecorder Recorder { get; } = recorder;
        public DeathInfo? Seen { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Job { get; set; } = string.Empty;
        public bool IsYou { get; set; }
    }
}
