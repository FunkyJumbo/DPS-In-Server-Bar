using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace DpsInServerBar.UI;

/// <summary>
/// The plugin's look: midnight-blue glass, gold accents and warm text, with rounded frames.
/// Push it around a window (PreDraw / PostDraw) so the frame and title bar get it too.
/// </summary>
public static class Theme
{
    // Palette
    public static readonly Vector4 Ink = Rgb(0x0B, 0x0F, 0x18, 0.96f);
    public static readonly Vector4 Panel = Rgb(0x11, 0x17, 0x23);
    public static readonly Vector4 Surface = Rgb(0x1B, 0x23, 0x33);
    public static readonly Vector4 SurfaceHover = Rgb(0x26, 0x31, 0x45);
    public static readonly Vector4 SurfaceActive = Rgb(0x2F, 0x3D, 0x56);
    public static readonly Vector4 Line = Rgb(0x2C, 0x38, 0x4D);
    public static readonly Vector4 LineSoft = Rgb(0x1F, 0x27, 0x36);

    public static readonly Vector4 Gold = Rgb(0xD6, 0xB0, 0x68);
    public static readonly Vector4 GoldBright = Rgb(0xF2, 0xD7, 0x96);
    public static readonly Vector4 GoldDim = Rgb(0x7E, 0x66, 0x33);
    public static readonly Vector4 Crystal = Rgb(0x6F, 0xB8, 0xE0);

    public static readonly Vector4 Text = Rgb(0xE7, 0xE1, 0xD3);
    public static readonly Vector4 TextMuted = Rgb(0x95, 0xA0, 0xB3);
    public static readonly Vector4 TextFaint = Rgb(0x5F, 0x6A, 0x7E);

    public static readonly Vector4 Good = Rgb(0x7C, 0xD3, 0x8B);
    public static readonly Vector4 Warn = Rgb(0xE8, 0xB8, 0x4B);
    public static readonly Vector4 Bad = Rgb(0xE4, 0x69, 0x5F);

    // Roles, for the damage bars of the player tables.
    public static readonly Vector4 Tank = Rgb(0x5B, 0x8F, 0xE0);
    public static readonly Vector4 Healer = Rgb(0x5F, 0xC2, 0x7A);
    public static readonly Vector4 Dps = Rgb(0xD9, 0x6A, 0x6A);

    /// <summary>Line colors of the graphs, in order.</summary>
    public static readonly Vector4[] Series = [Gold, Crystal, Good, Rgb(0xC0, 0x8C, 0xE8), Warn, TextMuted];

    public const float Radius = 6f;
    public const float RadiusLarge = 9f;

    /// <summary>A size in pixels at Dalamud's UI scale.</summary>
    public static float Px(float value) => value * ImGuiHelpers.GlobalScale;

    public static Vector2 Px(Vector2 value) => value * ImGuiHelpers.GlobalScale;

    public static Vector4 Alpha(Vector4 c, float a) => new(c.X, c.Y, c.Z, c.W * a);

    public static uint U32(Vector4 c, float a = 1f) => ImGui.GetColorU32(Alpha(c, a));

    private static Vector4 Rgb(int r, int g, int b, float a = 1f) => new(r / 255f, g / 255f, b / 255f, a);

    /// <summary>Pushes the colors and metrics of the theme. Dispose to restore Dalamud's style.</summary>
    public static IDisposable Push()
    {
        var clear = new Vector4(0, 0, 0, 0);
        var colors = ImRaii.PushColor(ImGuiCol.WindowBg, Ink)
            .Push(ImGuiCol.ChildBg, clear)
            .Push(ImGuiCol.PopupBg, Rgb(0x10, 0x16, 0x22, 0.98f))
            .Push(ImGuiCol.Border, Line)
            .Push(ImGuiCol.BorderShadow, clear)
            .Push(ImGuiCol.Text, Text)
            .Push(ImGuiCol.TextDisabled, TextFaint)
            .Push(ImGuiCol.TitleBg, Rgb(0x0D, 0x12, 0x1C, 0.98f))
            .Push(ImGuiCol.TitleBgActive, Rgb(0x14, 0x1C, 0x2B, 0.98f))
            .Push(ImGuiCol.TitleBgCollapsed, Rgb(0x0D, 0x12, 0x1C, 0.8f))
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
            .Push(ImGuiCol.TableHeaderBg, Rgb(0x16, 0x1E, 0x2C))
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
    public static void SectionHeader(string label, FontAwesomeIcon? icon = null)
    {
        ImGui.Dummy(new Vector2(0, Px(4)));
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
    public static void StatCard(string caption, string value, Vector4 accent, float width, string? hint = null,
                                Vector4? hintColor = null, Vector4? valueColor = null)
    {
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var pad = Px(new Vector2(10, 7));
        var line = ImGui.GetTextLineHeight();
        var font = ImGui.GetFont();
        var valueSize = ImGui.GetFontSize() * 1.45f;
        var height = pad.Y * 2 + line + valueSize + Px(2) + (hint is null ? 0 : line);

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

    /// <summary>Width of each of <paramref name="count"/> cards filling the line.</summary>
    public static float CardWidth(int count) =>
        count <= 0 ? 0 : (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X * (count - 1)) / count;

    /// <summary>A pill that toggles: gold when on. Returns true when clicked.</summary>
    public static bool Chip(string label, bool on)
    {
        var size = ImGui.CalcTextSize(label) + Px(new Vector2(20, 8));
        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton($"##chip{label}", size);
        var hovered = ImGui.IsItemHovered();

        var dl = ImGui.GetWindowDrawList();
        var max = pos + size;
        var rounding = size.Y / 2;
        if (on)
        {
            dl.AddRectFilled(pos, max, U32(Gold, hovered ? 0.24f : 0.16f), rounding);
            dl.AddRect(pos, max, U32(Gold, 0.7f), rounding);
        }
        else
        {
            dl.AddRectFilled(pos, max, U32(hovered ? SurfaceHover : Surface), rounding);
        }

        dl.AddText(pos + Px(new Vector2(10, 4)), U32(on ? GoldBright : TextMuted), label);
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
