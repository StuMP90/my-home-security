using System.Globalization;

namespace MyHomeSecurity.Core.HomeAssistant;

/// <summary>
/// Sensors that Home Assistant wrongly adds up. Some Tuya devices (an air purifier's PM2.5, for
/// one) report a reading that the integration treats as an amount to add, so the state is a running
/// total of every reading. The real reading is the step between the last two states.
/// </summary>
public static class HaRunningTotal
{
    /// <summary>A measurement above this is taken to be a running total.</summary>
    public const double Threshold = 10_000;

    /// <summary>How far back to look for the state before the latest one.</summary>
    public static readonly TimeSpan Lookback = TimeSpan.FromHours(1);

    /// <summary>
    /// True for a sensor tile showing its state where the state is a measurement (not a meter, whose
    /// total is meant to grow) above <see cref="Threshold"/>.
    /// </summary>
    public static bool Applies(HaEntityState state, string? field) =>
        (string.IsNullOrWhiteSpace(field) || field == "state")
        && state.Domain == "sensor"
        && state.GetField("state_class") == "measurement"
        && Parse(state.State) is > Threshold;

    /// <summary>The last step in <paramref name="states"/> (oldest first), or null if there isn't one going up.</summary>
    public static double? LastStep(IReadOnlyList<string> states)
    {
        var numbers = states.Select(Parse).OfType<double>().ToList();
        if (numbers.Count < 2)
            return null;
        var step = numbers[^1] - numbers[^2];
        return step >= 0 ? step : null;
    }

    public static string Format(double step) => step.ToString("0.#", CultureInfo.CurrentCulture);

    private static double? Parse(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}
