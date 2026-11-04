using System;
using System.Collections.Generic;
using System.Numerics;

namespace DpsInServerBar.UI;

public enum ThemePreset
{
    Midnight,
    Crimson,
    Emerald,
    Aether,
    Slate,
    Custom,
}

/// <summary>
/// The colors a theme is made of. The other shades (hover, borders, muted text...) are derived from them.
/// Saved in the configuration for the custom theme.
/// </summary>
[Serializable]
public sealed class ThemePalette
{
    public Vector4 Background { get; set; }
    public Vector4 Surface { get; set; }
    public Vector4 Accent { get; set; }
    public Vector4 Secondary { get; set; }
    public Vector4 Text { get; set; }
    public Vector4 Good { get; set; }
    public Vector4 Warn { get; set; }
    public Vector4 Bad { get; set; }
    public Vector4 Tank { get; set; }
    public Vector4 Healer { get; set; }
    public Vector4 Dps { get; set; }

    public ThemePalette Copy() => (ThemePalette)MemberwiseClone();

    /// <summary>The editable colors, with their label, for the settings.</summary>
    public IEnumerable<(string Label, Func<Vector4> Get, Action<Vector4> Set)> Entries()
    {
        yield return ("Background", () => Background, v => Background = v);
        yield return ("Surface (fields, cards)", () => Surface, v => Surface = v);
        yield return ("Accent", () => Accent, v => Accent = v);
        yield return ("Secondary accent", () => Secondary, v => Secondary = v);
        yield return ("Text", () => Text, v => Text = v);
        yield return ("Good", () => Good, v => Good = v);
        yield return ("Warning", () => Warn, v => Warn = v);
        yield return ("Bad", () => Bad, v => Bad = v);
        yield return ("Tank bars", () => Tank, v => Tank = v);
        yield return ("Healer bars", () => Healer, v => Healer = v);
        yield return ("DPS bars", () => Dps, v => Dps = v);
    }

    public static ThemePalette For(ThemePreset preset) => preset switch
    {
        ThemePreset.Crimson => new ThemePalette
        {
            Background = Rgb(0x16, 0x0B, 0x0E), Surface = Rgb(0x2A, 0x17, 0x1C), Accent = Rgb(0xE0, 0x9A, 0x86),
            Secondary = Rgb(0xD9, 0x5C, 0x6E), Text = Rgb(0xEE, 0xE2, 0xDF),
        }.WithStatus(),
        ThemePreset.Emerald => new ThemePalette
        {
            Background = Rgb(0x0A, 0x14, 0x11), Surface = Rgb(0x16, 0x26, 0x21), Accent = Rgb(0xC9, 0xB8, 0x6B),
            Secondary = Rgb(0x5F, 0xC9, 0x9B), Text = Rgb(0xE2, 0xE8, 0xDF),
        }.WithStatus(),
        ThemePreset.Aether => new ThemePalette
        {
            Background = Rgb(0x10, 0x0C, 0x1C), Surface = Rgb(0x20, 0x1A, 0x33), Accent = Rgb(0x7F, 0xD8, 0xE8),
            Secondary = Rgb(0xB0, 0x8C, 0xF0), Text = Rgb(0xE6, 0xE2, 0xF2),
        }.WithStatus(),
        ThemePreset.Slate => new ThemePalette
        {
            Background = Rgb(0x14, 0x15, 0x18), Surface = Rgb(0x24, 0x26, 0x2B), Accent = Rgb(0x6F, 0xA8, 0xF0),
            Secondary = Rgb(0xA0, 0xA8, 0xB8), Text = Rgb(0xE4, 0xE6, 0xEA),
        }.WithStatus(),
        _ => new ThemePalette
        {
            Background = Rgb(0x0B, 0x0F, 0x18), Surface = Rgb(0x1B, 0x23, 0x33), Accent = Rgb(0xD6, 0xB0, 0x68),
            Secondary = Rgb(0x6F, 0xB8, 0xE0), Text = Rgb(0xE7, 0xE1, 0xD3),
        }.WithStatus(),
    };

    public static string Name(ThemePreset preset) => preset switch
    {
        ThemePreset.Midnight => "Midnight (blue & gold)",
        ThemePreset.Crimson => "Crimson (wine & rose gold)",
        ThemePreset.Emerald => "Emerald (forest & gold)",
        ThemePreset.Aether => "Aether (violet & cyan)",
        ThemePreset.Slate => "Slate (grey & blue)",
        _ => "Custom",
    };

    // Status and role colors shared by every preset.
    private ThemePalette WithStatus()
    {
        Good = Rgb(0x7C, 0xD3, 0x8B);
        Warn = Rgb(0xE8, 0xB8, 0x4B);
        Bad = Rgb(0xE4, 0x69, 0x5F);
        Tank = Rgb(0x5B, 0x8F, 0xE0);
        Healer = Rgb(0x5F, 0xC2, 0x7A);
        Dps = Rgb(0xD9, 0x6A, 0x6A);
        return this;
    }

    private static Vector4 Rgb(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f, 1f);
}
