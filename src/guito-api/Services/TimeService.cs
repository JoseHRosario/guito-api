using GuitoApi.Exceptions;

namespace GuitoApi.Services;

public static class TimeService
{
    private static readonly TimeZoneInfo Lisbon = TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon");

    public static DateTimeOffset InLisbon(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Lisbon);

    public static DateTimeOffset CalendarMonthStart(DateTime month)
    {
        var local = new DateTime(month.Year, month.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        // Lisbon's midnight gaps end on a minute boundary; the month still starts
        // at its first existing wall-clock time, unlike a legacy date-only input.
        while (Lisbon.IsInvalidTime(local)) local = local.AddMinutes(1);
        var offset = Lisbon.IsAmbiguousTime(local)
            ? Lisbon.GetAmbiguousTimeOffsets(local).Max() : Lisbon.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    public static DateTimeOffset Midnight(DateTime date)
    {
        var local = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        if (Lisbon.IsInvalidTime(local) || Lisbon.IsAmbiguousTime(local))
            throw new ProblemException(400, "Legacy date has no unambiguous Lisbon midnight; supply an explicit-offset timestamp in bookingDate.");
        return new DateTimeOffset(local, Lisbon.GetUtcOffset(local));
    }
}
