using System.Text.Json.Nodes;
using MyHomeSecurity.Core.HomeAssistant;

namespace MyHomeSecurity.Core.Tests;

public sealed class HaRunningTotalTests
{
    private static HaEntityState Sensor(string state, string stateClass = "measurement", string id = "sensor.purifier_pm2_5") =>
        new(id, state, new JsonObject { ["state_class"] = stateClass, ["unit_of_measurement"] = "µg/m³" }, DateTimeOffset.UtcNow);

    [Theory]
    [InlineData("225924223.0", "measurement", "sensor.purifier_pm2_5", null, true)]
    [InlineData("989", "measurement", "sensor.purifier_pm2_5", null, false)]
    [InlineData("225924223.0", "total_increasing", "sensor.energy", null, false)]
    [InlineData("225924223.0", "measurement", "sensor.purifier_pm2_5", "attributes.x", false)]
    [InlineData("unavailable", "measurement", "sensor.purifier_pm2_5", null, false)]
    public void SpotsMeasurementsThatAreRunningTotals(string state, string stateClass, string id, string? field, bool expected) =>
        Assert.Equal(expected, HaRunningTotal.Applies(Sensor(state, stateClass, id), field));

    [Fact]
    public void TheReadingIsTheLastStep()
    {
        Assert.Equal(989, HaRunningTotal.LastStep(["225922244.0", "225923234.0", "unavailable", "225924223.0"]));
        Assert.Null(HaRunningTotal.LastStep(["225924223.0"]));
        Assert.Null(HaRunningTotal.LastStep(["225924223.0", "100"]));
    }

    [Fact]
    public void TheTileShowsTheStepNotTheTotal()
    {
        Assert.Equal("989", (Sensor("225924223.0") with { Step = 989 }).GetTileValue(null));
        Assert.Equal("", Sensor("225924223.0").GetTileValue(null));
        Assert.Equal("µg/m³", Sensor("225924223.0").GetTileUnits(null));
    }
}
