using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using DpsInServerBar.Combat;
using DpsInServerBar.Display;
using DpsInServerBar.Tracking;
using DpsInServerBar.UI;
using DpsInServerBar.Ultimates;
using Lumina.Excel.Sheets;

namespace DpsInServerBar.Windows;

public sealed class ConfigWindow : Window
{
    private readonly Plugin plugin;

    // Connection fields are edited locally and only applied on "Apply", to avoid reconnecting on every keystroke.
    private string hostInput;
    private bool editingCustomSeparator;
    private string fightFilter = string.Empty;
    private int renamingTheme = -1;
    private int renamingPreset = -1;
    private bool focusRename;
    private string renameBuffer = string.Empty;
    private int portInput;

    public ConfigWindow(Plugin plugin) : base(Loc.T("Raid Companion - Settings###DpsInServerBarConfig"))
    {
        Loc.Changed += () => WindowName = Loc.T("Raid Companion - Settings###DpsInServerBarConfig");
        this.plugin = plugin;
        AllowBackgroundBlur = false;
        hostInput = plugin.Configuration.Host;
        portInput = plugin.Configuration.Port;
        Size = new Vector2(860, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    /// <param name="Wip">Still being worked on: marked in the menu, with a warning on the page.</param>
    private sealed record Section(FontAwesomeIcon Icon, string Label, string Description, System.Action Draw, bool Wip = false);

    private Section[] Sections =>
    [
        new(FontAwesomeIcon.Server, Loc.T("DPS in server bar"), Loc.T("What the server bar shows, in which order, and how."), DrawServerBarTab),
        new(FontAwesomeIcon.CommentDots, Loc.T("Chat summary"), Loc.T("The recap printed in chat after each pull and each ultimate phase."), DrawChatSummaryTab),
        new(FontAwesomeIcon.Desktop, Loc.T("Display"), Loc.T("The compact meter and the main window."), DrawDisplayTab),
        new(FontAwesomeIcon.History, Loc.T("Pulls history"), Loc.T("Which pulls are recorded and kept."), DrawPullsTab),
        new(FontAwesomeIcon.Palette, Loc.T("Appearance"), Loc.T("Theme, colors and transparency of the plugin's windows."), DrawAppearanceTab),
        new(FontAwesomeIcon.Crosshairs, Loc.T("DPS check"), Loc.T("Boss HP and enrage of each fight: the party DPS needed to clear it."), DrawDpsCheckTab, Wip: true),
        new(FontAwesomeIcon.Plug, Loc.T("Data source"), Loc.T("Where the damage numbers come from: ACT or the built-in tracker."), DrawSourceTab),
        new(FontAwesomeIcon.ChartLine, Loc.T("FFLogs"), Loc.T("Estimate of your FFLogs percentile, from the FFLogs API."), DrawFflogsTab),
    ];

    private int selectedSection;
    private IDisposable? themeScope;

    // Height the window needs to show the whole page, from the last frame (pixels); null for a page that fills the window.
    private float? fitHeight;

    // Height of each page when it was last shown, to give it back when coming back.
    private readonly Dictionary<int, float> pageHeights = new();
    private int lastPage;
    private Vector2 windowSize;
    private float? restoreHeight;

    public override void PreDraw()
    {
        themeScope = Theme.Push();

        // Back to a page: the height it had when it was left.
        if (restoreHeight is { } restore)
        {
            Size = new Vector2(windowSize.X, restore) / ImGuiHelpers.GlobalScale;
            SizeCondition = ImGuiCond.Always;
            restoreHeight = null;
            fitHeight = null; // the limit of the page left does not apply
        }
        else if (SizeCondition == ImGuiCond.Always)
        {
            Size = null;
            SizeCondition = ImGuiCond.FirstUseEver;
        }

        // No taller than the page: no empty space at the bottom.
        var maxHeight = fitHeight is { } fit ? Math.Max(420, fit / ImGuiHelpers.GlobalScale) : float.MaxValue;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(680, 420), MaximumSize = new Vector2(float.MaxValue, maxHeight) };
    }

    public override void PostDraw()
    {
        themeScope?.Dispose();
        themeScope = null;
    }

    public override void Draw()
    {
        var sidebarWidth = Theme.Px(170);
        windowSize = ImGui.GetWindowSize();
        var contentTop = ImGui.GetCursorPosY();
        var navHeight = 0f;
        using (var nav = ImRaii.Child("##nav", new Vector2(sidebarWidth, -1)))
        {
            if (nav)
            {
                for (var i = 0; i < Sections.Length; i++)
                {
                    if (Theme.NavItem($"##nav{i}", Sections[i].Icon, Sections[i].Wip ? Loc.F("{0} (WIP)",Sections[i].Label) : Sections[i].Label, i == selectedSection))
                        selectedSection = i;
                }

                navHeight = ImGui.GetCursorPosY() + ImGui.GetTextLineHeight();

                // Version at the bottom of the menu.
                var version = $"v{Plugin.PluginInterface.Manifest.AssemblyVersion}";
                ImGui.SetCursorPosY(Math.Max(ImGui.GetCursorPosY(), ImGui.GetWindowHeight() - ImGui.GetTextLineHeightWithSpacing()));
                ImGui.SetCursorPosX(Theme.Px(12));
                ImGui.TextColored(Theme.TextFaint, version);
            }
        }

        // Thin vertical rule between the menu and the page.
        ImGui.SameLine();
        var top = ImGui.GetCursorScreenPos();
        var height = ImGui.GetContentRegionAvail().Y;
        ImGui.GetWindowDrawList().AddLine(top, top with { Y = top.Y + height }, Theme.U32(Theme.Line));
        ImGui.Dummy(new Vector2(Theme.Px(6), 0));
        ImGui.SameLine();

        var section = Sections[selectedSection];
        using var page = ImRaii.Child("##page", new Vector2(-1, -1));
        if (!page)
            return;

        Theme.Icon(section.Icon, Theme.Gold);
        ImGui.SameLine();
        ImGui.TextColored(Theme.GoldBright, section.Label);
        if (section.Wip)
        {
            ImGui.SameLine();
            Theme.Badge(Loc.T("WIP"), Theme.Warn);
        }

        Theme.Hint(section.Description);
        if (section.Wip)
        {
            ImGui.Spacing();
            Theme.Icon(FontAwesomeIcon.ExclamationTriangle, Theme.Warn);
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, Theme.Warn))
                ImGui.TextWrapped(Loc.T("This section is still a work in progress: the current values may be incomplete, or even wrong, for the time being."));
        }

        Theme.Rule();

