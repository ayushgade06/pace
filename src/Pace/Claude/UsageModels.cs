using System.Globalization;
using System.Text.Json;

namespace Pace.Claude;

/// One entry of the `limits` array returned by /api/oauth/usage.
public sealed record UsageLimit(string Kind, double Percent, DateTimeOffset? ResetsAt, string? ScopeName)
{
    /// `weekly_scoped` is labelled with the scope's model name when present.
    public string Title => Kind switch
    {
        "session" => "Current session (5h)",
        "weekly_all" => "Weekly · all models",
        "weekly_opus" => "Weekly · Opus",
        "weekly_sonnet" => "Weekly · Sonnet",
        "weekly_scoped" when ScopeName is not null => $"Weekly · {ScopeName}",
        _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Kind.Replace('_', ' ')),
    };
}

/// One row of `seven_day_breakdown.rows` ("This week by surface").
public sealed record BreakdownRow(string Key, string DisplayName, double Percent);

public sealed record UsageSnapshot(IReadOnlyList<UsageLimit> Limits, IReadOnlyList<BreakdownRow> Breakdown);

public static class UsageParser
{
    /// Lenient: unknown/missing/null fields are skipped instead of failing the whole response.
    /// Entries without a kind or percent are dropped rather than shown as 0% (no invented numbers).
    public static UsageSnapshot Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var limits = new List<UsageLimit>();
        if (root.TryGetProperty("limits", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var l in arr.EnumerateArray())
            {
                var kind = Str(l, "kind");
                var pct = Num(l, "percent");
                if (kind is null || pct is null) continue;
                limits.Add(new UsageLimit(kind, pct.Value, Date(Str(l, "resets_at")), ScopeName(l)));
            }

        var rows = new List<BreakdownRow>();
        if (root.TryGetProperty("seven_day_breakdown", out var bd) && bd.ValueKind == JsonValueKind.Object
            && bd.TryGetProperty("rows", out var rs) && rs.ValueKind == JsonValueKind.Array)
            foreach (var r in rs.EnumerateArray())
            {
                var pct = Num(r, "percent");
                var name = Str(r, "display_name") ?? Str(r, "key");
                // 0% surfaces are hidden.
                if (pct is > 0 && name is not null) rows.Add(new BreakdownRow(Str(r, "key") ?? name, name, pct.Value));
            }
        return new UsageSnapshot(limits, rows);
    }

    static string? ScopeName(JsonElement l) =>
        l.TryGetProperty("scope", out var s) && s.ValueKind == JsonValueKind.Object
            ? (s.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.Object ? Str(m, "display_name") : null)
              ?? (s.TryGetProperty("surface", out var f) && f.ValueKind == JsonValueKind.Object ? Str(f, "display_name") : null)
            : null;

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    static DateTimeOffset? Date(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : null;
}
