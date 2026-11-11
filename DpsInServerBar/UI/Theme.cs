using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace DpsInServerBar.UI;

/// <summary>
/// The plugin's look: a dark translucent background, an accent color and rounded frames (midnight blue and gold by default, other themes in the Appearance settings).
/// Push it around a window (PreDraw / PostDraw) so the frame and title bar get it too.
/// </summary>
public static class Theme
{
    // Palette: the colors of the selected theme (Appearance settings), the shades derived from them.
    private static ThemePalette palette = ThemePalette.For(ThemePreset.Midnight);
    private static float opacity = 0.96f;

    /// <summary>Window background, at the opacity chosen in the settings.</summary>
    public static Vector4 Ink => palette.Background with { W = opacity };
    public static Vector4 Panel => Mix(palette.Background, palette.Surface, 0.4f);
    public static Vector4 Surface => palette.Surface;
    public static Vector4 SurfaceHover => Lighten(palette.Surface, 0.07f);
    public static Vector4 SurfaceActive => Lighten(palette.Surface, 0.13f);
    public static Vector4 Line => Lighten(palette.Surface, 0.1f);
    public static Vector4 LineSoft => Lighten(palette.Surface, 0.03f);

    public static Vector4 Gold => palette.Accent;
    public static Vector4 GoldBright => Lighten(palette.Accent, 0.4f);
    public static Vector4 GoldDim => Mix(palette.Accent, palette.Background, 0.45f);
    public static Vector4 Crystal => palette.Secondary;

    public static Vector4 Text => palette.Text;
    public static Vector4 TextMuted => Mix(palette.Text, palette.Surface, 0.4f);
    public static Vector4 TextFaint => Mix(palette.Text, palette.Surface, 0.62f);

    public static Vector4 Good => palette.Good;
    public static Vector4 Warn => palette.Warn;
    public static Vector4 Bad => palette.Bad;

    // Roles, for the damage bars of the player tables.
    public static Vector4 Tank => palette.Tank;
    public static Vector4 Healer => palette.Healer;
    public static Vector4 Dps => palette.Dps;

    /// <summary>Line colors of the graphs, in order.</summary>
    public static Vector4[] Series => [Gold, Crystal, Good, Mix(Crystal, Bad, 0.5f), Warn, TextMuted];

    /// <summary>Applies the theme and background opacity chosen in the settings.</summary>
    public static void Configure(Configuration config)
    {
        palette = config.ActivePalette;
        opacity = Math.Clamp(config.BackgroundOpacity, 0.2f, 1f);
    }

    public static Vector4 Mix(Vector4 a, Vector4 b, float t) => a + (b - a) * t;

    /// <summary>Brightens a color toward white, keeping its alpha.</summary>
    public static Vector4 Lighten(Vector4 c, float t) =>
        new(c.X + (1 - c.X) * t, c.Y + (1 - c.Y) * t, c.Z + (1 - c.Z) * t, c.W);

    public const float Radius = 6f;
    public const float RadiusLarge = 9f;

    /// <summary>A size in pixels at Dalamud's UI scale.</summary>
    public static float Px(float value) => value * ImGuiHelpers.GlobalScale;

    public static Vector2 Px(Vector2 value) => value * ImGuiHelpers.GlobalScale;

    public static Vector4 Alpha(Vector4 c, float a) => new(c.X, c.Y, c.Z, c.W * a);

    public static uint U32(Vector4 c, float a = 1f) => ImGui.GetColorU32(Alpha(c, a));

