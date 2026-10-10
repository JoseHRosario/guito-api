using System.Globalization;
using System.Text.RegularExpressions;
using GuitoApi.Exceptions;

namespace GuitoApi.Model;

public static class ExpenseTime
{
    private static readonly TimeZoneInfo Lisbon = TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon");

    public static DateTimeOffset InLisbon(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Lisbon);

    public static DateTimeOffset Resolve(DateTime date, string? occurredAt)
    {
        if (occurredAt is null)
            return Midnight(date);
        if (!Regex.IsMatch(occurredAt, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$")
            || !DateTimeOffset.TryParse(occurredAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant))
            throw new ProblemException(400, "occurredAt must be ISO8601 with an explicit offset.");
        if (instant.Ticks % 10 != 0)
            throw new ProblemException(400, "occurredAt precision must not exceed microseconds.");
        var lisbonInstant = InLisbon(instant);
        if (lisbonInstant.Date != date.Date)
            throw new ProblemException(400, "occurredAt must match date in Europe/Lisbon.");
        return lisbonInstant;
    }

    public static DateTimeOffset Midnight(DateTime date)
    {
        var local = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        if (Lisbon.IsInvalidTime(local) || Lisbon.IsAmbiguousTime(local))
            throw new ProblemException(400, "Legacy date has no unambiguous Lisbon midnight; supply occurredAt.");
        return new DateTimeOffset(local, Lisbon.GetUtcOffset(local));
    }
}
