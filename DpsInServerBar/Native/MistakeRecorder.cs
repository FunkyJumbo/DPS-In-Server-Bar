using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using DpsInServerBar.Combat;
using DpsInServerBar.Tracking;
using Lumina.Excel.Sheets;

namespace DpsInServerBar.Native;

/// <summary>
/// Catches the mechanics failed by the party: an enemy action putting a penalty debuff (Vulnerability Up, Damage
/// Down, Down for the Count...) on a party member. Fed by the built-in tracker's action effect hook, game thread only.
/// </summary>
public sealed class MistakeRecorder
{
    // Penalty debuffs, by English name (the ids differ from one fight to another).
    private static readonly HashSet<string> PenaltyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Vulnerability Up",
        "Physical Vulnerability Up",
        "Magic Vulnerability Up",
        "Damage Down",
        "Down for the Count",
    };

    private static readonly HashSet<string> VulnerabilityNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Vulnerability Up",
        "Physical Vulnerability Up",
        "Magic Vulnerability Up",
    };

    // Several packets for one hit: the same debuff from the same action on the same player is counted once.
    private const double DuplicateSeconds = 1.0;
    private const int MaxMistakes = 500;

    private readonly IDataManager dataManager;
    private readonly HashSet<uint> penalties;
    private readonly HashSet<uint> vulnerabilities;
    private readonly List<MechanicMistake> mistakes = [];
    private DateTime combatStartUtc;
    private bool wasInCombat;
    private int revision;
    private int publishedRevision = -1;
    private IReadOnlyList<MechanicMistake> published = [];

    public MistakeRecorder(IDataManager dataManager)
    {
        this.dataManager = dataManager;
        var english = dataManager.GetExcelSheet<Status>(ClientLanguage.English);
        penalties = english.Where(s => PenaltyNames.Contains(s.Name.ExtractText())).Select(s => s.RowId).ToHashSet();
        vulnerabilities = english.Where(s => VulnerabilityNames.Contains(s.Name.ExtractText())).Select(s => s.RowId).ToHashSet();
    }

    /// <summary>Ignore a vulnerability put on a tank by an action hitting at most two players (a tankbuster).</summary>
    public bool IgnoreTankbusters { get; set; } = true;

    /// <summary>Mistakes of the fight in progress, or of the last one, oldest first.</summary>
    public IReadOnlyList<MechanicMistake> Current
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

    /// <summary>Called every frame with the combat state: a new fight starts a new list.</summary>
    public void Tick(bool inCombat)
    {
        if (inCombat && !wasInCombat)
        {
            combatStartUtc = DateTime.UtcNow;
            mistakes.Clear();
            revision++;
        }

        wasInCombat = inCombat;
    }

    public bool IsPenalty(uint statusId) => penalties.Contains(statusId);

    /// <summary>An enemy action put <paramref name="statusId"/> on a party member.</summary>
    public void OnPenalty(
        string player, string job, bool isYou, uint statusId, uint actionType, uint actionId, string source, int targetCount)
    {
        if (!wasInCombat || mistakes.Count >= MaxMistakes)
            return;

        if (IgnoreTankbusters && targetCount <= 2 && vulnerabilities.Contains(statusId)
            && JobRoles.Of(job) == JobRole.Tank)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var at = (now - combatStartUtc).TotalSeconds;
        var status = StatusName(statusId);
        var ability = ActionName(actionType, actionId);
        if (mistakes.Any(m => m.Player == player && m.Status == status && m.Ability == ability && at - m.AtSeconds < DuplicateSeconds))
            return;

        mistakes.Add(new MechanicMistake(at, player, job, isYou, status, ability, source));
        revision++;
    }

    // Names in the client's language, like the game shows them.
    private string StatusName(uint statusId) =>
        dataManager.GetExcelSheet<Status>().TryGetRow(statusId, out var row) && row.Name.ExtractText() is { Length: > 0 } name
            ? name
            : $"#{statusId}";

    private string ActionName(uint actionType, uint actionId) =>
        actionType == 1 && dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().TryGetRow(actionId, out var row)
                        && row.Name.ExtractText() is { Length: > 0 } name
            ? name
            : "unknown";
}
