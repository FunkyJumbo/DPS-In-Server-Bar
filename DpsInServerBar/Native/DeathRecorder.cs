using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace DpsInServerBar.Native;

/// <summary>What killed you: the hit that took your last HP, or a guess when there was none.</summary>
public sealed record DeathInfo(DateTime TimeUtc, string Ability, string Source, long Damage);

/// <summary>
/// Remembers the damage you took, your HP, height and debuffs, and turns them into a <see cref="DeathInfo"/> when you
/// die. Fed by the built-in tracker's hooks and framework update, on the game thread.
/// </summary>
public sealed class DeathRecorder
{
    // Hits that together took your last HP land within this time of your death.
    private static readonly TimeSpan LethalHitWindow = TimeSpan.FromSeconds(1.5);
    // A hit older than this when you die did not kill you.
    private static readonly TimeSpan HitWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(5);

    // A hit (or hits) doing less than this share of the HP you had did not kill you: something else did (arena edge,
    // fall, Doom), even if a boss auto-attack landed at the same time.
    private const double LethalShare = 0.9;

    // Falling off the arena: your height dropped by this much before dying, or keeps dropping after.
    private static readonly TimeSpan FallWindow = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan FallAfterDeathWindow = TimeSpan.FromSeconds(4);
    private const float FallHeight = 2f;

    // A debuff ending this close to your death (Doom and the like) is what killed you.
    private const float ExpiringDebuffSeconds = 1.5f;

    // ActionCategory 1 = auto-attack (the "attack"/"Shot" actions of players and enemies).
    private const uint AutoAttackCategory = 1;
    // Status.StatusCategory 2 = detrimental.
    private const byte DetrimentalCategory = 2;

    private const string Fall = "falling off the arena";
    private const string Instant = "instant death (arena edge?)";

    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly Queue<(DateTime TimeUtc, float Height)> heights = new();
    private readonly List<Hit> hits = [];
    private (uint StatusId, uint SourceId, float Remaining)[] lastDebuffs = [];
    private uint lastAliveHp;
    private Func<uint, string> nameOf = _ => string.Empty;
    private bool wasDead;

    // After a death without a lethal hit: watch whether your body keeps falling.
    private float deathHeight;
    private DateTime fallCheckUntilUtc;

    public DeathRecorder(IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;
    }

    /// <summary>Your last death, kept until the next one.</summary>
    public DeathInfo? LastDeath { get; private set; }

    /// <summary>Your last death if it happened in the given time range.</summary>
    public DeathInfo? DeathBetween(DateTime fromUtc, DateTime toUtc) =>
        LastDeath is { } death && death.TimeUtc >= fromUtc && death.TimeUtc <= toUtc ? death : null;

    /// <param name="actionType">1 = action (abilities of players and enemies alike).</param>
    public void OnHitTaken(uint actionType, uint actionId, string source, long damage) =>
        AddHit(new Hit(DateTime.UtcNow, ActionName(actionType, actionId), source, damage));

    public void OnDotTaken(uint statusId, string source, long damage) =>
        AddHit(new Hit(DateTime.UtcNow, $"{StatusName(statusId)} (DoT)", source, damage));

    /// <summary>
    /// Called every frame: records your HP, height and debuffs while alive, catches a death whose packet was missed and
    /// watches a body falling after death.
    /// </summary>
    public void Observe(IPlayerCharacter? player, Func<uint, string> resolveName)
    {
        nameOf = resolveName;
        if (player is null)
            return;

        var now = DateTime.UtcNow;
        var isDead = player.IsDead || (player.CurrentHp == 0 && player.MaxHp > 0);
        if (!isDead)
        {
            lastAliveHp = player.CurrentHp;
            heights.Enqueue((now, player.Position.Y));
            while (heights.Count > 0 && now - heights.Peek().TimeUtc > FallWindow)
                heights.Dequeue();

            lastDebuffs = player.StatusList
                .Where(s => s.StatusId != 0 && IsDetrimental(s.StatusId))
                .Select(s => (s.StatusId, s.SourceId, s.RemainingTime))
                .ToArray();
        }
        else if (now < fallCheckUntilUtc && deathHeight - player.Position.Y >= FallHeight
                 && LastDeath is { Ability: Instant } death)
        {
            LastDeath = death with { Ability = Fall };
            fallCheckUntilUtc = DateTime.MinValue;
            log.Information($"[DeathRecorder] body fell from {deathHeight:0.0} to {player.Position.Y:0.0} after death: {Fall}");
        }

        if (isDead && !wasDead)
            OnDeath();
        wasDead = isDead;
    }

