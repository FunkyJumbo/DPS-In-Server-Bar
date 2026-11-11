using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DpsInServerBar.Tracking;

public enum ComboMistakeKind
{
    /// <summary>A combo step used outside its combo: it lost its combo bonus.</summary>
    NoCombo,

    /// <summary>A combo left unfinished: another combo was started before its next step.</summary>
    Dropped,

    /// <summary>A combo left unfinished: its timer ran out before the next step.</summary>
    TimedOut,
}

/// <summary>One combo error in a pull.</summary>
/// <param name="ActionId">The action used; TimedOut: the last step done.</param>
/// <param name="ExpectedId">NoCombo: the step that should have come before; Dropped: the combo step left unfinished; TimedOut: the last step done.</param>
/// <param name="Expired">NoCombo: the combo had timed out (often downtime), rather than a wrong button.</param>
public sealed record ComboMistake(
    double AtSeconds, ComboMistakeKind Kind, uint ActionId, string Action, uint ExpectedId, string Expected, bool Expired);

/// <summary>
/// Finds your combo errors (1-2-3 combos): a step used without the one before it (wrong button), a combo restarted
/// before it was finished, or a combo whose timer ran out before its next step. The combos come from the game data (each step names the action
/// before it) and the combo in progress from the game. Actions the game lets you use without breaking a combo
/// are ignored. Game thread only.
/// </summary>
public sealed unsafe class ComboTracker
{
    private const int MaxMistakes = 300;

    private readonly IObjectTable objectTable;
    private readonly ICondition condition;
    private readonly IDataManager dataManager;

    // Per action of the job: the step it follows (0 for a combo start), and the steps that can follow it.
    private readonly Dictionary<uint, uint> previous = new();
    private readonly Dictionary<uint, List<uint>> next = new();
    private readonly HashSet<uint> preserves = new();
    private readonly Dictionary<uint, string> names = new();
    private uint job;

    // The combo in progress as of the last frame (the action hook sees the game state after the action).
    private uint comboAction;
    private float comboTimer;
    private bool actionSinceLastFrame;

    // Upgraded actions standing for their base action in combos (e.g. Raiden Thrust for True Thrust), this frame.
    private readonly Dictionary<uint, uint> baseOf = new();

    private readonly List<ComboMistake> mistakes = [];
    private bool wasInCombat;
    private DateTime combatStartUtc;
    private int revision;
    private int publishedRevision = -1;
    private IReadOnlyList<ComboMistake> published = [];

    public ComboTracker(IObjectTable objectTable, ICondition condition, IDataManager dataManager)
    {
        this.objectTable = objectTable;
        this.condition = condition;
        this.dataManager = dataManager;
    }

    /// <summary>Combo errors of the fight in progress, or of the last one, oldest first.</summary>
    public IReadOnlyList<ComboMistake> Current
    {
        get
        {
            if (publishedRevision != revision)
            {
                publishedRevision = revision;
                published = mistakes.ToList();
            }

            return published;
        }
    }

    /// <summary>Whether this job has combos to follow.</summary>
    public bool TracksCurrentJob => next.Count > 0;

    /// <summary>Called every frame: remembers the combo in progress.</summary>
    public void Update()
    {
        var inCombat = condition[ConditionFlag.InCombat];
        if (inCombat && !wasInCombat)
        {
            mistakes.Clear();
            combatStartUtc = DateTime.UtcNow;
            revision++;
        }

        wasInCombat = inCombat;

        var player = objectTable.LocalPlayer;
        if (player is null)
            return;
        if (player.ClassJob.RowId != job)
            Load(player.ClassJob.RowId);

        var am = ActionManager.Instance();
        if (am == null)
            return;

        // The timer ran out with a step still to come (and no action ended the combo): the combo is lost.
        var action = am->Combo.Action;
        var timer = am->Combo.Timer;
        if (inCombat && !player.IsDead && !actionSinceLastFrame && comboTimer > 0 && timer <= 0 && comboAction != 0
            && next.ContainsKey(baseOf.GetValueOrDefault(comboAction, comboAction)) && mistakes.Count < MaxMistakes)
        {
            var step = baseOf.GetValueOrDefault(comboAction, comboAction);
            Add(new ComboMistake((DateTime.UtcNow - combatStartUtc).TotalSeconds, ComboMistakeKind.TimedOut, comboAction,
                                 Name(comboAction), step, Name(step), true));
        }

        actionSinceLastFrame = false;
        comboAction = action;
        comboTimer = timer;

        baseOf.Clear();
        foreach (var step in next.Keys)
        {
            var adjusted = am->GetAdjustedActionId(step);
            if (adjusted != step)
                baseOf[adjusted] = step;
        }
    }

    /// <summary>One of your actions landed (from the action effect hook).</summary>
    public void OnActionUsed(uint actionId)
    {
        actionSinceLastFrame = true;
        if (!wasInCombat || mistakes.Count >= MaxMistakes || preserves.Contains(actionId))
            return;

        var isStep = previous.TryGetValue(actionId, out var before);
        if (!isStep)
            return; // not part of a combo

        var active = comboTimer > 0 ? comboAction : 0u;
        var current = baseOf.GetValueOrDefault(active, active);
        var at = (DateTime.UtcNow - combatStartUtc).TotalSeconds;

        if (before != 0)
        {
            // A step: the combo in progress must be the one before it.
            // After a timed-out combo (already counted), the step itself is not counted again.
            var timedOut = comboTimer <= 0 && baseOf.GetValueOrDefault(comboAction, comboAction) == before;
            if (before != current && before != active && !timedOut)
                Add(new ComboMistake(at, ComboMistakeKind.NoCombo, actionId, Name(actionId), before, Name(before), false));
            return;
        }

        // A combo start, while a combo was waiting for its next step.
        if (current != 0 && next.ContainsKey(current) && !next[current].Contains(actionId))
            Add(new ComboMistake(at, ComboMistakeKind.Dropped, actionId, Name(actionId), current, Name(current), false));
    }

    private void Add(ComboMistake mistake)
    {
        mistakes.Add(mistake);
        revision++;
    }

    // The combos of a job (and of its base class) from the game data.
    private void Load(uint classJob)
    {
        job = classJob;
        previous.Clear();
        next.Clear();
        preserves.Clear();

        var jobs = new HashSet<uint> { classJob };
        if (dataManager.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>().TryGetRow(classJob, out var cj) && cj.ClassJobParent.RowId != 0)
            jobs.Add(cj.ClassJobParent.RowId);

        var actions = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>()
                                 .Where(a => a.IsPlayerAction && jobs.Contains(a.ClassJob.RowId))
                                 .ToList();
        foreach (var a in actions.Where(a => a.ActionCombo.RowId != 0))
        {
            previous[a.RowId] = a.ActionCombo.RowId;
            if (!next.TryGetValue(a.ActionCombo.RowId, out var list))
                next[a.ActionCombo.RowId] = list = [];
            list.Add(a.RowId);
        }

        // Combo starts: actions with a next step but none before them.
        foreach (var start in next.Keys.Where(k => !previous.ContainsKey(k)).ToList())
            previous[start] = 0;

        foreach (var a in actions.Where(a => a.PreservesCombo))
            preserves.Add(a.RowId);
    }

    private string Name(uint actionId)
    {
        if (!names.TryGetValue(actionId, out var name))
        {
            name = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().TryGetRow(actionId, out var row) ? row.Name.ExtractText() : $"#{actionId}";
            names[actionId] = name;
        }

        return name;
    }
}
