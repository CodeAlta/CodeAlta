using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Automations;

/// <summary>
/// The Automation trigger: a run of another automation of this machine completed, failed, or waits for an approval, so
/// that automations can follow one another. It never starts on the runs of its own automation, and a chain of automations
/// that start each other stops after <see cref="MaximumChain"/> runs.
/// </summary>
internal sealed class AutomationRunTriggerSource : IAutomationTriggerSource
{
    /// <summary>A run completed.</summary>
    internal const string Completed = "completed";

    /// <summary>A run failed, or the application stopped while it was in progress.</summary>
    internal const string Failed = "failed";

    /// <summary>A run waits for an approval.</summary>
    internal const string Waiting = "waiting";

    /// <summary>The most runs of a chain of automations started by this trigger, one after the other.</summary>
    internal const int MaximumChain = 5;

    // A run recorded as ended a little before a reading may have been written after it: the readings overlap by this much,
    // and what was taken twice is told apart by its id.
    private static readonly TimeSpan Overlap = TimeSpan.FromMinutes(1);

    private readonly AutomationService _service;
    private readonly Lock _gate = new();
    private readonly HashSet<string> _stopped = new(StringComparer.Ordinal);

    /// <summary>Creates the source of the runs of a service.</summary>
    internal AutomationRunTriggerSource(AutomationService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
        Contribution = new PluginAutomationTriggerContribution
        {
            Type = AutomationSourceTypes.Automation,
            DisplayName = "Automation",
            Description = "When a run of another automation of this machine completes, fails or waits for an approval.",
            Events =
            [
                new(Completed, "Completed") { Description = "A run of the automation completed." },
                new(Failed, "Failed") { Description = "A run of the automation failed, or stopped when CodeAlta closed." },
                new(Waiting, "Waits for an approval") { Description = "A run of the automation waits for a person to approve a call." },
            ],
            Fields = [new("automations", "Automations", PluginTriggerFieldKind.Automations) { Description = "Only the runs of these automations, by id or name; those of any other automation when empty." }],
            Source = this,
            Signal = Signal,
        };
    }

    /// <summary>Gets what the trigger is, for the registry of the sources.</summary>
    internal PluginAutomationTriggerContribution Contribution { get; }

    /// <summary>Gets the signal raised when a run changed.</summary>
    internal PluginTriggerSignal Signal { get; } = new();

    /// <inheritdoc />
    public PluginTriggerDelivery Delivery => PluginTriggerDelivery.Polled;

    /// <summary>The run an event of this source is about, from its id; null for an id of another source.</summary>
    internal static string? RunOf(string eventId)
    {
        var parts = eventId.Split(':');
        return parts.Length >= 2 && parts[0] is Completed or Failed or Waiting && parts[1].Length > 0 ? parts[1] : null;
    }

    /// <inheritdoc />
    public ValueTask<PluginTriggerPage> ReadAsync(PluginTriggerContext context, string? cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var now = _service.Now;
        var runs = _service.Runs(null, AutomationStateStore.MaximumRuns);
        var waiting = runs.Where(static run => run.Status == AutomationRun.Waiting).Select(WaitingKey).ToHashSet(StringComparer.Ordinal);
        var (since, waited) = Read(cursor);
        if (since is null) return ValueTask.FromResult(new PluginTriggerPage([], Write(now, waiting)));

        var watched = context.Settings.Texts("automations");
        var events = new List<PluginTriggerEvent>();
        foreach (var run in runs.OrderBy(static run => run.EndedAt ?? run.StartedAt))
        {
            // An automation does not start on its own runs, whatever its trigger says.
            if (run.AutomationId == context.AutomationId) continue;
            if (watched.Count > 0 && !watched.Any(name => name == run.AutomationId || string.Equals(name, run.Name, StringComparison.OrdinalIgnoreCase))) continue;
            var on = run.Status switch
            {
                AutomationRun.Completed => Completed,
                AutomationRun.Failed or AutomationRun.Interrupted => Failed,
                AutomationRun.Waiting => Waiting,
                _ => null,
            };
            if (on is null || !context.Events.Contains(on, StringComparer.Ordinal)) continue;
            if (on == Waiting ? waited.Contains(WaitingKey(run)) : run.EndedAt is not { } ended || ended < since - Overlap) continue;
            if (Depth(run) >= MaximumChain)
            {
                Stop(context.AutomationId, run);
                continue;
            }

            events.Add(Event(on, run));
        }

        return ValueTask.FromResult(new PluginTriggerPage(events, Write(now, waiting)));
    }

