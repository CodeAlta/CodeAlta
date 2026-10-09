using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Orchestration.Jobs;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// Serves one tool call to the desktop window: its whole persisted record, which a history row only summarizes,
/// and, while the call runs, what it writes.
/// </summary>
/// <remarks>
/// A read names a session and the journal offset of the call's activity record, as its history row reported it,
/// and optionally the offset of the output record that the timeline attached to the call. Texts are served in
/// parts of at most <see cref="ChunkCharacters"/> UTF-16 code units; a longer one is continued by position.
/// Reading is not tied to a project: the calls of an archived project's sessions are served like any other.
/// </remarks>
[NeoRpcService("toolCalls", Version = 1)]
internal sealed class ToolCallsService
{
    /// <summary>Longest part of a text served by one read, in UTF-16 code units.</summary>
    internal const int ChunkCharacters = 256 * 1024;
    /// <summary>Longest text of one item of the live output channel, in UTF-16 code units.</summary>
    internal const int ItemCharacters = 64 * 1024;
    /// <summary>Most paths listed for the files a call read or modified.</summary>
    internal const int MaximumPaths = 64;

    private readonly Func<string, long, CancellationToken, Task<AgentEvent?>>? _read;
    private readonly RuntimeToolOutputProjection? _output;
    private readonly string? _epoch;
    private readonly TimeSpan _pace;

    /// <summary>
    /// Gets the background jobs of the sessions, whose output is followed like the one of a running call, under
    /// the identity of the job; null serves none.
    /// </summary>
    internal SessionJobService? Jobs { get; init; }

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal ToolCallsService()
    {
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <param name="reads">The host's admitted workspace reads.</param>
    /// <param name="output">The output of the running tool calls.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="reads"/> or <paramref name="output"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    internal ToolCallsService(OwnedSessionWorkspace reads, RuntimeToolOutputProjection output, string epoch)
        : this((reads ?? throw new ArgumentNullException(nameof(reads))).ReadHistoryRecordAsync, output, epoch)
    {
    }

    /// <summary>Creates the service over a literal record read.</summary>
    /// <param name="read">Reads the event at (session, record offset).</param>
    /// <param name="output">The output of the running tool calls; null serves no live output.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <param name="pace">The pause between two items of a live output; the default lets a burst of lines become one item.</param>
    /// <exception cref="ArgumentNullException"><paramref name="read"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    internal ToolCallsService(Func<string, long, CancellationToken, Task<AgentEvent?>> read, RuntimeToolOutputProjection? output, string epoch, TimeSpan? pace = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        _read = read;
        _output = output;
        _epoch = epoch;
        _pace = pace ?? TimeSpan.FromMilliseconds(40);
    }

    /// <summary>Returns the persisted record of a tool call, or the next part of one of its texts.</summary>
    [NeoRpcMethod("read")]
    public async Task<ToolCallResponse> ReadAsync(ToolCallRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_read is null) return Refused("unavailable");
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return Refused("stale_epoch");
        long outputOffset = -1;
        if (!Identity(request.SessionId) || !Offset(request.Offset, out var offset) || request.Position < 0
            || request.OutputOffset is not null && !Offset(request.OutputOffset, out outputOffset)
            || request.Part is not (null or "arguments" or "output" or "diff")) return Refused("invalid");
        cancellationToken.ThrowIfCancellationRequested();
        AgentEvent? record, attached = null;
        try
        {
            record = await ReadRecordAsync(request.SessionId, offset, cancellationToken).ConfigureAwait(false);
            if (request.OutputOffset is not null) attached = await ReadRecordAsync(request.SessionId, outputOffset, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ToolCallRefusal refusal) { return Refused(refusal.Status); }
        if (record is not AgentActivityEvent activity) return Refused("missing_record");
        // An output record that is not the one of this call is ignored, never shown as its output.
        var output = attached is AgentContentCompletedEvent { Kind: AgentContentKind.ToolOutput or AgentContentKind.CommandOutput or AgentContentKind.FileChangeOutput } completed
            && (string.Equals(completed.ParentActivityId, activity.ActivityId, StringComparison.Ordinal) || string.Equals(completed.ContentId, activity.ActivityId, StringComparison.Ordinal))
            ? completed : null;
        var call = ToolCallProjection.Project(activity, output);
        if (request.Part is { } part)
        {
            var text = part switch { "arguments" => call.Arguments, "output" => call.Output, _ => call.Diff };
            return text is null || request.Position > text.Length ? Refused("invalid") : new("ok", null, Part(text, request.Position));
        }

        return new("ok", new(activity.Kind.ToString(), activity.Phase.ToString(), call.Name, activity.Timestamp, call.Message, call.Command,
            call.WorkingDirectory, call.ExitCode, call.Error, Part(call.Arguments, 0), Part(call.Output, 0), Part(call.Diff, 0),
            call.ReadFiles, call.ModifiedFiles), null);
    }

