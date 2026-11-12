using System;
using System.Collections.Generic;

namespace DpsInServerBar.UI;

/// <summary>
/// Player names as shown in the windows: the real ones, or made-up ones when "Hide player names" is on
/// (for screenshots). A name always gets the same stand-in, in every window and from one session to the next.
/// </summary>
public static class Names
{
    /// <summary>Set from the configuration (Settings > Display).</summary>
    public static bool Anonymize { get; set; }

    private static readonly string[] First =
    [
        "Aldric", "Brynn", "Cassia", "Dorian", "Elowen", "Faelan", "Gwen", "Halric", "Isolde", "Jorah",
        "Kaelis", "Liora", "Maren", "Nyx", "Orin", "Perrin", "Quill", "Rowan", "Sable", "Tamsin",
        "Ulric", "Vesper", "Wren", "Xara", "Yorick", "Zephyr", "Aria", "Bastian", "Cyra", "Dax",
    ];

    private static readonly string[] Last =
    [
        "Ashdown", "Brightwater", "Coldbrook", "Duskmere", "Emberfall", "Frostvale", "Glimmer", "Hollowell",
        "Ironwood", "Juniper", "Kestrel", "Larkspur", "Moonwhisper", "Nightbloom", "Oakheart", "Pinecrest",
        "Quicksilver", "Ravenshaw", "Starling", "Thornfield", "Umberlee", "Valewind", "Willowmere", "Yarrow",
    ];

    // Names that are not players: left as they are.
    private static readonly HashSet<string> Kept = new(StringComparer.OrdinalIgnoreCase) { "", "You", "YOU", "Limit Break" };

    private static readonly Dictionary<string, string> Assigned = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Used = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The name to display for this player.</summary>
    public static string Show(string name)
    {
        if (!Anonymize || Kept.Contains(name))
            return name;
        if (Assigned.TryGetValue(name, out var shown))
            return shown;

        // Stable hash (string.GetHashCode changes every session), then the next free name on a clash.
        var hash = 2166136261u;
        foreach (var ch in name.ToLowerInvariant())
            hash = (hash ^ ch) * 16777619u;
        var total = First.Length * Last.Length;
        for (var i = 0; i < total; i++)
        {
            var index = (int)((hash + (uint)i) % (uint)total);
            var candidate = $"{First[index % First.Length]} {Last[index / First.Length]}";
            if (Used.Add(candidate))
            {
                shown = candidate;
                break;
            }
        }

        shown ??= "Adventurer";
        Assigned[name] = shown;
        return shown;
    }

    /// <summary>A list of player names joined for display.</summary>
    public static string Join(IEnumerable<string> names) => string.Join(", ", System.Linq.Enumerable.Select(names, Show));
}
