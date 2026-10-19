using System;
using System.Globalization;

namespace DpsInServerBar.Display;

public static class NumberFormat
{
    public static string Format(double value, NumberStyle style) => style switch
    {
        NumberStyle.Plain => Math.Round(value).ToString("0", CultureInfo.InvariantCulture),
        NumberStyle.Compact => Compact(value),
        _ => Math.Round(value).ToString("N0", CultureInfo.InvariantCulture),
    };

    private static string Compact(double value)
    {
        var abs = Math.Abs(value);
        return abs switch
        {
            >= 1_000_000 => (value / 1_000_000).ToString("0.00", CultureInfo.InvariantCulture) + "M",
            >= 10_000 => (value / 1_000).ToString("0.0", CultureInfo.InvariantCulture) + "k",
            _ => Math.Round(value).ToString("0", CultureInfo.InvariantCulture),
        };
    }

    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
}