    /// <summary>Streams what a running tool call writes: what it wrote so far, then each addition, until it ends.</summary>
    [NeoRpcMethod("observe")]
    public NeoRpcChannel<ToolCallOutputItem> Observe(ToolCallObserveRequest request, CancellationToken cancellationToken)
        => new(EnumerateAsync(request, cancellationToken), DesktopJsonContext.Default.ToolCallOutputItem);

    private async IAsyncEnumerable<ToolCallOutputItem> EnumerateAsync(ToolCallObserveRequest? request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_output is null) { yield return Ended("unavailable"); yield break; }
        if (request is null || !Identity(request.SessionId) || !Identity(request.ActivityId)) { yield return Ended("invalid"); yield break; }
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) { yield return Ended("stale_epoch"); yield break; }
        cancellationToken.ThrowIfCancellationRequested();
        // A background job of the session is followed the same way: it writes outside any tool call, until its command ends.
        var job = Jobs is not null && request.ActivityId.StartsWith(SessionJobService.IdPrefix, StringComparison.OrdinalIgnoreCase)
            && Jobs.Get(request.ActivityId) is { } found && string.Equals(found.SessionId, request.SessionId, StringComparison.OrdinalIgnoreCase);
        await using var observation = (job ? Jobs!.ObserveOutputAsync(request.ActivityId, cancellationToken) : _output.ObserveAsync(request.SessionId, request.ActivityId, cancellationToken))
            .GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            var moved = false;
            string? error = null;
            try { moved = await observation.MoveNextAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (InvalidOperationException) { error = "capacity"; }
            catch (Exception) { error = "observation_failed"; }
            if (error is not null) { yield return Ended(error); yield break; }
            if (!moved) yield break;
            var update = observation.Current;
            // A long update is sent as several items; only the first one restarts the output.
            for (var position = 0; ;)
            {
                var length = Math.Min(ItemCharacters, update.Text.Length - position);
                if (position + length < update.Text.Length && char.IsHighSurrogate(update.Text[position + length - 1])) length--;
                var last = position + length >= update.Text.Length;
                yield return new("ok", update.Text.Substring(position, length), Number(update.Start + position), Number(update.TotalCharacters),
                    update.IsReset && position == 0, update.IsComplete && last);
                position += length;
                if (last) break;
            }
            if (update.IsComplete) yield break;
            if (_pace > TimeSpan.Zero) await Task.Delay(_pace, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AgentEvent?> ReadRecordAsync(string sessionId, long offset, CancellationToken cancellationToken)
    {
        Task<AgentEvent?> reading;
        // Only the synchronous shared gate refusal is capacity; an admitted read reports its failure when awaited.
        try { reading = _read!(sessionId, offset, cancellationToken); }
        catch (ObjectDisposedException) { throw new ToolCallRefusal("closed"); }
        catch (InvalidOperationException) { throw new ToolCallRefusal("capacity"); }
        try { return await reading.ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ObjectDisposedException) { throw new ToolCallRefusal("closed"); }
        catch (CodeAlta.Agent.Runtime.AgentSessionHistoryException error)
        {
            throw new ToolCallRefusal(error.Code switch { "missing_session" => "missing_session", "invalid_cursor" => "missing_record", "record_too_large" => "too_large", _ => "read_failed" });
        }
        catch (Exception) { throw new ToolCallRefusal("read_failed"); } // Never serialize exception details: they name absolute paths.
    }

    private static ToolCallText? Part(string? text, int position)
    {
        if (text is null) return null;
        var length = Math.Min(ChunkCharacters, text.Length - position);
        if (position + length < text.Length && length > 0 && char.IsHighSurrogate(text[position + length - 1])) length--;
        return new(text.Substring(position, length), text.Length, position + length < text.Length);
    }

    private static ToolCallResponse Refused(string status) => new(status, null, null);

    private static ToolCallOutputItem Ended(string status) => new(status, string.Empty, "0", "0", true, true);

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static bool Offset(string? value, out long offset)
    {
        offset = -1;
        return value is { Length: >= 1 and <= 19 } && !value.Any(static character => character is < '0' or > '9')
            && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out offset);
    }

    private static bool Identity(string? value)
        => value is { Length: >= 1 and <= 256 } && !string.IsNullOrWhiteSpace(value) && value == value.Trim() && !value.Any(char.IsControl);

    private sealed class ToolCallRefusal(string status) : Exception
    {
        internal string Status { get; } = status;
    }
}

