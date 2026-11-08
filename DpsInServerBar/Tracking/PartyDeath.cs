using System.Collections.Generic;

namespace DpsInServerBar.Tracking;

/// <summary>One hit taken shortly before a death.</summary>
/// <param name="SecondsBefore">How long before the death it landed.</param>
public sealed record DeathHit(double SecondsBefore, string Ability, string Source, long Damage);

/// <summary>
/// A party member's death and what caused it. <see cref="AtSeconds"/> counts from the start of the pull.
/// </summary>
/// <param name="Cause">The ability that took the last HP, or a guess ("falling off the arena", "Doom (debuff)").</param>
/// <param name="Damage">Damage of that ability, 0 when there was no lethal hit.</param>
/// <param name="Recap">The last hits taken before the death, oldest first.</param>
public sealed record PartyDeath(
    double AtSeconds, string Player, string Job, bool IsYou, string Cause, string Source, long Damage, uint HpBefore,
    List<DeathHit> Recap);
