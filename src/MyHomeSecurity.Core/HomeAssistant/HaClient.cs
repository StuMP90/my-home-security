using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyHomeSecurity.Core.Models;

namespace MyHomeSecurity.Core.HomeAssistant;

public sealed record HaEntityState(string EntityId, string State, JsonObject Attributes, DateTimeOffset? LastChanged)
{
    /// <summary>For a sensor Home Assistant wrongly adds up, its latest reading (see <see cref="HaRunningTotal"/>).</summary>
    public double? Step { get; init; }

    public string FriendlyName =>
        Attributes["friendly_name"] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : EntityId;

    public string? UnitOfMeasurement =>
        Attributes["unit_of_measurement"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public string Domain => EntityId.Contains('.') ? EntityId[..EntityId.IndexOf('.')] : "";

    public bool IsOn => State.Equals("on", StringComparison.OrdinalIgnoreCase)
        || State.Equals("heat", StringComparison.OrdinalIgnoreCase)
        || State.Equals("open", StringComparison.OrdinalIgnoreCase);

    public bool IsUnavailable => State is "unavailable" or "unknown";

    /// <summary>
    /// The value a tile shows for <paramref name="field"/>: "state", "attributes.x" or "attributes.x.y";
    /// several comma-separated fields are joined with " · ".
    /// </summary>
    public string GetField(string? field)
    {
        if (string.IsNullOrWhiteSpace(field))
            return State;
        var parts = field.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(GetSingleField)
            .Where(s => s.Length > 0);
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// True for a heating or cooling entity (thermostat, radiator valve, Hive) whose tile shows the
    /// default field: its state is the mode ("auto", "heat"), so the tile shows the room temperature.
    /// </summary>
    private bool IsDefaultField(string? field) => string.IsNullOrWhiteSpace(field) || field == "state";

    private bool ShowsTemperature(string? field) =>
        IsDefaultField(field)
        && Domain is "climate" or "water_heater"
        && GetSingleField("current_temperature").Length > 0;

    private bool ShowsWeather(string? field) =>
        IsDefaultField(field) && Domain == "weather" && GetSingleField("temperature").Length > 0;

    /// <summary>
    /// The value a tile shows: <see cref="GetField"/>, but the room temperature for heating entities
    /// and the outside temperature for weather entities. A running total shows its latest step.
    /// </summary>
    public string GetTileValue(string? field) =>
        HaRunningTotal.Applies(this, field) ? Step is { } step ? HaRunningTotal.Format(step) : ""
        : ShowsTemperature(field) ? GetSingleField("current_temperature")
        : ShowsWeather(field) ? GetSingleField("temperature")
        : IsDefaultField(field) && Domain == "weather" ? WeatherCondition(State)
        : GetField(field);

    /// <summary>The units after a tile's value, unless the tile sets its own.</summary>
    public string GetTileUnits(string? field) =>
        ShowsTemperature(field) ? "°"
        : ShowsWeather(field) ? GetSingleField("temperature_unit") is { Length: > 0 } unit ? unit : "°"
        : IsDefaultField(field) ? UnitOfMeasurement ?? ""
        : "";

    /// <summary>
    /// The line under a tile's value. For heating, the mode, set temperature and whether it's
    /// heating, e.g. "Auto · set to 21° · heating". For weather, the conditions, humidity and
    /// wind, e.g. "Partly cloudy · 82% humidity · wind 14 km/h SW".
    /// </summary>
    public string GetTileDetail(string? field)
    {
        var parts = new List<string>();
        if (ShowsTemperature(field))
        {
            if (State.Length > 0)
                parts.Add(char.ToUpperInvariant(State[0]) + State[1..].Replace('_', ' '));
            if (GetSingleField("temperature") is { Length: > 0 } target)
                parts.Add($"set to {target}°");
            if (GetSingleField("hvac_action") is { Length: > 0 } action && action is not ("idle" or "off"))
                parts.Add(action.Replace('_', ' '));
        }
        else if (ShowsWeather(field))
        {
            parts.Add(WeatherCondition(State));
            if (GetSingleField("humidity") is { Length: > 0 } humidity)
                parts.Add($"{humidity}% humidity");
            if (GetSingleField("wind_speed") is { Length: > 0 } speed)
            {
                var wind = $"wind {speed}";
                if (GetSingleField("wind_speed_unit") is { Length: > 0 } unit)
                    wind += " " + unit;
                if (Compass(GetSingleField("wind_bearing")) is { Length: > 0 } direction)
                    wind += " " + direction;
                parts.Add(wind);
            }
        }
        return string.Join(" · ", parts.Where(p => p.Length > 0));
    }

    /// <summary>Home Assistant's weather conditions in words, e.g. "partlycloudy" → "Partly cloudy".</summary>
    public static string WeatherCondition(string condition) => condition switch
    {
        "clear-night" => "Clear",
        "cloudy" => "Cloudy",
        "exceptional" => "Exceptional",
        "fog" => "Fog",
        "hail" => "Hail",
        "lightning" => "Thunder",
        "lightning-rainy" => "Thunder and rain",
        "partlycloudy" => "Partly cloudy",
        "pouring" => "Heavy rain",
        "rainy" => "Rain",
        "snowy" => "Snow",
        "snowy-rainy" => "Sleet",
        "sunny" => "Sunny",
        "windy" => "Windy",
        "windy-variant" => "Windy and cloudy",
        "" or "unknown" or "unavailable" => condition,
        _ => char.ToUpperInvariant(condition[0]) + condition[1..].Replace('-', ' ').Replace('_', ' '),
    };

    // The bearing is usually degrees, but some integrations give a compass point already.
    private static string Compass(string bearing)
    {
        if (!double.TryParse(bearing, NumberStyles.Float, CultureInfo.CurrentCulture, out var degrees))
            return bearing;
        string[] points = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
        return points[(int)Math.Round(((degrees % 360) + 360) % 360 / 45) % 8];
    }

    private string GetSingleField(string field)
    {
        if (field is "state")
            return State;
        if (field is "last_changed")
            return LastChanged?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "";
        var path = field.StartsWith("attributes.", StringComparison.Ordinal) ? field["attributes.".Length..] : field;
        JsonNode? node = Attributes;
        foreach (var key in path.Split('.'))
        {
            node = node switch
            {
                JsonObject o => o[key],
                JsonArray a when int.TryParse(key, out var i) && i >= 0 && i < a.Count => a[i],
                _ => null,
            };
            if (node is null)
                return "";
        }
        return node switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonValue v when v.TryGetValue<double>(out var d) => d.ToString("0.##", CultureInfo.CurrentCulture),
            JsonValue v when v.TryGetValue<bool>(out var b) => b ? "Yes" : "No",
            JsonArray a => string.Join(", ", a.Select(x => x?.ToString())),
            _ => node.ToJsonString(),
        };
    }

    /// <summary>Light brightness as a percentage (0-100), if the light reports it.</summary>
    public int? BrightnessPercent =>
        Attributes["brightness"] is JsonValue v && v.TryGetValue<double>(out var b) ? (int)Math.Round(b * 100 / 255) : null;

    public bool SupportsBrightness =>
        Domain == "light" && Attributes["supported_color_modes"] is JsonArray modes
            && modes.Any(m => m?.GetValue<string>() is { } s && s != "onoff");

    /// <summary>A numeric attribute, e.g. "current_temperature"; null if missing or not a number.</summary>
    public double? Number(string attribute) =>
        Attributes[attribute] is JsonValue v && v.TryGetValue<double>(out var d) ? d
        : Attributes[attribute] is JsonValue s && s.TryGetValue<string>(out var text)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed
        : null;

    /// <summary>A thermostat with a boost preset (Hive, and others that offer one).</summary>
    public bool SupportsBoost =>
        Domain == "climate" && !IsUnavailable && Attributes["preset_modes"] is JsonArray modes
            && modes.Any(m => m is JsonValue v && v.TryGetValue<string>(out var s) && s == HaBoost.Preset);

    public bool IsBoosting =>
        Attributes["preset_mode"] is JsonValue v && v.TryGetValue<string>(out var s) && s == HaBoost.Preset;
}

public sealed record HaServiceInfo(string Domain, string Service);

public sealed class HaException(string message, Exception? inner = null, HttpStatusCode? status = null) : Exception(message, inner)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// Home Assistant REST API client. The token only ever goes to the configured server, in the
/// Authorization header.
/// </summary>
public sealed class HaClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _base;

