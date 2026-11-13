using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Fleet;

internal sealed partial class RemoteSessionHost
{
    // The journal each session was last read at, for the reads of one record (a forward window from its offset).
    private readonly ConcurrentDictionary<string, AgentHistoryRevision> _revisions = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    /// <remarks>The projects and sessions of the last listing; the page's list is made from <see cref="Listing"/>.</remarks>
    public Task<OwnedWorkspaceSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        var listing = Listing;
        var projects = (listing.Info?.Projects ?? []).Select(static project => new ProjectDescriptor
        {
            Id = project.Id, Name = project.Name, DisplayName = project.Name, ProjectPath = project.Path,
        }).ToArray();
        var sessions = listing.Sessions.Select(static session => new AgentSessionMetadata(session.Id, session.CreatedAt, session.UpdatedAt, session.Title,
            ProviderKey: session.ProviderKey, ModelId: session.ModelId, ParentSessionId: session.ParentSessionId)).ToArray();
        return Task.FromResult(new OwnedWorkspaceSnapshot(projects, sessions));
    }

    /// <inheritdoc />
    public Task<AgentSessionHistoryPage> ReadHistoryPageAsync(string sessionId, AgentSessionHistoryCursor? cursor, CancellationToken cancellationToken)
        => ReadWindowAsync(sessionId, cursor, backward: false, cancellationToken);

    /// <inheritdoc />
    public Task<AgentSessionHistoryPage> ReadHistoryTailPageAsync(string sessionId, AgentSessionHistoryCursor? cursor, CancellationToken cancellationToken)
        => ReadWindowAsync(sessionId, cursor, backward: true, cancellationToken);

    /// <inheritdoc />
    public Task<AgentSessionHistoryPage> ReadTimelinePageAsync(string sessionId, AgentSessionHistoryCursor? cursor, CancellationToken cancellationToken)
        => ReadWindowAsync(sessionId, cursor, backward: true, cancellationToken);

    /// <inheritdoc />
    public async Task<OwnedTimelineTail> ReadTimelineTailAsync(string sessionId, string? knownStamp, CancellationToken cancellationToken)
    {
        var result = await ReadEventsAsync(new FleetHistoryParams(sessionId, null, "backward", "events", knownStamp), cancellationToken).ConfigureAwait(false);
        if (result.Unchanged) return new OwnedTimelineTail(null, knownStamp);
        return new OwnedTimelineTail(ToPage(sessionId, result, backward: true), result.Stamp);
    }

    /// <inheritdoc />
    /// <remarks>The source of a journal stays on its machine: a remote session has no source view.</remarks>
    public Task<AgentHistorySourceChunk> ReadHistorySourceAsync(AgentHistoryRevision revision, long start, long end, long offset, CancellationToken cancellationToken)
        => Task.FromException<AgentHistorySourceChunk>(new AgentSessionHistoryException("unsupported_format"));

    /// <inheritdoc />
    public async Task<AgentEvent?> ReadHistoryRecordAsync(string sessionId, long offset, CancellationToken cancellationToken)
    {
        // A forward window that starts at the record: its first event is the one at that offset.
        string? cursor = null;
        if (offset > 0)
        {
            if (!_revisions.TryGetValue(sessionId, out var revision) || offset >= revision.Length) throw new AgentSessionHistoryException("invalid_cursor");
            cursor = Cursor('f', revision.Length, revision.LastWriteUtcTicks, offset);
        }

        var result = await ReadEventsAsync(new FleetHistoryParams(sessionId, cursor, "forward", "events"), cancellationToken).ConfigureAwait(false);
        return ToPage(sessionId, result, backward: false).Entries.FirstOrDefault(entry => entry.Offset == offset)?.Event
            ?? throw new AgentSessionHistoryException("invalid_cursor");
    }

    /// <inheritdoc />
    /// <remarks>The system prompt of a remote session stays on its machine.</remarks>
    public Task<AgentSystemPromptRecord> ReadSystemPromptAsync(string sessionId, long offset, CancellationToken cancellationToken)
        => Task.FromException<AgentSystemPromptRecord>(new AgentSessionHistoryException("unsupported_format"));

    private async Task<AgentSessionHistoryPage> ReadWindowAsync(string sessionId, AgentSessionHistoryCursor? cursor, bool backward, CancellationToken cancellationToken)
    {
        var direction = backward ? 'b' : 'f';
        var text = cursor is null ? null : Cursor(direction, cursor.Length, cursor.LastWriteUtcTicks, cursor.Offset);
        var result = await ReadEventsAsync(new FleetHistoryParams(sessionId, text, backward ? "backward" : "forward", "events"), cancellationToken).ConfigureAwait(false);
        return ToPage(sessionId, result, backward);
    }

    // A window of the journal's own events, with the errors of the node said as the history of this process says them.
    private async Task<FleetHistoryResult> ReadEventsAsync(FleetHistoryParams parameters, CancellationToken cancellationToken)
    {
        var client = RequireClient();
        try
        {
            return await client.ReadHistoryAsync(parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (FleetException exception) when (exception.Error.Code == FleetNodeErrorCodes.SessionNotFound)
        {
            throw new AgentSessionHistoryException("missing_session");
        }
        catch (FleetException exception) when (exception.Error.Code == FleetErrorCodes.ServerBusy)
        {
            throw new OwnedWorkspaceBusyException();
        }
        catch (FleetException exception) when (exception.Error.Code == FleetErrorCodes.InvalidParams)
        {
            throw new AgentSessionHistoryException(exception.Error.Hint == FleetErrorHints.Refresh ? "history_changed" : "invalid_cursor");
        }
    }

    private AgentSessionHistoryPage ToPage(string sessionId, FleetHistoryResult result, bool backward)
    {
        var entries = new List<AgentSessionHistoryEntry>(result.Events?.Count ?? 0);
        foreach (var value in result.Events ?? [])
        {
            AgentEvent? read;
            try { read = ReadEvent(value.Event); }
            catch (JsonException) { read = null; }
            // An event this version does not read is left out, as the journal reader of this process leaves it out.
            if (read is null) continue;
            entries.Add(new AgentSessionHistoryEntry(value.Offset, read) { SourceEnd = value.End });
        }

        AgentHistoryRevision? revision = null;
        if (result.Revision is { } read2)
        {
            revision = new AgentHistoryRevision(sessionId, read2.Length, read2.LastWriteUtcTicks);
            _revisions[sessionId] = revision;
        }

        return new AgentSessionHistoryPage(entries, ParseCursor(sessionId, result.Next, backward ? 'b' : 'f'), false) { Revision = revision };
    }

    // An event of the journal of the node; a prompt this device sent gets back the key the page gave it.
    private AgentEvent? ReadEvent(JsonElement value)
    {
        if (_ownKeyPrefix is null) return value.Deserialize(AgentJsonSerializerContext.Default.AgentEvent);
        var text = value.GetRawText();
        if (text.Contains(_ownKeyPrefix, StringComparison.Ordinal)) text = text.Replace(_ownKeyPrefix, "\"clientRequestId\":\"", StringComparison.Ordinal);
        return JsonSerializer.Deserialize(text, AgentJsonSerializerContext.Default.AgentEvent);
    }

    private static string Cursor(char direction, long length, long ticks, long offset)
        => string.Create(CultureInfo.InvariantCulture, $"{direction}.{length}.{ticks}.{offset}");

    // The cursors of the node name the journal and the offset, as the cursors of this process do: they go to the page
    // and back unchanged in meaning.
    private static AgentSessionHistoryCursor? ParseCursor(string sessionId, string? text, char direction)
    {
        if (text is null) return null;
        var parts = text.Split('.');
        if (parts.Length != 4 || parts[0].Length != 1 || parts[0][0] != direction
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var length)
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
            throw new AgentSessionHistoryException("invalid_cursor");
        return new AgentSessionHistoryCursor(sessionId, length, ticks, offset);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The display of the node, as replacements of a display of this one session. The observation outlives the
    /// connection: when the channel closes it waits for the machine and watches again, from a new baseline.
    /// </remarks>
    public async IAsyncEnumerable<RuntimeDisplayReplacement> ObserveDisplayAsync(string sessionId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var watch = await StartWatchAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (watch is null) yield break;
            await using (watch)
            {
                var epoch = Guid.NewGuid();
                var first = true;
                while (await ReadAsync(watch, cancellationToken).ConfigureAwait(false) is { } display)
                {
                    yield return ToReplacement(epoch, display, first, _ownKey);
                    first = false;
                    if (display.IsClosed) break;
                }
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                // The node ended the watch or the channel closed: the display comes back with the machine.
                try { await Task.Delay(_options.MinimumBackoff, _time, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { yield break; }
            }
        }
    }

    // The watch of a session once the machine is online; null when the node does not have the session.
    private async Task<FleetSessionWatch?> StartWatchAsync(string sessionId, CancellationToken cancellationToken)
    {
        while (true)
        {
            FleetNodeClient client;
            try { client = await WaitOnlineAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }
            try
            {
                return await client.WatchAsync(sessionId, cancellationToken).ConfigureAwait(false);
            }
            catch (FleetException exception) when (exception.Error.Code is FleetNodeErrorCodes.SessionNotFound or FleetErrorCodes.Forbidden)
            {
                return null;
            }
            catch (Exception exception) when (exception is FleetException or IOException or ObjectDisposedException or InvalidOperationException or TimeoutException)
            {
                try { await Task.Delay(_options.MinimumBackoff, _time, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return null; }
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
    }

    private static async Task<FleetSessionDisplay?> ReadAsync(FleetSessionWatch watch, CancellationToken cancellationToken)
    {
        try
        {
            return await watch.Updates.WaitToReadAsync(cancellationToken).ConfigureAwait(false) && watch.Updates.TryRead(out var display) ? display : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    internal static RuntimeDisplayReplacement ToReplacement(Guid epoch, FleetSessionDisplay display, bool initial, string? ownKey = null)
    {
        // The display of the node keeps the texts of earlier runs, without their times: the page shows what it does not
        // find in the loaded timeline after it, so only the run of the session now (the one updated last) is given.
        var run = display.Lifecycle?.RunId ?? display.Text.LastOrDefault(static text => text.RunId is not null)?.RunId;
        var texts = ImmutableArray.CreateBuilder<RuntimeDisplayText>(display.Text.Count);
        foreach (var text in display.Text)
        {
            if (run is not null && text.RunId != run) continue;
            if (!Enum.TryParse<AgentContentKind>(text.Kind, out var kind)) continue;
            // A prompt this device sent is known to the page by its own key: the echo it shows is then the same message.
            var key = text.ClientRequestId is { } recorded && ownKey is not null && recorded.StartsWith(ownKey, StringComparison.Ordinal) ? recorded[ownKey.Length..] : text.ClientRequestId;
            texts.Add(new RuntimeDisplayText(text.RunId, text.ContentId, kind, text.Text, text.IsComplete, text.IsTruncated, false) { ClientRequestId = key });
        }

        var tools = ImmutableArray.CreateBuilder<RuntimeDisplayToolActivity>(display.Tools.Count);
        foreach (var tool in display.Tools)
        {
            if (run is not null && tool.RunId != run) continue;
            if (!Enum.TryParse<AgentActivityPhase>(tool.Phase, out var phase)) continue;
            tools.Add(new RuntimeDisplayToolActivity("fleet", tool.RunId, tool.ActivityId, phase, tool.Name, false));
        }

        RuntimeDisplayLifecycle? lifecycle = display.Lifecycle is { } life && Enum.TryParse<SessionLifecycleEventKind>(life.Kind, out var lifeKind)
            ? new RuntimeDisplayLifecycle(lifeKind, life.RunId, life.Message)
            : null;
        AgentSessionUpdateKind? status = display.StatusKind is { } statusKind && Enum.TryParse<AgentSessionUpdateKind>(statusKind, out var parsed) ? parsed : null;
        var session = new RuntimeDisplaySession(display.SessionId, display.Revision, lifecycle, display.QueuedPromptCount, null, status, display.StatusMessage,
            texts.ToImmutable(), false, 0, 0) { ToolActivities = tools.ToImmutable() };
        return new RuntimeDisplayReplacement(new RuntimeDisplaySnapshot(epoch, display.Revision, false, [session], 0, 0), initial,
            initial ? null : display.PreviousRevision, !initial && display.HasGap);
    }

    /// <inheritdoc />
    public async Task<SessionRuntimeCurrentState> ReadRuntimeStateAsync(string sessionId, CancellationToken cancellationToken)
    {
        var state = await RequireClient().GetSessionStateAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var summary = Listing.Sessions.FirstOrDefault(session => string.Equals(session.Id, sessionId, StringComparison.OrdinalIgnoreCase));
        var provider = summary?.ProviderKey ?? "fleet";
        // The suggestion the machine shows, unless the user of this computer turned suggestions off.
        var suggestion = _options.ShowsPromptSuggestions?.Invoke() == true ? AgentPromptSuggestionEvent.Normalize(state.PromptSuggestion) : null;
        var suggestionRunId = suggestion is null ? null : state.PromptSuggestionRunId;
        if (state.AttachmentGeneration is { } generation and > 0)
        {
            return new SessionRuntimeCurrentState(state.RuntimeInstanceId, sessionId, false,
                new SessionRuntimeCurrentEntry(generation, false, false, state.ActiveRunId, state.QueueDrainInProgress, provider, provider, summary?.ModelId,
                    null, null, null) { PromptSuggestion = suggestion, PromptSuggestionRunId = suggestionRunId });
        }

        return new SessionRuntimeCurrentState(state.RuntimeInstanceId, sessionId, false, null)
        {
            PromptSuggestion = suggestion,
            PromptSuggestionRunId = suggestionRunId,
        };
    }

    /// <inheritdoc />
    /// <remarks>The scope the page names is the one of this window; the node checks its own.</remarks>
    public async Task<OwnedRuntimeObservation> ObserveRuntimeStateAsync(string sessionId, DateTimeOffset createdAt, string scope, string? projectId,
        string? projectPath, CancellationToken cancellationToken)
    {
        try
        {
            return new OwnedRuntimeObservation("ok", await ReadRuntimeStateAsync(sessionId, cancellationToken).ConfigureAwait(false));
        }
        catch (FleetException exception) when (exception.Error.Code == FleetNodeErrorCodes.SessionNotFound)
        {
            return new OwnedRuntimeObservation("metadata_unavailable", null);
        }
        catch (ObjectDisposedException)
        {
            return new OwnedRuntimeObservation("metadata_unavailable", null);
        }
    }

    /// <inheritdoc />
    /// <remarks>What a tool call writes is not followed across machines yet: its result shows in the timeline.</remarks>
    public IAsyncEnumerable<RuntimeToolOutputUpdate> ObserveToolOutputAsync(string sessionId, string activityId, CancellationToken cancellationToken)
        => AsyncEnumerable.Empty<RuntimeToolOutputUpdate>();
}
