using GuitoApi.Exceptions;

namespace GuitoApi.Services;

public static class TimeService
{
    private static readonly TimeZoneInfo Lisbon = TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon");

    public static DateTimeOffset InLisbon(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Lisbon);

    public static DateTimeOffset Midnight(DateTime date)
    {
        var local = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        if (Lisbon.IsInvalidTime(local) || Lisbon.IsAmbiguousTime(local))
            throw new ProblemException(400, "Legacy date has no unambiguous Lisbon midnight; supply an explicit-offset timestamp in bookingDate.");
        return new DateTimeOffset(local, Lisbon.GetUtcOffset(local));
    }
}
