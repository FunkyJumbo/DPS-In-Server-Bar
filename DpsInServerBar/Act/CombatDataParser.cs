using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using DpsInServerBar.Combat;

namespace DpsInServerBar.Act;

/// <summary>
/// Parses OverlayPlugin "CombatData" events. Every value in those payloads is a string,
/// sometimes formatted with the ACT machine's culture ("1234,56"), sometimes "∞", "NaN" or "---".
/// </summary>
public static class CombatDataParser
{
    public static CombatSnapshot? TryParse(JsonElement root, string? playerName)
    {
        if (root.ValueKind != JsonValueKind.Object || GetString(root, "type") != "CombatData")
            return null;

        var encounter = ParseEncounter(root.TryGetProperty("Encounter", out var enc) ? enc : default);

        var combatants = new List<CombatantInfo>();
        if (root.TryGetProperty("Combatant", out var combatantObj) && combatantObj.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in combatantObj.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Object)
                    combatants.Add(ParseCombatant(prop.Name, prop.Value, playerName));
            }
        }

        // "isActive" is a string ("true"/"false") in OverlayPlugin payloads, but accept a real bool too.
        var isActive = root.TryGetProperty("isActive", out var active) && active.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => bool.TryParse(active.GetString(), out var b) && b,
            _ => false,
        };

        return new CombatSnapshot(encounter, combatants, isActive, DateTime.UtcNow);
    }

    private static EncounterInfo ParseEncounter(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
            return new EncounterInfo(string.Empty, string.Empty, TimeSpan.Zero, 0, 0);

        return new EncounterInfo(
            Title: GetString(e, "title") ?? string.Empty,
            Zone: GetString(e, "CurrentZoneName") ?? string.Empty,
            Duration: TimeSpan.FromSeconds(GetNumber(e, "DURATION")),
            EncDps: GetNumber(e, "encdps"),
            TotalDamage: GetNumber(e, "damage"));
    }

    private static CombatantInfo ParseCombatant(string key, JsonElement c, string? playerName)
    {
        var name = GetString(c, "name") ?? key;
        var isYou = name == "YOU"
                    || key == "YOU"
                    || key.Contains("(YOU)", StringComparison.Ordinal)
                    || (!string.IsNullOrEmpty(playerName) && name.Equals(playerName, StringComparison.OrdinalIgnoreCase));

        return new CombatantInfo(
            Name: name,
            Job: (GetString(c, "Job") ?? string.Empty).ToUpperInvariant(),
            EncDps: GetNumber(c, "encdps", "ENCDPS", "EncDPS"),
            RDps: double.NaN,
            EncHps: GetNumber(c, "enchps", "ENCHPS"),
            Damage: GetNumber(c, "damage"),
            DamagePercent: GetNumber(c, "damage%"),
            CritPercent: GetNumber(c, "crithit%"),
            DirectHitPercent: GetNumber(c, "DirectHitPct"),
            CritDirectHitPercent: GetNumber(c, "CritDirectHitPct"),
            Deaths: (int)GetNumber(c, "deaths"),
            IsYou: isYou);
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    /// <summary>Returns the first parseable number among the given keys, or 0.</summary>
    private static double GetNumber(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (!obj.TryGetProperty(name, out var v))
                continue;

            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var direct))
                return Sanitize(direct);

            if (v.ValueKind == JsonValueKind.String && TryParseLoose(v.GetString(), out var parsed))
                return parsed;
        }

        return 0;
    }

    private static bool TryParseLoose(string? raw, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var s = raw.Trim().TrimEnd('%').Replace(" ", string.Empty).Replace(" ", string.Empty);

        // A lone comma is a decimal separator (fr/de cultures); otherwise commas are thousands separators.
        if (s.Contains(',') && !s.Contains('.') && s.IndexOf(',') == s.LastIndexOf(','))
            s = s.Replace(',', '.');
        else
            s = s.Replace(",", string.Empty);

        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return false;

        value = Sanitize(d);
        return true;
    }

    private static double Sanitize(double d) => double.IsFinite(d) ? d : 0;
}