/// <summary>What a tool call's records say, whole: the texts are not cut here.</summary>
internal sealed record ToolCallParts(string? Name, string? Message, string? Command, string? WorkingDirectory, int? ExitCode, string? Error,
    string? Arguments, string? Output, string? Diff, string[] ReadFiles, string[] ModifiedFiles);

/// <summary>Reads a tool call from the records providers write, with the shapes the terminal UI reads.</summary>
internal static class ToolCallProjection
{
    /// <summary>Projects the activity record of a call and, when the timeline attached one, its output record.</summary>
    /// <param name="activity">The newest activity record of the call.</param>
    /// <param name="output">The completed output record of the call, when there is one.</param>
    internal static ToolCallParts Project(AgentActivityEvent activity, AgentContentCompletedEvent? output)
    {
        ArgumentNullException.ThrowIfNull(activity);
        var details = activity.Details;
        var name = activity.Name is { Length: > 0 } given ? given
            : Text(details, "toolName") ?? Text(details, "mcpToolName") ?? Text(details, "tool") ?? Text(details, "name");
        var command = Text(details, "command") ?? Text(details, "arguments", "command") ?? Text(details, "input", "command")
            ?? (activity.Kind == AgentActivityKind.CommandExecution ? activity.Name : null);
        var directory = Text(details, "cwd") ?? Text(details, "arguments", "workdir") ?? Text(details, "arguments", "cwd") ?? Text(details, "input", "cwd");
        var ended = activity.Phase is AgentActivityPhase.Completed or AgentActivityPhase.Failed or AgentActivityPhase.Canceled;
        var text = output?.Content ?? Text(details, "aggregatedOutput") ?? ResultItems(details) ?? Text(details, "result", "content")
            ?? Text(details, "error", "message") ?? Text(details, "output", "body") ?? Text(details, "result", "detailedContent")
            ?? Text(details, "output") ?? Text(details, "result") ?? (ended ? activity.Message : null);
        var diff = Text(details, "diff") ?? Text(details, "result", "diff") ?? Text(details, "output", "diff")
            ?? Text(output?.Details, "diff");
        var error = Text(details, "result", "error") ?? Text(details, "error", "message")
            ?? (activity.Phase == AgentActivityPhase.Failed ? activity.Message : null);
        return new(name, activity.Message, command, directory,
            Number(details, "exitCode") ?? Number(details, "exit_code") ?? Number(details, "result", "exitCode"), error,
            Json(details, "arguments") ?? Json(details, "input"), text, diff, Paths(details, "readFiles"), Paths(details, "modifiedFiles"));
    }

    // The result of a tool of the CodeAlta runtime: its text items, in order.
    private static string? ResultItems(JsonElement? details)
    {
        if (Value(details, "result", "items") is not { ValueKind: JsonValueKind.Array } items) return null;
        StringBuilder? text = null;
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.String) continue;
            if (text is null) text = new();
            else text.Append('\n');
            text.Append(value.GetString());
        }
        return text?.ToString();
    }

    private static string? Json(JsonElement? details, string property)
        => Value(details, property) is { } value && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText() : null;

    private static int? Number(JsonElement? details, params string[] path)
        => Value(details, path) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number) ? number : null;

    private static string[] Paths(JsonElement? details, string property)
    {
        if (Value(details, property) is not { ValueKind: JsonValueKind.Array } values) return [];
        var paths = new List<string>();
        foreach (var value in values.EnumerateArray())
        {
            if (paths.Count == ToolCallsService.MaximumPaths) break;
            if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 1024 } path) paths.Add(path);
        }
        return paths.ToArray();
    }

    private static string? Text(JsonElement? value, params string[] path) => HistoryToolProjection.Text(value, path);

    private static JsonElement? Value(JsonElement? value, params string[] path) => HistoryToolProjection.Value(value, path);
}

