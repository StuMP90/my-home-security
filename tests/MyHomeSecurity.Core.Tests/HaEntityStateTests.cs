using System.Globalization;
using System.Text.Json.Nodes;
using MyHomeSecurity.Core.HomeAssistant;

namespace MyHomeSecurity.Core.Tests;

public sealed class HaEntityStateTests
{
    private static HaEntityState State(string id, string state, string attributes) =>
        new(id, state, JsonNode.Parse(attributes)!.AsObject(), null);

    [Fact]
    public void HeatingTilesShowTheTemperatureAndMode()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var lounge = State("climate.lounge", "auto",
            """{"current_temperature": 19.5, "temperature": 21, "hvac_action": "heating"}""");

        Assert.Equal("19.5", lounge.GetTileValue("state"));
        Assert.Equal("°", lounge.GetTileUnits("state"));
        Assert.Equal("Auto · set to 21° · heating", lounge.GetTileDetail("state"));
        // A field chosen in the editor is still shown as it is.
        Assert.Equal("21", lounge.GetTileValue("attributes.temperature"));
        Assert.Equal("", lounge.GetTileDetail("attributes.temperature"));
    }

    [Fact]
    public void IdleHeatingAndOtherEntitiesShowTheirState()
    {
        var landing = State("climate.landing", "off", """{"current_temperature": 17, "hvac_action": "idle"}""");
        Assert.Equal("Off", landing.GetTileDetail("state"));

        // Hot water without a temperature, and an ordinary sensor.
        var water = State("water_heater.hot_water", "eco", "{}");
        Assert.Equal("eco", water.GetTileValue("state"));
        Assert.Equal("", water.GetTileDetail("state"));
        var sensor = State("sensor.hall", "20.1", """{"unit_of_measurement": "°C"}""");
        Assert.Equal("20.1", sensor.GetTileValue("state"));
        Assert.Equal("°C", sensor.GetTileUnits("state"));
    }

    [Fact]
    public void WeatherTilesShowTheTemperatureAndConditions()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var forecast = State("weather.forecast_home", "partlycloudy", """
            {"temperature": 12.4, "temperature_unit": "°C", "humidity": 82,
             "wind_speed": 14.2, "wind_speed_unit": "km/h", "wind_bearing": 225.0}
            """);

        Assert.Equal("12.4", forecast.GetTileValue("state"));
        Assert.Equal("°C", forecast.GetTileUnits("state"));
        Assert.Equal("Partly cloudy · 82% humidity · wind 14.2 km/h SW", forecast.GetTileDetail(null));
        Assert.Equal("82", forecast.GetTileValue("attributes.humidity"));

        // Without a temperature, the conditions in words.
        var bare = State("weather.home", "lightning-rainy", "{}");
        Assert.Equal("Thunder and rain", bare.GetTileValue("state"));
        Assert.Equal("", bare.GetTileDetail("state"));
    }
}
