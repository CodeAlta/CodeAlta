using CodeAlta.Catalog;
using CodeAlta.Catalog.Worktrees;
using NeoAstra.Rpc;
using XenoAtom.Logging;

namespace CodeAlta.Desktop.Rpc;

internal sealed partial class WorkspaceService
{
    // The worktrees made for the next session of a project while its prompt is typed, by the identifier the page gave
    // them; the oldest are forgotten. Guarded by _sessionGate.
    private readonly Dictionary<string, PreparedWorktree> _preparedWorktrees = new(StringComparer.Ordinal);
    private readonly Queue<string> _preparedWorktreeOrder = new();
    private const int KeptPreparedWorktrees = 32;
    // Stops git when the window closes while a worktree is still made.
    private readonly CancellationTokenSource _preparationsClosing = new();
    // What git still does for those worktrees (made, removed), forgotten ones included: the window that closes waits
    // for it. Guarded by _sessionGate.
    private readonly List<Task> _preparedWork = [];

    /// <summary>
    /// Starts making a git worktree for the next session of a project, without waiting for it: the page shows its steps
    /// while the prompt is typed, and creates the session in it once it is ready (<c>createSession</c> with
    /// <c>PreparedWorktreeId</c>).
    /// </summary>
    /// <remarks>
    /// The same identifier again is the same worktree. A worktree that no session takes is removed when the page
    /// discards it (<c>discardWorktree</c>); git is stopped when the window closes.
    /// </remarks>
    /// <param name="request">The expected host epoch, the exact catalog project, the branch to start from and the identifier of the worktree.</param>
    /// <param name="cancellationToken">Cancels the reading of the catalog, before anything is made.</param>
    /// <returns>Where the worktree is, or a refusal of the request.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels before the worktree is started.</exception>
    [NeoRpcMethod("prepareWorktree")]
    public async Task<WorkspacePreparedWorktreeResponse> PrepareWorktreeAsync(WorkspacePrepareWorktreeRequest request, CancellationToken cancellationToken)
    {
        WorkspacePreparedWorktreeResponse Reply(string status) => new(status, _importEpoch, []);
        if (_worktrees is null || _importCatalog is null || _importEpoch is null) return Reply("unconfigured");
        if (request is null || !ValidEpoch(request.ExpectedHostEpoch) || !ValidRequestId(request.RequestId)
            || !ValidScopeValue(request.ProjectId, 256) || !ValidScopeValue(request.ProjectPath, 4096)
            || request.BaseBranch is not null && !GitWorktreeService.IsReferenceName(request.BaseBranch))
            return Reply("invalid_scope");
        try { if (!Path.IsPathFullyQualified(request.ProjectPath)) return Reply("invalid_scope"); }
        catch (ArgumentException) { return Reply("invalid_scope"); }
        if (request.ExpectedHostEpoch != _importEpoch) return Reply("stale_epoch");
        lock (_sessionGate)
        {
            if (_preparedWorktrees.TryGetValue(request.RequestId, out var known)) return ReadPrepared(known);
        }

        var project = await _importCatalog.GetByIdAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project is null || project.Archived || project.Id != request.ProjectId || project.ProjectPath != request.ProjectPath
            || !Directory.Exists(project.ProjectPath))
            return Reply("project_missing");