    public HaClient(string serverUrl, string token, TimeSpan? timeout = null, HttpMessageHandler? handler = null)
    {
        _base = NormaliseServerUrl(serverUrl) ?? throw new HaException("The server address isn't a valid http:// or https:// address.");
        _http = new HttpClient(handler ?? new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false }, disposeHandler: true)
        {
            BaseAddress = _base,
            Timeout = timeout ?? TimeSpan.FromSeconds(10),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>"homeassistant.local:8123" → "http://homeassistant.local:8123/"; null if unusable.</summary>
    public static Uri? NormaliseServerUrl(string? url)
    {
        url = url?.Trim();
        if (string.IsNullOrEmpty(url))
            return null;
        if (!url.Contains("://", StringComparison.Ordinal))
            url = "http://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return null;
        // Drop any path such as "/api/states/" that people copy from the docs.
        var path = uri.AbsolutePath;
        var api = path.IndexOf("/api", StringComparison.OrdinalIgnoreCase);
        if (api >= 0)
            path = path[..api];
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + path.TrimEnd('/') + "/");
    }

    /// <summary>
    /// True when a token would cross the network unencrypted to somewhere that isn't obviously on
    /// the home network (http:// to a public address or name).
    /// </summary>
    public static bool IsInsecure(string? url)
    {
        var uri = NormaliseServerUrl(url);
        if (uri is null || uri.Scheme == "https")
            return false;
        if (uri.IsLoopback)
            return false;
        if (IPAddress.TryParse(uri.Host, out var ip))
        {
            var b = ip.GetAddressBytes();
            return !(b.Length == 4 && (b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254)
                || ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal);
        }
        var host = uri.Host.ToLowerInvariant();
        return !(host.EndsWith(".local", StringComparison.Ordinal) || host.EndsWith(".lan", StringComparison.Ordinal)
            || host.EndsWith(".home", StringComparison.Ordinal) || host.EndsWith(".home.arpa", StringComparison.Ordinal)
            || host.EndsWith(".internal", StringComparison.Ordinal) || !host.Contains('.'));
    }

