namespace AdminConsole.Core.Tests.Fakes;

/// <summary>A clock the test advances by hand. Timers are not needed by the code under test, so only <see cref="GetUtcNow"/> is overridden.</summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public FakeTimeProvider(DateTimeOffset? start = null) => _now = start ?? new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