/// <summary>Asks for the record of a tool call, or for the next part of one of its texts.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="SessionId">The selected session.</param>
/// <param name="Offset">The journal offset of the call's activity record, as its history row reported it.</param>
/// <param name="OutputOffset">The journal offset of the output record the timeline attached to the call, or null.</param>
/// <param name="Part"><c>arguments</c>, <c>output</c> or <c>diff</c> to continue that text; null for the call.</param>
/// <param name="Position">Where the part starts in its text, in UTF-16 code units; 0 with a null <paramref name="Part"/>.</param>
internal sealed record ToolCallRequest(string ExpectedEpoch, string SessionId, string Offset, string? OutputOffset, string? Part, int Position);

/// <summary>A tool call, the next part of one of its texts, or the reason neither is served.</summary>
/// <param name="Status">
/// <c>ok</c>, or <c>unavailable</c>, <c>stale_epoch</c>, <c>invalid</c>, <c>capacity</c>, <c>closed</c>,
/// <c>missing_session</c>, <c>missing_record</c>, <c>too_large</c> or <c>read_failed</c>.
/// </param>
/// <param name="Call">The call, for a request without a part; null otherwise.</param>
/// <param name="Text">The part, for a request with one; null otherwise.</param>
internal sealed record ToolCallResponse(string Status, ToolCallView? Call, ToolCallText? Text);

/// <summary>What the records of a tool call say.</summary>
/// <param name="Kind">The kind of activity, such as <c>ToolCall</c> or <c>CommandExecution</c>.</param>
/// <param name="Phase">The phase the record reports, such as <c>Started</c> or <c>Completed</c>.</param>
/// <param name="Name">The name of the tool, when the record has one.</param>
/// <param name="Timestamp">The time of the record.</param>
/// <param name="Message">The message of the record, when it has one.</param>
/// <param name="Command">The command line, for a call that runs one.</param>
/// <param name="WorkingDirectory">The folder the call asked to run in, when its arguments name one.</param>
/// <param name="ExitCode">The exit code, when the record has one as a number.</param>
/// <param name="Error">The error the call reported, when it failed with one.</param>
/// <param name="Arguments">The arguments, as their JSON text or as the string the provider gave.</param>
/// <param name="Output">The text of the result.</param>
/// <param name="Diff">The unified diff of the edits the call made.</param>
/// <param name="ReadFiles">The files the call read, at most <see cref="ToolCallsService.MaximumPaths"/>.</param>
/// <param name="ModifiedFiles">The files the call modified, at most <see cref="ToolCallsService.MaximumPaths"/>.</param>
internal sealed record ToolCallView(string Kind, string Phase, string? Name, DateTimeOffset Timestamp, string? Message, string? Command,
    string? WorkingDirectory, int? ExitCode, string? Error, ToolCallText? Arguments, ToolCallText? Output, ToolCallText? Diff,
    string[] ReadFiles, string[] ModifiedFiles);

/// <summary>A part of a text of a tool call.</summary>
/// <param name="Text">The part.</param>
/// <param name="Length">The length of the whole text, in UTF-16 code units.</param>
/// <param name="More">Whether the text continues after this part.</param>
internal sealed record ToolCallText(string Text, int Length, bool More);

/// <summary>Asks for what a running tool call writes.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="SessionId">The selected session.</param>
/// <param name="ActivityId">The activity identity of the call, as its history row or its live row reported it.</param>
internal sealed record ToolCallObserveRequest(string ExpectedEpoch, string SessionId, string ActivityId);

/// <summary>One item of the output of a running tool call.</summary>
/// <param name="Status"><c>ok</c>, or <c>unavailable</c>, <c>invalid</c>, <c>stale_epoch</c>, <c>capacity</c> or <c>observation_failed</c>.</param>
/// <param name="Text">What the call wrote since the previous item.</param>
/// <param name="Start">Position of the first unit of <paramref name="Text"/> in everything the call wrote, as a decimal string.</param>
/// <param name="Total">Everything the call wrote so far, in UTF-16 code units, as a decimal string.</param>
/// <param name="IsReset">The item does not continue the previous one: the page replaces what it shows.</param>
/// <param name="IsComplete">The call ended or is not running: no item follows.</param>
internal sealed record ToolCallOutputItem(string Status, string Text, string Start, string Total, bool IsReset, bool IsComplete);
