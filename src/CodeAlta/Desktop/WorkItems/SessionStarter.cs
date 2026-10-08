using CodeAlta.Agent;
using CodeAlta.Catalog;
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
internal sealed record SessionStartResult(string? SessionId, string? Problem, string? Reason = null);

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
/// so that a refusal leaves no empty session and no empty worktree behind.
/// </summary>
internal sealed class SessionStarter : ISessionStarter
{
    private readonly CodeAltaHost _host;
    private readonly GitWorktreeService? _worktrees;

    internal SessionStarter(CodeAltaHost host, GitWorktreeService? worktrees)
    {
        ArgumentNullException.ThrowIfNull(host);
        (_host, _worktrees) = (host, worktrees);
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
            WorkItemSelection? like = null;
            if (model?.Asked is null && !string.IsNullOrWhiteSpace(likeSessionId)
                && (await _host.Commands.GetObservedSelectionChoicesAsync(likeSessionId, CancellationToken.None).ConfigureAwait(false))?.Current is { } current)
            {
                like = new(current.ProviderKey, current.ModelId, current.ModelId is null ? null : current.ReasoningEffort?.ToString().ToLowerInvariant());
            }

            var choice = await SessionStartChoice.ChooseAsync(
                    _host.ModelProviderRegistry.ListProviders(),
                    new CodeAltaConfigStore(_host.CatalogOptions).GetEffectiveDefaultProvider(project.ProjectPath),
                    model?.Asked,
                    like,
                    model?.Recorded,
                    providerId => _host.ModelProviderInitializationService.GetModelsAsync(providerId, CancellationToken.None))
                .ConfigureAwait(false);
            if (choice.Provider is not { } provider) return Refuse(choice.Problem ?? "No model provider is enabled.");

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

            var session = await _host.Commands.CreateDraftSessionAsync(project, provider, title, null, created?.Folder).ConfigureAwait(false);
            sessionId = session.SessionId;
            // The host completes what the choice leaves to the provider and to the model.
            var selection = new OwnedSessionSelection(provider.ProviderId.Value, agent, choice.ModelId, choice.ModelId is null ? null : choice.Effort);
            var admission = _host.Commands.AdmitSend(new OwnedTextSendRequest(origin + ":" + Guid.NewGuid().ToString("N"), sessionId, prompt(created?.Folder ?? project.ProjectPath)) { Selection = selection }, CancellationToken.None);
            if (admission.Kind is not (OwnedSessionCommandAdmissionKind.Accepted or OwnedSessionCommandAdmissionKind.Replay) || admission.Receipt is null)
            {
                return new(sessionId, $"The session was created, but the prompt was not accepted ({admission.Kind.ToString().ToLowerInvariant()}).");
            }

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

        static SessionStartResult Refuse(string problem) => new(null, problem);
    }

    private static string FirstLine(string message)
    {
        var line = message.ReplaceLineEndings("\n").Split('\n')[0].Trim();
        return line.Length <= 300 ? line : line[..300];
    }
}
