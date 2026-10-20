using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;
using XenoAtom.Logging;

namespace CodeAlta.Desktop.Rpc;

internal sealed partial class WorkspaceService
{
    // The records of a page of a preview, as a page of a journal has them: at most this many, and about this much text.
    private const int ClaudeCodePreviewPageRecords = 100;
    private const int ClaudeCodePreviewPageCharacters = 256 * 1024;

    private readonly Func<ProjectDescriptor?, ModelProviderDescriptor, string, CancellationToken, Task<ExternalConversationPreviewResult>>? _previewExternalConversation;

    /// <summary>
    /// A page of a Claude Code session started in VS Code or a terminal, which no session of CodeAlta continues, read to
    /// be shown before it is imported: the records an import would write, as a page of the timeline of a session, the
    /// newest first. Nothing is written.
    /// </summary>
    /// <param name="request">
    /// The exact project it is shown for (none for a session of no project), the session of the CLI as the listing gave
    /// it, the expected host epoch, and the place of the page: none for the newest records, else the <c>next</c> of the
    /// page after it.
    /// </param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>
    /// <c>ok</c> with the page, whose session is <see cref="SessionRuntimeService.ExternalConversationPreviewPrefix"/> and
    /// the session of the CLI; <c>existing</c> with the session of CodeAlta that continues the conversation; or
    /// <c>not_found</c>, <c>too_large</c>, <c>history_changed</c> (the transcript changed since the page after it),
    /// <c>invalid_cursor</c>, or a refusal (<c>unconfigured</c>, <c>invalid_scope</c>, <c>stale_epoch</c>,
    /// <c>project_missing</c>, <c>no_provider</c>, <c>closed</c>, <c>read_failed</c>, <c>wire_limit</c>).
    /// </returns>
    /// <exception cref="OperationCanceledException">The caller cancels its wait.</exception>
    [NeoRpcMethod("claudeCodeSessionPreview", TimeoutMilliseconds = 120_000)]
    public async Task<WorkspaceClaudeCodeSessionPreviewResponse> ClaudeCodeSessionPreviewAsync(WorkspaceClaudeCodeSessionPreviewRequest request,
        CancellationToken cancellationToken)
    {
        WorkspaceClaudeCodeSessionPreviewResponse Reply(string status, string? sessionId = null, string? title = null, HistoryResponse? page = null)
            => new(status, _importEpoch, request?.ProjectId, request?.ProjectPath, request?.CliSessionId, sessionId, title, page ?? new(status, [], null, false));
        if (_previewExternalConversation is null || _sessionProviders is null || _importCatalog is null || _importEpoch is null) return Reply("unconfigured");
        if (request is null || !ValidEpoch(request.ExpectedHostEpoch) || !ValidClaudeCodeScope(request.ProjectId, request.ProjectPath)
            || !ClaudeCodeConversationWire.IsConversationId(request.CliSessionId) || !ValidSpace(request.SpaceId) || !ValidProvider(request.ProviderId))
            return Reply("invalid_scope");
        if (request.ExpectedHostEpoch != _importEpoch) return Reply("stale_epoch");
        var previewId = ClaudeCodePreviewSessionId(request.CliSessionId);
        AgentSessionHistoryCursor? cursor;
        try { cursor = ParseCursor(new HistoryRequest(previewId, request.Cursor), 2); }
        catch (Exception exception) when (exception is FormatException or OverflowException) { return Reply("invalid_cursor"); }
        var project = request.ProjectId is null ? null : await ExactProjectAsync(request.ProjectId, request.ProjectPath!).ConfigureAwait(false);
        if (request.ProjectId is not null && project is null) return Reply("project_missing");
        if (ClaudeCodeProviderOf(project?.ProjectPath, request.SpaceId, request.ProviderId) is not { } provider) return Reply("no_provider");
        ExternalConversationPreviewResult result;
        try { result = await _previewExternalConversation(project, provider, request.CliSessionId, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception failure)
        {
            if (LogManager.IsInitialized) LogManager.GetLogger("CodeAlta.Desktop.Rpc").Warn(failure, "A Claude Code session could not be read to be shown");
            return Reply("read_failed");
        }

        if (result.Status == "existing") return result.SessionId is { } existing && ValidScopeValue(existing, 256) ? Reply("existing", existing) : Reply("read_failed");
        if (result.Status != "ok" || result.Preview is not { } preview) return Reply(result.Status is "not_found" or "too_large" or "closed" ? result.Status : "read_failed");
        var title = preview.Title.Length <= 256 ? preview.Title : preview.Title[..(char.IsHighSurrogate(preview.Title[255]) ? 255 : 256)];
        if (cursor is not null && cursor.Length == preview.Length && cursor.LastWriteUtcTicks == preview.LastWriteUtcTicks && cursor.Offset > preview.Events.Count)
            return Reply("invalid_cursor", title: title);
        try
        {
            var page = ClaudeCodePreviewPage(previewId, preview, cursor);
            if (page is null) return Reply("history_changed", title: title);
            var projected = ProjectHistory(page, 2);
            return Reply(projected.Status, title: title, page: projected);
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException)
        {
            return Reply("read_failed", title: title);
        }
    }

    /// <summary>The identity the records of the preview of a conversation of the CLI are given: they belong to no session.</summary>
    internal static string ClaudeCodePreviewSessionId(string cliSessionId) => SessionRuntimeService.ExternalConversationPreviewPrefix + cliSessionId;

    /// <summary>
    /// The page of a preview that ends before <paramref name="cursor"/> (the newest without one), as a reverse page of a
    /// journal: the records are numbered from 1 in order, their number is their offset, and the cursor of the older page
    /// names the transcript by its length and its last write time. Null when the cursor names another revision of it.
    /// </summary>
    internal static AgentSessionHistoryPage? ClaudeCodePreviewPage(string previewId, ExternalConversationPreview preview, AgentSessionHistoryCursor? cursor)
    {
        var events = preview.Events;
        var end = events.Count;
        if (cursor is not null)
        {
            if (cursor.Length != preview.Length || cursor.LastWriteUtcTicks != preview.LastWriteUtcTicks) return null;
            if (cursor.Offset > events.Count) throw new FormatException("The place of the page is beyond the records of the preview.");
            end = (int)cursor.Offset - 1;
        }

        var start = end;
        var characters = 0;
        while (start > 0 && end - start < ClaudeCodePreviewPageRecords)
        {
            var size = PreviewCharacters(events[start - 1]);
            if (start < end && characters + size > ClaudeCodePreviewPageCharacters) break;
            characters += size;
            start--;
        }

        var entries = new AgentSessionHistoryEntry[end - start];
        for (var index = start; index < end; index++) entries[index - start] = new(index + 1, events[index]);
        // The offset of a record must stay below the length the cursor names: the records are far fewer than the bytes they come from.
        var next = start > 0 && start + 1 < preview.Length ? new AgentSessionHistoryCursor(previewId, preview.Length, preview.LastWriteUtcTicks, start + 1) : null;
        return new AgentSessionHistoryPage(entries, next, false);
    }

    // About how much text a record of a preview carries.
    private static int PreviewCharacters(AgentEvent value)
        => value switch
        {
            AgentContentCompletedEvent completed => completed.Content.Length + (completed.Details?.GetRawText().Length ?? 0),
            AgentActivityEvent activity => (activity.Message?.Length ?? 0) + (activity.Details?.GetRawText().Length ?? 0),
            AgentSessionUpdateEvent update => update.Message?.Length ?? 0,
            _ => 256,
        };
}

/// <param name="ExpectedHostEpoch">The epoch of the host the page knows.</param>
/// <param name="ProjectId">The exact project it is shown for; null with <paramref name="ProjectPath"/> for a session of no project.</param>
/// <param name="ProjectPath">Its exact folder.</param>
/// <param name="CliSessionId">The session of the CLI, as the listing gave it.</param>
/// <param name="Cursor">None for the newest records; else the <c>next</c> of the page after the one asked for.</param>
/// <param name="SpaceId">The space the page shows, whose provider reads the session; null for the default space.</param>
/// <param name="ProviderId">The provider that listed the session, which reads it; null for the one of the project in that space.</param>
internal sealed record WorkspaceClaudeCodeSessionPreviewRequest(string ExpectedHostEpoch, string? ProjectId, string? ProjectPath, string CliSessionId,
    HistoryCursor? Cursor, string? SpaceId = null, string? ProviderId = null);

/// <summary>A page of the preview of a Claude Code session started elsewhere.</summary>
/// <param name="Status"><c>ok</c>, <c>existing</c>, or why there is no page.</param>
/// <param name="SessionId">The session of CodeAlta that continues the conversation, with <c>existing</c>.</param>
/// <param name="Title">The title of the conversation, with <c>ok</c>.</param>
/// <param name="Page">The page, as the timeline of a session reads one; its status is the one of the reply.</param>
internal sealed record WorkspaceClaudeCodeSessionPreviewResponse(string Status, string? HostEpoch, string? ProjectId, string? ProjectPath, string? CliSessionId,
    string? SessionId, string? Title, HistoryResponse Page);