        PreparedWorktree prepared;
        lock (_sessionGate)
        {
            if (_sessionsClosed) return Reply("closed");
            if (_preparedWorktrees.TryGetValue(request.RequestId, out var known)) return ReadPrepared(known);
            prepared = new PreparedWorktree(project, request.BaseBranch);
            _preparedWorktrees[request.RequestId] = prepared;
            _preparedWorktreeOrder.Enqueue(request.RequestId);
            while (_preparedWorktreeOrder.Count > KeptPreparedWorktrees
                && _preparedWorktrees.Remove(_preparedWorktreeOrder.Dequeue(), out var forgotten))
            {
                // Forgotten, it can no longer be taken: it goes as if the page had discarded it.
                if (!forgotten.Used && !forgotten.Discarded) Discard(forgotten);
            }

            // On the thread pool: git is started there, never under the gate nor on the thread of the caller.
            prepared.Work = Task.Run(() => PrepareOwnedAsync(prepared));
            Track(prepared.Work);
            return ReadPrepared(prepared);
        }
    }

    /// <summary>Reads where a worktree the page asked for is: its steps, then the worktree or why git made none.</summary>
    /// <param name="request">The expected host epoch and the identifier of the worktree.</param>
    /// <returns>
    /// <c>preparing</c>, <c>ready</c>, <c>failed</c>, <c>used</c> (a session took it), <c>discarded</c>, <c>unknown</c>,
    /// or a refusal of the request. Nothing is waited for.
    /// </returns>
    [NeoRpcMethod("preparedWorktree")]
    public WorkspacePreparedWorktreeResponse PreparedWorktreeState(WorkspaceCreateSessionOutcomeRequest request)
    {
        WorkspacePreparedWorktreeResponse Reply(string status) => new(status, _importEpoch, []);
        if (_importEpoch is null) return Reply("unconfigured");
        if (request is null || !ValidEpoch(request.ExpectedHostEpoch) || !ValidRequestId(request.RequestId)) return Reply("invalid_scope");
        if (request.ExpectedHostEpoch != _importEpoch) return Reply("stale_epoch");
        lock (_sessionGate)
        {
            return _preparedWorktrees.TryGetValue(request.RequestId, out var prepared) ? ReadPrepared(prepared) : Reply("unknown");
        }
    }

    /// <summary>
    /// Gives up a worktree the page asked for and no session took: it is removed with its branch once git has made it,
    /// unless it holds changes. A worktree a session took stays.
    /// </summary>
    /// <param name="request">The expected host epoch and the identifier of the worktree.</param>
    /// <returns>The state of the worktree after the request, or a refusal of the request. The removal is not waited for.</returns>
    [NeoRpcMethod("discardWorktree")]
    public WorkspacePreparedWorktreeResponse DiscardWorktree(WorkspaceCreateSessionOutcomeRequest request)
    {
        WorkspacePreparedWorktreeResponse Reply(string status) => new(status, _importEpoch, []);
        if (_importEpoch is null) return Reply("unconfigured");
        if (request is null || !ValidEpoch(request.ExpectedHostEpoch) || !ValidRequestId(request.RequestId)) return Reply("invalid_scope");
        if (request.ExpectedHostEpoch != _importEpoch) return Reply("stale_epoch");
        lock (_sessionGate)
        {
            if (!_preparedWorktrees.TryGetValue(request.RequestId, out var prepared)) return Reply("unknown");
            if (!prepared.Used && !prepared.Discarded) Discard(prepared);
            return ReadPrepared(prepared);
        }
    }

    // Takes a worktree made for the next session of the project, when it is the one asked for and no session took it.
    private PreparedWorktree? TakePreparedWorktree(string? id, ProjectDescriptor project, string? baseBranch)
    {
        if (id is null) return null;
        lock (_sessionGate)
        {
            if (!_preparedWorktrees.TryGetValue(id, out var prepared) || prepared.Used || prepared.Discarded
                || prepared.Project.Id != project.Id || prepared.Project.ProjectPath != project.ProjectPath || prepared.BaseBranch != baseBranch)
                return null;
            prepared.Used = true;
            return prepared;
        }
    }

    // Called under _sessionGate. A worktree that git still makes is removed once it is made (PrepareOwnedAsync).
    private void Discard(PreparedWorktree prepared)
    {
        prepared.Discarded = true;
        // On the thread pool: git is never started under the gate.
        if (prepared.Created is { } created) Track(Task.Run(() => RemovePreparedAsync(prepared, created)));
    }

    // Called under _sessionGate.
    private void Track(Task work)
    {
        _preparedWork.RemoveAll(static task => task.IsCompleted);
        _preparedWork.Add(work);
    }

    private async Task<GitWorktreeCreation> PrepareOwnedAsync(PreparedWorktree prepared)
    {
        var steps = prepared.Steps;
        steps.Start(SessionCreationSteps.Worktree);
        GitWorktreeCreation created;
        try
        {
            created = await _worktrees!.CreateAsync(prepared.Project, prepared.BaseBranch, WorktreeSteps(steps), _preparationsClosing.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            created = new("failed", "The window closed before the worktree was made.");
        }
        catch (Exception failure)
        {
            if (LogManager.IsInitialized) LogManager.GetLogger("CodeAlta.Desktop.Rpc").Error(failure, "Worktree preparation failed");
            created = new("failed");
        }

        if (created.Succeeded) steps.Complete();
        else steps.Fail(created.Message);
        bool discarded;
        lock (_sessionGate)
        {
            prepared.Created = created;
            discarded = prepared.Discarded;
        }

        if (discarded) await RemovePreparedAsync(prepared, created).ConfigureAwait(false);
        return created;
    }

    private async Task RemovePreparedAsync(PreparedWorktree prepared, GitWorktreeCreation created)
    {
        if (!created.Succeeded || created.Root is null) return;
        try { await _worktrees!.RemoveAsync(prepared.Project.ProjectPath, created.Root, force: false, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception failure)
        {
            if (LogManager.IsInitialized) LogManager.GetLogger("CodeAlta.Desktop.Rpc").Warn(failure, "A worktree no session took could not be removed");
        }
    }

    // Called under _sessionGate.
    private WorkspacePreparedWorktreeResponse ReadPrepared(PreparedWorktree prepared)
    {
        var created = prepared.Created;
        var status = prepared.Used ? "used" : prepared.Discarded ? "discarded" : created is null ? "preparing" : created.Succeeded ? "ready" : "failed";
        return new(status, _importEpoch, prepared.Steps.Read())
        {
            ProjectId = prepared.Project.Id,
            BaseBranch = prepared.BaseBranch,
            Branch = created?.Branch,
            Reason = created is { Succeeded: false } ? created.Status : null,
            Message = created is { Succeeded: false } ? created.Message : null,
            Warning = created?.Warning,
        };
    }

    // What git says while it makes a worktree, as the steps of a creation.
    private static Action<GitWorktreeProgress> WorktreeSteps(SessionCreationSteps steps) => progress =>
    {
        if (progress.Step == GitWorktreeProgress.Add) steps.Detail(progress.Branch);
        else if (progress.Detail is not null) steps.Detail(progress.Detail);
        else steps.Start(progress.Step == GitWorktreeProgress.Submodules ? SessionCreationSteps.Submodules : SessionCreationSteps.Checkout);
    };

    // Called once, when the window closes, after which no worktree is prepared: those no session took are removed (a
    // page of the next start makes its own), git is stopped for those still made, and what git does is waited for.
    private async Task ClosePreparedWorktreesAsync()
    {
        lock (_sessionGate)
        {
            foreach (var prepared in _preparedWorktrees.Values)
            {
                if (!prepared.Used && !prepared.Discarded) Discard(prepared);
            }
        }

        await _preparationsClosing.CancelAsync().ConfigureAwait(false);
        while (true)
        {
            Task[] running;
            lock (_sessionGate) running = [.. _preparedWork.Where(static task => !task.IsCompleted)];
            if (running.Length == 0) return;
            await Task.WhenAll(running).ConfigureAwait(false);
        }
    }

    // A worktree made for the next session of a project. Its fields are guarded by _sessionGate.
    private sealed class PreparedWorktree(ProjectDescriptor project, string? baseBranch)
    {
        public ProjectDescriptor Project { get; } = project;
        public string? BaseBranch { get; } = baseBranch;
        public SessionCreationSteps Steps { get; } = new();
        public Task<GitWorktreeCreation>? Work { get; set; }
        public GitWorktreeCreation? Created { get; set; }
        public bool Used { get; set; }
        public bool Discarded { get; set; }
    }
}

/// <param name="ExpectedHostEpoch">The host the page reads.</param>
/// <param name="ProjectId">The catalog project the worktree is for.</param>
/// <param name="ProjectPath">The folder of that project, as the page knows it.</param>
/// <param name="BaseBranch">The branch the worktree starts from; the commit the folder of the project is on when null.</param>
/// <param name="RequestId">A GUID the page gives the worktree, by which it reads it, uses it and discards it.</param>
internal sealed record WorkspacePrepareWorktreeRequest(string ExpectedHostEpoch, string ProjectId, string ProjectPath, string? BaseBranch, string RequestId);

/// <param name="Status">
/// <c>preparing</c>, <c>ready</c>, <c>failed</c>, <c>used</c>, <c>discarded</c>, <c>unknown</c>, or a refusal:
/// <c>unconfigured</c>, <c>invalid_scope</c>, <c>stale_epoch</c>, <c>project_missing</c>, <c>closed</c>.
/// </param>
/// <param name="HostEpoch">The host that answers.</param>
/// <param name="Steps">The steps so far: the worktree, its files, its submodules.</param>
internal sealed record WorkspacePreparedWorktreeResponse(string Status, string? HostEpoch, IReadOnlyList<WorkspaceCreationStep> Steps)
{
    /// <summary>Gets the project the worktree is for.</summary>
    public string? ProjectId { get; init; }
    /// <summary>Gets the branch the worktree starts from; null for the commit of the folder of the project.</summary>
    public string? BaseBranch { get; init; }
    /// <summary>Gets the branch of the worktree, once git made it.</summary>
    public string? Branch { get; init; }
    /// <summary>With <c>failed</c>: <c>not_repository</c>, <c>no_commit</c>, <c>git_unavailable</c>, <c>invalid</c>, <c>timeout</c> or <c>failed</c>.</summary>
    public string? Reason { get; init; }
    /// <summary>With <c>failed</c>: what git said.</summary>
    public string? Message { get; init; }
    /// <summary>What git said when it could not check the submodules out; the worktree can be worked in all the same.</summary>
    public string? Warning { get; init; }
}
