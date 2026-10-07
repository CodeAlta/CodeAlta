using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.WorkItems;
using CodeAlta.Catalog.Worktrees;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.SystemPrompts;

namespace CodeAlta.Desktop.WorkItems;

/// <summary>What starting a task or a plan in a new session gave.</summary>
/// <param name="SessionId">The session that was created; null when none was.</param>
/// <param name="Problem">Why the work did not start; null when it did.</param>
/// <param name="Reason">A short code for the page when the problem is one it has a text for.</param>
internal sealed record WorkItemStartResult(string? SessionId, string? Problem, string? Reason = null);

/// <summary>Starts the work of a task or a plan in a new session.</summary>
internal interface IWorkItemRunner
{
    /// <summary>Creates a session of the project, in its folder or in a new git worktree, and sends it the item.</summary>
    /// <param name="project">The project.</param>
    /// <param name="kind">One of <see cref="WorkItemKinds"/>.</param>
    /// <param name="id">The id of the item.</param>
    /// <param name="worktree">Whether the session works in a new git worktree.</param>
    /// <param name="likeSessionId">A session whose provider, model and effort the new one takes; null for the defaults.</param>
    /// <returns>The session, or why the work did not start. Failures are results, not exceptions.</returns>
    Task<WorkItemStartResult> StartAsync(ProjectDescriptor project, string kind, string id, bool worktree, string? likeSessionId);
}

/// <summary>
/// Starts a task or a plan in the host of the application, as a new session does when the user creates one:
/// the session is the user's, runs the default agent prompt and can ask. Everything is checked before the
/// session is created, so that a refusal leaves no empty session and no empty worktree behind.
/// </summary>
internal sealed class WorkItemRunner : IWorkItemRunner
{
    private readonly CodeAltaHost _host;
    private readonly WorkItemService _items;
    private readonly GitWorktreeService? _worktrees;

    internal WorkItemRunner(CodeAltaHost host, WorkItemService items, GitWorktreeService? worktrees)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(items);
        (_host, _items, _worktrees) = (host, items, worktrees);
    }

    /// <inheritdoc />
    public async Task<WorkItemStartResult> StartAsync(ProjectDescriptor project, string kind, string id, bool worktree, string? likeSessionId)
    {
        ArgumentNullException.ThrowIfNull(project);
        string? sessionId = null;
        GitWorktreeCreation? created = null;
        try
        {
            if (project.Archived || !Directory.Exists(project.ProjectPath)) return Refuse("The folder of the project is missing.");
            var task = kind == WorkItemKinds.Task ? _items.GetTask(project, id) : null;
            var plan = kind == WorkItemKinds.Plan ? _items.GetPlan(project, id) : null;
            if (task is null && plan is null) return new(null, "The item is no longer there.", "not_found");
            if (worktree && _worktrees is null) return Refuse("Worktrees are not available.");

            // The session that showed the item gives its provider and its model; without one, the defaults of a new session.
            OwnedSessionSelection? like = null;
            if (!string.IsNullOrWhiteSpace(likeSessionId))
            {
                like = (await _host.Commands.GetObservedSelectionChoicesAsync(likeSessionId, CancellationToken.None).ConfigureAwait(false))?.Current;
            }

            var providers = _host.ModelProviderRegistry.ListProviders();
            var providerKey = like?.ProviderKey
                ?? new CodeAltaConfigStore(_host.CatalogOptions).GetEffectiveDefaultProvider(project.ProjectPath)
                ?? providers.FirstOrDefault()?.ProviderId.Value;
            if (providerKey is null) return Refuse("No model provider is enabled.");
            if (!_host.ModelProviderRegistry.TryGetProvider(new ModelProviderId(providerKey), out var provider) || !provider.IsEnabled)
            {
                provider = providers.FirstOrDefault();
                like = null;
                if (provider is null) return Refuse("No model provider is enabled.");
            }

            var agent = AgentPromptCatalog.DefaultPromptName;
            var prompts = await _host.Commands.GetDraftPromptChoicesAsync(new(project.Id, project.ProjectPath), CancellationToken.None).ConfigureAwait(false);
            if (prompts is null) return Refuse("The application is closing.");
            if (!prompts.Any(candidate => candidate.Id == agent)) return Refuse($"There is no agent prompt '{agent}'.");
            if (!_host.Commands.HasCapacity) return Refuse("Too many commands are pending in CodeAlta. Try again in a moment.");

            if (worktree)
            {
                created = await _worktrees!.CreateAsync(project, null, CancellationToken.None).ConfigureAwait(false);
                if (!created.Succeeded) return new(null, created.Message ?? "The worktree could not be created.", "worktree_" + created.Status);
            }

            var title = WorkItemPrompts.SessionTitle(task?.Title ?? plan!.Title);
            var session = await _host.Commands.CreateDraftSessionAsync(project, provider, title, null, created?.Folder).ConfigureAwait(false);
            sessionId = session.SessionId;
            var prompt = task is not null ? WorkItemPrompts.ForTask(task) : WorkItemPrompts.ForPlan(plan!, project.ProjectPath, created?.Folder ?? project.ProjectPath);
            // A model the session that showed the item ran with is kept; the host completes what is left to it.
            var selection = new OwnedSessionSelection(provider.ProviderId.Value, agent, like?.ModelId, like?.ModelId is null ? null : like.ReasoningEffort);
            var admission = _host.Commands.AdmitSend(new OwnedTextSendRequest("workitem:" + Guid.NewGuid().ToString("N"), sessionId, prompt) { Selection = selection }, CancellationToken.None);
            if (admission.Kind is not (OwnedSessionCommandAdmissionKind.Accepted or OwnedSessionCommandAdmissionKind.Replay) || admission.Receipt is null)
            {
                return new(sessionId, $"The session was created, but the prompt was not accepted ({admission.Kind.ToString().ToLowerInvariant()}).");
            }

            _items.SetRunner(project.Id, kind, id, sessionId);
            return new(sessionId, null);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException or IOException
            or UnauthorizedAccessException or ObjectDisposedException or NotSupportedException or OperationCanceledException)
        {
            if (sessionId is null && created is { Succeeded: true, Root: { } root })
            {
                // Nothing was written there yet: the checkout that no session will use goes.
                try { await _worktrees!.RemoveAsync(project.ProjectPath, root, force: false, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) { }
            }

            return new(sessionId, FirstLine(exception.Message));
        }

        static WorkItemStartResult Refuse(string problem) => new(null, problem);
    }

    private static string FirstLine(string message)
    {
        var line = message.ReplaceLineEndings("\n").Split('\n')[0].Trim();
        return line.Length <= 300 ? line : line[..300];
    }
}
