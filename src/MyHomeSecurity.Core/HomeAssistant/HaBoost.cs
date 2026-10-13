using MyHomeSecurity.Core.Models;

namespace MyHomeSecurity.Core.HomeAssistant;

/// <summary>Thermostat boosts: the choices offered, and whether a boost has really started.</summary>
public static class HaBoost
{
    /// <summary>Home Assistant's boost preset.</summary>
    public const string Preset = "boost";

    public static readonly IReadOnlyList<int> MinuteChoices = [30, 60, 120];
    public const int DefaultMinutes = 60;

    /// <summary>The boost temperature goes up and down in half degrees.</summary>
    public const double Step = 0.5;

    /// <summary>How long to wait for the thermostat to report the boost's temperature before believing it anyway.</summary>
    public static readonly TimeSpan ConfirmTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Hive reports a boost through its cloud, which can take a while.</summary>
    public static readonly TimeSpan PendingFor = TimeSpan.FromSeconds(20);

    private const double DefaultMin = 5, DefaultMax = 32;

    /// <summary><paramref name="temperature"/> to the nearest half degree, within the thermostat's range.</summary>
    public static double Clamp(HaEntityState state, double temperature)
    {
        var min = state.Number("min_temp") ?? DefaultMin;
        var max = Math.Max(min, state.Number("max_temp") ?? DefaultMax);
        return Math.Clamp(Math.Round(temperature / Step) * Step, min, max);
    }

    /// <summary>A degree above the room or the set temperature, whichever is higher, rounded up to a half degree.</summary>
    public static double DefaultTemperature(HaEntityState state)
    {
        var start = Math.Max(state.Number("current_temperature") ?? 20, state.Number("temperature") ?? 0);
        return Clamp(state, Math.Ceiling((start + 1) / Step) * Step);
    }

    /// <summary>
    /// Whether the boost the thermostat reports is the one asked for. Hive says it's boosting
    /// before its cloud has passed on the new temperature, so until the reported set temperature
    /// matches (or a few minutes pass) the boost is shown as starting.
    /// </summary>
    public static bool IsConfirmed(HaEntityState state, HaBoostRecord? record, DateTimeOffset now)
    {
        if (!state.IsBoosting)
            return false;
        if (record?.EndsAt is not { } ends || ends <= now)
            return true;
        if (state.Number("temperature") is { } target && Math.Abs(target - record.Temperature) <= 0.25)
            return true;
        return now - (ends - TimeSpan.FromMinutes(record.Minutes)) >= ConfirmTimeout;
    }

    /// <summary>"30 min", "1 h", "2 h".</summary>
    public static string MinutesText(int minutes) =>
        minutes < 60 ? $"{minutes} min" : $"{minutes / 60.0:0.#} h";
}
