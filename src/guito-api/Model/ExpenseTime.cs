using System.Globalization;
using System.Text.RegularExpressions;
using GuitoApi.Exceptions;

namespace GuitoApi.Model;

public static class ExpenseTime
{
    private static readonly TimeZoneInfo Lisbon = TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon");

    public static DateTimeOffset InLisbon(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Lisbon);

    public static DateTimeOffset Resolve(string date)
    {
        if (string.IsNullOrWhiteSpace(date))
            throw new ProblemException(400, "date is required.");
        if (DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return Midnight(day);
        if (!Regex.IsMatch(date, @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,6})?(Z|[+-][0-9]{2}:[0-9]{2})\z")
            || !DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant))
            throw new ProblemException(400, "date must be yyyy-MM-dd or ISO8601 with an explicit offset and at most six fractional digits.");
        return InLisbon(instant);
    }

    public static DateTimeOffset Midnight(DateTime date)
    {
        var local = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        if (Lisbon.IsInvalidTime(local) || Lisbon.IsAmbiguousTime(local))
            throw new ProblemException(400, "Legacy date has no unambiguous Lisbon midnight; supply an explicit-offset timestamp in date.");
        return new DateTimeOffset(local, Lisbon.GetUtcOffset(local));
    }
}
