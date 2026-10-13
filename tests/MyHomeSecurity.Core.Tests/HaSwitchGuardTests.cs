using MyHomeSecurity.Core.HomeAssistant;

namespace MyHomeSecurity.Core.Tests;

public sealed class HaSwitchGuardTests
{
    private DateTime _now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void HoldsTheNewStateUntilConfirmed()
    {
        var guard = new HaSwitchGuard(TimeSpan.FromSeconds(4), () => _now);
        guard.Set("switch.a", true);

        Assert.True(guard.Resolve("switch.a", false)); // HA still reports the old state
        Assert.True(guard.Resolve("switch.a", true));  // confirmed: guard released
        Assert.False(guard.Resolve("switch.a", false)); // later changes show straight away
    }

    [Fact]
    public void GivesUpAfterTheHoldTime()
    {
        var guard = new HaSwitchGuard(TimeSpan.FromSeconds(4), () => _now);
        guard.Set("switch.a", true);
        _now += TimeSpan.FromSeconds(5);
        Assert.False(guard.Resolve("switch.a", false));
    }

    [Fact]
    public void OtherEntitiesAndClearedOnesPassThrough()
    {
        var guard = new HaSwitchGuard(TimeSpan.FromSeconds(4), () => _now);
        guard.Set("switch.a", true);
        Assert.False(guard.Resolve("switch.b", false));
        guard.Clear("switch.a");
        Assert.False(guard.Resolve("switch.a", false));
    }
}