        // Pages opening on a group title: the same gap above it as above the next titles (the rule pads 4px itself).
        if (section.Draw != (System.Action)DrawDpsCheckTab)
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() - Theme.Px(4));

        // A little room at the end of each group of settings (before the next title, and after the last one).
        Theme.SectionSpacing = Theme.Px(12);
        Theme.SectionTitleGap = Theme.Px(8);
        try
        {
            section.Draw();
        }
        finally
        {
            Theme.SectionSpacing = 0;
            Theme.SectionTitleGap = null;
        }

        ImGui.Dummy(new Vector2(0, Theme.Px(12)));

        // The DPS check page lists its fights in a box filling the window: no limit there.
        fitHeight = section.Draw == (System.Action)DrawDpsCheckTab
                        ? null
                        : contentTop + Math.Max(navHeight, ImGui.GetCursorPosY() - ImGui.GetStyle().ItemSpacing.Y) + ImGui.GetStyle().WindowPadding.Y;

        // Another page now: remember this one's height, and give the new one its own.
        if (selectedSection != lastPage)
        {
            pageHeights[lastPage] = windowSize.Y;
            if (pageHeights.TryGetValue(selectedSection, out var savedHeight))
                restoreHeight = savedHeight;
            lastPage = selectedSection;
        }
    }

    private void DrawServerBarTab()
    {
        var c = plugin.Configuration;
        var changed = false;

        DrawBarPresets(c);

        Theme.SectionHeader(Loc.T("Value"));
        var metric = (int)c.Metric;
        ImGui.SetNextItemWidth(Theme.Px(240));
        if (ImGui.Combo(Loc.T("Metric"), ref metric, Loc.T("Personal DPS\0Party DPS (pDPS)\0Raid-contributed DPS (rDPS)\0")))
        {
            c.Metric = (DisplayMetric)metric;
            changed = true;
        }

        var style = (int)c.NumberStyle;
        ImGui.SetNextItemWidth(Theme.Px(240));
        if (ImGui.Combo(Loc.T("Number format"), ref style, "12345\012,345\012.3k\0"))
        {
            c.NumberStyle = (NumberStyle)style;
            changed = true;
        }

        Theme.SectionHeader(Loc.T("Server bar elements"));
        Theme.Hint(Loc.T("Tick what to show and use the arrows to change the order."));
        changed |= DrawBarLayout(c);
        changed |= DrawSeparator(c);
        ImGui.Dummy(new Vector2(0, Theme.Px(3)));
        DrawBarPreviewButton();

        Theme.SectionHeader(Loc.T("Behavior"));
        changed |= Checkbox(Loc.T("Hide while no data source is available"), c.HideWhenDisconnected, v => c.HideWhenDisconnected = v);

        var hide = c.HideAfterSeconds;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt(Loc.T("Hide N seconds after combat (0 = never)"), ref hide))
        {
            c.HideAfterSeconds = Math.Clamp(hide, 0, 3600);
            changed = true;
        }

        Theme.SectionHeader(Loc.T("Colors"));
        changed |= Checkbox(Loc.T("Use colors"), c.UseColors, v => c.UseColors = v);
        using (ImRaii.Disabled(!c.UseColors))
        {
            changed |= ColorEdit(Loc.T("Final value"), c.FinalColor, v => c.FinalColor = v);
            changed |= ColorEdit(Loc.T("pDPS below check"), c.BelowThresholdColor, v => c.BelowThresholdColor = v);
            changed |= ColorEdit(Loc.T("pDPS above check"), c.AboveThresholdColor, v => c.AboveThresholdColor = v);
        }

        if (changed)
            plugin.OnConfigChanged();
    }

    private void DrawDisplayTab()
    {
        var c = plugin.Configuration;
        var changed = false;

        Theme.SectionHeader(Loc.T("Language"));
        var languages = Loc.Languages.Select(l => l.Code == "auto" ? Loc.T(l.Name) : l.Name).ToArray();
        var language = Math.Max(0, Array.FindIndex(Loc.Languages, l => l.Code == c.Language));
        ImGui.SetNextItemWidth(Theme.Px(260));
        if (ImGui.Combo(Loc.T("Language of the plugin"), ref language, languages, languages.Length))
        {
            c.Language = Loc.Languages[language].Code;
            changed = true;
        }
        Help(Loc.T("Auto follows the language chosen in Dalamud's settings. Game data (actions, bosses, debuffs) stays in the game client's language."));

        Theme.SectionHeader(Loc.T("Compact meter"));
        Theme.Hint(Loc.T("The small pop-up meter (/raidcompanion compact): player and DPS / rDPS, then the columns ticked here."));
        foreach (var (column, label) in new[]
                 {
                     (CompactColumn.Share, Loc.T("% of the party")), (CompactColumn.Crit, Loc.T("Crit rate")),
                     (CompactColumn.DirectHit, Loc.T("Direct hit rate")), (CompactColumn.Deaths, Loc.T("Deaths")),
                 })
        {
            var on = c.CompactColumns.HasFlag(column);
            if (ImGui.Checkbox(label, ref on))
            {
                c.CompactColumns = on ? c.CompactColumns | column : c.CompactColumns & ~column;
                changed = true;
            }
        }

        changed |= Checkbox(Loc.T("Show the header"), c.CompactShowHeader, v => c.CompactShowHeader = v);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("Without it, switch DPS / rDPS from the right click menu."));

        var compactPercent = (int)Math.Round(c.CompactOpacity * 100);
        ImGui.SetNextItemWidth(Theme.Px(240));
        if (ImGui.SliderInt(Loc.T("Background opacity##compact"), ref compactPercent, 0, 100, "%d%%"))
        {
            c.CompactOpacity = compactPercent / 100f;
            changed = true;
        }

        if (Theme.GhostButton(plugin.CompactOpen ? Loc.T("Close the compact meter") : Loc.T("Open the compact meter")))
            plugin.SetCompact(!plugin.CompactOpen);

        Theme.SectionHeader(Loc.T("Main window"));
        changed |= Checkbox(Loc.T("Show DPS graphs in the main window"), c.ShowGraph, v => c.ShowGraph = v);
        changed |= Checkbox(Loc.T("Include Limit Break in party DPS"), c.IncludeLimitBreakInParty, v => c.IncludeLimitBreakInParty = v);

        Theme.SectionHeader(Loc.T("Screenshots"));
        changed |= Checkbox(Loc.T("Hide player names"), c.HideNames, v => { c.HideNames = v; Names.Anonymize = v; });
        Help(Loc.T("Every player name in the windows (you included) is replaced by a made-up one, always the same for a given player: handy for screenshots. Recorded pulls keep the real names."));

        if (changed)
            plugin.OnConfigChanged();
    }

    /// <summary>The saved server bar setups: apply, rename, delete, and the command that switches to each.</summary>
    private void DrawBarPresets(Configuration c)
    {
        var changed = false;
        var presets = c.BarPresets;

        Theme.SectionHeader(Loc.T("Presets"));
        var current = c.CurrentBarPreset;
        var modified = current is not null && !current.Matches(c);
        Theme.Hint(current is null
                       ? Loc.T("Save the setup below (value, format, elements and order) to switch to it with a command or a macro.")
                       : Loc.T("Selecting a preset loads its settings below. Change them, then save them into the preset."));

        int? toDelete = null;
        for (var i = 0; i < presets.Count; i++)
        {
            var preset = presets[i];
            using var id = ImRaii.PushId($"preset{i}");

            if (renamingPreset == i)
            {
                ImGui.SetNextItemWidth(Theme.Px(220));
                if (focusRename)
                {
                    ImGui.SetKeyboardFocusHere();
                    focusRename = false;
                }

                var done = ImGui.InputText("##rename", ref renameBuffer, 64, ImGuiInputTextFlags.EnterReturnsTrue);
                if (done || ImGui.IsItemDeactivated())
                {
                    if (renameBuffer.Trim().Length > 0)
                        preset.Name = renameBuffer.Trim();
                    renamingPreset = -1;
                    changed = true;
                }
            }
            else
            {
                var selected = c.ActiveBarPreset == i;
                if (ImGui.RadioButton(preset.Name, selected) && !selected)
                    plugin.ApplyBarPreset(i);

                if (ImGui.IsItemHovered())
                {
                    var shown = preset.Layout.Where(e => e.Enabled).Select(e => Describe(e.Element).Label).ToList();
                    var metric = preset.Metric switch
                    {
                        DisplayMetric.Party => Loc.T("Party DPS"),
                        DisplayMetric.Raid => Loc.T("rDPS"),
                        _ => Loc.T("Personal DPS"),
                    };
                    ImGui.SetTooltip($"{metric}\n{(shown.Count > 0 ? string.Join("\n", shown) : Loc.T("(nothing shown)"))}");
                }

                if (selected && modified)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(Theme.Warn, Loc.T("(modified)"));
                }

                ImGui.SameLine();
                if (Theme.IconButton("rename", FontAwesomeIcon.Pen, Loc.T("Rename")))
                {
                    renamingPreset = i;
                    renameBuffer = preset.Name;
                    focusRename = true;
                }
            }

            ImGui.SameLine();
            if (Theme.IconButton("delete", FontAwesomeIcon.TrashAlt, Loc.T("Delete this preset (hold Ctrl)"), Theme.TextFaint)
                && ImGui.GetIO().KeyCtrl)
            {
                toDelete = i;
            }

            ImGui.SameLine();
            var command = $"/raidcompanion preset {i + 1}";
            ImGui.TextDisabled(command);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.T("Click to copy the command, for a macro."));
            if (ImGui.IsItemClicked())
                ImGui.SetClipboardText(command);
        }

        if (toDelete is { } index)
        {
            presets.RemoveAt(index);
            if (c.ActiveBarPreset == index)
                c.ActiveBarPreset = -1;
            else if (c.ActiveBarPreset > index)
                c.ActiveBarPreset--;
            renamingPreset = -1;
            changed = true;
        }

        if (current is not null)
        {
            using (ImRaii.Disabled(!modified))
            {
                if (Theme.PrimaryButton(Loc.F("Save changes to \"{0}\"",current.Name)))
                {
                    current.CaptureFrom(c);
                    changed = true;
                }

                ImGui.SameLine();
                if (Theme.GhostButton(Loc.T("Revert")))
                    plugin.ApplyBarPreset(c.ActiveBarPreset);
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(Loc.T("Reloads the settings saved in the preset."));

            ImGui.SameLine();
        }

        if (Theme.GhostButton(Loc.T("Save as a new preset")))
        {
            var preset = new BarPreset { Name = NewPresetName(c) };
            preset.CaptureFrom(c);
            presets.Add(preset);
            c.ActiveBarPreset = presets.Count - 1;
            changed = true;
        }

        if (presets.Count > 0)
            Help(Loc.T("In game or in a macro: /raidcompanion preset 2 (by number), /raidcompanion preset Raid (by name) or /raidcompanion preset next (the next one)."));

        if (changed)
            plugin.OnConfigChanged();
    }

    // "Preset 1", "Preset 2"... the first number not taken.
    private static string NewPresetName(Configuration c)
    {
        for (var n = 1; ; n++)
        {
            var name = Loc.F("Preset {0}",n);
            if (c.BarPresets.All(p => !p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return name;
        }
    }

    private void DrawAppearanceTab()
    {
        var c = plugin.Configuration;
        var changed = false;
        var savedSelected = c.SavedThemeIndex >= 0 && c.SavedThemeIndex < c.SavedThemes.Count;

        Theme.SectionHeader(Loc.T("Theme"));
        foreach (var preset in Enum.GetValues<ThemePreset>())
        {
            var colors = preset == ThemePreset.Custom ? c.CustomTheme : ThemePalette.For(preset);
            DrawSwatches(colors);
            ImGui.SameLine();
            var selected = !savedSelected && c.ThemePreset == preset;
            if (ImGui.RadioButton(ThemePalette.Name(preset), selected) && !selected)
            {
                c.ThemePreset = preset;
                c.SavedThemeIndex = -1;
                changed = true;
            }
        }

        changed |= DrawSavedThemes();

        Theme.SectionHeader(Loc.T("Background"));
        var percent = (int)Math.Round(c.BackgroundOpacity * 100);
        ImGui.SetNextItemWidth(Theme.Px(240));
        if (ImGui.SliderInt(Loc.T("Background opacity"), ref percent, 20, 100, "%d%%"))
        {
            c.BackgroundOpacity = percent / 100f;
            changed = true;
        }

        Help(Loc.T("How much of the game shows through the plugin's windows. Text and fields stay opaque."));

        // The colors edited: those of the saved theme in use, otherwise the custom theme.
        savedSelected = c.SavedThemeIndex >= 0 && c.SavedThemeIndex < c.SavedThemes.Count;
        var edited = savedSelected ? c.SavedThemes[c.SavedThemeIndex].Palette : c.CustomTheme;
        Theme.SectionHeader(savedSelected ? Loc.F("Colors of \"{0}\"",c.SavedThemes[c.SavedThemeIndex].Name) : Loc.T("Custom colors"));
        Theme.Hint(savedSelected
                       ? Loc.T("Changes are saved in this theme right away.")
                       : Loc.T("Pick your own colors. Changing one switches to the Custom theme; hover, border and muted text shades are derived from them."));

        using (ImRaii.Disabled(savedSelected || c.ThemePreset == ThemePreset.Custom))
        {
            if (Theme.GhostButton(Loc.T("Start from the selected theme")))
            {
                c.CustomTheme = ThemePalette.For(c.ThemePreset).Copy();
                c.ThemePreset = ThemePreset.Custom;
                changed = true;
            }
        }

        ImGui.SameLine();
        if (Theme.GhostButton(Loc.T("Save as a new theme")))
        {
            c.SavedThemes.Add(new SavedTheme { Name = NewThemeName(c), Palette = c.ActivePalette.Copy() });
            c.SavedThemeIndex = c.SavedThemes.Count - 1;
            changed = true;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("Saves the colors in use as a theme of its own, which you can rename."));

        if (!savedSelected)
        {
            ImGui.SameLine();
            if (Theme.GhostButton(Loc.T("Reset custom colors")))
            {
                c.CustomTheme = ThemePalette.For(ThemePreset.Midnight);
                changed = true;
            }
        }

        using (var table = ImRaii.Table("##customcolors", 2, ImGuiTableFlags.SizingStretchSame))
        {
            if (table)
            {
                foreach (var (label, get, set) in edited.Entries())
                {
                    ImGui.TableNextColumn();
                    var color = get();
                    if (ImGui.ColorEdit4(label, ref color, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoAlpha))
                    {
                        set(color);
                        if (!savedSelected)
                            c.ThemePreset = ThemePreset.Custom;
                        changed = true;
                    }
                }
            }
        }

        Theme.SectionHeader(Loc.T("Preview"));
        var width = Theme.CardWidth(3);
        Theme.StatCard(Loc.T("DPS"), "21,234", Theme.Gold, width, Loc.T("#2 of 8"));
        ImGui.SameLine();
        Theme.StatCard(Loc.T("Party DPS"), "182,345", Theme.Good, width, Loc.T("need 176,600 (+3.3%)"), Theme.Good);
        ImGui.SameLine();
        Theme.StatCard(Loc.T("GCD uptime"), "98.7%", Theme.Crystal, width, Loc.T("lost 1.2s"));
        Theme.Badge(Loc.T("Kill"), Theme.Good);
        ImGui.SameLine();
        Theme.Badge(Loc.T("Wipe"), Theme.Bad);
        ImGui.SameLine();
        Theme.Chip(Loc.T("Savage 12"), true);
        ImGui.SameLine();
        Theme.Chip(Loc.T("Dungeon 4"), false);
        ImGui.SameLine();
        Theme.PrimaryButton(Loc.T("Main action"));
        ImGui.SameLine();
        Theme.GhostButton(Loc.T("Other action"));

        if (changed)
            plugin.OnConfigChanged();
    }

    /// <summary>The saved themes, each selectable, renamable and deletable.</summary>
    private bool DrawSavedThemes()
    {
        var c = plugin.Configuration;
        var changed = false;
        if (c.SavedThemes.Count == 0)
            return false;

        Theme.SectionHeader(Loc.T("Saved themes"));
        int? toDelete = null;
        for (var i = 0; i < c.SavedThemes.Count; i++)
        {
            var saved = c.SavedThemes[i];
            using var id = ImRaii.PushId(i);
            DrawSwatches(saved.Palette);
            ImGui.SameLine();

            if (renamingTheme == i)
            {
                // Enter or leaving the field validates; an empty name keeps the old one.
                ImGui.SetNextItemWidth(Theme.Px(220));
                if (focusRename)
                {
                    ImGui.SetKeyboardFocusHere();
                    focusRename = false;
                }

                var done = ImGui.InputText("##rename", ref renameBuffer, 64, ImGuiInputTextFlags.EnterReturnsTrue);
                if (done || ImGui.IsItemDeactivated())
                {
                    if (renameBuffer.Trim().Length > 0)
                        saved.Name = renameBuffer.Trim();
                    renamingTheme = -1;
                    changed = true;
                }
            }
            else
            {
                var selected = c.SavedThemeIndex == i;
                if (ImGui.RadioButton(saved.Name, selected) && !selected)
                {
                    c.SavedThemeIndex = i;
                    changed = true;
                }

                ImGui.SameLine();
                if (Theme.IconButton("rename", FontAwesomeIcon.Pen, Loc.T("Rename")))
                {
                    renamingTheme = i;
                    renameBuffer = saved.Name;
                    focusRename = true;
                }
            }

            ImGui.SameLine();
            if (Theme.IconButton("delete", FontAwesomeIcon.TrashAlt, Loc.T("Delete this theme (hold Ctrl)"), Theme.TextFaint)
                && ImGui.GetIO().KeyCtrl)
            {
                toDelete = i;
            }
        }

        if (toDelete is { } index)
        {
            c.SavedThemes.RemoveAt(index);
            if (c.SavedThemeIndex == index)
                c.SavedThemeIndex = -1;
            else if (c.SavedThemeIndex > index)
                c.SavedThemeIndex--;
            renamingTheme = -1;
            changed = true;
        }

        return changed;
    }

    // "Custom theme 1", "Custom theme 2"... the first number not taken.
    private static string NewThemeName(Configuration c)
    {
        for (var n = 1; ; n++)
        {
            var name = Loc.F("Custom theme {0}",n);
            if (c.SavedThemes.All(t => !t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return name;
        }
    }

    /// <summary>Small squares showing a theme's background, surface and accents.</summary>
    private static void DrawSwatches(ThemePalette colors)
    {
        var size = ImGui.GetFrameHeight();
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var square = size * 0.7f;
        var y = pos.Y + (size - square) / 2;
        Vector4[] shown = [colors.Background, colors.Surface, colors.Accent, colors.Secondary];
        for (var i = 0; i < shown.Length; i++)
        {
            var x = pos.X + i * (square + Theme.Px(3));
            dl.AddRectFilled(new Vector2(x, y), new Vector2(x + square, y + square), ImGui.GetColorU32(shown[i]), Theme.Px(3));
            dl.AddRect(new Vector2(x, y), new Vector2(x + square, y + square), Theme.U32(Theme.Line), Theme.Px(3));
        }

        ImGui.Dummy(new Vector2(shown.Length * (square + Theme.Px(3)), size));
    }

    private static (SummaryElement Element, string Label)[] SummaryLabels =>
    [
        (SummaryElement.Job, Loc.T("Job")),
        (SummaryElement.Dps, Loc.T("DPS")),
        (SummaryElement.Rdps, Loc.T("rDPS")),
        (SummaryElement.Percentile, Loc.T("FFLogs %")),
        (SummaryElement.Uptime, Loc.T("GCD")),
        (SummaryElement.PartyDps, Loc.T("pDPS")),
        (SummaryElement.Deaths, Loc.T("Deaths")),
        (SummaryElement.KilledBy, Loc.T("Killed by")),
        (SummaryElement.Mistakes, Loc.T("Mechanics")),
    ];

    private static (string Label, string Value)[] SeparatorPresets =>
    [
        (Loc.T("Space"), " "),
        ("|", " | "),
        ("/", " / "),
        ("-", " - "),
        (Loc.T("Double space"), "  "),
    ];

    private bool DrawSeparator(Configuration c)
    {
        var changed = false;
        var preset = Array.FindIndex(SeparatorPresets, p => p.Value == c.BarSeparator);
        var labels = SeparatorPresets.Select(p => p.Label).Append(Loc.T("Custom")).ToArray();
        var custom = labels.Length - 1;
        var selected = editingCustomSeparator || preset < 0 ? custom : preset;

        ImGui.SetNextItemWidth(130);
        if (ImGui.Combo(Loc.T("Separator between elements"), ref selected, labels, labels.Length))
        {
            // "Custom" keeps the current text as a starting point.
            editingCustomSeparator = selected == custom;
            if (selected < SeparatorPresets.Length)
            {
                c.BarSeparator = SeparatorPresets[selected].Value;
                changed = true;
            }
        }

        if (selected == custom)
        {
            var text = c.BarSeparator;
            ImGui.SetNextItemWidth(130);
            if (ImGui.InputText(Loc.T("Custom separator"), ref text, 16))
            {
                c.BarSeparator = text;
                changed = true;
            }

            Help(Loc.T("Spaces count: \" | \" puts a space on each side. Some characters may not exist in the game font."));
        }

        return changed;
    }

    private void DrawBarPreviewButton()
    {
        var left = plugin.Bar.PreviewLeft;
        if (left > TimeSpan.Zero)
        {
            if (Theme.GhostButton(Loc.F("Stop the preview ({0:0}s)",Math.Ceiling(left.TotalSeconds))))
                plugin.Bar.StopPreview();
        }
        else if (Theme.PrimaryButton(Loc.T("Preview the server bar")))
        {
            plugin.Bar.StartPreview();
        }

        Help(Loc.T("Shows made-up values in the server bar for 10 seconds, for every element ticked above, in their order. Changes made meanwhile show up right away."));
    }

    private static bool DrawBarLayout(Configuration c)
    {
        var changed = false;
        var layout = c.BarLayout;

        using var table = ImRaii.Table("##barlayout", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit);
        if (!table)
            return false;

        ImGui.TableSetupColumn("##order", ImGuiTableColumnFlags.WidthFixed, 56);
        ImGui.TableSetupColumn("##element", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("##example", ImGuiTableColumnFlags.WidthFixed, Theme.Px(170));

        for (var i = 0; i < layout.Count; i++)
        {
            var item = layout[i];
            using var id = ImRaii.PushId(i);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            using (ImRaii.Disabled(i == 0))
            {
                if (ImGui.ArrowButton("##up", ImGuiDir.Up))
                {
                    (layout[i - 1], layout[i]) = (layout[i], layout[i - 1]);
                    changed = true;
                }
            }

            ImGui.SameLine(0, 2);
            using (ImRaii.Disabled(i == layout.Count - 1))
            {
                if (ImGui.ArrowButton("##down", ImGuiDir.Down))
                {
                    (layout[i + 1], layout[i]) = (layout[i], layout[i + 1]);
                    changed = true;
                }
            }

            ImGui.TableNextColumn();
            var (label, example, help) = Describe(item.Element);
            var enabled = item.Enabled;
            if (ImGui.Checkbox(label, ref enabled))
            {
                item.Enabled = enabled;
                changed = true;
            }

            if (help is not null)
                Help(help);

            ImGui.TableNextColumn();
            ImGui.TextDisabled(example);
        }

        return changed;
    }

    private static (string Label, string Example, string? Help) Describe(BarElement element) => element switch
    {
        BarElement.Rank => (Loc.T("Rank in the party"), "#2", Loc.T("Personal DPS and rDPS only, when in a party.")),
        BarElement.RoleGap => (Loc.T("Rank in your role and gap"), "#1 (+8%)",
                               Loc.T("Compared with players of your role only (tanks, healers or DPS): with the first one, or with the second one when you are first. Uses rDPS when the metric is rDPS.")),
        BarElement.Value => (Loc.T("Value"), Loc.T("21,234 DPS"), null),
        BarElement.Pace => (Loc.T("Pace vs your best pull"), Loc.T("Best +4%"),
                            Loc.T("Damage done so far compared with your best pull of the same encounter, at the same second.")),
        BarElement.Percentile => (Loc.T("FFLogs percentile (estimate)"), "~92",
                                  Loc.T("Your rDPS placed among FFLogs' rankings for your job on this fight. Needs FFLogs API credentials (FFLogs tab).")),
        BarElement.Uptime => (Loc.T("GCD uptime"), Loc.T("GCD 98.7%"),
                              Loc.T("Share of the time an enemy was targetable during which your GCD was rolling or you were casting. Deaths count as lost time. In an ultimate, uptime of the current phase.")),
        BarElement.PhaseDps => (Loc.T("Ultimate phase boss DPS"), Loc.T("Boss 145,000 (+3.6%)"),
                                Loc.T("In an ultimate, DPS on the bosses of the current phase only, and its gap to the DPS needed for the phase enrage (DPS check tab). Green / red against that requirement.")),
        BarElement.Phase => (Loc.T("Ultimate phase"), "P1", Loc.T("Current phase number, in the supported ultimates (DPS check tab).")),
        BarElement.EnrageTimer => (Loc.T("Enrage timer"), Loc.T("Enrage 3:12"),
                                   Loc.T("Time left before the enrage entered for this fight in the DPS check tab. Red for the last 30 seconds.")),
        BarElement.DotUptime => (Loc.T("DoT uptime"), Loc.T("DoT 97.1%"),
                                 Loc.T("Jobs with a damage over time only: share of the time each of your DoTs was on the boss while it could be attacked. Several DoTs are shown one after the other.")),
        BarElement.Duration => (Loc.T("Fight duration"), "3:42", Loc.T("Time since the pull. Stays on the final time once the fight is over.")),
        _ => (element.ToString(), string.Empty, null),
    };

    private void DrawDpsCheckTab()
    {
        var changed = false;

        Theme.Hint(Loc.T("Min pDPS = boss HP / enrage time, or the value typed by hand when one of them is missing. It colors the pDPS value and the enrage timer."));

        var boss = plugin.Boss.Current;
        var territory = plugin.CurrentTerritory;
        ImGui.TextDisabled(boss is not null
                               ? Loc.F("Boss detected: {0} - {1:N0} HP",boss.Name,boss.MaxHp)
                               : Loc.T("Boss HP is read from the game during a fight (enemy with the most HP)."));
        ImGui.TextDisabled(Loc.F("Current zone: {0} ({1})",ZoneName(territory),territory));

        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##fightfilter", Loc.T("Search a fight"), ref fightFilter, 64);
        ImGui.Spacing();

        using (var child = ImRaii.Child("##dpschecks", new Vector2(-1, -1)))
        {
            if (child)
            {
                changed |= DrawFightSection(FightCategory.Ultimate, Loc.T("Ultimate"), boss);
                changed |= DrawFightSection(FightCategory.Savage, Loc.T("Savage"), boss);
                changed |= DrawFightSection(FightCategory.Extreme, Loc.T("Extreme"), boss);
                changed |= DrawCustomChecks(boss);
            }
        }

        if (changed)
            plugin.OnConfigChanged();
    }

    /// <summary>One row per fight of the kind, most recent first. A check is only saved once something is entered.</summary>
    private bool DrawFightSection(FightCategory category, string label, BossInfo? boss)
    {
        var c = plugin.Configuration;
        var changed = false;
        var fights = plugin.Fights.Of(category)
                           .Where(f => fightFilter.Length == 0 || f.Name.Contains(fightFilter, StringComparison.OrdinalIgnoreCase))
                           .ToList();

        var flags = category == FightCategory.Ultimate || fightFilter.Length > 0 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
        if (!ImGui.CollapsingHeader($"{label} ({fights.Count})###section{category}", flags))
            return false;

        if (category == FightCategory.Ultimate)
        {
            changed |= Checkbox(Loc.T("Track ultimate phases"), c.UltimateMode, v => c.UltimateMode = v);
            Help(Loc.T("In an ultimate the fight never stops between phases. Each supported phase gets its own timer, starting when its boss becomes targetable, and only its own bosses count for the DPS check (adds are ignored). Enrage timer and Ultimate phase boss DPS then follow the current phase. Each phase's enrage is measured from the phase start."));
        }

        // Then one group per expansion, the most recent first (and open).
        var first = true;
        foreach (var expansion in fights.GroupBy(f => f.ExpansionId).OrderByDescending(g => g.Key))
        {
            var open = first || fightFilter.Length > 0;
            first = false;
            var nodeFlags = open ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
            using var node = ImRaii.TreeNode($"{expansion.First().Expansion} ({expansion.Count()})###exp{category}{expansion.Key}", nodeFlags);
            if (!node)
                continue;

            // Ultimates: one more level, each ultimate with its phases.
            if (category == FightCategory.Ultimate)
            {
                foreach (var fight in expansion)
                    changed |= DrawUltimateFight(fight, boss);
            }
            else
            {
                changed |= DrawFightTable(category, $"{category}{expansion.Key}", expansion.ToList(), boss);
            }
        }

        return changed;
    }

    private bool DrawFightTable(FightCategory category, string id, List<FightEntry> fights, BossInfo? boss)
    {
        var c = plugin.Configuration;
        var changed = false;
        // The names are never cut: their column is as wide as the longest one, and the table scrolls when too narrow.
        var nameWidth = NameColumnWidth(Loc.T("Fight"), fights.Select(f => f.Name));
        var layout = CheckTable(fights.Count + (category == FightCategory.Savage ? fights.Count / 4 : 0), nameWidth + Theme.Px(CheckColumnsWidth));
        using var table = ImRaii.Table($"##fights{id}", 5, layout.Flags, layout.Size, layout.InnerWidth);
        if (!table)
            return false;

        ImGui.TableSetupColumn(Loc.T("Fight"), ImGuiTableColumnFlags.WidthFixed, nameWidth);
        ImGui.TableSetupColumn(Loc.T("Boss HP"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(140));
        ImGui.TableSetupColumn(Loc.T("Enrage (m:s)"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(90));
        ImGui.TableSetupColumn(Loc.T("Min pDPS"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(90));
        ImGui.TableSetupColumn("##notes", ImGuiTableColumnFlags.WidthStretch, 1f);
        Theme.TableHeaders();

        // Savage raids come in tiers of four fights: a thin band between tiers (not while searching, the list is cut).
        var tierSeparators = category == FightCategory.Savage && fightFilter.Length == 0;
        for (var i = 0; i < fights.Count; i++)
        {
            var fight = fights[i];
            if (tierSeparators && i > 0 && i % 4 == 0)
            {
                ImGui.TableNextRow(ImGuiTableRowFlags.None, Theme.Px(3));
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, Theme.U32(Theme.Gold, 0.35f));
            }

            using var rowId = ImRaii.PushId((int)fight.TerritoryId);
            ImGui.TableNextRow();
            var here = fight.TerritoryId == plugin.CurrentTerritory;
            var check = FightCheck(fight);
            // Without a check of the player, the built-in one is shown (greyed), and typing a value overrides it.
            var builtIn = check is null ? FightDefaults.Threshold(fight.TerritoryId, string.Empty) : null;
            var shown = check ?? builtIn;

            ImGui.TableNextColumn();
            if (here)
                ImGui.TextColored(c.AboveThresholdColor, fight.Name);
            else
                ImGui.TextUnformatted(fight.Name);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(here ? Loc.F("You are here (zone {0}).",fight.TerritoryId) : Loc.F("Zone {0}",fight.TerritoryId));

            using (ImRaii.PushColor(ImGuiCol.Text, Theme.TextFaint, builtIn is not null))
            {
                ImGui.TableNextColumn();
                if (DrawHpInput(shown?.BossHp ?? 0, here ? boss : null, out var hp))
                    changed |= EditFightCheck(fight, t => t.BossHp = hp);

                ImGui.TableNextColumn();
                if (DrawEnrageInput(shown?.EnrageSeconds ?? 0, out var enrage))
                    changed |= EditFightCheck(fight, t => t.EnrageSeconds = enrage);
            }

            ImGui.TableNextColumn();
            if (shown is { UsesBossHp: true })
            {
                // Computed from HP and enrage: shown, not editable.
                if (builtIn is not null)
                    ImGui.TextColored(Theme.TextFaint, shown.EffectiveMinPartyDps.ToString("N0"));
                else
                    ImGui.TextUnformatted(shown.EffectiveMinPartyDps.ToString("N0"));
            }
            else
            {
                var min = check?.MinPartyDps ?? 0;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputDouble("##minpdps", ref min, 0, 0, "%.0f"))
                    changed |= EditFightCheck(fight, t => t.MinPartyDps = Math.Max(0, min));
            }

            ImGui.TableNextColumn();
            if (builtIn is not null)
                DrawBuiltInNote(fight.TerritoryId);
        }

        return changed;
    }

    /// <summary>The phases of a supported ultimate, under its row: each with its own HP, enrage and required DPS.</summary>
    /// <summary>
    /// An ultimate as its own node: one block per phase (title, then the boss row with HP, enrage and required DPS),
    /// and the whole-fight check last. Ultimates whose phases are not supported yet only have the whole-fight check.
    /// </summary>
    private bool DrawUltimateFight(FightEntry fight, BossInfo? boss)
    {
        var here = fight.TerritoryId == plugin.CurrentTerritory;
        using var color = ImRaii.PushColor(ImGuiCol.Text, plugin.Configuration.AboveThresholdColor, here);
        using var node = ImRaii.TreeNode($"{fight.Name}###ult{fight.TerritoryId}");
        color.Dispose();
        if (!node)
            return false;

        var definition = UltimateDefinitions.All.FirstOrDefault(u => u.TerritoryId == fight.TerritoryId);
        if (definition is null)
        {
            Theme.Hint(Loc.T("Phases not supported yet: one check for the whole fight."));
            return DrawFightTable(FightCategory.Ultimate, $"ult{fight.TerritoryId}", [fight], boss);
        }

        // Transitions have no boss, so nothing to check: only the boss phases are listed.
        foreach (var phase in definition.Phases.Where(p => !p.IsTransition))
        {
            using var id = ImRaii.PushId(phase.Key);
            ImGui.TextColored(Theme.Gold, PhaseTitle(phase));
            DrawPhaseTable(phase);
        }

        ImGui.TextColored(Theme.Gold, Loc.T("Whole fight"));
        Help(Loc.T("Optional: a check on the party DPS of the whole pull, used for the pDPS value of the server bar."));
        return DrawFightTable(FightCategory.Ultimate, $"ultall{fight.TerritoryId}", [fight], boss);
    }

    // "P1" -> "Phase 1"; named steps ("Limit Cut") stay as they are.
    private static string PhaseTitle(UltimatePhase phase) =>
        phase.Label.Length > 1 && phase.Label[0] == 'P' && phase.Label[1..].All(char.IsDigit)
            ? Loc.F("Phase {0}",phase.Label[1..])
            : phase.Label;

    /// <summary>The boss row of an ultimate phase: combined HP, enrage, required DPS, timings from your pulls.</summary>
    private void DrawPhaseTable(UltimatePhase phase)
    {
        var c = plugin.Configuration;
        var tracker = plugin.Ultimates;

        var nameWidth = NameColumnWidth(Loc.T("Boss"), [phase.Name]);
        // The notes column holds the "Use last wipe" button: keep room for it too.
        var layout = CheckTable(1, nameWidth + Theme.Px(CheckColumnsWidth + 170));
        using var table = ImRaii.Table("##phase", 5, layout.Flags, layout.Size, layout.InnerWidth);
        if (!table)
            return;

        ImGui.TableSetupColumn(Loc.T("Boss"), ImGuiTableColumnFlags.WidthFixed, nameWidth);
        ImGui.TableSetupColumn(Loc.T("Boss HP (all)"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(140));
        ImGui.TableSetupColumn(Loc.T("Enrage (m:s)"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(90));
        ImGui.TableSetupColumn(Loc.T("Required DPS"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(90));
        ImGui.TableSetupColumn("##notes", ImGuiTableColumnFlags.WidthStretch, 1f);
        Theme.TableHeaders();
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(phase.Name);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.F("Tracked: {0}",string.Join(", ", phase.TrackedBosses)));

        ImGui.TableNextColumn();
        DrawPhaseHp(phase);

        ImGui.TableNextColumn();
        var enrage = c.UltimateEnrageSeconds.GetValueOrDefault(phase.Key);
        if (DrawEnrageInput(enrage, out var newEnrage))
            SetPhaseEnrage(phase.Key, newEnrage);

        ImGui.TableNextColumn();
        var phaseHp = c.PhaseCheckHp(phase.Key, 0);
        if (phaseHp > 0 && enrage > 0)
            ImGui.TextUnformatted(NumberFormat.Format(phaseHp / enrage, NumberStyle.Grouped));
        else
            ImGui.TextDisabled("-");

        ImGui.TableNextColumn();
        if (tracker.LastWipes.TryGetValue(phase.Key, out var wipe))
        {
            if (ImGui.SmallButton(Loc.F("Use last wipe ({0})",NumberFormat.Duration(wipe))))
                SetPhaseEnrage(phase.Key, Math.Round(wipe.TotalSeconds));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.T("Phase time when your last pull ended in this phase, e.g. a wipe to the enrage."));
        }

        if (tracker.LastClears.TryGetValue(phase.Key, out var clear))
        {
            if (tracker.LastWipes.ContainsKey(phase.Key))
                ImGui.SameLine();
            ImGui.TextDisabled(Loc.F("last clear {0}",NumberFormat.Duration(clear)));
        }
    }

    /// <summary>Checks that are not the zone-wide check of a listed fight: other zones, or a single encounter.</summary>
    private bool DrawCustomChecks(BossInfo? boss)
    {
        var c = plugin.Configuration;
        var changed = false;

        if (!ImGui.CollapsingHeader(Loc.T("Other / custom###sectionCustom")))
            return false;

        Theme.Hint(Loc.T("Any other zone, or a single encounter of a zone: an entry with an encounter name only applies to that fight and wins over a zone-wide one. Zone 0 = any zone."));

        var territory = plugin.CurrentTerritory;
        var zoneName = ZoneName(territory);
        var encounter = plugin.Sources.Latest?.Encounter.Title ?? string.Empty;

        using (ImRaii.Disabled(territory == 0))
        {
            if (ImGui.Button(Loc.T("Add current zone")))
            {
                c.Thresholds.Add(new PartyDpsThreshold { TerritoryId = territory, Label = zoneName });
                changed = true;
            }
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(encounter.Length == 0))
        {
            if (ImGui.Button(encounter.Length > 0 ? Loc.F("Add last encounter ({0})",encounter) : Loc.T("Add last encounter")))
            {
                c.Thresholds.Add(new PartyDpsThreshold
                {
                    TerritoryId = territory, EncounterName = encounter, Label = encounter, BossHp = boss?.MaxHp ?? 0,
                });
                changed = true;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button(Loc.T("Add empty row")))
        {
            c.Thresholds.Add(new PartyDpsThreshold());
            changed = true;
        }

        var customRows = c.Thresholds.Count(t => !IsFightCheck(t));
        var layout = CheckTable(customRows, Theme.Px(60 + 2 * 160 + CheckColumnsWidth + 24));
        using var table = ImRaii.Table("##thresholds", 7, layout.Flags, layout.Size, layout.InnerWidth);
        if (!table)
            return changed;

        ImGui.TableSetupColumn(Loc.T("Zone ID"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(60));
        ImGui.TableSetupColumn(Loc.T("Encounter (optional)"), ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn(Loc.T("Label"), ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn(Loc.T("Boss HP"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(140));
        ImGui.TableSetupColumn(Loc.T("Enrage (m:s)"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(90));
        ImGui.TableSetupColumn(Loc.T("Min pDPS"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(90));
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, Theme.Px(24));
        Theme.TableHeaders();

        for (var i = 0; i < c.Thresholds.Count; i++)
        {
            var t = c.Thresholds[i];
            if (IsFightCheck(t))
                continue;

            using var id = ImRaii.PushId(i);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var tid = (int)t.TerritoryId;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputInt("##tid", ref tid, 0))
            {
                t.TerritoryId = (uint)Math.Max(0, tid);
                changed = true;
            }

            ImGui.TableNextColumn();
            var name = t.EncounterName;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##enc", ref name, 128))
            {
                t.EncounterName = name.Trim();
                changed = true;
            }

            ImGui.TableNextColumn();
            var label = t.Label;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##label", ref label, 128))
            {
                t.Label = label;
                changed = true;
            }

            ImGui.TableNextColumn();
            if (DrawHpInput(t.BossHp, boss, out var hp))
            {
                t.BossHp = hp;
                changed = true;
            }

            ImGui.TableNextColumn();
            if (DrawEnrageInput(t.EnrageSeconds, out var enrage))
            {
                t.EnrageSeconds = enrage;
                changed = true;
            }

            ImGui.TableNextColumn();
            if (t.UsesBossHp)
            {
                ImGui.TextUnformatted(t.EffectiveMinPartyDps.ToString("N0"));
            }
            else
            {
                var min = t.MinPartyDps;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputDouble("##minpdps", ref min, 0, 0, "%.0f"))
                {
                    t.MinPartyDps = Math.Max(0, min);
                    changed = true;
                }
            }

            ImGui.TableNextColumn();
            if (ImGui.Button("X"))
            {
                c.Thresholds.RemoveAt(i);
                changed = true;
                break;
            }
        }

        return changed;
    }

    // Boss HP, enrage and min / required DPS columns, with their cell padding.
    private const float CheckColumnsWidth = 400;

    /// <summary>Width of a name column: its longest entry (or header), never cut.</summary>
    private static float NameColumnWidth(string header, IEnumerable<string> names) =>
        names.Append(header).Max(n => ImGui.CalcTextSize(n).X) + Theme.Px(8);

    /// <summary>
    /// Layout of a DPS check table, sized to its rows. Narrower than <paramref name="minWidth"/>, it keeps that width and scrolls
    /// sideways instead of squashing its columns (a scrolling table needs an explicit height).
    /// </summary>
    private static (ImGuiTableFlags Flags, Vector2 Size, float InnerWidth) CheckTable(int rows, float minWidth)
    {
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.PadOuterX;
        var narrow = ImGui.GetContentRegionAvail().X < minWidth;
        var height = 0f;
        if (narrow)
        {
            flags |= ImGuiTableFlags.ScrollX;
            var cell = ImGui.GetStyle().CellPadding.Y * 2;
            height = ImGui.GetTextLineHeight() + cell + rows * (ImGui.GetFrameHeight() + cell)
                     + ImGui.GetStyle().ScrollbarSize + Theme.Px(4);
        }

        return (flags, new Vector2(-1, height), narrow ? minWidth : 0);
    }

    /// <summary>"built-in" with the values of each fight of the zone on hover.</summary>
    private static void DrawBuiltInNote(uint territory)
    {
        ImGui.TextDisabled(Loc.T("built-in"));
        if (!ImGui.IsItemHovered())
            return;

        var lines = FightDefaults.In(territory).Select(d =>
            Loc.F("{0}: {1} HP, enrage ",FightDefaults.LocalName(d.Boss),NumberFormat.Format(d.BossHp, NumberStyle.Grouped))
            + (d.EnrageSeconds > 0 ? NumberFormat.Duration(TimeSpan.FromSeconds(d.EnrageSeconds)) : Loc.T("unknown")));
        ImGui.SetTooltip(Loc.T("Read from FFLogs: boss HP from recent logs, enrage from the wipes to the enrage after release.\n")
                         + string.Join("\n", lines) + Loc.T("\nType a value to use your own instead."));
    }

    // A listed fight's own check: its whole zone, no encounter name.
    private bool IsFightCheck(PartyDpsThreshold t) => t.EncounterName.Length == 0 && plugin.Fights.Contains(t.TerritoryId);

    private PartyDpsThreshold? FightCheck(FightEntry fight) =>
        plugin.Configuration.Thresholds.FirstOrDefault(t => t.TerritoryId == fight.TerritoryId && t.EncounterName.Length == 0);

    /// <summary>Edits the fight's check, creating it on the first value and dropping it once everything is cleared.</summary>
    private bool EditFightCheck(FightEntry fight, System.Action<PartyDpsThreshold> edit)
    {
        var thresholds = plugin.Configuration.Thresholds;
        var check = FightCheck(fight);
        if (check is null)
        {
            check = new PartyDpsThreshold { TerritoryId = fight.TerritoryId, Label = fight.Name };
            // Start from the built-in values when the zone has a single fight (they would not fit both fights of a zone).
            if (FightDefaults.In(fight.TerritoryId).ToList() is [var single])
            {
                check.BossHp = single.BossHp;
                check.EnrageSeconds = single.EnrageSeconds;
            }

            thresholds.Add(check);
        }

        edit(check);
        if (check is { BossHp: 0, EnrageSeconds: 0, MinPartyDps: 0 })
            thresholds.Remove(check);
        return true;
    }

    /// <summary>HP field, with a button taking the max HP of the boss being fought.</summary>
    private static bool DrawHpInput(double value, BossInfo? boss, out double result)
    {
        result = value;
        var changed = false;
        ImGui.SetNextItemWidth(-40);
        if (ImGui.InputDouble("##hp", ref result, 0, 0, "%.0f"))
        {
            result = Math.Max(0, result);
            changed = true;
        }

        ImGui.SameLine(0, 2);
        using (ImRaii.Disabled(boss is null))
        {
            if (ImGui.SmallButton(Loc.T("Get")))
            {
                result = boss!.MaxHp;
                changed = true;
            }
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(Loc.T("Use the max HP of the boss of the current fight (only in this fight's zone).\nFor a fight with several bosses or HP pools, add them up by hand."));
        }

        return changed;
    }

    /// <summary>Minutes : seconds fields.</summary>
    private static bool DrawEnrageInput(double value, out double result)
    {
        var minutes = (int)(value / 60);
        var seconds = (int)(value % 60);
        ImGui.SetNextItemWidth(40);
        var changed = ImGui.InputInt("##min", ref minutes, 0);
        ImGui.SameLine(0, 2);
        ImGui.TextUnformatted(":");
        ImGui.SameLine(0, 2);
        ImGui.SetNextItemWidth(-1);
        changed |= ImGui.InputInt("##sec", ref seconds, 0);
        result = changed ? Math.Clamp(minutes, 0, 120) * 60 + Math.Clamp(seconds, 0, 59) : value;
        return changed;
    }

    private void DrawChatSummaryTab()
    {
        var c = plugin.Configuration;
        var changed = false;

        Theme.SectionHeader(Loc.T("When"));
        changed |= Checkbox(Loc.T("Print a summary in chat after each pull"), c.ChatSummary, v => c.ChatSummary = v);
        using (ImRaii.Disabled(!c.ChatSummary))
        {
            ImGui.Indent();
            changed |= Checkbox(Loc.T("Also in dungeons"), c.ChatSummaryInDungeons, v => c.ChatSummaryInDungeons = v);
            changed |= Checkbox(Loc.T("Also at the end of each ultimate phase"), c.ChatSummaryPhases, v => c.ChatSummaryPhases = v);
            ImGui.Unindent();
        }

        changed |= Checkbox(Loc.T("Print each party death in chat as it happens"), c.ChatDeaths, v => c.ChatDeaths = v);
        Help(Loc.T("During the fight, for every party member (you included): who died, when, and what killed them, e.g. [Death] 7:35 Cyra Frostvale (DNC): Memento Mori (Necron, 3,948,501). Stops once the party wipes (the recap tells the rest). Party members far from you may be missed."));
        using (ImRaii.Disabled(!c.ChatDeaths))
        {
            ImGui.Indent();
            changed |= Checkbox(Loc.T("Show the overkill"), c.ChatDeathsOverkill, v => c.ChatDeathsOverkill = v);
            Help(Loc.T("The damage of the killing hit beyond the HP that was left, e.g. ... (Necron, 3,948,501) overkill 3,812,240. Not shown for deaths without a hit (fall, debuff)."));
            ImGui.Unindent();
        }

        using (ImRaii.Disabled(!c.ChatSummary))
        {
            Theme.SectionHeader(Loc.T("Values shown"));
            foreach (var (element, label) in SummaryLabels)
            {
                // Same line as the previous checkbox when it fits.
                if (element != SummaryLabels[0].Element)
                {
                    ImGui.SameLine();
                    var width = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize(label).X;
                    if (ImGui.GetContentRegionAvail().X < width)
                        ImGui.NewLine();
                }
                var on = c.ChatSummaryElements.HasFlag(element);
                if (ImGui.Checkbox($"{label}##summary", ref on))
                {
                    c.ChatSummaryElements = on ? c.ChatSummaryElements | element : c.ChatSummaryElements & ~element;
                    changed = true;
                }
            }

            Theme.SectionHeader(Loc.T("Comparison"));
            changed |= Checkbox(Loc.T("Compare DPS, rDPS, GCD and pDPS with your best kill"), c.ChatSummaryCompareBest,
                                v => c.ChatSummaryCompareBest = v);
            Help(Loc.T("After each value: NEW BEST with the gain, or the gap to your best kill of the same fight. Wipes are never compared."));
            Theme.Hint(Loc.T("e.g. [Recap] Brute Bomber (4:32) Kill | DRK | DPS 21,234 #2/8 NEW BEST (+2.1%) | rDPS 20,511 #3/8 (best -1.2%) | GCD 98.7%"));
        }

        Theme.SectionHeader(Loc.T("Preview"));
        if (Theme.PrimaryButton(Loc.T("Preview the chat summary")))
            plugin.Pulls.PrintPreview();
        Help(Loc.T("Prints a summary with made-up values in your chat (only you see it), showing the values ticked above. With ultimate phase summaries on, a phase summary too."));

        if (changed)
            plugin.OnConfigChanged();
    }

    private void DrawPullsTab()
    {
        var c = plugin.Configuration;
        var changed = false;

        Theme.SectionHeader(Loc.T("Recording"));
        changed |= Checkbox(Loc.T("Only record pulls inside duties"), c.RecordOnlyInDuties, v => c.RecordOnlyInDuties = v);

        ImGui.TextUnformatted(Loc.T("Content recorded"));
        Help(Loc.T("Kinds of content whose pulls are saved in the history. The others are not kept (their chat summary is still printed). Pulls already recorded stay."));
        var categories = ContentCategories.Labels;
        for (var i = 0; i < categories.Length; i++)
        {
            var (category, label) = categories[i];
            if (i > 0)
            {
                // Same line as the previous checkbox when it fits.
                ImGui.SameLine();
                var width = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize(label).X;
                if (ImGui.GetContentRegionAvail().X < width)
                    ImGui.NewLine();
            }

            var on = (c.RecordedCategories & category) != 0;
            changed |= Checkbox($"{label}##record{i}", on, v => c.RecordedCategories = v ? c.RecordedCategories | category : c.RecordedCategories & ~category);
        }

        ImGui.Dummy(new Vector2(0, Theme.Px(4)));

        var min = c.MinPullSeconds;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt(Loc.T("Ignore pulls shorter than (s)"), ref min))
        {
            c.MinPullSeconds = Math.Clamp(min, 0, 600);
            changed = true;
        }

        var max = c.MaxHistory;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt(Loc.T("Pulls kept in history"), ref max, 50))
        {
            c.MaxHistory = Math.Clamp(max, 10, 5000);
            changed = true;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("Number of pulls saved (10 to 5000, 300 by default). Past it, the oldest pulls are removed when a new one is recorded."));

        var window = c.GraphWindowSeconds;
        ImGui.SetNextItemWidth(120);
        if (ImGui.SliderInt(Loc.T("Rolling DPS window (s)"), ref window, 3, 60))
        {
            c.GraphWindowSeconds = window;
            changed = true;
        }

        Theme.SectionHeader(Loc.T("Failed mechanics"));
        changed |= Checkbox(Loc.T("Ignore tankbuster vulnerabilities on tanks"), c.MistakesIgnoreTankbusters,
                            v => c.MistakesIgnoreTankbusters = v);
        Theme.HelpMarker(
            Loc.T("A failed mechanic is an enemy action putting a penalty debuff on a party member: Vulnerability Up (physical, magic), Damage Down, Down for the Count.\nTankbusters often put a vulnerability on the tanks on purpose: with this on, a vulnerability on a tank from an action hitting at most two players is not counted."));
        Theme.Icon(FontAwesomeIcon.ExclamationTriangle, Theme.Warn);
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, Theme.Warn))
            ImGui.TextWrapped(Loc.T("Work in progress: this option is not perfect yet, some tankbusters may still be counted (or other hits ignored)."));

        Theme.SectionHeader(Loc.T("History"));
        Theme.Badge(Loc.F("{0} / {1} pulls",plugin.History.Pulls.Count,c.MaxHistory), plugin.History.Pulls.Count >= c.MaxHistory ? Theme.Warn : Theme.Gold);
        ImGui.SameLine();
        Theme.Hint(Loc.T("recorded. /raidcompanion history to browse them."));

        if (changed)
            plugin.OnConfigChanged();
    }

    /// <summary>
    /// Combined HP of the phase's bosses: detected in game (all of them must die, so they add up), or entered by hand.
    /// </summary>
    private void DrawPhaseHp(UltimatePhase phase)
    {
        var c = plugin.Configuration;
        var detected = c.UltimatePhaseHpDetected.GetValueOrDefault(phase.Key);
        var manual = c.UltimatePhaseHp.GetValueOrDefault(phase.Key);

        ImGui.SetNextItemWidth(-1);
        var hint = detected > 0 ? NumberFormat.Format(detected, NumberStyle.Grouped) : Loc.T("not seen yet");
        var text = manual > 0 ? manual.ToString("0") : string.Empty;
        if (ImGui.InputTextWithHint("##hp", hint, ref text, 16))
        {
            if (double.TryParse(text.Replace(",", string.Empty).Replace(" ", string.Empty), out var value) && value > 0)
                c.UltimatePhaseHp[phase.Key] = value;
            else
                c.UltimatePhaseHp.Remove(phase.Key);
            plugin.OnConfigChanged();
        }

        if (ImGui.IsItemHovered())
        {
            var bosses = phase.TrackedBosses.Count > 1 ? $" ({string.Join(" + ", phase.TrackedBosses)})" : string.Empty;
            ImGui.SetTooltip(Loc.F("Combined max HP of the phase's bosses{0}.\n",bosses)
                             + (detected > 0 ? Loc.F("Read in game: {0:N0}.\n",detected) : Loc.T("Read automatically the first time the phase is reached.\n"))
                             + Loc.T("Type a value to override it, clear the field to go back to the detected HP."));
        }
    }

    private void SetPhaseEnrage(string key, double seconds)
    {
        var c = plugin.Configuration;
        if (seconds > 0)
            c.UltimateEnrageSeconds[key] = seconds;
        else
            c.UltimateEnrageSeconds.Remove(key);
        plugin.OnConfigChanged();
    }

    private void DrawFflogsTab()
    {
        var c = plugin.Configuration;
        var percentiles = plugin.Percentiles;

        Theme.SectionHeader(Loc.T("Percentile"));
        if (Checkbox(Loc.T("Estimate the FFLogs percentile of your rDPS"), c.ShowPercentile, v => c.ShowPercentile = v))
            plugin.OnConfigChanged();

        Theme.Hint(Loc.T("Uses the FFLogs API v2. On fflogs.com, open your profile > API Clients (fflogs.com/api/clients), create a client (any name, redirect URL http://localhost) and paste its Client ID and Client Secret below. The old \"V1 Client Key\" does not work with this API."));
        Theme.SectionHeader(Loc.T("API credentials"));

        var credentialsChanged = false;
        var clientId = c.FflogsClientId;
        if (ImGui.InputText(Loc.T("Client ID"), ref clientId, 128))
        {
            c.FflogsClientId = clientId.Trim();
            credentialsChanged = true;
        }

        var secret = c.FflogsClientSecret;
        if (ImGui.InputText(Loc.T("Client Secret"), ref secret, 128, ImGuiInputTextFlags.Password))
        {
            c.FflogsClientSecret = secret.Trim();
            credentialsChanged = true;
        }

        if (credentialsChanged)
        {
            percentiles.Reset();
            plugin.OnConfigChanged();
        }

        using (ImRaii.Disabled(!percentiles.HasCredentials || percentiles.Testing))
        {
            if (Theme.GhostButton(percentiles.Testing ? Loc.T("Testing...") : Loc.T("Test connection")))
                percentiles.TestConnection();
        }

        if (percentiles.TestResult is { } result)
        {
            ImGui.SameLine();
            ImGui.TextColored(result.Ok ? Theme.Good : Theme.Bad, result.Ok ? result.Message : Loc.F("Failed: {0}",result.Message));
        }

        Theme.SectionHeader(Loc.T("Current fight"));
        var territory = plugin.CurrentTerritory;
        var boss = plugin.Boss.Current;
        ImGui.TextDisabled(Loc.F("Current fight: {0}",percentiles.Describe(territory, boss?.NameId ?? 0)));
        if (boss is null)
            ImGui.TextDisabled(Loc.T("(the boss is detected during a fight)"));

        var forced = c.FflogsEncounterOverrides.GetValueOrDefault(territory);
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt(Loc.F("FFLogs encounter id for this zone ({0})",territory), ref forced, 0))
        {
            if (forced > 0)
                c.FflogsEncounterOverrides[territory] = forced;
            else
                c.FflogsEncounterOverrides.Remove(territory);
            plugin.OnConfigChanged();
        }

        Help(Loc.T("Only needed if the automatic match by boss name fails. The id is in the FFLogs URL of the fight (fflogs.com/zone/rankings/...?boss=ID). 0 = automatic."));

        if (percentiles.LastError is { } err)
            ImGui.TextColored(Theme.Bad, err);

        Theme.SectionHeader(Loc.T("How it works"));
        Theme.Hint(Loc.T("The estimate compares your rDPS to the best parse of each character for your job (current partition). During the fight it is only a trend: rDPS jumps around burst windows."));
    }

    private void DrawSourceTab()
    {
        var c = plugin.Configuration;

        Theme.SectionHeader(Loc.T("Source"));
        // The recommended source first.
        CombatSourceKind[] kinds = [CombatSourceKind.Native, CombatSourceKind.Auto, CombatSourceKind.Act];
        string[] labels = [Loc.T("Built-in only (recommended)"), Loc.T("Auto (ACT if available, else built-in)"), Loc.T("ACT only")];
        var source = Array.IndexOf(kinds, c.Source);
        ImGui.SetNextItemWidth(Theme.Px(300));
        if (ImGui.Combo(Loc.T("Source"), ref source, labels, labels.Length))
        {
            c.Source = kinds[source];
            plugin.OnConfigChanged();
        }

        Help(Loc.T("The built-in tracker reads the damage straight from the game: no ACT needed, and it is the one that computes rDPS and tells what killed you. ACT is only useful if you already run it."));

        ImGui.TextDisabled(Loc.F("In use: {0}",plugin.Sources.Current.Name));
        ImGui.Spacing();

        if (ImGui.CollapsingHeader(Loc.T("ACT / OverlayPlugin"), ImGuiTreeNodeFlags.DefaultOpen))
        {
            Theme.Hint(Loc.T("In ACT: Plugins > OverlayPlugin WSServer, then start the server. Default address is 127.0.0.1:10501."));

            ImGui.InputText(Loc.T("Host"), ref hostInput, 128);
            ImGui.InputInt(Loc.T("Port"), ref portInput);
            portInput = Math.Clamp(portInput, 1, 65535);

            var dirty = hostInput != c.Host || portInput != c.Port;
            using (ImRaii.Disabled(!dirty))
            {
                if (ImGui.Button(Loc.T("Apply")))
                {
                    c.Host = hostInput.Trim();
                    c.Port = portInput;
                    plugin.OnConfigChanged(reconnect: true);
                }
            }

            ImGui.SameLine();
            if (ImGui.Button(Loc.T("Reconnect now")))
                plugin.OnConfigChanged(reconnect: true);

            ImGui.SameLine();
            if (ImGui.Button(Loc.T("Reset")))
            {
                hostInput = "127.0.0.1";
                portInput = 10501;
            }

            ImGui.TextDisabled(Loc.F("Status: {0}",plugin.Sources.Act.StatusText));
        }

        if (ImGui.CollapsingHeader(Loc.T("Built-in tracker"), ImGuiTreeNodeFlags.DefaultOpen))
        {
            Theme.Hint(Loc.T("Reads the damage of your party directly from the game, without ACT. Pets are merged into their owner and the Limit Break is counted separately. DoTs whose owner cannot be determined are ignored, so values can be slightly lower than ACT's."));
            ImGui.TextDisabled(Loc.F("Status: {0}",plugin.Sources.Native.StatusText));
        }
    }

    private static void Help(string text) => Theme.HelpMarker(text);

    private static bool Checkbox(string label, bool value, System.Action<bool> set)
    {
        if (!ImGui.Checkbox(label, ref value))
            return false;
        set(value);
        return true;
    }

    private static bool ColorEdit(string label, Vector4 value, System.Action<Vector4> set)
    {
        if (!ImGui.ColorEdit4(label, ref value, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoAlpha))
            return false;
        set(value);
        return true;
    }

    private static string ZoneName(uint territory) =>
        Plugin.DataManager.GetExcelSheet<TerritoryType>().TryGetRow(territory, out var row)
            ? row.PlaceName.Value.Name.ExtractText()
            : Loc.T("Unknown");
}