    public void OnDeath()
    {
        var now = DateTime.UtcNow;
        // Seen twice (death packet + dead state): keep the first one.
        if (LastDeath is { } last && now - last.TimeUtc < DuplicateWindow)
            return;

        hits.RemoveAll(h => now - h.TimeUtc > HitWindow);
        var hp = Math.Max(lastAliveHp, 1u);
        var lethalNeeded = hp * LethalShare;

        // The hits that landed together right before you died and took your last HP (biggest one named) ...
        var final = hits.Where(h => now - h.TimeUtc <= LethalHitWindow).ToList();
        // ... or a single big hit a bit earlier (death applied late).
        var delayed = hits.LastOrDefault(h => h.Damage >= lethalNeeded);

        if (final.Count > 0 && final.Sum(h => h.Damage) >= lethalNeeded)
            LastDeath = final.MaxBy(h => h.Damage)!.ToDeath(now);
        else if (delayed is not null)
            LastDeath = delayed.ToDeath(now);
        else if (ExpiringDebuff() is { } debuff)
            LastDeath = new DeathInfo(now, $"{StatusName(debuff.StatusId)} (debuff)", nameOf(debuff.SourceId), 0);
        else if (FellBeforeDeath())
            LastDeath = new DeathInfo(now, Fall, string.Empty, 0);
        else
            LastDeath = new DeathInfo(now, Instant, string.Empty, 0);

        log.Information(
            $"[DeathRecorder] death: {LastDeath.Ability} | HP before {lastAliveHp:N0} | hits (last 10 s): "
            + (hits.Count == 0 ? "none" : string.Join(", ", hits.Select(h => $"{h.Ability} {h.Damage:N0} {(now - h.TimeUtc).TotalSeconds:0.0}s ago")))
            + $" | height {(heights.Count > 0 ? $"{heights.Max(s => s.Height):0.0} -> {heights.Last().Height:0.0}" : "?")}"
            + $" | debuffs: {(lastDebuffs.Length == 0 ? "none" : string.Join(", ", lastDebuffs.Select(d => $"{StatusName(d.StatusId)} {d.Remaining:0.0}s")))}");

        deathHeight = heights.Count > 0 ? heights.Last().Height : 0;
        fallCheckUntilUtc = heights.Count > 0 && LastDeath.Ability == Instant ? now + FallAfterDeathWindow : DateTime.MinValue;
        hits.Clear();
        heights.Clear();
        lastDebuffs = [];
    }

    private void AddHit(Hit hit)
    {
        hits.Add(hit);
        hits.RemoveAll(h => hit.TimeUtc - h.TimeUtc > HitWindow);
    }

    // Your height went down just before dying: you fell.
    private bool FellBeforeDeath() =>
        heights.Count > 1 && heights.Max(s => s.Height) - heights.Last().Height >= FallHeight;

    private (uint StatusId, uint SourceId, float Remaining)? ExpiringDebuff()
    {
        // Remaining 0 = permanent debuff, it did not run out.
        foreach (var debuff in lastDebuffs.OrderBy(d => d.Remaining))
        {
            if (debuff.Remaining > 0 && debuff.Remaining <= ExpiringDebuffSeconds)
                return debuff;
        }

        return null;
    }

    private bool IsDetrimental(uint statusId) =>
        dataManager.GetExcelSheet<Status>().TryGetRow(statusId, out var row) && row.StatusCategory == DetrimentalCategory;

    // Names in the client's language, like the game shows them.
    private string ActionName(uint actionType, uint actionId)
    {
        if (actionType != 1 || !dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().TryGetRow(actionId, out var row))
            return $"#{actionId}";
        if (row.ActionCategory.RowId == AutoAttackCategory)
            return "Auto-attack";
        return row.Name.ExtractText() is { Length: > 0 } name ? name : $"#{actionId}";
    }

    private string StatusName(uint statusId) =>
        dataManager.GetExcelSheet<Status>().TryGetRow(statusId, out var row) && row.Name.ExtractText() is { Length: > 0 } name
            ? name
            : "damage over time";

    private sealed record Hit(DateTime TimeUtc, string Ability, string Source, long Damage)
    {
        public DeathInfo ToDeath(DateTime now) => new(now, Ability, Source, Damage);
    }
}
