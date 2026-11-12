using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace DpsInServerBar;

/// <summary>
/// The plugin's texts in the chosen language. Texts are written in English in the code and looked up in the
/// translation tables (Localization/fr.json, Localization/de.json, embedded); a text without translation stays in English.
/// </summary>
public static class Loc
{
    public static readonly (string Code, string Name)[] Languages =
    [
        ("auto", "Auto (Dalamud language)"),
        ("en", "English"),
        ("fr", "Français"),
        ("de", "Deutsch"),
    ];

    private static Dictionary<string, string> table = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> Cache = new(StringComparer.Ordinal);

    /// <summary>Language in use: en, fr or de.</summary>
    public static string Code { get; private set; } = "en";

    /// <summary>Raised after the language changes (texts computed once must be computed again).</summary>
    public static event Action? Changed;

    /// <summary>Picks the language: "auto" follows Dalamud's, anything other than fr / de falls back to English.</summary>
    public static void Set(string setting, string dalamudLanguage)
    {
        var code = setting == "auto" ? dalamudLanguage : setting;
        code = code is "fr" or "de" ? code : "en";
        if (code == Code && (code == "en" || table.Count > 0))
            return;

        Code = code;
        table = code == "en" ? new(StringComparer.Ordinal) : Load(code);
        lock (Cache)
            Cache.Clear();
        Changed?.Invoke();
    }

    private static Dictionary<string, string> Load(string code)
    {
        try
        {
            using var stream = typeof(Loc).Assembly.GetManifestResourceStream($"DpsInServerBar.Localization.{code}.json");
            if (stream is null)
                return new(StringComparer.Ordinal);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) is { } loaded
                       ? new Dictionary<string, string>(loaded, StringComparer.Ordinal)
                       : new(StringComparer.Ordinal);
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, $"Could not load the {code} translations");
            return new(StringComparer.Ordinal);
        }
    }

    /// <summary>The text in the current language. What follows "##" (an ImGui id) is kept as it is.</summary>
    public static string T(string text)
    {
        if (table.Count == 0 || text.Length == 0)
            return text;
        lock (Cache)
        {
            if (Cache.TryGetValue(text, out var done))
                return done;

            var hash = text.IndexOf("##", StringComparison.Ordinal);
            var visible = hash >= 0 ? text[..hash] : text;
            var result = table.TryGetValue(visible, out var translated) && translated.Length > 0 ? translated : visible;
            if (hash >= 0)
                result += text[hash..];
            Cache[text] = result;
            return result;
        }
    }

    /// <summary>The text in the current language, with {0}, {1}... filled in.</summary>
    public static string F(string format, params object?[] args)
    {
        try
        {
            return string.Format(CultureInfo.CurrentCulture, T(format), args);
        }
        catch (FormatException)
        {
            // A broken translation: fall back on the English text.
            return string.Format(CultureInfo.CurrentCulture, format, args);
        }
    }
}
