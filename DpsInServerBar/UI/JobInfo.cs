using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game;
using Dalamud.Interface.Textures;
using DpsInServerBar.Combat;
using Lumina.Excel.Sheets;

namespace DpsInServerBar.UI;

/// <summary>Job icon and role color from an English job abbreviation (as sent by ACT, e.g. "DRK").</summary>
public static class JobInfo
{
    // Framed job icons: 62100 + ClassJob row.
    private const uint IconBase = 62100;

    private static Dictionary<string, uint>? jobs;

    /// <summary>Damage bar color of a job: by role, grey for the Limit Break and unknown jobs.</summary>
    public static Vector4 RoleColor(string abbreviation) =>
        Find(abbreviation) is null
            ? Theme.TextMuted
            : JobRoles.Of(abbreviation) switch
            {
                JobRole.Tank => Theme.Tank,
                JobRole.Healer => Theme.Healer,
                _ => Theme.Dps,
            };

    /// <summary>Draws the job icon as a square of the given size; an empty space when the job is unknown.</summary>
    public static void DrawIcon(string abbreviation, float size)
    {
        if (Find(abbreviation) is { } id
            && Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(IconBase + id)).GetWrapOrDefault() is { } wrap)
        {
            ImGui.Image(wrap.Handle, new Vector2(size, size));
        }
        else
        {
            ImGui.Dummy(new Vector2(size, size));
        }
    }

    /// <summary>Draws the job icon at a screen position without moving the cursor (to place it next to aligned text).</summary>
    public static void DrawIconAt(string abbreviation, Vector2 screenPos, float size)
    {
        if (Find(abbreviation) is { } id
            && Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(IconBase + id)).GetWrapOrDefault() is { } wrap)
        {
            ImGui.GetWindowDrawList().AddImage(wrap.Handle, screenPos, screenPos + new Vector2(size, size));
        }
    }

    private static uint? Find(string abbreviation)
    {
        jobs ??= Load();
        return jobs.TryGetValue(abbreviation, out var id) ? id : null;
    }

    private static Dictionary<string, uint> Load()
    {
        var result = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Plugin.DataManager.GetExcelSheet<ClassJob>(ClientLanguage.English))
        {
            var abbreviation = row.Abbreviation.ExtractText();
            if (abbreviation.Length == 0 || row.RowId == 0)
                continue;

            result.TryAdd(abbreviation, row.RowId);
        }

        return result;
    }
}
