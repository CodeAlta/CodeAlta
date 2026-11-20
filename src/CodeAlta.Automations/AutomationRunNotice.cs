using System.Globalization;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Automations;

/// <summary>The notification of a run that ended, for an automation that asks for one (<see cref="AutomationDefinition.Notify"/>).</summary>
internal static class AutomationRunNotice
{
    /// <summary>
    /// The notification of a run: the name of its automation; how it ended and how long it took; then the start of its answer,
    /// or why it failed. A click opens its session.
    /// </summary>
    /// <param name="run">The run as it ended.</param>
    /// <param name="answer">The last answer of its session, or what its script returned; null when there is none.</param>
    /// <returns>The notification.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="run"/> is null.</exception>
    internal static PluginNotification Of(AutomationRun run, string? answer)
    {
        ArgumentNullException.ThrowIfNull(run);
        var (tone, how) = run.Status switch
        {
            AutomationRun.Completed => (PluginNotificationTone.Success, "Completed"),
            AutomationRun.Cancelled => (PluginNotificationTone.Warning, "Cancelled"),
            _ => (PluginNotificationTone.Danger, "Failed"),
        };
        var heading = run.EndedAt is { } ended && ended >= run.StartedAt ? $"{how} in {Duration(ended - run.StartedAt)}." : how + ".";
        // A run that failed says why; one that ended otherwise, the start of its answer.
        var detail = AutomationText.Line(run.Status == AutomationRun.Failed ? run.Message ?? answer : answer, PluginNotification.MaximumBodyLength - heading.Length - 1);
        return new PluginNotification
        {
            Title = AutomationText.Line(string.IsNullOrWhiteSpace(run.Name) ? run.AutomationId : run.Name, PluginNotification.MaximumTitleLength),
            Body = detail.Length == 0 ? heading : heading + "\n" + detail,
            Tone = tone,
            SessionId = run.SessionId,
        };
    }

    /// <summary>A duration as the notification says it: <c>45 s</c>, <c>3 min 12 s</c>, <c>1 h 05 min</c>.</summary>
    internal static string Duration(TimeSpan span) => span.TotalMinutes < 1 ? string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, (int)span.TotalSeconds)} s")
        : span.TotalHours < 1 ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes} min {span.Seconds} s")
        : string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours} h {span.Minutes:00} min");
}
