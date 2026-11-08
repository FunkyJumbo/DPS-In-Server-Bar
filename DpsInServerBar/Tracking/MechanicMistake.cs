namespace DpsInServerBar.Tracking;

/// <summary>
/// A failed mechanic: an enemy action put a penalty debuff (Vulnerability Up, Damage Down...) on a party member.
/// <see cref="AtSeconds"/> counts from the start of combat.
/// </summary>
public sealed record MechanicMistake(
    double AtSeconds, string Player, string Job, bool IsYou, string Status, string Ability, string Source);
