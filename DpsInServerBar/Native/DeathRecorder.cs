using System;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace DpsInServerBar.Native;

/// <summary>What killed you: the last hit you took before dying.</summary>
public sealed record DeathInfo(DateTime TimeUtc, string Ability, string Source, long Damage);

/// <summary>
/// Remembers the last damage you took (ability, source, amount) and turns it into a <see cref="DeathInfo"/> when you die.
/// Fed by the built-in tracker's hooks, on the game thread.
/// </summary>
public sealed class DeathRecorder
{
    // A hit older than this when you die did not kill you (e.g. a death from a doom-like debuff).
    private static readonly TimeSpan HitWindow = TimeSpan.FromSeconds(10);

    private readonly IDataManager dataManager;
    private (DateTime TimeUtc, string Ability, string Source, long Damage)? lastHit;

    public DeathRecorder(IDataManager dataManager) => this.dataManager = dataManager;

    /// <summary>Your last death, kept until the next one.</summary>
    public DeathInfo? LastDeath { get; private set; }

    /// <summary>Your last death if it happened in the given time range.</summary>
    public DeathInfo? DeathBetween(DateTime fromUtc, DateTime toUtc) =>
        LastDeath is { } death && death.TimeUtc >= fromUtc && death.TimeUtc <= toUtc ? death : null;

    /// <param name="actionType">1 = action (abilities of players and enemies alike).</param>
    public void OnHitTaken(uint actionType, uint actionId, string source, long damage) =>
        lastHit = (DateTime.UtcNow, ActionName(actionType, actionId), source, damage);

    public void OnDotTaken(uint statusId, string source, long damage) =>
        lastHit = (DateTime.UtcNow, StatusName(statusId), source, damage);

    public void OnDeath()
    {
        var now = DateTime.UtcNow;
        LastDeath = lastHit is { } hit && now - hit.TimeUtc <= HitWindow
                        ? new DeathInfo(now, hit.Ability, hit.Source, hit.Damage)
                        : new DeathInfo(now, "unknown (no recent hit)", string.Empty, 0);
        lastHit = null;
    }

    // Names in the client's language, like the game shows them.
    private string ActionName(uint actionType, uint actionId) =>
        actionType == 1 && dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().TryGetRow(actionId, out var row)
        && row.Name.ExtractText() is { Length: > 0 } name
            ? name
            : $"#{actionId}";

    private string StatusName(uint statusId) =>
        dataManager.GetExcelSheet<Status>().TryGetRow(statusId, out var row) && row.Name.ExtractText() is { Length: > 0 } name
            ? $"{name} (DoT)"
            : "damage over time";
}
