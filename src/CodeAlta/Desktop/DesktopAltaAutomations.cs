using System.Globalization;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Automations;
using CodeAlta.LiveTool;

namespace CodeAlta.Desktop;

/// <summary>
/// Gives the <c>alta automation</c> commands the automations of the application: the ones the window lists.
/// </summary>
/// <param name="automations">The automations of the application.</param>
/// <param name="projects">The host's project catalog, for the folder of the project a new automation runs in.</param>
internal sealed class DesktopAltaAutomations(AutomationService automations, ProjectCatalog projects) : IAltaAutomations
{
    /// <inheritdoc />
    public IReadOnlyList<AltaAutomation> List() => [.. automations.Snapshot.Entries.Select(Automation)];

    /// <inheritdoc />
    public IReadOnlyList<AltaAutomationRun> ListRuns(string? id, int limit) => [.. automations.Runs(id, Math.Clamp(limit, 1, 1000)).Select(Run)];

    /// <inheritdoc />
    public AltaAutomationRun? FindRunOfSession(string sessionId)
        => string.IsNullOrWhiteSpace(sessionId) ? null : automations.RunOfSession(sessionId.Trim()) is { } run ? Run(run) : null;

    /// <inheritdoc />
    public async Task<AltaAutomationRun?> RunAsync(string id, CancellationToken cancellationToken)
        => await automations.RunAsync(id, cancellationToken).ConfigureAwait(false) is { } run ? Run(run) : null;

    /// <inheritdoc />
    public async Task<AltaAutomationChange> CreateAsync(AltaAutomationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!AutomationModelRef.TryParse(request.Model, out var model, out var problem)) return new("refused", null, problem);
        var triggers = new List<AutomationTrigger>();
        foreach (var text in request.Triggers)
        {
            if (!AutomationTriggerText.TryParse(text, out var trigger, out problem)) return new("refused", null, problem);
            triggers.Add(trigger!);
        }

        string? folder = null;
        if (request.ProjectId is { } projectId)
        {
            var project = await projects.GetByIdAsync(projectId, cancellationToken).ConfigureAwait(false);
            if (project is null || project.Archived) return new("refused", null, "Its project is not one of the projects of CodeAlta.");
            folder = project.ProjectPath;
        }
        else if (request.StoreInProject)
        {
            return new("refused", null, "A chat is kept in the configuration of the user.");
        }

        var definition = new AutomationDefinition(AutomationDefinition.NewId(), request.Name ?? string.Empty)
        {
            Prompt = request.Prompt ?? string.Empty,
            Enabled = request.Enabled,
            Project = request.StoreInProject ? null : folder,
            Model = model,
            Agent = string.IsNullOrWhiteSpace(request.Agent) ? null : request.Agent.Trim(),
            CatchUp = request.CatchUp,
            Triggers = triggers,
        };
        return await automations.SaveAsync(definition, request.StoreInProject ? request.ProjectId : null, cancellationToken).ConfigureAwait(false) is { } refused
            ? new("refused", null, refused) : new("ok", definition.Id);
    }

    /// <inheritdoc />
    public async Task<AltaAutomationChange> SetEnabledAsync(string id, bool enabled, CancellationToken cancellationToken)
    {
        if (automations.Snapshot.Find(id) is null) return new("not_found");
        return await automations.SetEnabledAsync(id, enabled, cancellationToken).ConfigureAwait(false) is { } refused ? new("refused", id, refused) : new("ok", id);
    }

    /// <inheritdoc />
    public async Task<AltaAutomationChange> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        if (automations.Snapshot.Find(id) is null) return new("not_found");
        return await automations.DeleteAsync(id, cancellationToken).ConfigureAwait(false) is { } refused ? new("refused", id, refused) : new("ok", id);
    }

    private AltaAutomation Automation(AutomationEntry entry)
        => new(entry.Id, entry.Definition.Name, entry.Definition.Enabled, entry.Definition.Prompt, entry.ProjectId, entry.ProjectPath, entry.Source.FilePath,
            entry.Definition.Model.Format(), entry.Definition.Agent, [.. entry.Definition.Triggers.Select(AutomationTriggerText.Format)], entry.Problem,
            automations.NextDue(entry.Id), automations.IsRunning(entry.Id), automations.IsAllowed(entry));

    private static AltaAutomationRun Run(AutomationRun run)
        => new(run.Id, run.AutomationId, run.Name, run.SessionId, run.ProjectId, run.StartedAt, run.EndedAt, run.Trigger, run.Detail, run.Status, run.Message);
}

