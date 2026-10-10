namespace GuitoApi.Tests;

internal sealed class SsmFakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan elapsed) => _now += elapsed;
}
