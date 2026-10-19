namespace DpsInServerBar.Combat;

public enum CombatSourceKind
{
    /// <summary>ACT when it is reachable, the built-in tracker otherwise.</summary>
    Auto,
    Act,
    Native,
}

/// <summary>Something that produces <see cref="CombatSnapshot"/>s: ACT over WebSocket, or the built-in tracker.</summary>
public interface ICombatSource
{
    string Name { get; }

    /// <summary>True when the source is able to produce data right now.</summary>
    bool IsReady { get; }

    /// <summary>Short human readable status, for tooltips and the main window.</summary>
    string StatusText { get; }

    CombatSnapshot? Latest { get; }
}
