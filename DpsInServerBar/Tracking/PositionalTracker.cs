using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

namespace DpsInServerBar.Tracking;

/// <summary>One of your positional actions in a pull: whether its positional bonus landed.</summary>
public sealed record PositionalUse(double AtSeconds, uint ActionId, string Action, bool Rear, bool Hit);

/// <summary>
/// Counts the positionals you hit and miss (melee jobs). The game reports on each hit the potency bonus it got, in
/// percent; for each positional action the bonuses that include the positional are known (the community table of
/// the DamageInfo plugin), so the hit is read from the game itself: True North, omnidirectional bosses and
/// Directional Disregard are handled by the game. A bonus not in the table falls back to where you stood. Game thread only.
/// </summary>
public sealed class PositionalTracker
{
    private const ushort TrueNorth = 1250;
    private const int MaxUses = 1000;

    private sealed record Def(bool Rear, int[] HitBonuses);

    // Action id -> rear or flank, and the bonus percents that mean the positional landed (levels 50-100, buffed or not,
    // combo or not).
    private static readonly Dictionary<uint, Def> Defs = new()
    {
        [56] = new(false, [18, 14, 13, 20, 12]),                         // Snap Punch
        [66] = new(true, [18, 17, 15, 14]),                              // Demolish
        [36947] = new(false, [16, 11]),                                  // Pouncing Coeurl
        [88] = new(true, [28, 61]),                                      // Chaos Thrust
        [25772] = new(true, [28, 66, 22, 58]),                           // Chaotic Spring
        [3554] = new(false, [28, 66, 22, 58]),                           // Fang and Claw
        [3556] = new(true, [28, 66, 22, 58]),                            // Wheeling Thrust
        [2255] = new(true, [70, 54, 23, 37, 20, 30, 50, 63, 15, 21, 42, 52]), // Aeolian Edge
        [3563] = new(false, [30, 65, 20, 52, 37, 72]),                   // Armor Crush
        [2258] = new(true, [25]),                                        // Trick Attack
        [7481] = new(true, [33, 72, 31, 70, 23, 61]),                    // Gekko
        [7482] = new(false, [33, 72, 31, 70, 23, 61]),                   // Kasha
        [24382] = new(false, [11, 13, 9, 10]),                           // Gibbet
        [24383] = new(true, [11, 13, 9, 10]),                            // Gallows
        [36970] = new(false, [7]),                                       // Executioner's Gibbet
        [36971] = new(true, [7]),                                        // Executioner's Gallows
        [34610] = new(false, [48, 54, 60, 70, 50, 63]),                  // Flanksting Strike
        [34611] = new(false, [48, 54, 60, 70, 50, 63]),                  // Flanksbane Fang
        [34612] = new(true, [48, 54, 60, 70, 50, 63]),                   // Hindsting Strike
        [34613] = new(true, [48, 54, 60, 70, 50, 63]),                   // Hindsbane Fang
        [34621] = new(false, [7]),                                       // Hunter's Coil
        [34622] = new(true, [7]),                                        // Swiftskin's Coil
    };

    private readonly IObjectTable objectTable;
    private readonly ICondition condition;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly List<PositionalUse> uses = [];
    private readonly HashSet<(uint, byte)> loggedUnknown = [];
    private bool wasInCombat;
    private DateTime combatStartUtc;
    private int revision;
    private int publishedRevision = -1;
    private IReadOnlyList<PositionalUse> published = [];

    public PositionalTracker(IObjectTable objectTable, ICondition condition, IDataManager dataManager, IPluginLog log)
    {
        this.objectTable = objectTable;
        this.condition = condition;
        this.dataManager = dataManager;
        this.log = log;
    }

    /// <summary>Whether the action has a positional.</summary>
    public static bool IsPositional(uint actionId) => Defs.ContainsKey(actionId);

    /// <summary>Your positional actions in the fight in progress, or in the last one, oldest first.</summary>
    public IReadOnlyList<PositionalUse> Current
    {
        get
        {
            if (publishedRevision != revision)
            {
                publishedRevision = revision;
                published = uses.ToList();
            }

            return published;
        }
    }

    /// <summary>Called every frame.</summary>
    public void Update()
    {
        var inCombat = condition[ConditionFlag.InCombat];
        if (inCombat && !wasInCombat)
        {
            uses.Clear();
            combatStartUtc = DateTime.UtcNow;
            revision++;
        }

        wasInCombat = inCombat;
    }

    /// <summary>One of your actions hit an enemy (first target), with the potency bonus the game reported.</summary>
    public void OnHit(uint actionId, byte bonusPercent, uint targetId)
    {
        if (!wasInCombat || uses.Count >= MaxUses || !Defs.TryGetValue(actionId, out var def))
            return;

        bool hit;
        if (def.HitBonuses.Contains(bonusPercent))
        {
            hit = true;
        }
        else
        {
            // A bonus not in the table: a miss if where you stood says so too.
            hit = StoodRight(def.Rear, targetId) ?? false;
            if (hit && loggedUnknown.Add((actionId, bonusPercent)))
                log.Info($"Positional bonus {bonusPercent}% of action {actionId} not in the table, counted as a hit from your position.");
        }

        uses.Add(new PositionalUse((DateTime.UtcNow - combatStartUtc).TotalSeconds, actionId, ActionName(actionId), def.Rear, hit));
        revision++;
    }

    // Whether you stood behind (rear) or on a side (flank) of the target, or had True North; null if unknown.
    private bool? StoodRight(bool rear, uint targetId)
    {
        var player = objectTable.LocalPlayer;
        if (player is null || objectTable.SearchById(targetId) is not IGameObject target)
            return null;
        if (player.StatusList.Any(s => s.StatusId == TrueNorth))
            return true;

        var facing = new Vector2(MathF.Sin(target.Rotation), MathF.Cos(target.Rotation));
        var toPlayer = new Vector2(player.Position.X - target.Position.X, player.Position.Z - target.Position.Z);
        if (toPlayer.LengthSquared() < 0.0001f)
            return null;

        var cos = Vector2.Dot(facing, Vector2.Normalize(toPlayer));
        var behind = cos <= -MathF.Sqrt(0.5f);
        var front = cos >= MathF.Sqrt(0.5f);
        return rear ? behind : !behind && !front;
    }

    private string ActionName(uint actionId) =>
        dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().TryGetRow(actionId, out var row)
            ? row.Name.ExtractText()
            : $"#{actionId}";
}
