using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.ModelTiers;
using CodeAlta.Catalog.WorkItems;
using CodeAlta.Catalog.Worktrees;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.SystemPrompts;

namespace CodeAlta.Desktop.WorkItems;

/// <summary>What starting a piece of work in a new session gave.</summary>
/// <param name="SessionId">The session that was created; null when none was.</param>
/// <param name="Problem">Why the work did not start; null when it did.</param>
/// <param name="Reason">A short code for the page when the problem is one it has a text for.</param>
internal sealed record SessionStartResult(string? SessionId, string? Problem, string? Reason = null)
{
    /// <summary>Gets what went wrong without stopping the start, such as a worktree whose branch could not be marked; null when nothing did.</summary>
    public string? Warning { get; init; }
}

/// <summary>Starts a piece of work (a task, a plan, an issue) in a new session of a project.</summary>
internal interface ISessionStarter
{
    /// <summary>Creates a session of the project, in its folder or in a new git worktree, and sends it a prompt.</summary>
    /// <param name="project">The project.</param>
    /// <param name="title">The name of the session.</param>
    /// <param name="prompt">Gives the prompt for the folder the session works in.</param>
    /// <param name="worktree">Whether the session works in a new git worktree.</param>
    /// <param name="likeSessionId">A session whose provider, model and effort the new one takes; null when no session shows the work.</param>
    /// <param name="origin">A word for what starts the session, for the identity of its first prompt.</param>
    /// <param name="model">What the work itself says of the provider, the model and the effort; null when nothing.</param>
    /// <returns>The session, or why the work did not start. Failures are results, not exceptions.</returns>
    Task<SessionStartResult> StartAsync(ProjectDescriptor project, string title, Func<string, string> prompt, bool worktree, string? likeSessionId, string origin, SessionStartModel? model);
}

/// <summary>
/// Starts work in the host of the application, as a new session does when the user creates one: the session is
/// the user's, runs the default agent prompt and can ask. Everything is checked before the session is created,
/// so that a refusal leaves no empty session and no empty worktree behind. The work is started from the window:
/// the session belongs to the space the window shows.
/// </summary>
internal sealed class SessionStarter : ISessionStarter
{
    private readonly CodeAltaHost _host;
    private readonly GitWorktreeService? _worktrees;
    private readonly Func<string?> _shownSpace;

