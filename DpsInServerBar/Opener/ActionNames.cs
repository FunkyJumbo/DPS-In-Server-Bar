using System;
using System.Collections.Generic;
using Dalamud.Game;
using Action = Lumina.Excel.Sheets.Action;

namespace DpsInServerBar.Opener;

/// <summary>English action names to ids and back, for matching openers written with action names.</summary>
public static class ActionNames
{
    private static Dictionary<string, uint>? byName;
    private static readonly Dictionary<uint, string> NameCache = new();

    /// <summary>English name of an action, normalized for comparison ("" when unknown).</summary>
    public static string Of(uint id)
    {
        if (NameCache.TryGetValue(id, out var name))
            return name;

        name = Plugin.DataManager.GetExcelSheet<Action>(ClientLanguage.English).TryGetRow(id, out var row)
            ? Normalize(row.Name.ExtractText())
            : string.Empty;
        NameCache[id] = name;
        return name;
    }

    /// <summary>Id of the player (PvE) action with this English name, 0 when none.</summary>
    public static uint Find(string englishName)
    {
        byName ??= Load();
        return byName.GetValueOrDefault(Normalize(englishName));
    }

    public static bool Is(uint id, string englishName) => Of(id) == Normalize(englishName);

    public static string Normalize(string name) => name.Replace('’', '\'').Trim().ToLowerInvariant();

    private static Dictionary<string, uint> Load()
    {
        // Several rows can share a name (PvP versions, Ten Chi Jin or NPC copies): prefer the player action (the
        // first, original row), then any PvE job action, then anything not PvP.
        var result = new Dictionary<string, uint>();
        var job = new Dictionary<string, uint>();
        var fallback = new Dictionary<string, uint>();
        foreach (var row in Plugin.DataManager.GetExcelSheet<Action>(ClientLanguage.English))
        {
            var name = Normalize(row.Name.ExtractText());
            if (name.Length == 0 || row.IsPvP)
                continue;

            if (row.IsPlayerAction || row.IsRoleAction)
                result.TryAdd(name, row.RowId);
            else if (row.ClassJobLevel > 0)
                job.TryAdd(name, row.RowId);
            else
                fallback.TryAdd(name, row.RowId);
        }

        foreach (var (name, id) in job)
            result.TryAdd(name, id);
        foreach (var (name, id) in fallback)
            result.TryAdd(name, id);
        return result;
    }
}