    /// <summary>Pushes the colors and metrics of the theme. Dispose to restore Dalamud's style.</summary>
    public static IDisposable Push()
    {
        var clear = new Vector4(0, 0, 0, 0);
        var colors = ImRaii.PushColor(ImGuiCol.WindowBg, Ink)
            .Push(ImGuiCol.ChildBg, clear)
            .Push(ImGuiCol.PopupBg, Panel with { W = 0.98f })
            .Push(ImGuiCol.Border, Line)
            .Push(ImGuiCol.BorderShadow, clear)
            .Push(ImGuiCol.Text, Text)
            .Push(ImGuiCol.TextDisabled, TextFaint)
            .Push(ImGuiCol.TitleBg, palette.Background with { W = Math.Max(opacity, 0.9f) })
            .Push(ImGuiCol.TitleBgActive, Panel with { W = Math.Max(opacity, 0.9f) })
            .Push(ImGuiCol.TitleBgCollapsed, palette.Background with { W = 0.8f })
            .Push(ImGuiCol.FrameBg, Surface)
            .Push(ImGuiCol.FrameBgHovered, SurfaceHover)
            .Push(ImGuiCol.FrameBgActive, SurfaceActive)
            .Push(ImGuiCol.Button, Surface)
            .Push(ImGuiCol.ButtonHovered, SurfaceHover)
            .Push(ImGuiCol.ButtonActive, SurfaceActive)
            .Push(ImGuiCol.Header, Alpha(Crystal, 0.16f))
            .Push(ImGuiCol.HeaderHovered, Alpha(Crystal, 0.26f))
            .Push(ImGuiCol.HeaderActive, Alpha(Crystal, 0.34f))
            .Push(ImGuiCol.Separator, Line)
            .Push(ImGuiCol.SeparatorHovered, Alpha(Gold, 0.6f))
            .Push(ImGuiCol.SeparatorActive, Gold)
            .Push(ImGuiCol.CheckMark, Gold)
            .Push(ImGuiCol.SliderGrab, Gold)
            .Push(ImGuiCol.SliderGrabActive, GoldBright)
            .Push(ImGuiCol.ResizeGrip, Alpha(Gold, 0.25f))
            .Push(ImGuiCol.ResizeGripHovered, Alpha(Gold, 0.55f))
            .Push(ImGuiCol.ResizeGripActive, Alpha(Gold, 0.85f))
            .Push(ImGuiCol.Tab, clear)
            .Push(ImGuiCol.TabHovered, Alpha(Crystal, 0.18f))
            .Push(ImGuiCol.TabActive, Alpha(Crystal, 0.14f))
            .Push(ImGuiCol.TableHeaderBg, Panel)
            .Push(ImGuiCol.TableBorderStrong, Line)
            .Push(ImGuiCol.TableBorderLight, LineSoft)
            .Push(ImGuiCol.TableRowBg, clear)
            .Push(ImGuiCol.TableRowBgAlt, Alpha(Surface, 0.45f))
            .Push(ImGuiCol.ScrollbarBg, clear)
            .Push(ImGuiCol.ScrollbarGrab, Line)
            .Push(ImGuiCol.ScrollbarGrabHovered, Alpha(Crystal, 0.5f))
            .Push(ImGuiCol.ScrollbarGrabActive, Alpha(Crystal, 0.75f))
            .Push(ImGuiCol.PlotLines, Gold)
            .Push(ImGuiCol.PlotHistogram, Crystal)
            .Push(ImGuiCol.NavHighlight, Alpha(Gold, 0.7f));

        var styles = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, Px(RadiusLarge))
            .Push(ImGuiStyleVar.WindowBorderSize, 1f)
            .Push(ImGuiStyleVar.WindowPadding, Px(new Vector2(14, 12)))
            .Push(ImGuiStyleVar.ChildRounding, Px(Radius))
            .Push(ImGuiStyleVar.FrameRounding, Px(Radius))
            .Push(ImGuiStyleVar.FrameBorderSize, 0f)
            .Push(ImGuiStyleVar.FramePadding, Px(new Vector2(9, 5)))
            .Push(ImGuiStyleVar.PopupRounding, Px(Radius))
            .Push(ImGuiStyleVar.ItemSpacing, Px(new Vector2(8, 6)))
            .Push(ImGuiStyleVar.ItemInnerSpacing, Px(new Vector2(6, 5)))
            .Push(ImGuiStyleVar.IndentSpacing, Px(18))
            .Push(ImGuiStyleVar.ScrollbarSize, Px(11))
            .Push(ImGuiStyleVar.ScrollbarRounding, Px(6))
            .Push(ImGuiStyleVar.GrabMinSize, Px(12))
            .Push(ImGuiStyleVar.GrabRounding, Px(5))
            .Push(ImGuiStyleVar.TabRounding, Px(Radius))
            .Push(ImGuiStyleVar.CellPadding, Px(new Vector2(8, 5)));

