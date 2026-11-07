using System;
using System.Collections.Generic;
using System.Linq;

namespace DpsInServerBar.Opener;

public enum StepState
{
    Pending,
    Done,

    /// <summary>Skipped: the next GCD was used before it.</summary>
    Missed,
}

/// <summary>
/// One try of an opener. Actions must come in order; the oGCDs of a weave window (between two GCDs) can come in any
/// order. Using a GCD while oGCDs before it are still pending marks them missed. Any other action is a mistake.
/// </summary>
public sealed class OpenerRun
{
    /// <summary>A try of <paramref name="opener"/>, with or without its pre-pull steps and its tincture.</summary>
    public OpenerRun(OpenerDefinition opener, bool prepull, bool potion)
    {
        Opener = opener;
        Prepull = prepull;
        Potion = potion;
        Steps = opener.Steps.Where(s => (prepull || s.Prepull is null) && (potion || s.Kind != StepKind.Potion)).ToList();
        States = new StepState[Steps.Count];
        Times = new double?[Steps.Count];
    }

    public OpenerDefinition Opener { get; }
    public bool Prepull { get; }
    public bool Potion { get; }

    /// <summary>The steps practiced (the opener's, without the ones turned off).</summary>
    public IReadOnlyList<OpenerStep> Steps { get; }
    public StepState[] States { get; }

    /// <summary>Time of each step, in seconds from the pull (negative before it).</summary>
    public double?[] Times { get; }

    /// <summary>Moment of the pull (clock zero); null until started.</summary>
    public DateTime? PullUtc { get; private set; }

    public double? FinishedAt { get; private set; }

    /// <summary>When the first step was done (the first skill used), null before.</summary>
    public DateTime? FirstActionUtc { get; private set; }

    /// <summary>When the last step was done.</summary>
    public DateTime? FinishedUtc { get; private set; }
    public int Mistakes { get; private set; }
    public int Missed => States.Count(s => s == StepState.Missed);
    public bool Started => PullUtc is not null;
    public bool Finished => FinishedAt is not null;

    /// <summary>Last wrong action: (step expected, when, action type, action id).</summary>
    public (int Step, DateTime When, byte Type, uint Id)? LastWrong { get; private set; }

    public double Clock => PullUtc is { } pull ? (Finished ? FinishedAt!.Value : (DateTime.UtcNow - pull).TotalSeconds) : 0;

    /// <summary>First step still pending, or -1 when all are resolved.</summary>
    public int Next => Array.IndexOf(States, StepState.Pending);

    public void Start(double countdownSeconds) => PullUtc = DateTime.UtcNow.AddSeconds(countdownSeconds);

    /// <summary>Follows the game's countdown: the pull is when it reaches zero.</summary>
    public void SyncPull(double remainingSeconds) => PullUtc = DateTime.UtcNow.AddSeconds(remainingSeconds);

    public void OnAction(byte type, uint id)
    {
        if (Finished)
            return;

        var next = Next;
        if (next < 0)
            return;

        // Items only count when the tincture is part of the run (no mistake for a potion when it is turned off).
        if (type == 2 && !Steps.Any(s => s.Kind == StepKind.Potion))
            return;

        var name = type == 1 ? ActionNames.Of(id) : string.Empty;

        // Not started: the first step starts the run, as if pressed at its planned time.
        if (!Started)
        {
            if (!Matches(Steps[next], type, name))
                return;
            PullUtc = DateTime.UtcNow.AddSeconds(Steps[next].Prepull ?? 0);
        }

        // Candidates: the pending steps up to the next GCD (included).
        var end = next;
        while (end < States.Length && !Steps[end].IsGcd)
            end++;
        end = Math.Min(end, States.Length - 1);

        for (var i = next; i <= end; i++)
        {
            if (States[i] != StepState.Pending || !Matches(Steps[i], type, name))
                continue;

            if (Steps[i].IsGcd)
            {
                for (var j = next; j < i; j++)
                {
                    if (States[j] == StepState.Pending)
                        States[j] = StepState.Missed;
                }
            }

            States[i] = StepState.Done;
            FirstActionUtc ??= DateTime.UtcNow;
            Times[i] = (DateTime.UtcNow - PullUtc!.Value).TotalSeconds;
            LastWrong = null;
            if (Next < 0)
            {
                FinishedAt = Times[i];
                FinishedUtc = DateTime.UtcNow;
            }
            return;
        }

        // Actions typed for a step shown as a whole (Ninja mudras): never a mistake.
        if (Opener.Ignored?.Any(n => ActionNames.Normalize(n) == name) == true)
            return;

        Mistakes++;
        LastWrong = (next, DateTime.UtcNow, type, id);
    }

    private static bool Matches(OpenerStep step, byte type, string name) =>
        step.Kind == StepKind.Potion
            ? type == 2
            : type == 1 && step.Names.Any(n => ActionNames.Normalize(n) == name);
}
