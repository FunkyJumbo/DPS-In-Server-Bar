using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.JobGauge.Types;
using Dalamud.Plugin.Services;

namespace DpsInServerBar.Tracking;

/// <summary>Gauge lost to overcapping in a pull, for one resource.</summary>
/// <param name="Resource">Gauge name, e.g. "Blood".</param>
/// <param name="Wasted">Amount lost, in the gauge's units (stacks for stack gauges).</param>
/// <param name="Count">Number of times it overcapped.</param>
/// <param name="Actions">Actions that overcapped, most first, e.g. "Souleater ×2"; empty for time-based gauges.</param>
public sealed record GaugeWaste(string Resource, int Max, double Wasted, int Count, string Actions);

/// <summary>
/// Measures the job gauge you lose by overcapping. Gauges filled by actions: the gain of each action is learned
/// from what it gave while the gauge had room (the most frequent gain), then an action used with the gauge too
/// full for that gain counts the overflow as wasted. Gauges filled over time (lilies, Addersgall, Polyglot): the
/// time spent full, in units of their refill time. Game thread only.
/// </summary>
public sealed class GaugeTracker
{
    // A gauge change this long after an action is still that action's.
    private static readonly TimeSpan AttributionWindow = TimeSpan.FromSeconds(1.0);

    private readonly IJobGauges gauges;
    private readonly IObjectTable objectTable;
    private readonly ICondition condition;
    private readonly IDataManager dataManager;

    // Gains learned per job, gauge and action (kept for the session): every gain seen.
    private readonly Dictionary<(string Job, string Resource, uint Action), List<int>> learned = new();

    private readonly Dictionary<string, ResourceState> states = new();
    private string job = string.Empty;
    private bool wasInCombat;
    private DateTime lastFrameUtc;
    private int revision;
    private int publishedRevision = -1;
    private IReadOnlyList<GaugeWaste> published = [];

    public GaugeTracker(IJobGauges gauges, IObjectTable objectTable, ICondition condition, IDataManager dataManager)
    {
        this.gauges = gauges;
        this.objectTable = objectTable;
        this.condition = condition;
        this.dataManager = dataManager;
    }

    /// <summary>Gauge lost in the fight in progress, or in the last one: only the gauges that overcapped.</summary>
    public IReadOnlyList<GaugeWaste> Current
    {
        get
        {
            if (publishedRevision != revision)
            {
                publishedRevision = revision;
                published = states.Values.Where(s => s.Wasted > 0)
                                  .Select(s => new GaugeWaste(s.Def.Name, s.Def.Max, s.Wasted, s.Count,
                                                               string.Join(", ", s.ByAction.OrderByDescending(a => a.Value)
                                                                                  .Select(a => a.Value > 1 ? $"{ActionName(a.Key)} ×{a.Value}" : ActionName(a.Key)))))
                                  .ToList();
            }

            return published;
        }
    }

    /// <summary>One of your actions landed (from the action effect hook).</summary>
    public void OnActionUsed(uint actionId)
    {
        if (!wasInCombat)
            return;

        var now = DateTime.UtcNow;
        foreach (var state in states.Values)
        {
            if (state.Def.RegenSeconds is null && state.Last is { } value)
                state.Pending.Add(new PendingAction(actionId, now, value));
        }
    }

    /// <summary>Called every frame.</summary>
    public void Update()
    {
        var now = DateTime.UtcNow;
        var inCombat = condition[ConditionFlag.InCombat];
        var player = objectTable.LocalPlayer;
        var currentJob = player?.ClassJob.ValueNullable?.Abbreviation.ExtractText() ?? string.Empty;

        if (inCombat && !wasInCombat)
            Reset(currentJob);
        else if (currentJob != job)
            Reset(currentJob);
        wasInCombat = inCombat;

        var dt = (now - lastFrameUtc).TotalSeconds;
        lastFrameUtc = now;
        if (!inCombat || player is null)
            return;

        foreach (var state in states.Values)
        {
            int value;
            try
            {
                value = state.Def.Read(gauges);
            }
            catch
            {
                continue; // gauge not readable right now (job change in progress)
            }

            if (state.Def.RegenSeconds is { } regen)
                TrackTimeAtCap(state, value, regen, dt, player.IsDead);
            else
                TrackActions(state, value, now);

            state.Last = value;
        }
    }

    private void TrackTimeAtCap(ResourceState state, int value, double regen, double dt, bool dead)
    {
        if (dt <= 0 || dt > 1 || dead)
            return;

        var full = value >= state.Def.Max;
        if (full)
        {
            if (!state.WasFull)
                state.Count++;
            state.Wasted += dt / regen;
            revision++;
        }

        state.WasFull = full;
    }

