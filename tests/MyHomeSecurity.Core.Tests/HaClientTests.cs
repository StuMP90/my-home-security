using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MyHomeSecurity.Core.HomeAssistant;

namespace MyHomeSecurity.Core.Tests;

public sealed class HaClientTests
{
    [Theory]
    [InlineData("homeassistant.local:8123", "http://homeassistant.local:8123/")]
    [InlineData("http://192.168.1.10:8123/", "http://192.168.1.10:8123/")]
    [InlineData("https://ha.example.com/api/states", "https://ha.example.com/")]
    [InlineData("https://example.com/ha/api/", "https://example.com/ha/")]
    public void NormalisesServerUrls(string input, string expected) =>
        Assert.Equal(expected, HaClient.NormaliseServerUrl(input)?.ToString());

    [Theory]
    [InlineData("")]
    [InlineData("ftp://example.com")]
    [InlineData("http://")]
    public void RejectsUnusableUrls(string input) => Assert.Null(HaClient.NormaliseServerUrl(input));

    [Theory]
    [InlineData("http://192.168.1.10:8123", false)]
    [InlineData("http://10.0.0.5:8123", false)]
    [InlineData("http://172.20.0.5", false)]
    [InlineData("http://localhost:8123", false)]
    [InlineData("http://homeassistant.local:8123", false)]
    [InlineData("https://ha.example.com", false)]
    [InlineData("http://ha.example.com", true)]
    [InlineData("http://8.8.8.8:8123", true)]
    public void FlagsTokensThatWouldCrossTheInternetUnencrypted(string url, bool insecure) =>
        Assert.Equal(insecure, HaClient.IsInsecure(url));

    [Fact]
    public async Task SendsTheTokenAndParsesStates()
    {
        var handler = new FakeHandler(request =>
        {
            Assert.Equal("Bearer secret-token", request.Headers.Authorization?.ToString());
            Assert.Equal("/api/states/switch.porch", request.RequestUri!.AbsolutePath);
            return Json("""{"entity_id":"switch.porch","state":"on","attributes":{"friendly_name":"Porch light"},"last_changed":"2026-01-02T03:04:05+00:00"}""");
        });
        using var client = new HaClient("homeassistant.local:8123", "secret-token", handler: handler);

        var state = await client.GetStateAsync("switch.porch");

        Assert.Equal("on", state.State);
        Assert.Equal("Porch light", state.FriendlyName);
    }

    [Fact]
    public async Task SwitchesUseTheEntitysDomain()
    {
        HttpRequestMessage? sent = null;
        string? body = null;
        var handler = new FakeHandler(request =>
        {
            sent = request;
            body = request.Content!.ReadAsStringAsync().Result;
            return Json("[]");
        });
        using var client = new HaClient("http://192.168.1.10:8123", "t", handler: handler);

        await client.SetOnAsync("light.kitchen", false);

        Assert.Equal(HttpMethod.Post, sent!.Method);
        Assert.Equal("/api/services/light/turn_off", sent.RequestUri!.AbsolutePath);
        Assert.Equal("light.kitchen", JsonNode.Parse(body!)!["entity_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnauthorisedIsReportedAsAnHaException()
    {
        using var client = new HaClient("http://192.168.1.10:8123", "bad",
            handler: new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        await Assert.ThrowsAsync<HaException>(() => client.GetStateAsync("switch.x"));
    }

    [Fact]
    public async Task BoostsHiveHeatingForAWhile()
    {
        var calls = new List<(string Path, JsonNode? Body)>();
        var handler = new FakeHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            calls.Add((path, request.Content is null ? null : JsonNode.Parse(request.Content.ReadAsStringAsync().Result)));
            return path == "/api/services" && request.Method == HttpMethod.Get
                ? Json("""[{"domain":"climate","services":{"set_preset_mode":{}}},{"domain":"hive","services":{"boost_heating_on":{},"boost_heating_off":{}}}]""")
                : Json("[]");
        });
        using var client = new HaClient("http://192.168.1.10:8123", "t", handler: handler);

        Assert.True(await client.BoostAsync("climate.heating", 22.5, 60));
        await client.CancelBoostAsync("climate.heating");

        // The services are only asked for once.
        Assert.Equal(["/api/services", "/api/services/hive/boost_heating_on", "/api/services/hive/boost_heating_off"],
            calls.Select(c => c.Path));
        var on = calls[1].Body!;
        Assert.Equal("climate.heating", on["entity_id"]!.GetValue<string>());
        Assert.Equal(22.5, on["temperature"]!.GetValue<double>());
        Assert.Equal("01:00:00", on["time_period"]!.GetValue<string>());
        Assert.Equal("climate.heating", calls[2].Body!["entity_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task OtherThermostatsBoostWithTheirPreset()
    {
        var calls = new List<(string Path, JsonNode? Body)>();
        var handler = new FakeHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            calls.Add((path, request.Content is null ? null : JsonNode.Parse(request.Content.ReadAsStringAsync().Result)));
            return path == "/api/services" ? Json("""[{"domain":"climate","services":{"set_preset_mode":{}}}]""") : Json("[]");
        });
        using var client = new HaClient("http://192.168.1.10:8123", "t", handler: handler);

        Assert.False(await client.BoostAsync("climate.lounge", 22, 30));
        await client.CancelBoostAsync("climate.lounge");

        Assert.Equal(["/api/services", "/api/services/climate/set_preset_mode", "/api/services/climate/set_preset_mode"],
            calls.Select(c => c.Path));
        Assert.Equal("boost", calls[1].Body!["preset_mode"]!.GetValue<string>());
        Assert.Equal("none", calls[2].Body!["preset_mode"]!.GetValue<string>());
        Assert.Equal("climate.lounge", calls[2].Body!["entity_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task ReadsTheLastStepFromHistory()
    {
        var handler = new FakeHandler(request =>
        {
            Assert.StartsWith("/api/history/period/", request.RequestUri!.AbsolutePath);
            Assert.Contains("filter_entity_id=sensor.purifier_pm2_5", request.RequestUri.Query);
            return Json("""[[{"entity_id":"sensor.purifier_pm2_5","state":"225922244.0","attributes":{}},{"state":"225923234.0","last_changed":"x"},{"state":"225924223.0","last_changed":"y"}]]""");
        });
        using var client = new HaClient("homeassistant.local:8123", "secret-token", handler: handler);

        Assert.Equal(989, await client.GetLastStepAsync("sensor.purifier_pm2_5", DateTimeOffset.UtcNow));
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
