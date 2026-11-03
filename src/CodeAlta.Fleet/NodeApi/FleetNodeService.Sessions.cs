using System.Globalization;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Fleet.Channels;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Fleet.NodeApi;

public sealed partial class FleetNodeService
{
    /// <summary>The most sessions of a list.</summary>
    internal const int MaxListedSessions = 500;

    /// <summary>The longest text of a record of a history window; a longer one is cut.</summary>
    internal const int MaxHistoryText = 16 * 1024;

    /// <summary>The most bytes of the records of one history window, well under the frame limit.</summary>
    internal const int MaxHistoryWindowBytes = 1024 * 1024;

    private const int MaxSessionIdLength = 256;

    private async Task<FleetNodeInfo> NodeInfoAsync(FleetNodeCall call, FleetEmpty parameters, CancellationToken cancellationToken)
    {
        var spaces = await _host.SpaceCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        var projects = await _host.ProjectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        return new FleetNodeInfo(_options.Name, _options.Identity.DeviceId, FleetChannelProtocol.OperatingSystemName, _options.Version,
            [.. spaces.Select(static space => new FleetSpaceInfo(space.Id, space.Name))],
            [.. projects.Where(static project => !project.Archived).Select(static project => new FleetProjectInfo(project.Id,
                string.IsNullOrWhiteSpace(project.DisplayName) ? project.Name : project.DisplayName, project.ProjectPath, [.. project.Spaces]))],
            Describe(call.Device!.Permissions));
    }

    // The sessions as the desktop lists them: the snapshot of the owned workspace (WorkspaceReads), the project and the
    // space the journal header records, and the title lists show (SessionRuntimeService.ListedTitle).
    private async Task<FleetSessionListResult> ListSessionsAsync(FleetNodeCall call, FleetSessionListParams parameters, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(parameters.Limit ?? 100, 1, MaxListedSessions);
        OwnedWorkspaceSnapshot snapshot;
        try
        {
            snapshot = await _host.WorkspaceReads.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OwnedWorkspaceBusyException)
        {
            throw new FleetException(FleetErrorCodes.ServerBusy, FleetErrorHints.Retry, "The node is busy reading its sessions.");
        }

        var listed = new List<(AgentSessionMetadata Session, string? ProjectId, string? SpaceId, string Title, bool Archived)>();
        foreach (var session in snapshot.Sessions.OrderByDescending(static value => value.UpdatedAt).ThenByDescending(static value => value.SessionId, StringComparer.Ordinal))
        {
            var archived = session.ViewState?.Archived == true;
            if (archived && !parameters.IncludeArchived) continue;
            var (kind, project, space) = Scope(session, snapshot);
            if (parameters.ProjectId is { } projectId && !string.Equals(project?.Id, projectId, StringComparison.Ordinal)) continue;
            if (parameters.SpaceId is { } spaceId && !string.Equals(space ?? "default", spaceId, StringComparison.OrdinalIgnoreCase)) continue;
            listed.Add((session, project?.Id, space, SessionRuntimeService.ListedTitle(session, kind, project), archived));
            if (listed.Count > limit) break;
        }

        var page = listed.Take(limit).ToArray();
        var atWork = await _host.Commands.ReadSessionsAtWorkAsync([.. page.Select(static item => item.Session.SessionId)]).ConfigureAwait(false);
        var waiting = new HashSet<string>(_host.Commands.Asks.ListWaitingSessions(), StringComparer.OrdinalIgnoreCase);
        return new FleetSessionListResult([.. page.Select(item => new FleetSessionSummary(item.Session.SessionId, item.Title, item.ProjectId, item.SpaceId,
            item.Session.ParentSessionId ?? item.Session.ViewState?.ParentSessionId, item.Session.ViewState?.ProviderKey ?? item.Session.ProviderKey,
            item.Session.ViewState?.ModelId ?? item.Session.ModelId, item.Session.CreatedAt, item.Session.UpdatedAt,
            item.Archived ? "archived" : waiting.Contains(item.Session.SessionId) ? "waiting" : atWork.Contains(item.Session.SessionId) ? "working" : "idle"))],
            listed.Count > limit);
    }

