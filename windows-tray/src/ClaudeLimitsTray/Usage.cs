using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeLimitsTray;

internal sealed record LimitWindow(string Kind, double Percent, DateTimeOffset? ResetsAt)
{
    public string Label => Kind switch
    {
        "five_hour" => "5h",
        "seven_day" => "7d",
        "seven_day_opus" => "7d Opus",
        "seven_day_sonnet" => "7d Sonnet",
        _ => Kind.Replace('_', ' '),
    };

    public string Left => Usage.FormatLeft(ResetsAt);

    public bool IsReset => ResetsAt is { } reset && reset <= DateTimeOffset.Now;

    public double ShownPercent => IsReset ? 0 : Percent;
}

internal static class Usage
{
    public const double WarnLevel = 70;
    public const double DangerLevel = 90;

    private static readonly string[] PercentNames = { "utilization", "percent_used", "percentUsed", "used_percent" };
    private static readonly string[] ResetNames = { "resets_at", "reset_at", "resetsAt" };

    public static List<LimitWindow> Parse(string json)
    {
        var list = new List<LimitWindow>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return list;

        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) continue;
            if (!TryPercent(property.Value, out var percent)) continue;
            list.Add(new LimitWindow(property.Name, Math.Round(percent, 1), ReadReset(property.Value)));
        }

        return list.OrderBy(w => w.Kind switch { "five_hour" => 0, "seven_day" => 1, _ => 2 }).ThenBy(w => w.Kind).ToList();
    }

    public static string Shape(string json)
    {
        var redacted = Regex.Replace(json, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", "<uuid>");
        return redacted.Length > 600 ? redacted[..600] + "…" : redacted;
    }

    public static string FormatLeft(DateTimeOffset? resetsAt)
    {
        if (resetsAt is not { } reset) return "";
        var minutes = Math.Max(0, (int)Math.Ceiling((reset - DateTimeOffset.Now).TotalMinutes));
        var days = minutes / 1440;
        var hours = minutes % 1440 / 60;
        var mins = minutes % 60;
        if (days > 0) return $"{days}d {hours}h";
        return hours > 0 ? $"{hours}h {mins}m" : $"{mins}m";
    }

    public static Color ColorOf(double percent) => percent >= DangerLevel
        ? Color.FromArgb(229, 72, 77)
        : percent >= WarnLevel ? Color.FromArgb(245, 166, 35) : Color.FromArgb(48, 164, 108);

    private static bool TryPercent(JsonElement window, out double percent)
    {
        foreach (var name in PercentNames)
        {
            if (window.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
            {
                percent = value.GetDouble();
                return true;
            }
        }

        percent = 0;
        return false;
    }

    private static DateTimeOffset? ReadReset(JsonElement window)
    {
        foreach (var name in ResetNames)
        {
            if (window.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var reset))
            {
                return reset;
            }
        }

        return null;
    }
}
