using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;
using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

// What a session has not taken yet, and what its tool calls did: `alta session queued` lists the prompts of its queue,
// `alta session unqueue` and `alta session unsteer` take a prompt back, `alta session tool` reads one tool call whole.
internal sealed partial class BuiltInAltaCommandContributor
{
    /// <summary>The most characters of a text of a tool call that <c>session tool</c> returns when it names no limit.</summary>
    internal const int DefaultToolText = 16 * 1024;

    private const int MaximumToolText = 1024 * 1024;

    // The newest records of a call that are read: its phases and its output, a few at most.
    private const int MaximumToolRecords = 64;

    // The longest subject of a tool call in a record of `session events`: what it works on, on one line.
    private const int MaximumToolSubject = 160;

    private static Command CreateSessionQueuedCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var command = Leaf("queued", "List the prompts waiting in the queue of a session, in the order they will be sent.");
        command.Add("<session-id>", "CodeAlta session id.", value => sessionId = value);
        command.Add(async (_, _) => await HandleSessionQueuedAsync(context, sessionId).ConfigureAwait(false));
        AddHelpText(command, "`alta session unqueue <session-id> <queue-item-id>` removes one of them.");
        return command;
    }

    private static Command CreateSessionUnqueueCommand(AltaCommandContext context)
    {
        string? sessionId = null, itemId = null;
        var command = Leaf("unqueue", "Remove a prompt from the queue of a session, unless the session is taking it.");
        command.Add("<session-id>", "CodeAlta session id.", value => sessionId = value);
        command.Add("<queue-item-id>", "The queueItemId of the prompt, as `alta session queue` and `alta session queued` give it.", value => itemId = value);
        command.Add(async (_, _) => await HandleSessionUnqueueAsync(context, sessionId, itemId).ConfigureAwait(false));
        AddHelpText(command, "The result says `removed`, `sending` when the session is taking the prompt, or `not_found` when the queue no longer holds it.");
        return command;
    }

    private static Command CreateSessionUnsteerCommand(AltaCommandContext context)
    {
        string? sessionId = null, withdrawalId = null;
        var command = Leaf("unsteer", "Take back a prompt steered into a run, until the run gives it to the model.");
        command.Add("<session-id>", "CodeAlta session id.", value => sessionId = value);
        command.Add("<withdrawal-id>", "The withdrawalId of the prompt, as `alta session steer` gives it.", value => withdrawalId = value);
        command.Add(async (_, _) => await HandleSessionUnsteerAsync(context, sessionId, withdrawalId).ConfigureAwait(false));
        AddHelpText(command, "The result says `withdrawn: false` when it is too late: the model has the prompt, or the run ended.");
        return command;
    }

    private static Command CreateSessionToolCommand(AltaCommandContext context)
    {
        string? sessionId = null, activityId = null;
        long? since = null;
        var maximum = DefaultToolText;
        int? maximumOutput = null, outputOffset = null;
        var command = Leaf("tool", "Read one tool call of a session: what it runs, in which folder, and what it gave.");
        command.Add("<session-id>", "CodeAlta session id.", value => sessionId = value);
        command.Add("<activity-id>", "The activityId of the call, as `alta session events` gives it.", value => activityId = value);
        command.Add("since=", "Read only the events after this sequence number: the one before the first record of the call.", (long value) => since = value);
        command.Add("max-text=", $"The most characters of each text: the start of the arguments and of the diff, and a part of the output (default {DefaultToolText}).", (int value) => maximum = value);
        command.Add("max-output=", "The most characters of the output, when it differs from --max-text.", (int value) => maximumOutput = value);
        command.Add("output-offset=", "Read the output from this character, to go through a long one. Without it, a call that runs a command gives the end of its output and any other call the start.", (int value) => outputOffset = value);
        command.Add(async (_, _) => await HandleSessionToolAsync(context, sessionId, activityId, since, maximum, maximumOutput ?? maximum, outputOffset).ConfigureAwait(false));
        AddHelpText(command, "Example: `alta session tool <session-id> <activity-id> --since 120 --max-text 8000`. `outputOffset` and `outputLength` say which part of the output came.");
        return command;
    }

    private static async ValueTask<int> HandleSessionQueuedAsync(AltaCommandContext context, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return UsageError(context, "usage.missingSession", "Session id is required.", "alta session queued");
        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime)) return AltaExitCodes.ServiceUnavailable;
        var infoResult = await ResolveSessionInfoAsync(context, sessionId.Trim()).ConfigureAwait(false);
        if (infoResult.ExitCode != AltaExitCodes.Success) return infoResult.ExitCode;
        var session = infoResult.Info!.Session;
        if (await RefuseSessionOfOtherSpaceAsync(context, session.SessionId, "reads the queue of").ConfigureAwait(false) is { } otherSpace) return otherSpace;

        var prompts = await runtime.GetQueuedPromptsAsync(session, context.CancellationToken).ConfigureAwait(false);
        foreach (var prompt in prompts)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.session.queued.item",
                version = 1,
                correlationId = context.CorrelationId,
                sessionId = session.SessionId,
                queueItemId = prompt.QueueItemId,
                kind = prompt.Kind,
                state = prompt.State,
                prompt = prompt.Prompt,
                files = (prompt.Images ?? []).Select(static file => new { title = file.Title ?? Path.GetFileName(file.Path), mediaType = file.MediaType }).ToArray(),
                createdAt = prompt.CreatedAt,
            });
        }

        WriteSummary(context, "alta.session.queued.summary", prompts.Count, truncated: false);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSessionUnqueueAsync(AltaCommandContext context, string? sessionId, string? itemId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return UsageError(context, "usage.missingSession", "Session id is required.", "alta session unqueue");
        if (string.IsNullOrWhiteSpace(itemId)) return UsageError(context, "usage.missingQueueItem", "The id of a queued prompt is required.", "alta session unqueue");
        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime)) return AltaExitCodes.ServiceUnavailable;
        var infoResult = await ResolveSessionInfoAsync(context, sessionId.Trim()).ConfigureAwait(false);
        if (infoResult.ExitCode != AltaExitCodes.Success) return infoResult.ExitCode;
        var session = infoResult.Info!.Session;
        if (await RefuseSessionOfOtherSpaceAsync(context, session.SessionId, "changes the queue of").ConfigureAwait(false) is { } otherSpace) return otherSpace;

        var removal = await runtime.RemoveQueuedPromptAsync(session, itemId.Trim(), context.CancellationToken).ConfigureAwait(false);
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.session.unqueued",
            version = 1,
            correlationId = context.CorrelationId,
            sessionId = session.SessionId,
            queueItemId = itemId.Trim(),
            status = removal switch { QueuedPromptRemoval.Removed => "removed", QueuedPromptRemoval.Sending => "sending", _ => "not_found" },
        });
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSessionUnsteerAsync(AltaCommandContext context, string? sessionId, string? withdrawalId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return UsageError(context, "usage.missingSession", "Session id is required.", "alta session unsteer");
        if (string.IsNullOrWhiteSpace(withdrawalId)) return UsageError(context, "usage.missingWithdrawalId", "The withdrawal id of a steered prompt is required.", "alta session unsteer");
        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime)) return AltaExitCodes.ServiceUnavailable;
        var infoResult = await ResolveSessionInfoAsync(context, sessionId.Trim()).ConfigureAwait(false);
        if (infoResult.ExitCode != AltaExitCodes.Success) return infoResult.ExitCode;
        var session = infoResult.Info!.Session;
        if (await RefuseSessionOfOtherSpaceAsync(context, session.SessionId, "takes back the prompts of").ConfigureAwait(false) is { } otherSpace) return otherSpace;

        var withdrawn = await runtime.TryWithdrawSteerAsync(session, withdrawalId.Trim(), context.CancellationToken).ConfigureAwait(false);
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.session.unsteered",
            version = 1,
            correlationId = context.CorrelationId,
            sessionId = session.SessionId,
            withdrawalId = withdrawalId.Trim(),
            withdrawn,
        });
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSessionToolAsync(AltaCommandContext context, string? sessionId, string? activityId, long? since, int maximum, int maximumOutput, int? outputOffset)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return UsageError(context, "usage.missingSession", "Session id is required.", "alta session tool");
        if (string.IsNullOrWhiteSpace(activityId)) return UsageError(context, "usage.missingActivity", "The activity id of a tool call is required.", "alta session tool");
        if (maximum is <= 0 or > MaximumToolText) return UsageError(context, "usage.invalidLimit", $"--max-text must be from 1 to {MaximumToolText}.", "alta session tool");
        if (maximumOutput is <= 0 or > MaximumToolText) return UsageError(context, "usage.invalidLimit", $"--max-output must be from 1 to {MaximumToolText}.", "alta session tool");
        if (outputOffset < 0) return UsageError(context, "usage.invalidOffset", "--output-offset cannot be negative.", "alta session tool");
        if (since < 0) return UsageError(context, "usage.invalidSince", "--since cannot be negative.", "alta session tool");
        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime)) return AltaExitCodes.ServiceUnavailable;
        var infoResult = await ResolveSessionInfoAsync(context, sessionId.Trim()).ConfigureAwait(false);
        if (infoResult.ExitCode != AltaExitCodes.Success) return infoResult.ExitCode;
        var info = infoResult.Info!;
        if (await RefuseSessionOfOtherSpaceAsync(context, info.Session.SessionId, "reads the tool calls of").ConfigureAwait(false) is { } otherSpace) return otherSpace;

        var id = activityId.Trim();
        bool OfTheCall(AgentEvent record) => record switch
        {
            AgentActivityEvent activity => string.Equals(activity.ActivityId, id, StringComparison.Ordinal),
            AgentContentCompletedEvent { Kind: AgentContentKind.ToolOutput or AgentContentKind.CommandOutput or AgentContentKind.FileChangeOutput } output
                => string.Equals(output.ParentActivityId, id, StringComparison.Ordinal) || string.Equals(output.ContentId, id, StringComparison.Ordinal),
            _ => false,
        };

        // The turns the call may be in are read, not the whole journal; the whole history only when that cannot be done.
        var window = await runtime.TryReadStoredHistoryWindowAsync(info.Session, since, MaximumToolRecords, fromTail: true, OfTheCall, context.CancellationToken).ConfigureAwait(false);
        IReadOnlyList<AgentEvent> records = window is not null
            ? [.. window.Events.Select(static item => item.Event)]
            : [.. (await ReadSessionHistoryAsync(context, runtime, info, lastTurn: false).ConfigureAwait(false) ?? []).Where(OfTheCall)];
        var activities = records.OfType<AgentActivityEvent>().ToArray();
        if (activities.Length == 0) return NotFound(context, "session.toolNotFound", $"Session '{info.Session.SessionId}' has no tool call '{id}'.");

        var output = records.OfType<AgentContentCompletedEvent>().LastOrDefault();
        var call = activities.Select((activity, index) => AgentToolCallProjection.Project(activity, index == activities.Length - 1 ? output : null)).Aggregate(Newest);
        var (arguments, argumentsLength) = StartOf(call.Arguments, maximum);
        var (diff, diffLength) = StartOf(call.Diff, maximum);
        var (error, _) = StartOf(call.Error, maximum);
        var (text, outputStart, outputLength) = OutputOf(call.Output, call.Command is not null, outputOffset, maximumOutput);
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.session.tool",
            version = 1,
            correlationId = context.CorrelationId,
            sessionId = info.Session.SessionId,
            activityId = id,
            kind = activities[^1].Kind.ToString().ToLowerInvariant(),
            phase = activities[^1].Phase.ToString().ToLowerInvariant(),
            name = call.Name,
            startedAt = activities[0].Timestamp,
            at = activities[^1].Timestamp,
            command = call.Command,
            workingDirectory = call.WorkingDirectory,
            sessionFolder = info.Session.WorkingDirectory,
            exitCode = call.ExitCode,
            error,
            arguments,
            argumentsLength,
            output = text,
            outputOffset = outputStart,
            outputLength,
            diff,
            diffLength,
            readFiles = call.ReadFiles,
            modifiedFiles = call.ModifiedFiles,
        });
        return AltaExitCodes.Success;
    }

    // Each phase of a call says part of it, as its arguments and then its result: the newest that says a thing wins.
    private static AgentToolCallParts Newest(AgentToolCallParts older, AgentToolCallParts newer) => new(
        newer.Name ?? older.Name, newer.Message ?? older.Message, newer.Command ?? older.Command, newer.WorkingDirectory ?? older.WorkingDirectory,
        newer.ExitCode ?? older.ExitCode, newer.Error ?? older.Error, newer.Arguments ?? older.Arguments, newer.Output ?? older.Output, newer.Diff ?? older.Diff,
        newer.ReadFiles.Length > 0 ? newer.ReadFiles : older.ReadFiles, newer.ModifiedFiles.Length > 0 ? newer.ModifiedFiles : older.ModifiedFiles);

    private static (string? Text, int Length) StartOf(string? text, int maximum)
    {
        if (text is null) return (null, 0);
        if (text.Length <= maximum) return (text, text.Length);
        return (text[..(char.IsHighSurrogate(text[maximum - 1]) ? maximum - 1 : maximum)], text.Length);
    }

    // The part of an output that was asked for, else the part that says the most: the end of what a command printed,
    // the start of anything else, such as a file it read.
    private static (string? Text, int Offset, int Length) OutputOf(string? output, bool ranCommand, int? offset, int maximum)
    {
        if (output is null) return (null, 0, 0);
        if (offset is { } asked)
        {
            var from = Math.Min(asked, output.Length);
            // A limit that falls inside a pair moves before it, whichever side of the limit a read is on: two parts that meet lose nothing.
            var start = BeforePair(output, from);
            var end = BeforePair(output, maximum >= output.Length - from ? output.Length : from + maximum);
            return (output[start..end], start, output.Length);
        }

        if (ranCommand)
        {
            var (tail, _) = EndOf(output, maximum);
            return (tail, output.Length - (tail?.Length ?? 0), output.Length);
        }

        var (head, _) = StartOf(output, maximum);
        return (head, 0, output.Length);
    }

    private static int BeforePair(string text, int index)
        => index > 0 && index < text.Length && char.IsLowSurrogate(text[index]) && char.IsHighSurrogate(text[index - 1]) ? index - 1 : index;

    private static (string? Text, int Length) EndOf(string? text, int maximum)
    {
        if (text is null) return (null, 0);
        if (text.Length <= maximum) return (text, text.Length);
        var start = text.Length - maximum;
        return (text[(char.IsLowSurrogate(text[start]) ? start + 1 : start)..], text.Length);
    }

    // What a tool call works on, on one line, for a record of `session events`; null when it says no more than the name of the tool.
    private static string? ToolSubject(AgentActivityEvent activity)
    {
        var subject = AgentToolCallProjection.Subject(activity);
        if (string.IsNullOrWhiteSpace(subject) || subject == activity.Name || subject == activity.Message) return null;
        foreach (var line in subject.AsSpan().EnumerateLines())
        {
            var text = line.Trim();
            if (text.IsEmpty) continue;
            return text.Length <= MaximumToolSubject ? text.ToString()
                : string.Concat(text[..(char.IsHighSurrogate(text[MaximumToolSubject - 1]) ? MaximumToolSubject - 1 : MaximumToolSubject)], "…");
        }

        return null;
    }
}