    private void TrackActions(ResourceState state, int value, DateTime now)
    {
        // A rise goes to the latest action used just before it.
        if (state.Last is { } last && value > last
            && state.Pending.LastOrDefault(p => p.Gain == 0 && now - p.TimeUtc <= AttributionWindow) is { } owner)
        {
            owner.Gain = value - last;
            var key = (job, state.Def.Name, owner.ActionId);
            if (!learned.TryGetValue(key, out var gains))
                learned[key] = gains = [];
            gains.Add(owner.Gain);
            if (gains.Count > 50)
                gains.RemoveAt(0);
        }

        // Actions old enough to be settled: did they overflow?
        for (var i = state.Pending.Count - 1; i >= 0; i--)
        {
            var pending = state.Pending[i];
            if (now - pending.TimeUtc <= AttributionWindow)
                continue;

            state.Pending.RemoveAt(i);
            if (!learned.TryGetValue((job, state.Def.Name, pending.ActionId), out var gains) || gains.Count == 0)
                continue; // gain not known yet

            var expected = gains.GroupBy(g => g).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;
            var overflow = pending.ValueAtUse + expected - state.Def.Max;
            if (overflow <= 0 || pending.Gain >= expected)
                continue;

            state.Wasted += Math.Min(overflow, expected);
            state.Count++;
            state.ByAction[pending.ActionId] = state.ByAction.GetValueOrDefault(pending.ActionId) + 1;
            revision++;
        }
    }

    private void Reset(string newJob)
    {
        job = newJob;
        states.Clear();
        foreach (var def in Definitions(newJob))
            states[def.Name] = new ResourceState(def);
        revision++;
    }

    private string ActionName(uint actionId) =>
        dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().TryGetRow(actionId, out var row) && row.Name.ExtractText() is { Length: > 0 } name
            ? name
            : $"#{actionId}";

    /// <summary>Whether some gauge of this job (English abbreviation) is watched.</summary>
    public static bool Tracks(string job) => Definitions(job.ToUpperInvariant()).Any();

    /// <summary>The gauges worth watching for a job. Gauges filled by crits or by chance are left out.</summary>
    private static IEnumerable<ResourceDef> Definitions(string job) => job switch
    {
        "DRK" => [new(Loc.T("Blood"), 100, g => g.Get<DRKGauge>().Blood)],
        "WAR" => [new(Loc.T("Beast Gauge"), 100, g => g.Get<WARGauge>().BeastGauge)],
        "GNB" => [new(Loc.T("Cartridges"), 3, g => g.Get<GNBGauge>().Ammo)],
        "DRG" => [new(Loc.T("Firstminds' Focus"), 2, g => g.Get<DRGGauge>().FirstmindsFocusCount)],
        "NIN" => [new(Loc.T("Ninki"), 100, g => g.Get<NINGauge>().Ninki), new(Loc.T("Kazematoi"), 5, g => g.Get<NINGauge>().Kazematoi)],
        "SAM" => [new(Loc.T("Kenki"), 100, g => g.Get<SAMGauge>().Kenki), new(Loc.T("Meditation"), 3, g => g.Get<SAMGauge>().MeditationStacks)],
        "RPR" => [new(Loc.T("Soul"), 100, g => g.Get<RPRGauge>().Soul), new(Loc.T("Shroud"), 100, g => g.Get<RPRGauge>().Shroud)],
        "VPR" => [new(Loc.T("Rattling Coils"), 3, g => g.Get<VPRGauge>().RattlingCoilStacks), new(Loc.T("Serpent Offerings"), 100, g => g.Get<VPRGauge>().SerpentOffering)],
        "MCH" => [new(Loc.T("Heat"), 100, g => g.Get<MCHGauge>().Heat), new(Loc.T("Battery"), 100, g => g.Get<MCHGauge>().Battery)],
        "RDM" => [new(Loc.T("White Mana"), 100, g => g.Get<RDMGauge>().WhiteMana), new(Loc.T("Black Mana"), 100, g => g.Get<RDMGauge>().BlackMana)],
        "PCT" => [new(Loc.T("Palette"), 100, g => g.Get<PCTGauge>().PalleteGauge), new(Loc.T("Paint"), 5, g => g.Get<PCTGauge>().Paint)],
        "SCH" => [new(Loc.T("Fae Aether"), 100, g => g.Get<SCHGauge>().FairyGauge)],
        "WHM" => [new(Loc.T("Lilies"), 3, g => g.Get<WHMGauge>().Lily, RegenSeconds: 20), new(Loc.T("Blood Lily"), 3, g => g.Get<WHMGauge>().BloodLily)],
        "SGE" => [new(Loc.T("Addersgall"), 3, g => g.Get<SGEGauge>().Addersgall, RegenSeconds: 20)],
        "BLM" => [new(Loc.T("Polyglot"), 3, g => g.Get<BLMGauge>().PolyglotStacks, RegenSeconds: 30)],
        _ => [],
    };

    private sealed record ResourceDef(string Name, int Max, Func<IJobGauges, int> Read, double? RegenSeconds = null);

    private sealed class ResourceState(ResourceDef def)
    {
        public ResourceDef Def { get; } = def;
        public int? Last { get; set; }
        public bool WasFull { get; set; }
        public double Wasted { get; set; }
        public int Count { get; set; }
        public List<PendingAction> Pending { get; } = [];
        public Dictionary<uint, int> ByAction { get; } = new();
    }

    private sealed class PendingAction(uint actionId, DateTime timeUtc, int valueAtUse)
    {
        public uint ActionId { get; } = actionId;
        public DateTime TimeUtc { get; } = timeUtc;
        public int ValueAtUse { get; } = valueAtUse;
        public int Gain { get; set; }
    }
}
