using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game;
using Dalamud.Interface.Textures;

namespace DpsInServerBar.UI;

/// <summary>Game icons of actions, and of job gauges (shown with the action that spends them). Draw thread only.</summary>
public static class ActionIcons
{
    // Each gauge shown with the icon of the action that spends it (English names, looked up in the English sheet).
    private static readonly Dictionary<string, string> GaugeActions = new()
    {
        ["Blood"] = "Bloodspiller",
        ["Beast Gauge"] = "Fell Cleave",
        ["Cartridges"] = "Burst Strike",
        ["Firstminds' Focus"] = "Wyrmwind Thrust",
        ["Ninki"] = "Bhavacakra",
        ["Kazematoi"] = "Aeolian Edge",
        ["Kenki"] = "Hissatsu: Shinten",
        ["Meditation"] = "Shoha",
        ["Soul"] = "Blood Stalk",
        ["Shroud"] = "Enshroud",
        ["Rattling Coils"] = "Uncoiled Fury",
        ["Serpent Offerings"] = "Reawaken",
        ["Heat"] = "Hypercharge",
        ["Battery"] = "Automaton Queen",
        ["White Mana"] = "Veraero",
        ["Black Mana"] = "Verthunder",
        ["Palette"] = "Subtractive Palette",
        ["Paint"] = "Holy in White",
        ["Fae Aether"] = "Aetherpact",
        ["Lilies"] = "Afflatus Solace",
        ["Blood Lily"] = "Afflatus Misery",
        ["Addersgall"] = "Druochole",
        ["Polyglot"] = "Xenoglossy",
    };

    private static readonly Dictionary<uint, uint> IconsById = new();
    private static readonly Dictionary<string, uint> IconsByName = new();

    /// <summary>Icon id of an action, 0 if unknown.</summary>
    public static uint OfAction(uint actionId)
    {
        if (!IconsById.TryGetValue(actionId, out var icon))
        {
            icon = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().TryGetRow(actionId, out var row) ? row.Icon : 0u;
            IconsById[actionId] = icon;
        }

        return icon;
    }

    /// <summary>Name of an action in the client language, "#id" if unknown.</summary>
    public static string NameOf(uint actionId) =>
        Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().TryGetRow(actionId, out var row) ? row.Name.ExtractText() : $"#{actionId}";

    /// <summary>Icon id for a gauge (the action spending it), 0 if unknown.</summary>
    public static uint OfGauge(string gauge)
    {
        if (!GaugeActions.TryGetValue(gauge, out var action))
            return 0;

        if (!IconsByName.TryGetValue(action, out var icon))
        {
            icon = 0;
            foreach (var row in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>(ClientLanguage.English))
            {
                if (row.IsPlayerAction && row.Name.ExtractText() == action)
                {
                    icon = row.Icon;
                    break;
                }
            }

            IconsByName[action] = icon;
        }

        return icon;
    }

    /// <summary>Draws an icon inline (a square of the given size, empty space if missing); returns whether it is hovered.</summary>
    public static bool Draw(uint icon, float size)
    {
        if (icon != 0 && Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(icon)).GetWrapOrDefault() is { } wrap)
            ImGui.Image(wrap.Handle, new Vector2(size, size));
        else
            ImGui.Dummy(new Vector2(size, size));
        return ImGui.IsItemHovered();
    }

    /// <summary>Draws an icon at a screen position without moving the cursor.</summary>
    public static void DrawAt(ImDrawListPtr draw, uint icon, Vector2 pos, float size, uint tint = 0xFFFFFFFF)
    {
        if (icon != 0 && Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(icon)).GetWrapOrDefault() is { } wrap)
            draw.AddImageRounded(wrap.Handle, pos, pos + new Vector2(size, size), Vector2.Zero, Vector2.One, tint, size * 0.15f);
    }
}