/// <summary>
/// A trigger on one line, as the <c>alta automation</c> commands show and take it: <c>daily@09:00,17:30</c>,
/// <c>hourly@15</c>, <c>hourly@15/2</c>, <c>weekly@mon,thu@08:30</c>, <c>cron@0 9 * * 1-5</c>, <c>issue@opened</c>,
/// <c>pull_request@updated</c>, the last two with <c>+anyone</c> for items of any author, <c>jira@created</c>, <c>jira@updated</c>.
/// </summary>
internal static class AutomationTriggerText
{
    /// <summary>Writes a trigger.</summary>
    internal static string Format(AutomationTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        return trigger.Kind switch
        {
            AutomationTriggerKind.Hourly => "hourly@" + trigger.Minute.ToString(CultureInfo.InvariantCulture) + (trigger.Every == 1 ? string.Empty : "/" + trigger.Every.ToString(CultureInfo.InvariantCulture)),
            AutomationTriggerKind.Daily => "daily@" + string.Join(',', trigger.At),
            AutomationTriggerKind.Weekly => "weekly@" + string.Join(',', trigger.Days.Select(static day => AutomationTrigger.DayNames[(int)day])) + "@" + string.Join(',', trigger.At),
            AutomationTriggerKind.Cron => "cron@" + trigger.Expression,
            _ => trigger.KindName + "@" + trigger.Event + (trigger.Authors == AutomationAuthors.Anyone ? "+anyone" : string.Empty),
        };
    }

    /// <summary>Reads a trigger.</summary>
    /// <returns>Whether the text is a trigger that can run; otherwise <paramref name="problem"/> says why not.</returns>
    internal static bool TryParse(string? text, out AutomationTrigger? trigger, out string? problem)
    {
        (trigger, problem) = (null, null);
        var value = text?.Trim() ?? string.Empty;
        var at = value.IndexOf('@');
        var name = (at < 0 ? value : value[..at]).Trim().ToLowerInvariant();
        var rest = at < 0 ? string.Empty : value[(at + 1)..].Trim();
        if (name == "pr") name = "pull_request";
        if (!AutomationTrigger.TryParseKind(name, out var kind))
        {
            problem = $"'{text}' is not a trigger. Write daily@09:00, hourly@15, weekly@mon,thu@08:30, cron@0 9 * * 1-5, issue@opened, pull_request@opened or jira@created.";
            return false;
        }

        switch (kind)
        {
            case AutomationTriggerKind.Hourly:
                var parts = rest.Split('/');
                if (parts.Length > 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var minute)
                    || !int.TryParse(parts.Length == 2 ? parts[1] : "1", NumberStyles.None, CultureInfo.InvariantCulture, out var every))
                {
                    problem = "An hourly trigger is written hourly@<minute>, or hourly@<minute>/<hours> for every few hours.";
                    return false;
                }

                trigger = new(kind) { Minute = minute, Every = every };
                break;
            case AutomationTriggerKind.Daily:
                if (!TryTimes(rest, out var daily, out problem)) return false;
                trigger = new(kind) { At = daily };
                break;
            case AutomationTriggerKind.Weekly:
                var split = rest.Split('@');
                var days = new List<DayOfWeek>();
                foreach (var day in split[0].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var index = Array.IndexOf(AutomationTrigger.DayNames, day.ToLowerInvariant());
                    if (index < 0)
                    {
                        problem = $"'{day}' is not a day of the week: sun, mon, tue, wed, thu, fri or sat.";
                        return false;
                    }

                    if (!days.Contains((DayOfWeek)index)) days.Add((DayOfWeek)index);
                }

                if (split.Length != 2 || !TryTimes(split[1], out var weekly, out problem))
                {
                    problem ??= "A weekly trigger is written weekly@<days>@<times>, such as weekly@mon,thu@08:30.";
                    return false;
                }

                days.Sort();
                trigger = new(kind) { Days = days, At = weekly };
                break;
            case AutomationTriggerKind.Cron:
                trigger = new(kind) { Expression = string.Join(' ', rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) };
                break;
            default:
                var anyone = rest.EndsWith("+anyone", StringComparison.OrdinalIgnoreCase);
                var @event = (anyone ? rest[..^"+anyone".Length] : rest).Trim().ToLowerInvariant();
                if (kind == AutomationTriggerKind.Jira) @event = @event is "" or "opened" ? "created" : @event;
                trigger = new(kind) { Event = @event.Length == 0 ? "opened" : @event, Authors = anyone ? AutomationAuthors.Anyone : AutomationAuthors.Trusted };
                break;
        }

        // What makes the trigger one that can run is said by the same check as for a configuration file.
        problem = AutomationConfig.Validate(new AutomationDefinition(AutomationDefinition.NewId(), "trigger") { Prompt = "trigger", Triggers = [trigger] });
        if (problem is null) return true;
        trigger = null;
        return false;
    }

    private static bool TryTimes(string text, out List<AutomationTime> times, out string? problem)
    {
        (times, problem) = ([], null);
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!AutomationTime.TryParse(part, out var time))
            {
                problem = $"'{part}' is not a time of day, such as 09:00.";
                return false;
            }

            if (!times.Contains(time)) times.Add(time);
        }

        times.Sort();
        if (times.Count > 0) return true;
        problem = "A time of day is missing, such as 09:00.";
        return false;
    }
}