    internal SessionStarter(CodeAltaHost host, GitWorktreeService? worktrees, Func<string?> shownSpace)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(shownSpace);
        (_host, _worktrees, _shownSpace) = (host, worktrees, shownSpace);
    }

    /// <inheritdoc />
    public async Task<SessionStartResult> StartAsync(ProjectDescriptor project, string title, Func<string, string> prompt, bool worktree, string? likeSessionId, string origin, SessionStartModel? model)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(prompt);
        string? sessionId = null;
        GitWorktreeCreation? created = null;
        try
        {
            if (project.Archived || !Directory.Exists(project.ProjectPath)) return Refuse("The folder of the project is missing.");
            if (worktree && _worktrees is null) return Refuse("Worktrees are not available.");

            // What the user chose comes first, then the session that shows the item, then the session that proposed
            // it; without any of them, the default provider of the configuration, as for a new session.
            // A session that shows the work and runs on a tier, or on Adaptive, gives it to the new session with its model.
            WorkItemSelection? like = null;
            SessionStartTier? likeTier = null;
            if (model?.Asked is null && model?.Tier is null && !string.IsNullOrWhiteSpace(likeSessionId)
                && (await _host.Commands.GetObservedSelectionChoicesAsync(likeSessionId, CancellationToken.None).ConfigureAwait(false))?.Current is { } current)
            {
                like = new(current.ProviderKey, current.ModelId, current.ModelId is null ? null : current.ReasoningEffort?.ToString().ToLowerInvariant());
                likeTier = await TierOfAsync(likeSessionId).ConfigureAwait(false);
            }

            // The session is in the space the window shows: it starts with a provider of that space. One the user
            // chose there is refused; the provider of a session of another space gives way to one of this space.
            var space = _shownSpace();
            var spaceId = SpaceCatalog.ResolveSessionSpace(space, project, await _host.SpaceCatalog.LoadAsync(CancellationToken.None).ConfigureAwait(false));
            var inSpace = new SpaceFilter(new CachedSpaceAssignments(new CodeAltaConfigStore(_host.CatalogOptions)).Current(), spaceId);
            if (model?.Asked is { ProviderKey: { Length: > 0 } askedKey } && !inSpace.Allows(SpaceResourceKind.Provider, askedKey))
                return Refuse($"The provider '{askedKey}' is not available in the space '{spaceId}'.");
            // A tier the user chose gives the model, as the tiers of the space give it now.
            var asked = model?.Asked;
            if (model?.Tier is { } askedTier && asked is null)
            {
                var resolved = await TierModelAsync(askedTier, spaceId, project.Id).ConfigureAwait(false);
                if (resolved.Problem is { } problem) return Refuse(problem);
                asked = resolved.Selection;
            }

            var choice = await SessionStartChoice.ChooseAsync(
                    [.. _host.ModelProviderRegistry.ListProviders().Where(candidate => inSpace.Allows(SpaceResourceKind.Provider, candidate.ProviderId.Value))],
                    new CodeAltaConfigStore(_host.CatalogOptions).GetEffectiveDefaultProvider(project.ProjectPath),
                    asked,
                    like,
                    model?.Recorded,
                    providerId => _host.ModelProviderInitializationService.GetModelsAsync(providerId, CancellationToken.None))
                .ConfigureAwait(false);
            if (choice.Provider is not { } provider) return Refuse(choice.Problem ?? "No model provider is enabled.");
            // The tier of that session goes with its model only: a session that starts on another provider has none.
            if (likeTier is not null && !string.Equals(like?.ProviderKey, provider.ProviderId.Value, StringComparison.OrdinalIgnoreCase)) likeTier = null;

            // The agent the space of the session starts its new sessions with, else the default one.
            var drafts = await _host.Commands.GetDraftPromptsAsync(new(project.Id, project.ProjectPath), space, CancellationToken.None).ConfigureAwait(false);
            if (drafts is null) return Refuse("The application is closing.");
            var agent = model?.AgentPromptId ?? drafts.DefaultPromptId;
            if (!drafts.Prompts.Any(candidate => candidate.Id == agent)) return Refuse($"There is no agent prompt '{agent}'.");
            if (model?.PermissionMode is { } mode && !_host.Commands.GetNewSessionPermissionModes(provider.ProviderId.Value).Modes.Contains(mode, StringComparer.Ordinal))
                return Refuse($"The provider '{provider.ProviderId.Value}' does not offer the permission mode '{mode}'.");
            if (!_host.Commands.HasCapacity) return Refuse("Too many commands are pending in CodeAlta. Try again in a moment.");

            if (worktree)
            {
                created = await _worktrees!.CreateAsync(project, null, model?.Branch, progress: null, withoutHooks: false, CancellationToken.None).ConfigureAwait(false);
                if (!created.Succeeded) return new(null, created.Message ?? "The worktree could not be created.", "worktree_" + created.Status);
            }
            else if (model is { Branch: { } branchName, Worktree: null } && await SwitchProjectFolderAsync(project, branchName).ConfigureAwait(false) is { } refused)
            {
                return refused;
            }

            var folder = created?.Folder;
            if (!worktree && model?.Worktree is { } existing)
            {
                // A worktree the work is about, such as the one whose changes the user reviewed.
                if (!Directory.Exists(existing) || !GitWorktreeService.SameRepository(project.ProjectPath, existing)) return Refuse("The worktree is missing.");
                folder = existing;
            }

            var session = await _host.Commands.CreateDraftSessionAsync(project, provider, title, null, folder, space, model?.PermissionMode, null, null, model?.SessionOptions).ConfigureAwait(false);
            sessionId = session.SessionId;
            // The review and the sandbox chosen for the session, before anything is sent to it.
            if (model?.ReviewBeforeCommit is { } review
                && (await _host.Commands.SetReviewBeforeCommitAsync(sessionId, review, CancellationToken.None).ConfigureAwait(false)).Status is var reviewed and not "ok")
                return new(sessionId, $"The session was created, but its review before commit could not be chosen ({reviewed}).");
            if (model?.Sandbox is { } sandbox
                && (await _host.Commands.SetSandboxAsync(sessionId, sandbox, CancellationToken.None).ConfigureAwait(false)).Status is var sandboxed and not "ok")
                return new(sessionId, $"The session was created, but its sandbox '{sandbox}' could not be chosen ({sandboxed}).");
            // The tier, or Adaptive, before the first prompt, which then runs on it: a tier the user chose is required, the one
            // of the session that shows the work is taken when it can be.
            if ((model?.Tier ?? likeTier) is { } tier && await PutOnTierAsync(sessionId, tier).ConfigureAwait(false) is { } notOnTier && model?.Tier is not null)
                return new(sessionId, $"The session was created, but it could not be put on {tier.Name} ({notOnTier}).");
            // A script of the library runs through exec, which the session then has whatever the setting of Code Mode says.
            if (model?.CodeMode == true) _host.CodeModeRuns.RequireCodeMode(sessionId);
            // The host completes what the choice leaves to the provider and to the model.
            var selection = new OwnedSessionSelection(provider.ProviderId.Value, agent, choice.ModelId, choice.ModelId is null ? null : choice.Effort);
            var admission = _host.Commands.AdmitSend(new OwnedTextSendRequest(origin + ":" + Guid.NewGuid().ToString("N"), sessionId, prompt(folder ?? project.ProjectPath))
                { Selection = selection, Provenance = model?.Provenance }, CancellationToken.None);
            if (admission.Kind is not (OwnedSessionCommandAdmissionKind.Accepted or OwnedSessionCommandAdmissionKind.Replay) || admission.Receipt is null)
            {
                return new(sessionId, $"The session was created, but the prompt was not accepted ({admission.Kind.ToString().ToLowerInvariant()}).");
            }

            return new(sessionId, null) { Warning = created?.Warning };
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

        static SessionStartResult Refuse(string problem) => new(null, problem);
    }

    // The tier, or Adaptive with its level, a session runs on; null for a model chosen by hand or a session that is gone.
    private async Task<SessionStartTier?> TierOfAsync(string sessionId)
    {
        var (tier, adaptive) = await _host.RuntimeService.GetSessionModelChoiceAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        if (adaptive) return new(null, true, await _host.RuntimeService.GetSessionAdaptiveLevelAsync(sessionId, CancellationToken.None).ConfigureAwait(false));
        return tier is { } chosen ? new(chosen, false, null) : null;
    }

    // The model a new session on a tier starts with: the first ready one of the tier, or the one of the level of Adaptive, in
    // the tiers of the space, as `alta session create --tier` and the composer take it.
    private async Task<(WorkItemSelection? Selection, string? Problem)> TierModelAsync(SessionStartTier tier, string? spaceId, string projectId)
    {
        var scope = await _host.RuntimeService.LoadModelTierScopeAsync(spaceId, projectId, CancellationToken.None).ConfigureAwait(false);
        if (tier.Adaptive)
        {
            return scope.IsAdaptiveReady && scope.Adaptive!.Resolve(tier.Level) is { } level
                ? (Selection(level.Model.ProviderKey, level.Model.ModelId, level.Model.PreferredReasoning), null)
                : (null, "Adaptive is not set up: give its levels a model in Settings > Models.");
        }

        var name = ModelTiers.NameOf(tier.Tier!.Value);
        var targets = scope.Tiers.Get(tier.Tier.Value);
        if (targets.Count == 0) return (null, $"The {name} tier has no model: set the tiers in Settings > Models.");
        return ModelTierSelection.SelectForNewSession(scope, tier.Tier.Value, _host.ModelProviderInitializationService.CurrentStates,
                key => _host.ModelProviderRegistry.TryGetProvider(new ModelProviderId(key), out var descriptor) ? descriptor : null, requestedReasoning: null) is { } choice
            ? (Selection(choice.ProviderKey, choice.ModelId, choice.Reasoning), null)
            : (null, $"No model of the {name} tier is ready ({string.Join(", ", targets.Select(static target => target.Format()))}).");

        static WorkItemSelection Selection(string providerKey, string modelId, AgentReasoningEffort? effort)
            => new(providerKey, modelId, effort?.ToString().ToLowerInvariant());
    }

    // Puts a new session on its tier, or on Adaptive: null when it is there, else what the host answered.
    private async Task<string?> PutOnTierAsync(string sessionId, SessionStartTier tier)
    {
        var status = tier.Adaptive
            ? (await _host.Commands.SetSessionAdaptiveAsync(sessionId, tier.Level, CancellationToken.None).ConfigureAwait(false)).Status
            : await _host.Commands.SetSessionTierAsync(sessionId, tier.Tier, CancellationToken.None).ConfigureAwait(false);
        return status == "ok" ? null : status;
    }

    // Moves the checkout of the project to a new branch for the work, or says why not: never under a session at work in
    // it, never onto a branch that is there (also in another case), and what git refuses comes back with its message.
    // Changes that are not committed go along, as git does for a new branch.
    private async Task<SessionStartResult?> SwitchProjectFolderAsync(ProjectDescriptor project, string branch)
    {
        if (_worktrees is null) return new(null, "Branches are not available.");
        if (GitWorktreeService.FindCheckoutRoot(project.ProjectPath) is not { } checkout)
            return new(null, "The folder of the project is in no git repository.", "worktree_not_repository");
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (_host.RuntimeService.ListBusySessionFolders().Any(session => GitWorktreeService.FindCheckoutRoot(session.Folder) is { } busy && string.Equals(busy, checkout, comparison)))
            return new(null, "A session is at work in the folder of the project: its branch is not changed under it.", "branch_in_use");
        if (await _worktrees.FindBranchAsync(checkout, branch, CancellationToken.None).ConfigureAwait(false) is { } taken)
            return new(null, $"The branch {taken} already exists.", "branch_exists");
        var switched = await _worktrees.SwitchAsync(checkout, branch, create: true, CancellationToken.None).ConfigureAwait(false);
        return switched.Status == GitWorktreeService.Ok ? null : new(null, switched.Message ?? "The branch could not be created.", "branch_" + switched.Status);
    }

    private static string FirstLine(string message)
    {
        var line = message.ReplaceLineEndings("\n").Split('\n')[0].Trim();
        return line.Length <= 300 ? line : line[..300];
    }
}