    // How many runs started by this trigger led to a run, one after the other.
    private int Depth(AutomationRun run)
    {
        var depth = 0;
        for (var cause = run.Cause; cause is not null && depth <= MaximumChain; cause = _service.Run(cause)?.Cause) depth++;
        return depth;
    }

    // A chain that went too far is said once on the automation that would have gone on with it.
    private void Stop(string automationId, AutomationRun run)
    {
        lock (_gate)
        {
            if (!_stopped.Add(automationId + "|" + run.Id)) return;
            if (_stopped.Count > AutomationStateStore.MaximumRuns) _stopped.Clear();
        }

        _service.RecordSkipped(automationId, AutomationSourceTypes.Automation, Title(run, "ended"),
            $"It would follow a chain of {MaximumChain} automations that started one another: the chain stops there.");
    }

    private static string WaitingKey(AutomationRun run) => run.Id + "@" + (run.Approval ?? string.Empty);

    private static string Title(AutomationRun run, string verb) => $"{AutomationText.Line(run.Name ?? run.AutomationId, 80)} {verb}";

    private PluginTriggerEvent Event(string on, AutomationRun run)
    {
        var verb = on switch { Completed => "completed", Failed => "failed", _ => "waits for an approval" };
        var fields = new List<KeyValuePair<string, string>> { new("Automation", run.Name ?? run.AutomationId), new("Status", run.Status) };
        if (run.Message is { Length: > 0 } message) fields.Add(new("Message", message));
        if (run.Detail is { Length: > 0 } detail) fields.Add(new("Started by", $"{run.Trigger}: {detail}"));
        else fields.Add(new("Started by", run.Trigger));
        if (run.SessionId is { } session) fields.Add(new("Session", session));
        var data = new JsonObject
        {
            ["automationId"] = run.AutomationId, ["automationName"] = run.Name, ["runId"] = run.Id, ["status"] = run.Status, ["message"] = run.Message,
            ["sessionId"] = run.SessionId, ["projectId"] = run.ProjectId, ["trigger"] = run.Trigger, ["detail"] = run.Detail,
            ["startedAt"] = run.StartedAt.ToString("O", CultureInfo.InvariantCulture), ["endedAt"] = run.EndedAt?.ToString("O", CultureInfo.InvariantCulture),
            ["chain"] = Depth(run) + 1,
        };
        var id = on == Waiting ? $"{on}:{run.Id}:{run.Approval}" : $"{on}:{run.Id}";
        return new PluginTriggerEvent(id, on, Title(run, verb)) { OccurredAt = run.EndedAt, Fields = fields, DataJson = data.ToJsonString() };
    }

    // The cursor: when the last reading was, and the runs that waited then, which are not events again.
    private static string Write(DateTimeOffset now, IEnumerable<string> waiting)
        => new JsonObject { ["since"] = now.ToString("O", CultureInfo.InvariantCulture), ["waiting"] = new JsonArray([.. waiting.Order(StringComparer.Ordinal).Select(static key => (JsonNode?)key)]) }.ToJsonString();

    private static (DateTimeOffset? Since, IReadOnlySet<string> Waited) Read(string? cursor)
    {
        if (cursor is null) return (null, new HashSet<string>());
        try
        {
            if (JsonNode.Parse(cursor) is JsonObject root && DateTimeOffset.TryParse(root["since"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var since))
                return (since, (root["waiting"] as JsonArray ?? []).Select(static key => key?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.Ordinal));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            // A cursor that cannot be read starts again from now.
        }

        return (null, new HashSet<string>());
    }
}
