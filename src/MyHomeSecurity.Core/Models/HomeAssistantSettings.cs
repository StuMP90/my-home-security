namespace MyHomeSecurity.Core.Models;

public sealed class HomeAssistantSettings
{
    /// <summary>e.g. "http://homeassistant.local:8123". Empty = Home Assistant not used.</summary>
    public string ServerUrl { get; set; } = "";

    /// <summary>Long-lived access token (Home Assistant: Profile → Security).</summary>
    public string Token { get; set; } = "";

    /// <summary>Seconds between refreshes of the tiles.</summary>
    public double PollSeconds { get; set; } = 2;

    public List<HaTileConfig> Tiles { get; set; } = new();

    /// <summary>Entity ID → the last boost asked for on that thermostat.</summary>
    public Dictionary<string, HaBoostRecord> Boosts { get; set; } = new();

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ServerUrl) && !string.IsNullOrWhiteSpace(Token);

    public const double MinimumPollSeconds = 0.5;
}

public enum HaTileKind
{
    /// <summary>Chosen from the entity's domain: switches for switch/light/input_boolean/fan, values otherwise.</summary>
    Auto,
    Value,
    Switch,
}

public enum HaTileSize
{
    Small,
    Large,
}

public sealed class HaTileConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EntityId { get; set; } = "";

    /// <summary>
    /// What to show: "state" (default), "attributes.temperature", or several joined with commas
    /// ("attributes.message,attributes.start_time"), shown one after another.
    /// </summary>
    public string Field { get; set; } = "state";

    public string Name { get; set; } = "";
    public string Units { get; set; } = "";
    public HaTileKind Kind { get; set; } = HaTileKind.Auto;
    public HaTileSize Size { get; set; } = HaTileSize.Small;

    public string Domain => EntityId.Contains('.') ? EntityId[..EntityId.IndexOf('.')] : "";
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? EntityId : Name;

    public static readonly IReadOnlySet<string> SwitchDomains =
        new HashSet<string> { "switch", "light", "input_boolean", "fan", "automation", "siren" };

    public bool IsSwitch => Kind switch
    {
        HaTileKind.Switch => true,
        HaTileKind.Value => false,
        _ => SwitchDomains.Contains(Domain) && (string.IsNullOrWhiteSpace(Field) || Field == "state"),
    };

    public HaTileConfig Clone() => (HaTileConfig)MemberwiseClone();
}

/// <summary>
/// The last boost asked for on a thermostat: the starting point for the next one, and when this
/// one ends (known only when Hive's boost service was used; Home Assistant doesn't report it).
/// </summary>
public sealed record HaBoostRecord(double Temperature, int Minutes, DateTimeOffset? EndsAt);
