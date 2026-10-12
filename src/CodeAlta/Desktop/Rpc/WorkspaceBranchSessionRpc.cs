using System.Globalization;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

// The fork and the rewind of a session, and the turns that name the points they are made at.
internal sealed partial class WorkspaceService
{
    // A page of turns: with 800 characters of each prompt it stays under the 700 KiB budget of a response at six
    // bytes per UTF-16 unit, whatever the prompts hold.
    private const int TurnsPerPage = 100;
    private const int TurnPromptLength = 800;
    // The prompt a fork or a rewind hands back to the composer, as long as a draft can be.
    private const int ReturnedPromptLength = 32 * 1024;

    private readonly Func<string, CancellationToken, Task<SessionTurnsSnapshot?>>? _readTurns;
    private readonly Func<string, long, CancellationToken, Task<AgentEvent?>>? _readRecord;
    private readonly Func<string, string?, string, string?, string?, Task<SessionBranchOutcome>>? _forkSession;
    private readonly Func<string, string?, string, string, bool, Task<SessionBranchOutcome>>? _rewindSession;
    private Task? _branchWork;

    /// <summary>Lists the turns of a session, the most recent page first: each is a point the session can be forked at or rewound to.</summary>
    [NeoRpcMethod("sessionTurns")]
    public async Task<WorkspaceSessionTurnsResponse> SessionTurnsAsync(WorkspaceSessionTurnsRequest request, CancellationToken cancellationToken)
    {
        WorkspaceSessionTurnsResponse Reply(string status) => new(status, _importEpoch, request?.SessionId, [], 0, null, null);
        if (_readTurns is null || _importEpoch is null) return Reply("unconfigured");
        if (request is null || !ValidEpoch(request.ExpectedHostEpoch) || !ValidScopeValue(request.SessionId, 256)
            || request.BeforeTurn is < 1) return Reply("invalid_scope");
        if (request.ExpectedHostEpoch != _importEpoch) return Reply("stale_epoch");
        SessionTurnsSnapshot? snapshot;
        try
        {
            snapshot = await _readTurns(request.SessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Reply("read_failed");
        }

        if (snapshot is null) return Reply("session_missing");
        var turns = snapshot.Turns.Turns;
        // The page ends before the turn the caller has already, or with the last turn.
        var end = Math.Min(turns.Count, (request.BeforeTurn ?? turns.Count + 1) - 1);
        var start = Math.Max(0, end - TurnsPerPage);
        var page = new List<WorkspaceSessionTurn>(Math.Max(0, end - start));
        for (var index = start; index < end; index++)
        {
            var turn = turns[index];
            var prompt = Bounded(turn.PromptPreview, TurnPromptLength, out var cut);
            page.Add(new WorkspaceSessionTurn(
                turn.Number,
                turn.RunId,
                turn.RunId == snapshot.ActiveRunId ? "running" : turn.Status switch
                {
                    AgentSessionTurnStatus.Completed => "completed",
                    AgentSessionTurnStatus.Failed => "failed",
                    _ => "unfinished",
                },
                turn.StartedAt,
                turn.EndedAt,
                turn.PromptOffset is { } offset ? offset.ToString(CultureInfo.InvariantCulture) : null,
                prompt,
                cut || turn.PromptTruncated,
                ValidScopeValue(turn.SourceSessionId, 256) ? turn.SourceSessionId : null,
                turn.AskId is not null,
                turn.StartedByProvider,
                turn.SteerCount,
                turn.AssistantMessageCount,
                turn.ToolCallCount,
                turn.CompactionCount));
        }

        var revision = snapshot.Turns.Revision;
        return new("ok", _importEpoch, request.SessionId, [.. page], turns.Count,
            ValidScopeValue(snapshot.ActiveRunId, 256) ? snapshot.ActiveRunId : null,
            new(revision.SessionId, revision.Length.ToString(CultureInfo.InvariantCulture), revision.LastWriteUtcTicks.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>Forks an exact owned catalog session; cancellation only stops waiting after admission.</summary>
    [NeoRpcMethod("forkSession")]
    public async Task<WorkspaceForkSessionResponse> ForkSessionAsync(WorkspaceForkSessionRequest request, CancellationToken cancellationToken)
    {
        WorkspaceForkSessionResponse Reply(string status) => new(status, _importEpoch, null, null, null, request?.SessionId, null, null, 0, null, false);
        if (_forkSession is null || _importCatalog is null || _importEpoch is null) return Reply("unconfigured");
        if (request is null || !ValidEpoch(request.ExpectedHostEpoch) || !ValidBranchScope(request.Scope, request.ProjectId, request.ProjectPath, request.SessionId)
            || request.BeforeRunId is not null && !ValidScopeValue(request.BeforeRunId, 256) || !ValidTitle(request.Title))
            return Reply("invalid_scope");
        if (request.ExpectedHostEpoch != _importEpoch) return Reply("stale_epoch");
        cancellationToken.ThrowIfCancellationRequested();
        Task<WorkspaceForkSessionResponse> work;
        lock (_sessionGate)
        {
            if (_sessionsClosed) return Reply("closed");
            if (_deleteWork is not null || _renameWork is not null || _branchWork is not null) return Reply("busy");
            var completion = new TaskCompletionSource<WorkspaceForkSessionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _branchWork = work = completion.Task;
            _ = ForkOwnedAsync(request, completion);
        }

        return await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Rewinds an exact owned catalog session; cancellation only stops waiting after admission.</summary>
    [NeoRpcMethod("rewindSession")]
    public async Task<WorkspaceRewindSessionResponse> RewindSessionAsync(WorkspaceRewindSessionRequest request, CancellationToken cancellationToken)
    {
        WorkspaceRewindSessionResponse Reply(string status) => new(status, _importEpoch, null, null, null, request?.SessionId, 0, 0, null, false, null, null, 0);
        if (_rewindSession is null || _importCatalog is null || _importEpoch is null) return Reply("unconfigured");
        if (request is null || !ValidEpoch(request.ExpectedHostEpoch) || !ValidBranchScope(request.Scope, request.ProjectId, request.ProjectPath, request.SessionId)
            || !ValidScopeValue(request.BeforeRunId, 256))
            return Reply("invalid_scope");
        if (request.ExpectedHostEpoch != _importEpoch) return Reply("stale_epoch");
        cancellationToken.ThrowIfCancellationRequested();
        Task<WorkspaceRewindSessionResponse> work;
        lock (_sessionGate)
        {
            if (_sessionsClosed) return Reply("closed");
            if (_deleteWork is not null || _renameWork is not null || _branchWork is not null) return Reply("busy");
            var completion = new TaskCompletionSource<WorkspaceRewindSessionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _branchWork = work = completion.Task;
            _ = RewindOwnedAsync(request, completion);
        }

        return await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ForkOwnedAsync(WorkspaceForkSessionRequest request, TaskCompletionSource<WorkspaceForkSessionResponse> completion)
    {
        WorkspaceForkSessionResponse Reply(string status, SessionBranchOutcome? outcome = null, string? prompt = null, bool cut = false)
            => new(status, _importEpoch, request.Scope, request.ProjectId, request.ProjectPath, request.SessionId,
                outcome?.Session?.SessionId, outcome?.Session?.Title, outcome?.KeptTurnCount ?? 0, prompt, cut);
        try
        {
            if (!await ScopeExistsAsync(request.Scope, request.ProjectId, request.ProjectPath).ConfigureAwait(false))
            {
                completion.TrySetResult(Reply("scope_missing"));
                return;
            }

            var outcome = await _forkSession!(request.SessionId, request.ProjectId, request.ProjectPath!, request.BeforeRunId, request.Title).ConfigureAwait(false);
            if (outcome.Status != "ok" || outcome.Session is null)
            {
                completion.TrySetResult(Reply(outcome.Status == "ok" ? "fork_unconfirmed" : outcome.Status));
                return;
            }

            // The prompt of the turn the fork stops before is what the user continues from: it goes to the composer.
            var (prompt, cut) = await ReadPromptAsync(request.SessionId, outcome.NextTurn).ConfigureAwait(false);
            completion.TrySetResult(Reply("ok", outcome, prompt, cut));
        }
        catch (Exception)
        {
            // The journal of the fork may exist. No retry or definite refusal after admission.
            completion.TrySetResult(Reply("fork_unconfirmed"));
        }
        finally { lock (_sessionGate) { if (ReferenceEquals(_branchWork, completion.Task)) _branchWork = null; } }
    }

    private async Task RewindOwnedAsync(WorkspaceRewindSessionRequest request, TaskCompletionSource<WorkspaceRewindSessionResponse> completion)
    {
        WorkspaceRewindSessionResponse Reply(string status, SessionBranchOutcome? outcome = null, string? prompt = null, bool cut = false)
            => new(status, _importEpoch, request.Scope, request.ProjectId, request.ProjectPath, request.SessionId,
                outcome?.KeptTurnCount ?? 0, outcome is null ? 0 : outcome.Turns.Count - outcome.KeptTurnCount, prompt, cut,
                outcome?.RemovedTurns?.SessionId, outcome?.RemovedTurns?.Title, outcome?.Session?.RewindCount ?? 0);
        try
        {
            if (!await ScopeExistsAsync(request.Scope, request.ProjectId, request.ProjectPath).ConfigureAwait(false))
            {
                completion.TrySetResult(Reply("scope_missing"));
                return;
            }

            // The record of the prompt is gone once the session is rewound: it is read first.
            var (prompt, cut) = (null as string, false);
            if (_readTurns is not null && await _readTurns(request.SessionId, CancellationToken.None).ConfigureAwait(false) is { } snapshot)
            {
                (prompt, cut) = await ReadPromptAsync(request.SessionId,
                    snapshot.Turns.Turns.FirstOrDefault(turn => turn.RunId == request.BeforeRunId)).ConfigureAwait(false);
            }

            var outcome = await _rewindSession!(request.SessionId, request.ProjectId, request.ProjectPath!, request.BeforeRunId, request.KeepRemovedTurns)
                .ConfigureAwait(false);
            completion.TrySetResult(outcome.Status == "ok" ? Reply("ok", outcome, prompt, cut) : Reply(outcome.Status));
        }
        catch (Exception)
        {
            // The journal may have been replaced. No retry or definite refusal after admission.
            completion.TrySetResult(Reply("rewind_unconfirmed"));
        }
        finally { lock (_sessionGate) { if (ReferenceEquals(_branchWork, completion.Task)) _branchWork = null; } }
    }

    // The whole text of the prompt that started a turn, as its record holds it; what the turn list keeps of it otherwise.
    private async Task<(string? Prompt, bool Cut)> ReadPromptAsync(string sessionId, AgentSessionTurn? turn)
    {
        if (turn is null) return (null, false);
        // A prompt an agent sent, or the answer to a question, is not one the user wrote: nothing to continue from.
        if (turn.SourceSessionId is not null || turn.AskId is not null || turn.StartedByProvider) return (null, false);
        var text = turn.PromptPreview;
        var cut = turn.PromptTruncated;
        if (cut && turn.PromptOffset is { } offset && _readRecord is not null)
        {
            try
            {
                if (await _readRecord(sessionId, offset, CancellationToken.None).ConfigureAwait(false)
                    is AgentContentCompletedEvent { Kind: AgentContentKind.User } record && record.ContentId == turn.PromptContentId)
                {
                    text = record.Content;
                    cut = false;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or AgentSessionHistoryException) { }
        }

        var bounded = Bounded(text, ReturnedPromptLength, out var shortened);
        return (bounded.Length == 0 ? null : bounded, cut || shortened);
    }

    private async Task<bool> ScopeExistsAsync(string scope, string? projectId, string? projectPath)
    {
        if (scope != "project") return true;
        var project = await _importCatalog!.GetByIdAsync(projectId!, CancellationToken.None).ConfigureAwait(false);
        return project is not null && !project.Archived && project.Id == projectId && project.ProjectPath == projectPath;
    }

    private bool ValidBranchScope(string? scope, string? projectId, string? projectPath, string? sessionId)
        => ValidScopeValue(sessionId, 256) && scope is "global" or "project"
            && (scope != "global" || projectId is null && projectPath == _importCatalog!.Options.GlobalRoot)
            && (scope != "project" || ValidScopeValue(projectId, 256) && ValidScopeValue(projectPath, 4096));

    // Text for the wire: no lone surrogate, at most a number of UTF-16 units, cut between characters.
    private static string Bounded(string? value, int maximum, out bool cut)
    {
        cut = false;
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var length = Math.Min(value.Length, maximum);
        if (length < value.Length && length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
        cut = length < value.Length;
        var text = cut ? value[..length] : value;
        for (var index = 0; index < text.Length; index++)
        {
            if (!char.IsSurrogate(text[index])) continue;
            if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])) { index++; continue; }
            // A journal can hold what no JSON string can: such a character is replaced, as a decoder does.
            return string.Create(text.Length, text, static (span, source) =>
            {
                for (var position = 0; position < source.Length; position++)
                {
                    var value = source[position];
                    if (char.IsHighSurrogate(value) && position + 1 < source.Length && char.IsLowSurrogate(source[position + 1]))
                    {
                        span[position] = value;
                        span[position + 1] = source[position + 1];
                        position++;
                    }
                    else span[position] = char.IsSurrogate(value) ? '�' : value;
                }
            });
        }

        return text;
    }
}

internal sealed record WorkspaceSessionTurnsRequest(string ExpectedHostEpoch, string SessionId, int? BeforeTurn = null);

/// <summary>
/// One turn of a session. <paramref name="Status"/> is completed, failed, unfinished or running.
/// <paramref name="StartedByProvider"/> is true for a turn the provider started by itself, whose prompt nobody wrote.
/// </summary>
internal sealed record WorkspaceSessionTurn(int Number, string RunId, string Status, DateTimeOffset StartedAt, DateTimeOffset? EndedAt,
    string? PromptOffset, string Prompt, bool PromptTruncated, string? SourceSessionId, bool AnswersQuestion, bool StartedByProvider, int Steers,
    int AssistantMessages, int ToolCalls, int Compactions);

/// <summary>A page of turns in session order. <paramref name="Total"/> is the number of turns of the session.</summary>
internal sealed record WorkspaceSessionTurnsResponse(string Status, string? HostEpoch, string? SessionId, WorkspaceSessionTurn[] Turns,
    int Total, string? ActiveRunId, HistoryRevision? Revision);

internal sealed record WorkspaceForkSessionRequest(string ExpectedHostEpoch, string Scope, string? ProjectId, string? ProjectPath,
    string SessionId, string? BeforeRunId, string? Title);

/// <summary>The fork. <paramref name="Prompt"/> is the prompt of the turn the fork stops before, for the composer.</summary>
internal sealed record WorkspaceForkSessionResponse(string Status, string? HostEpoch, string? Scope, string? ProjectId, string? ProjectPath,
    string? SessionId, string? ForkSessionId, string? Title, int TurnCount, string? Prompt, bool PromptTruncated);

internal sealed record WorkspaceRewindSessionRequest(string ExpectedHostEpoch, string Scope, string? ProjectId, string? ProjectPath,
    string SessionId, string BeforeRunId, bool KeepRemovedTurns);

/// <summary>The rewound session. <paramref name="Prompt"/> is the prompt of the first removed turn, for the composer.</summary>
internal sealed record WorkspaceRewindSessionResponse(string Status, string? HostEpoch, string? Scope, string? ProjectId, string? ProjectPath,
    string? SessionId, int TurnCount, int RemovedTurnCount, string? Prompt, bool PromptTruncated, string? RemovedTurnsSessionId,
    string? RemovedTurnsTitle, int RewindCount);
