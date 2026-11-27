using System.Globalization;

namespace Municode.Common.Services;

/// <summary>
/// One budget period in UTC: <c>[Start, End)</c>, its stable key (also the alert dedupe key) and the hours billed in each
/// calendar month it touches. A whole calendar month counts as <see cref="BudgetPricing.HoursPerMonth"/> hours; a partial
/// month counts its actual hours. Monthly caps and minimum charges apply per calendar-month segment.
/// </summary>
public sealed record BudgetPeriodWindow(string Type, string Key, DateTimeOffset Start, DateTimeOffset End,
    IReadOnlyList<TimeSpan> MonthSegments)
{
    public decimal Hours => (decimal)MonthSegments.Sum(x => x.TotalHours);
}

public static class BudgetPeriods
{
    public const string Month = "month";
    public const string Week = "week";
    public const string Quarter = "quarter";
    public static readonly IReadOnlyList<string> All = [Month, Week, Quarter];
    public const int MaxKeyLength = 8;

    public static bool IsValid(string? type) => type is Month or Week or Quarter;

    /// <summary>The calendar month (<c>2026-11</c>), ISO 8601 week starting Monday (<c>2026-W48</c>) or calendar quarter
    /// (<c>2026-Q4</c>) containing <paramref name="at"/>.</summary>
    public static BudgetPeriodWindow For(string type, DateTimeOffset at)
    {
        var day = at.UtcDateTime.Date;
        var month = new DateTimeOffset(day.Year, day.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var fullMonth = TimeSpan.FromHours((double)BudgetPricing.HoursPerMonth);
        switch (type)
        {
            case Week:
                var start = new DateTimeOffset(day.AddDays(-(((int)day.DayOfWeek + 6) % 7)), TimeSpan.Zero);
                var end = start.AddDays(7);
                var key = string.Create(CultureInfo.InvariantCulture,
                    $"{ISOWeek.GetYear(start.UtcDateTime):D4}-W{ISOWeek.GetWeekOfYear(start.UtcDateTime):D2}");
                var boundary = new DateTimeOffset(end.Year, end.Month, 1, 0, 0, 0, TimeSpan.Zero);
                IReadOnlyList<TimeSpan> segments = boundary > start && boundary < end ? [boundary - start, end - boundary] : [end - start];
                return new(Week, key, start, end, segments);
            case Quarter:
                var first = month.AddMonths(-((day.Month - 1) % 3));
                return new(Quarter, string.Create(CultureInfo.InvariantCulture, $"{first.Year:D4}-Q{(first.Month - 1) / 3 + 1}"),
                    first, first.AddMonths(3), [fullMonth, fullMonth, fullMonth]);
            case Month:
                return new(Month, month.ToString("yyyy-MM", CultureInfo.InvariantCulture), month, month.AddMonths(1), [fullMonth]);
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown budget period.");
        }
    }

    /// <summary>Keys of the current and previous period of every type, for listing recent alert deliveries.</summary>
    public static IReadOnlyList<string> RecentKeys(DateTimeOffset at) =>
        All.SelectMany(type =>
        {
            var current = For(type, at);
            return new[] { current.Key, For(type, current.Start.AddTicks(-1)).Key };
        }).Distinct(StringComparer.Ordinal).ToList();

    public static string Describe(BudgetPeriodWindow window)
    {
        var name = window.Type switch { Week => "ISO week", Quarter => "calendar quarter", _ => "calendar month" };
        return $"{name} {window.Key} ({Date(window.Start)} to {Date(window.End.AddDays(-1))}, UTC)";
    }

    private static string Date(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