    private static (SessionViewKind? Kind, ProjectDescriptor? Project, string? Space) Scope(AgentSessionMetadata session, OwnedWorkspaceSnapshot snapshot)
    {
        var space = session.SpaceId;
        if (!snapshot.SessionHeaders.TryGetValue(session.SessionId, out var header) || header.SessionId != session.SessionId
            || header.CreatedAt != session.CreatedAt || header.WorkingDirectory != session.WorkspacePath)
            return (null, null, space);
        space = header.SpaceId ?? space;
        if (header.Kind == SessionViewKind.GlobalSession && header.ProjectRef is null) return (SessionViewKind.GlobalSession, null, space);
        if (header.Kind == SessionViewKind.ProjectSession
            && ProjectCatalog.ResolveSessionProject(header.ProjectRef, session.WorkspacePath, snapshot.Projects) is { } project)
            return (SessionViewKind.ProjectSession, project, space);
        return (null, null, space);
    }

    private async Task<FleetSessionState> SessionStateAsync(FleetNodeCall call, FleetSessionRef parameters, CancellationToken cancellationToken)
    {
        var sessionId = RequireSessionId(parameters.SessionId);
        var state = await _host.RuntimeService.GetCurrentStateAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return new FleetSessionState(sessionId, state.RuntimeInstanceId, state.Entry?.AttachmentGeneration, state.Entry?.ActiveRunId,
            state.Entry?.QueueDrainInProgress ?? false);
    }