        return new Scope(colors, styles);
    }

    private sealed class Scope(params IDisposable[] parts) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            for (var i = parts.Length - 1; i >= 0; i--)
                parts[i].Dispose();
        }
    }

    /// <summary>A FontAwesome glyph inline.</summary>
    public static void Icon(FontAwesomeIcon icon, Vector4? color = null)
    {
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        ImGui.TextColored(color ?? Text, icon.ToIconString());
    }

    public static float IconWidth(FontAwesomeIcon icon)
    {
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        return ImGui.CalcTextSize(icon.ToIconString()).X;
    }

    /// <summary>Window header: gold icon, title, muted subtitle and a gold rule under it.</summary>
    public static void WindowHeader(FontAwesomeIcon icon, string title, string? subtitle = null)
    {
        Icon(icon, Gold);
        ImGui.SameLine();
        ImGui.TextColored(GoldBright, title);
        if (subtitle is not null)
        {
            ImGui.SameLine();
            ImGui.TextColored(TextMuted, subtitle);
        }

        Rule();
    }

    /// <summary>A separator fading out toward both ends, with a gold core.</summary>
    public static void Rule(float padding = 4f)
    {
        ImGui.Dummy(new Vector2(0, Px(padding)));
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var mid = start.X + width / 2;
        var y = start.Y;
        uint gold = U32(Gold, 0.65f), none = U32(Gold, 0f);
        dl.AddRectFilledMultiColor(start, new Vector2(mid, y + 1), none, gold, gold, none);
        dl.AddRectFilledMultiColor(new Vector2(mid, y), new Vector2(start.X + width, y + 1), gold, none, none, gold);
        ImGui.Dummy(new Vector2(0, Px(padding)));
    }

    /// <summary>Section title: a short gold rule, then the label in gold capitals.</summary>
    /// <param name="help">Shown on hover of a ? right after the title.</param>
    /// <summary>Extra room above section titles (pixels), set by a view that wants its sections further apart.</summary>
    public static float SectionSpacing { get; set; }

    public static void SectionHeader(string label, FontAwesomeIcon? icon = null, string? help = null)
    {
        ImGui.Dummy(new Vector2(0, Px(4) + SectionSpacing));
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var lineY = pos.Y + ImGui.GetTextLineHeight() / 2;
        dl.AddLine(new Vector2(pos.X, lineY), new Vector2(pos.X + Px(12), lineY), U32(Gold, 0.8f), 1.5f);
        ImGui.SetCursorScreenPos(new Vector2(pos.X + Px(18), pos.Y));
        if (icon is { } glyph)
        {
            Icon(glyph, Gold);
            ImGui.SameLine();
        }

        ImGui.TextColored(Gold, label.ToUpperInvariant());
        if (help is not null)
            HelpMarker(help);
        ImGui.Dummy(new Vector2(0, Px(1)));
    }

    /// <summary>Muted explanation text, wrapped to the available width.</summary>
    public static void Hint(string text)
    {
        using var color = ImRaii.PushColor(ImGuiCol.Text, TextMuted);
        ImGui.TextWrapped(text);
    }

    /// <summary>A question-mark glyph after the previous item, showing the text on hover.</summary>
    public static void HelpMarker(string text)
    {
        ImGui.SameLine();
        Icon(FontAwesomeIcon.QuestionCircle, TextFaint);
        if (ImGui.IsItemHovered())
        {
            using var tooltip = ImRaii.Tooltip();
            ImGui.PushTextWrapPos(Px(380));
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
        }
    }

    /// <summary>Header row of a table: white text on the accent color (toned down so the text stays readable).</summary>
    public static void TableHeaders()
    {
        using var color = ImRaii.PushColor(ImGuiCol.TableHeaderBg, Alpha(Gold, 0.45f))
            .Push(ImGuiCol.Text, Vector4.One);
        ImGui.TableHeadersRow();
    }

    /// <summary>A rounded pill for counts and states.</summary>
    public static void Badge(string label, Vector4 color)
    {
        var size = ImGui.CalcTextSize(label);
        var pad = Px(new Vector2(7, 1));
        var pos = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        var max = pos + size + pad * 2;
        dl.AddRectFilled(pos, max, U32(color, 0.16f), size.Y);
        dl.AddRect(pos, max, U32(color, 0.55f), size.Y);
        dl.AddText(pos + pad, U32(color), label);
        ImGui.Dummy(size + pad * 2);
    }

    /// <summary>Filled gold button for the main action of a view.</summary>
    public static bool PrimaryButton(string label, Vector2 size = default)
    {
        using var colors = ImRaii.PushColor(ImGuiCol.Button, Gold)
            .Push(ImGuiCol.ButtonHovered, GoldBright)
            .Push(ImGuiCol.ButtonActive, GoldDim)
            .Push(ImGuiCol.Text, Ink with { W = 1f });
        return ImGui.Button(label, size);
    }

    /// <summary>Outlined button for secondary actions.</summary>
    public static bool GhostButton(string label, Vector2 size = default)
    {
        using var colors = ImRaii.PushColor(ImGuiCol.Button, new Vector4(0, 0, 0, 0))
            .Push(ImGuiCol.ButtonHovered, Alpha(Gold, 0.14f))
            .Push(ImGuiCol.ButtonActive, Alpha(Gold, 0.26f))
            .Push(ImGuiCol.Border, Alpha(Gold, 0.55f));
        using var border = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 1f);
        return ImGui.Button(label, size);
    }

    /// <summary>A button showing only a FontAwesome glyph, with a tooltip.</summary>
    public static bool IconButton(string id, FontAwesomeIcon icon, string tooltip, Vector4? color = null)
    {
        bool clicked;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        using (ImRaii.PushColor(ImGuiCol.Text, color ?? TextMuted))
        using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0, 0, 0, 0)))
        {
            clicked = ImGui.Button($"{icon.ToIconString()}##{id}");
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
        return clicked;
    }

    /// <summary>
    /// A figure on a card: small caption, big value, optional hint line. The accent colors the top edge and the value.
    /// </summary>
    /// <param name="fullHeight">Keeps the hint line's room even without a hint, so that cards side by side match.</param>
    public static void StatCard(string caption, string value, Vector4 accent, float width, string? hint = null,
                                Vector4? hintColor = null, Vector4? valueColor = null, bool fullHeight = false)
    {
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var pad = Px(new Vector2(10, 7));
        var line = ImGui.GetTextLineHeight();
        var font = ImGui.GetFont();
        var valueSize = ImGui.GetFontSize() * 1.45f;
        var height = pad.Y * 2 + line + valueSize + Px(2) + (hint is null && !fullHeight ? 0 : line);

        // Wider than asked when the caption, value or hint would not fit.
        width = Math.Max(width, StatCardWidth(caption, value, hint));

        var max = pos + new Vector2(width, height);
        dl.AddRectFilled(pos, max, U32(Surface, 0.8f), Px(Radius));
        dl.AddRect(pos, max, U32(Line, 0.8f), Px(Radius));
        dl.AddRectFilled(pos + new Vector2(Px(Radius), 0), new Vector2(max.X - Px(Radius), pos.Y + Px(2)), U32(accent, 0.9f));

        var y = pos.Y + pad.Y;
        dl.AddText(new Vector2(pos.X + pad.X, y), U32(TextMuted), caption.ToUpperInvariant());
        y += line + Px(1);
        dl.AddText(font, valueSize, new Vector2(pos.X + pad.X, y), U32(valueColor ?? Text), value);
        y += valueSize + Px(1);
        if (hint is not null)
            dl.AddText(new Vector2(pos.X + pad.X, y), U32(hintColor ?? TextFaint), hint);

        ImGui.Dummy(new Vector2(width, height));
    }

    /// <summary>The narrowest a card can be with this caption, value and hint.</summary>
    public static float StatCardWidth(string caption, string value, string? hint)
    {
        var valueSize = ImGui.GetFontSize() * 1.45f;
        var content = Math.Max(ImGui.CalcTextSize(caption.ToUpperInvariant()).X,
                               ImGui.CalcTextSize(value).X * valueSize / ImGui.GetFontSize());
        if (hint is not null)
            content = Math.Max(content, ImGui.CalcTextSize(hint).X);
        return content + Px(10) * 2;
    }

    /// <summary>Width of each of <paramref name="count"/> cards filling the line.</summary>
    public static float CardWidth(int count) =>
        count <= 0 ? 0 : (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X * (count - 1)) / count;

    /// <summary>A pill that toggles: gold when on. Returns true when clicked.</summary>
    /// <param name="accent">Draws the chip as on, in this color (a button standing out rather than a toggle).</param>
    public static bool Chip(string label, bool on, bool transparent = false, Vector4? accent = null)
    {
        var color = accent ?? Gold;
        on |= accent is not null;
        var size = ImGui.CalcTextSize(label) + Px(new Vector2(20, 8));
        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton($"##chip{label}", size);
        var hovered = ImGui.IsItemHovered();

        var dl = ImGui.GetWindowDrawList();
        var max = pos + size;
        var rounding = size.Y / 2;
        if (on)
        {
            dl.AddRectFilled(pos, max, U32(color, hovered ? 0.24f : 0.16f), rounding);
            dl.AddRect(pos, max, U32(color, 0.7f), rounding);
        }
        else if (transparent)
        {
            // On a see-through window: an outline, filled only on hover.
            if (hovered)
                dl.AddRectFilled(pos, max, U32(SurfaceHover, 0.6f), rounding);
            dl.AddRect(pos, max, U32(TextMuted, 0.5f), rounding);
        }
        else
        {
            dl.AddRectFilled(pos, max, U32(hovered ? SurfaceHover : Surface), rounding);
        }

        dl.AddText(pos + Px(new Vector2(10, 4)), U32(on ? Lighten(color, 0.4f) : TextMuted), label);
        return clicked;
    }

    /// <summary>
    /// Pills to switch between views, with a gold underline on the selected one. Returns true when it changed.
    /// </summary>
    public static bool TabStrip(string id, ref int selected, params (FontAwesomeIcon Icon, string Label)[] tabs)
    {
        var changed = false;
        var dl = ImGui.GetWindowDrawList();
        for (var i = 0; i < tabs.Length; i++)
        {
            if (i > 0)
                ImGui.SameLine(0, Px(4));

            var (icon, label) = tabs[i];
            var glyph = icon.ToIconString();
            float glyphWidth;
            using (ImRaii.PushFont(UiBuilder.IconFont))
                glyphWidth = ImGui.CalcTextSize(glyph).X;

            var textSize = ImGui.CalcTextSize(label);
            var size = new Vector2(glyphWidth + Px(8) + textSize.X, textSize.Y) + Px(new Vector2(28, 12));
            var pos = ImGui.GetCursorScreenPos();
            if (ImGui.InvisibleButton($"##{id}{i}", size) && selected != i)
            {
                selected = i;
                changed = true;
            }

            var hovered = ImGui.IsItemHovered();
            var isSelected = selected == i;
            var max = pos + size;
            if (isSelected)
            {
                dl.AddRectFilled(pos, max, U32(Gold, 0.12f), Px(Radius));
                dl.AddRectFilled(new Vector2(pos.X + Px(8), max.Y - Px(2)), new Vector2(max.X - Px(8), max.Y), U32(Gold));
            }
            else if (hovered)
            {
                dl.AddRectFilled(pos, max, U32(SurfaceHover, 0.7f), Px(Radius));
            }

            var color = isSelected ? GoldBright : hovered ? Text : TextMuted;
            var textPos = pos + Px(new Vector2(14, 6));
            using (ImRaii.PushFont(UiBuilder.IconFont))
                dl.AddText(textPos, U32(isSelected ? Gold : color), glyph);
            dl.AddText(textPos + new Vector2(glyphWidth + Px(8), 0), U32(color), label);
        }

        return changed;
    }

    /// <summary>
    /// A navigation entry for a side menu: icon and label, a gold bar and tint when selected. Returns true when clicked.
    /// </summary>
    public static bool NavItem(string id, FontAwesomeIcon icon, string label, bool selected)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var height = ImGui.GetTextLineHeight() + Px(14);
        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();

        var dl = ImGui.GetWindowDrawList();
        var max = pos + new Vector2(width, height);
        if (selected)
        {
            dl.AddRectFilled(pos, max, U32(Gold, 0.12f), Px(Radius));
            dl.AddRectFilled(pos, new Vector2(pos.X + Px(3), max.Y), U32(Gold), Px(2));
        }
        else if (hovered)
        {
            dl.AddRectFilled(pos, max, U32(SurfaceHover, 0.7f), Px(Radius));
        }

        var textY = pos.Y + (height - ImGui.GetTextLineHeight()) / 2;
        var color = selected ? GoldBright : hovered ? Text : TextMuted;
        var glyph = icon.ToIconString();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var glyphWidth = ImGui.CalcTextSize(glyph).X;
            var iconColumn = Px(18);
            dl.AddText(new Vector2(pos.X + Px(12) + (iconColumn - glyphWidth) / 2, textY), U32(selected ? Gold : color), glyph);
        }

        dl.AddText(new Vector2(pos.X + Px(38), textY), U32(color), label);
        return clicked;
    }
}