    /// <summary>Checks the server and token; returns the Home Assistant message ("API running.").</summary>
    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        var json = await GetJsonAsync("api/", ct);
        return json is JsonObject o && o["message"] is JsonValue v && v.TryGetValue<string>(out var m) ? m : "Connected.";
    }

    public async Task<HaEntityState> GetStateAsync(string entityId, CancellationToken ct = default)
    {
        var json = await GetJsonAsync("api/states/" + Uri.EscapeDataString(entityId.Trim()), ct);
        // Some integrations (calendars, via templates) return an array; take the first.
        if (json is JsonArray { Count: > 0 } array)
            json = array[0];
        return ParseState(json as JsonObject ?? throw new HaException($"Unexpected reply for {entityId}."));
    }

    public async Task<List<HaEntityState>> GetStatesAsync(CancellationToken ct = default)
    {
        var json = await GetJsonAsync("api/states", ct);
        return json is JsonArray array
            ? [.. array.OfType<JsonObject>().Select(ParseState).OrderBy(s => s.EntityId, StringComparer.Ordinal)]
            : [];
    }

    /// <summary>
    /// The step between the entity's last two recorded states, up to <paramref name="changed"/>
    /// (see <see cref="HaRunningTotal"/>). Null if the history doesn't have two.
    /// </summary>
    public async Task<double?> GetLastStepAsync(string entityId, DateTimeOffset changed, CancellationToken ct = default)
    {
        static string Time(DateTimeOffset t) => Uri.EscapeDataString(t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
        var json = await GetJsonAsync(
            $"api/history/period/{Time(changed - HaRunningTotal.Lookback)}?end_time={Time(changed.AddSeconds(1))}"
            + $"&filter_entity_id={Uri.EscapeDataString(entityId.Trim())}&minimal_response&no_attributes", ct);
        if (json is not JsonArray { Count: > 0 } outer || outer[0] is not JsonArray series)
            return null;
        var states = series.OfType<JsonObject>()
            .Select(o => o["state"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "")
            .ToList();
        return HaRunningTotal.LastStep(states);
    }

    public async Task<List<HaServiceInfo>> GetServicesAsync(CancellationToken ct = default)
    {
        var json = await GetJsonAsync("api/services", ct);
        var result = new List<HaServiceInfo>();
        if (json is not JsonArray array)
            return result;
        foreach (var domain in array.OfType<JsonObject>())
        {
            var name = domain["domain"]?.GetValue<string>();
            if (name is null || domain["services"] is not JsonObject services)
                continue;
            foreach (var service in services)
                result.Add(new HaServiceInfo(name, service.Key));
        }
        return result;
    }

    public async Task CallServiceAsync(string domain, string service, JsonObject data, CancellationToken ct = default)
    {
        using var content = new StringContent(data.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await SendAsync(() => _http.PostAsync($"api/services/{Uri.EscapeDataString(domain)}/{Uri.EscapeDataString(service)}", content, ct), ct);
    }

    /// <summary>Turns a switch-like entity on or off (switch, light, fan, input_boolean, automation, siren...).</summary>
    public Task SetOnAsync(string entityId, bool on, CancellationToken ct = default)
    {
        var domain = entityId.Contains('.') ? entityId[..entityId.IndexOf('.')] : "homeassistant";
        if (!HaTileConfig.SwitchDomains.Contains(domain))
            domain = "homeassistant";
        return CallServiceAsync(domain, on ? "turn_on" : "turn_off", new JsonObject { ["entity_id"] = entityId }, ct);
    }

    public Task SetBrightnessAsync(string entityId, int percent, CancellationToken ct = default) =>
        CallServiceAsync("light", "turn_on", new JsonObject
        {
            ["entity_id"] = entityId,
            ["brightness_pct"] = Math.Clamp(percent, 1, 100),
        }, ct);

    /// <summary>
    /// Boosts a thermostat to <paramref name="temperature"/> for <paramref name="minutes"/>, after
    /// which its schedule carries on. Uses Hive's boost service where the server has it. Otherwise
    /// it sets the standard boost preset, which can't take a temperature or a duration: the
    /// thermostat uses its own. Returns true if Hive's service was used, so the end time is known.
    /// </summary>
    public async Task<bool> BoostAsync(string entityId, double temperature, int minutes, CancellationToken ct = default)
    {
        if (await HasHiveBoostAsync(ct))
        {
            await CallServiceAsync("hive", "boost_heating_on", new JsonObject
            {
                ["entity_id"] = entityId,
                ["time_period"] = TimeSpan.FromMinutes(minutes).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
                ["temperature"] = temperature,
            }, ct);
            return true;
        }
        await SetPresetModeAsync(entityId, HaBoost.Preset, ct);
        return false;
    }

    /// <summary>Ends a boost; the thermostat goes back to its schedule.</summary>
    public async Task CancelBoostAsync(string entityId, CancellationToken ct = default)
    {
        if (await HasHiveBoostAsync(ct))
            await CallServiceAsync("hive", "boost_heating_off", new JsonObject { ["entity_id"] = entityId }, ct);
        else
            await SetPresetModeAsync(entityId, "none", ct);
    }

    public Task SetPresetModeAsync(string entityId, string presetMode, CancellationToken ct = default) =>
        CallServiceAsync("climate", "set_preset_mode", new JsonObject
        {
            ["entity_id"] = entityId,
            ["preset_mode"] = presetMode,
        }, ct);

    // Whether the server has Hive's boost services, which take a temperature and duration. Asked
    // once per client (a client is one server and token).
    private bool? _hiveBoost;

    private async Task<bool> HasHiveBoostAsync(CancellationToken ct)
    {
        _hiveBoost ??= (await GetServicesAsync(ct)).Contains(new HaServiceInfo("hive", "boost_heating_on"));
        return _hiveBoost.Value;
    }

    private async Task<JsonNode?> GetJsonAsync(string path, CancellationToken ct)
    {
        using var response = await SendAsync(() => _http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct), ct);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonNode.ParseAsync(stream, cancellationToken: ct);
        }
        catch (JsonException ex)
        {
            throw new HaException("The server's reply wasn't valid JSON — is this a Home Assistant address?", ex);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Func<Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await send();
        }
        catch (HttpRequestException ex)
        {
            throw new HaException($"Couldn't reach Home Assistant: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new HaException("Home Assistant didn't answer in time.", ex);
        }
        if (response.IsSuccessStatusCode)
            return response;
        var status = response.StatusCode;
        response.Dispose();
        throw status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new HaException("Home Assistant rejected the access token.", status: status),
            HttpStatusCode.NotFound => new HaException("Not found (check the entity ID or server address).", status: status),
            HttpStatusCode.BadRequest => new HaException("Home Assistant refused the request (400).", status: status),
            _ when (int)status is >= 300 and < 400 => new HaException("The server redirected the request; use the address Home Assistant shows in its settings (http:// or https://).", status: status),
            _ => new HaException($"Home Assistant answered {(int)status}.", status: status),
        };
    }

    internal static HaEntityState ParseState(JsonObject o)
    {
        var id = o["entity_id"]?.GetValue<string>() ?? "";
        var state = o["state"] is JsonValue sv && sv.TryGetValue<string>(out var s) ? s : o["state"]?.ToJsonString() ?? "";
        var attributes = o["attributes"] as JsonObject ?? new JsonObject();
        DateTimeOffset? changed = o["last_changed"] is JsonValue cv && cv.TryGetValue<string>(out var c)
            && DateTimeOffset.TryParse(c, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        // Detach so the record doesn't keep the whole parsed document alive.
        return new HaEntityState(id, state, (JsonObject)attributes.DeepClone(), changed);
    }

    public void Dispose() => _http.Dispose();
}
