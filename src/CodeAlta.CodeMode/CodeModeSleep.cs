using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeAlta.CodeMode;

/// <summary>
/// A sleep of a script (<c>sleep(duration)</c>): its key, the step it sleeps in, and when it wakes. Its key counts the sleeps
/// of the same step path before it (<c>sleep#0</c>, <c>deploy#0/sleep#1</c>), so that a resume finds its wake time again.
/// </summary>
/// <param name="Key">The key of the sleep.</param>
/// <param name="StepId">The step it sleeps in, or null.</param>
/// <param name="WakeAt">When it wakes.</param>
public sealed partial record CodeModeSleep(string Key, string? StepId, DateTimeOffset WakeAt)
{
    /// <summary>The longest sleep.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromDays(7);

    /// <summary>
    /// Reads the duration of <c>sleep(duration)</c>: milliseconds as a number, or a number and a unit as a string
    /// (<c>"500ms"</c>, <c>"30s"</c>, <c>"5m"</c>, <c>"2h"</c>, <c>"1d"</c>), from 0 to 7 days.
    /// </summary>
    /// <param name="value">The duration, as JSON.</param>
    /// <param name="duration">The duration.</param>
    /// <param name="error">Why it is refused.</param>
    /// <returns>True when it is a duration.</returns>
    public static bool TryParse(JsonElement value, out TimeSpan duration, out string? error)
    {
        duration = TimeSpan.Zero;
        error = null;
        double milliseconds;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            milliseconds = number;
        }
        else if (value.ValueKind == JsonValueKind.String && DurationPattern().Match(value.GetString()!.Trim()) is { Success: true } match)
        {
            var amount = double.Parse(match.Groups["amount"].ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture);
            milliseconds = amount * match.Groups["unit"].Value switch
            {
                "ms" => 1,
                "s" => 1_000,
                "m" => 60_000,
                "h" => 3_600_000,
                _ => 86_400_000,
            };
        }
        else
        {
            error = "sleep(duration): the duration is milliseconds, or a text such as \"30s\", \"5m\", \"2h\" or \"1d\".";
            return false;
        }

        if (!double.IsFinite(milliseconds) || milliseconds < 0 || milliseconds > MaxDuration.TotalMilliseconds)
        {
            error = "sleep(duration): the duration is from 0 to 7 days.";
            return false;
        }

        duration = TimeSpan.FromMilliseconds(milliseconds);
        return true;
    }

    /// <summary>Gives the key of a sleep: the step path it sleeps in, and how many sleeps that path made before it.</summary>
    /// <param name="stepKey">The key of the step it sleeps in, or null outside any step.</param>
    /// <param name="occurrence">How many sleeps of that step path came before it.</param>
    /// <returns>The key, such as <c>deploy#0/sleep#1</c>.</returns>
    public static string KeyOf(string? stepKey, int occurrence)
        => string.Create(CultureInfo.InvariantCulture, $"{(stepKey is null ? string.Empty : stepKey + "/")}sleep#{occurrence}");

    [GeneratedRegex(@"\A(?<amount>[0-9]+(?:\.[0-9]+)?)\s*(?<unit>ms|s|m|h|d)\z")]
    private static partial Regex DurationPattern();
}
