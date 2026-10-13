using System.Text.Json.Nodes;
using MyHomeSecurity.Core.HomeAssistant;
using MyHomeSecurity.Core.Models;

namespace MyHomeSecurity.Core.Tests;

public sealed class HaBoostTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 18, 0, 0, TimeSpan.Zero);

    private static HaEntityState State(string id, string state, string attributes) =>
        new(id, state, JsonNode.Parse(attributes)!.AsObject(), null);

    private static HaEntityState Hive(string preset = "none", double target = 19) =>
        State("climate.heating", "heat",
            $$"""{"current_temperature": 18.2, "temperature": {{target}}, "min_temp": 5, "max_temp": 32, "preset_modes": ["none", "boost"], "preset_mode": "{{preset}}"}""");

    [Fact]
    public void ThermostatsWithABoostPresetCanBoost()
    {
        Assert.True(Hive().SupportsBoost);
        Assert.False(Hive().IsBoosting);
        Assert.True(Hive("boost").IsBoosting);

        Assert.False(State("climate.lounge", "heat", """{"preset_modes": ["eco", "comfort"]}""").SupportsBoost);
        Assert.False(State("climate.lounge", "heat", "{}").SupportsBoost);
        Assert.False(State("climate.heating", "unavailable", """{"preset_modes": ["boost"]}""").SupportsBoost);
        Assert.False(State("switch.boost", "on", """{"preset_modes": ["boost"]}""").SupportsBoost);
    }

    [Fact]
    public void ReadsNumbersAndNumericStrings()
    {
        var state = State("climate.x", "heat", """{"a": 21.5, "b": "19.5", "c": "warm"}""");
        Assert.Equal(21.5, state.Number("a"));
        Assert.Equal(19.5, state.Number("b"));
        Assert.Null(state.Number("c"));
        Assert.Null(state.Number("missing"));
    }

    [Fact]
    public void DefaultIsADegreeAboveTheRoomOrTheTarget()
    {
        // Room 18.2, target 19: a degree above the target.
        Assert.Equal(20, HaBoost.DefaultTemperature(Hive()));
        // Room warmer than the target: a degree above the room, rounded up to a half degree.
        Assert.Equal(22.5, HaBoost.DefaultTemperature(State("climate.x", "heat", """{"current_temperature": 21.3, "temperature": 15}""")));
        // Nothing reported: 21°.
        Assert.Equal(21, HaBoost.DefaultTemperature(State("climate.x", "heat", "{}")));
    }

    [Fact]
    public void ClampsToHalfDegreesWithinTheThermostatsRange()
    {
        var state = State("climate.x", "heat", """{"min_temp": 7, "max_temp": 25}""");
        Assert.Equal(21.5, HaBoost.Clamp(state, 21.4));
        Assert.Equal(25, HaBoost.Clamp(state, 30));
        Assert.Equal(7, HaBoost.Clamp(state, 2));
        Assert.Equal(32, HaBoost.Clamp(State("climate.x", "heat", "{}"), 40));
    }

    [Fact]
    public void BoostIsStartingUntilTheThermostatReportsItsTemperature()
    {
        var record = new HaBoostRecord(23, 60, Now.AddMinutes(59));

        Assert.False(HaBoost.IsConfirmed(Hive(), record, Now));
        // Hive says it's boosting before the new temperature comes through.
        Assert.False(HaBoost.IsConfirmed(Hive("boost", 19), record, Now));
        Assert.True(HaBoost.IsConfirmed(Hive("boost", 23), record, Now));
        // Believed anyway after a few minutes.
        Assert.True(HaBoost.IsConfirmed(Hive("boost", 19), record, Now.AddMinutes(6)));
        // Boosts started elsewhere, or without a known end, are taken as they are.
        Assert.True(HaBoost.IsConfirmed(Hive("boost", 19), null, Now));
        Assert.True(HaBoost.IsConfirmed(Hive("boost", 19), record with { EndsAt = null }, Now));
        Assert.True(HaBoost.IsConfirmed(Hive("boost", 19), record, Now.AddHours(2)));
    }

    [Theory]
    [InlineData(30, "30 min")]
    [InlineData(60, "1 h")]
    [InlineData(90, "1.5 h")]
    [InlineData(120, "2 h")]
    public void DescribesDurations(int minutes, string text) => Assert.Equal(text, HaBoost.MinutesText(minutes));
}