    // Windows of the journal by cursor, read by the owned workspace as the desktop's history routes read them
    // (ReadHistoryTailPageAsync backward, ReadHistoryPageAsync forward), each record projected and cut to a bound.
    private async Task<FleetHistoryResult> HistoryAsync(FleetNodeCall call, FleetHistoryParams parameters, CancellationToken cancellationToken)
    {
        var sessionId = RequireSessionId(parameters.SessionId);
        var backward = parameters.Direction switch
        {
            null or "backward" => true,
            "forward" => false,
            _ => throw new FleetException(FleetErrorCodes.InvalidParams, null, "The direction of a history window is 'backward' or 'forward'."),
        };
        var events = parameters.Format switch
        {
            null or "entries" => false,
            "events" => true,
            _ => throw new FleetException(FleetErrorCodes.InvalidParams, null, "The format of a history window is 'entries' or 'events'."),
        };
        var cursor = parameters.Cursor is null ? null : ParseCursor(sessionId, parameters.Cursor, backward);
        AgentSessionHistoryPage page;
        string? stamp = null;
        try
        {
            if (events && backward)
            {
                // The timeline route: the events with the end of their records and the revision, as the desktop reads them.
                if (cursor is null)
                {
                    var known = parameters.Known is { Length: > 0 and <= 512 } value ? value : null;
                    var tail = await _host.WorkspaceReads.ReadTimelineTailAsync(sessionId, known, cancellationToken).ConfigureAwait(false);
                    if (tail.Unchanged) return new FleetHistoryResult([], null, false) { Events = [], Stamp = tail.Stamp, Unchanged = true };
                    page = tail.Page!;
                    stamp = tail.Stamp;
                }
                else
                {
                    page = await _host.WorkspaceReads.ReadTimelinePageAsync(sessionId, cursor, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                page = backward
                    ? await _host.WorkspaceReads.ReadHistoryTailPageAsync(sessionId, cursor, cancellationToken).ConfigureAwait(false)
                    : await _host.WorkspaceReads.ReadHistoryPageAsync(sessionId, cursor, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OwnedWorkspaceBusyException)
        {
            throw new FleetException(FleetErrorCodes.ServerBusy, FleetErrorHints.Retry, "The node is busy reading histories.");
        }
        catch (AgentSessionHistoryException exception) when (exception.Code == "missing_session")
        {
            throw SessionNotFound(sessionId);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw SessionNotFound(sessionId);
        }
        catch (AgentSessionHistoryException exception) when (exception.Code is "invalid_cursor" or "history_changed")
        {
            throw new FleetException(FleetErrorCodes.InvalidParams, FleetErrorHints.Refresh, "The history changed since the cursor was given: read it again from its end.");
        }

        if (events) return EventsWindow(sessionId, page, backward, stamp);

        // Every record that fits: a window that would pass the budget ends before it, and its cursor resumes there.
        var entries = new List<FleetHistoryEntry>(page.Entries.Count);
        var budget = MaxHistoryWindowBytes;
        var ordered = backward ? page.Entries.Reverse() : page.Entries;
        AgentSessionHistoryEntry? stoppedAt = null;
        foreach (var entry in ordered)
        {
            var projected = Project(entry);
            var size = JsonSerializer.SerializeToUtf8Bytes(projected, FleetNodeJsonContext.Default.FleetHistoryEntry).Length;
            if (size > budget && entries.Count > 0)
            {
                stoppedAt = entry;
                break;
            }

            budget -= size;
            entries.Add(projected);
        }

        if (backward) entries.Reverse();
        var next = page.Next;
        if (stoppedAt is not null && (page.Next ?? (page.Revision is { } revision ? new AgentSessionHistoryCursor(sessionId, revision.Length, revision.LastWriteUtcTicks, 0) : null)) is { } basis)
        {
            // Backward, the next window ends before the oldest record kept; forward, it starts at the first record left out.
            var offset = backward ? entries[0].Offset : stoppedAt.Offset;
            next = offset > 0 && offset < basis.Length ? basis with { Offset = offset } : next;
        }

        return new FleetHistoryResult(entries, next is null ? null : FormatCursor(next, backward), next is not null);
    }

    // The journal's own events, as many as fit the budget of a window: one that would pass it ends the window before it
    // (backward, before the oldest kept; forward, at the first left out), and the cursor resumes there. A window that
    // is cut has no stamp: the next read of the latest window is a whole one.
    private static FleetHistoryResult EventsWindow(string sessionId, AgentSessionHistoryPage page, bool backward, string? stamp)
    {
        var events = new List<FleetHistoryEvent>(page.Entries.Count);
        var budget = MaxHistoryWindowBytes;
        var ordered = backward ? page.Entries.Reverse() : page.Entries;
        AgentSessionHistoryEntry? stoppedAt = null;
        foreach (var entry in ordered)
        {
            var json = entry.Event.ToJson();
            if (json.Length * 3 > budget && events.Count > 0)
            {
                stoppedAt = entry;
                break;
            }

            using var document = JsonDocument.Parse(json);
            budget -= json.Length * 3;
            events.Add(new FleetHistoryEvent(entry.Offset, backward ? entry.SourceEnd : null, document.RootElement.Clone()));
        }

        if (backward) events.Reverse();
        var next = page.Next;
        if (stoppedAt is not null && (page.Next ?? (page.Revision is { } revision ? new AgentSessionHistoryCursor(sessionId, revision.Length, revision.LastWriteUtcTicks, 0) : null)) is { } basis)
        {
            var offset = backward ? events[0].Offset : stoppedAt.Offset;
            next = offset > 0 && offset < basis.Length ? basis with { Offset = offset } : next;
            stamp = null;
        }

        return new FleetHistoryResult([], next is null ? null : FormatCursor(next, backward), next is not null)
        {
            Events = events,
            Revision = page.Revision is { } read ? new FleetHistoryRevision(read.Length, read.LastWriteUtcTicks) : null,
            Stamp = stamp,
        };
    }

    private static FleetHistoryEntry Project(AgentSessionHistoryEntry entry)
    {
        var value = entry.Event;
        var runId = value.RunId?.Value;
        return value switch
        {
            AgentContentCompletedEvent content => Entry("content", content.Kind.ToString(), content.Content) with { ActivityId = content.ContentId, ParentId = content.ParentActivityId },
            AgentContentDeltaEvent delta => Entry("contentDelta", delta.Kind.ToString(), delta.Delta) with { ActivityId = delta.ContentId, ParentId = delta.ParentActivityId },
            AgentActivityEvent activity => Entry("activity", activity.Kind.ToString(), activity.Message) with
            {
                Name = activity.Name, Phase = activity.Phase.ToString(), ActivityId = activity.ActivityId, ParentId = activity.ParentActivityId,
            },
            AgentErrorEvent error => Entry("error", null, error.Message),
            AgentNotesEvent notes => Entry("notes", notes.Kind.ToString(), notes.Markdown),
            AgentWorkflowEvent workflow => Entry("workflow", workflow.Kind.ToString(), workflow.Markdown ?? workflow.Title ?? workflow.Message) with
            {
                Name = workflow.WorkflowRunId, Phase = workflow.Status, ActivityId = workflow.ChildSessionId ?? workflow.StepId, ParentId = workflow.CallId,
            },
            AgentSessionUpdateEvent update => Entry("sessionUpdate", update.Kind.ToString(), null),
            _ => Entry("other", value.GetType().Name, null),
        };

        FleetHistoryEntry Entry(string type, string? kind, string? text)
        {
            var truncated = text is { Length: > MaxHistoryText };
            if (truncated)
            {
                var length = MaxHistoryText;
                if (char.IsHighSurrogate(text![length - 1])) length--;
                text = text[..length];
            }

            return new FleetHistoryEntry(entry.Offset, value.Timestamp, type, kind, text, truncated, runId);
        }
    }

    // A cursor is opaque to clients: its direction, then the journal revision and the offset of the store's cursor.
    private static string FormatCursor(AgentSessionHistoryCursor cursor, bool backward)
        => string.Create(CultureInfo.InvariantCulture, $"{(backward ? 'b' : 'f')}.{cursor.Length}.{cursor.LastWriteUtcTicks}.{cursor.Offset}");

    private static AgentSessionHistoryCursor ParseCursor(string sessionId, string text, bool backward)
    {
        var parts = text.Split('.');
        if (parts.Length != 4 || parts[0] != (backward ? "b" : "f")
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var length)
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
            || length <= 0 || offset <= 0 || offset >= length || ticks > DateTime.MaxValue.Ticks)
            throw new FleetException(FleetErrorCodes.InvalidParams, null, "The history cursor is not one this node gave for that direction.");
        return new AgentSessionHistoryCursor(sessionId, length, ticks, offset);
    }

    private static string RequireSessionId(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > MaxSessionIdLength || sessionId != sessionId.Trim()
            || sessionId.Any(static c => char.IsControl(c) || c is '/' or '\\' or ':'))
            throw new FleetException(FleetErrorCodes.InvalidParams, null, "A session id is required.");
        return sessionId;
    }

    private static FleetException SessionNotFound(string sessionId)
        => new(FleetNodeErrorCodes.SessionNotFound, null, $"The node has no session '{sessionId}'.");

    // A session the node has, to drive: found as a resume finds it (which may add a transient project for its folder).
    private async Task<SessionViewDescriptor> RequireSessionAsync(string? sessionId, CancellationToken cancellationToken)
    {
        var id = RequireSessionId(sessionId);
        return await _host.RuntimeService.TryGetResumableSessionAsync(id, cancellationToken).ConfigureAwait(false) ?? throw SessionNotFound(id);
    }

    // A session the node has, to look at: found without changing anything of the node.
    private async Task<SessionViewDescriptor> RequireViewedSessionAsync(string? sessionId, CancellationToken cancellationToken)
    {
        var id = RequireSessionId(sessionId);
        return await _host.RuntimeService.TryGetRecoverableSessionAsync(id, cancellationToken).ConfigureAwait(false) ?? throw SessionNotFound(id);
    }
}
