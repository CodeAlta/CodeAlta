using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.SystemPrompts;

namespace CodeAlta.LiveTool;

internal interface IAltaSessionQueryService
{
    IAsyncEnumerable<AltaSessionInfo> LoadAsync(AltaCommandContext context);
}

internal sealed class AltaSessionQueryService : IAltaSessionQueryService
{
    // Lists the agent prompts a session can name; replaced by tests that count the listings.
    private readonly Func<AgentPromptCatalogQuery, IReadOnlyList<AgentPromptDescriptor>> _listPrompts;

    public AltaSessionQueryService()
        : this(static query => new AgentPromptCatalog().ListEffectivePrompts(query))
    {
    }

    internal AltaSessionQueryService(Func<AgentPromptCatalogQuery, IReadOnlyList<AgentPromptDescriptor>> listPrompts)
    {
        ArgumentNullException.ThrowIfNull(listPrompts);
        _listPrompts = listPrompts;
    }

    public IAsyncEnumerable<AltaSessionInfo> LoadAsync(AltaCommandContext context, CancellationToken cancellationToken = default)
        => LoadAsync(context, includeActivity: true, cancellationToken);

    /// <summary>Lists the sessions.</summary>
    /// <param name="context">The command.</param>
    /// <param name="includeActivity">
    /// False to leave out whether each session runs, which asks the runtime of each session it holds: the infos then
    /// say no (<see cref="AltaSessionInfo.ActivityKnown"/> is false) until <see cref="CompleteActivityAsync"/> completes
    /// those that are written. All the rest is the same.
    /// </param>
    /// <param name="cancellationToken">Cancellation token; that of the command when default.</param>
    public async IAsyncEnumerable<AltaSessionInfo> LoadAsync(
        AltaCommandContext context,
        bool includeActivity,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken = cancellationToken == default ? context.CancellationToken : cancellationToken;

        var runtime = context.Services.Get<SessionRuntimeService>();
        var sessionCatalog = context.Services.Get<SessionViewCatalog>();
        if (runtime is null && sessionCatalog is null)
        {
            AltaJsonlWriter.WriteError(
                context.Stderr,
                context.CorrelationId,
                "service.unavailable",
                AltaExitCodes.ServiceUnavailable,
                $"Required in-process service '{nameof(SessionRuntimeService)}' or '{nameof(SessionViewCatalog)}' is unavailable.");
            yield break;
        }

        var viewState = await TryLoadViewStateAsync(context, sessionCatalog, cancellationToken).ConfigureAwait(false);
        var knownPrompts = new KnownPrompts(context, _listPrompts);
        if (runtime is not null)
        {
            await foreach (var session in runtime.ListRecoverableSessionsAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                yield return await BuildSessionInfoAsync(runtime, sessionCatalog, viewState, knownPrompts, session, includeActivity, cancellationToken).ConfigureAwait(false);
            }

            yield break;
        }

        foreach (var session in await sessionCatalog!.LoadInternalAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return await BuildSessionInfoAsync(runtime, sessionCatalog, viewState, knownPrompts, session, includeActivity, cancellationToken).ConfigureAwait(false);
        }
    }

    IAsyncEnumerable<AltaSessionInfo> IAltaSessionQueryService.LoadAsync(AltaCommandContext context)
        => LoadAsync(context);

    /// <summary>
    /// Resolves sessions by their identifiers without listing the others: each info is the one <see cref="LoadAsync"/>
    /// lists for the session, in the order of the identifiers.
    /// </summary>
    /// <returns>
    /// The infos; null when one of the identifiers is not the exact identifier of a session the store finds, and the
    /// caller then lists the sessions (which also finds an identifier that differs in case, and reports one that is
    /// missing). Nothing is written to <see cref="AltaCommandContext.Stderr"/> then.
    /// </returns>
    /// <remarks>
    /// The warnings of the resolution are written to <see cref="AltaCommandContext.Stderr"/> only when it succeeds, so
    /// that a caller that lists the sessions after a null result writes those of the listing alone, as before.
    /// </remarks>
    public async Task<IReadOnlyList<AltaSessionInfo>?> TryResolveAsync(AltaCommandContext context, IReadOnlyList<string> sessionIds)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sessionIds);
        var cancellationToken = context.CancellationToken;
        if (sessionIds.Count == 0 || context.Services.Get<SessionRuntimeService>() is not { } runtime)
        {
            return null;
        }

        var sessionCatalog = context.Services.Get<SessionViewCatalog>();
        using var warnings = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var quiet = context with { Stderr = warnings };
        try
        {
            var viewState = await TryLoadViewStateAsync(quiet, sessionCatalog, cancellationToken).ConfigureAwait(false);
            var knownPrompts = new KnownPrompts(quiet, _listPrompts);
            var resolved = new Dictionary<string, AltaSessionInfo>(StringComparer.Ordinal);
            var infos = new AltaSessionInfo[sessionIds.Count];
            for (var index = 0; index < sessionIds.Count; index++)
            {
                var sessionId = sessionIds[index];
                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    return null;
                }

                if (!resolved.TryGetValue(sessionId, out var info))
                {
                    var session = await runtime.TryGetRecoverableSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
                    if (session is null || !string.Equals(session.SessionId, sessionId, StringComparison.Ordinal))
                    {
                        return null;
                    }

                    info = await BuildSessionInfoAsync(runtime, sessionCatalog, viewState, knownPrompts, session, includeActivity: true, cancellationToken).ConfigureAwait(false);
                    resolved[sessionId] = info;
                }

                infos[index] = info;
            }

            context.Stderr.Write(warnings.ToString());
            return infos;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The listing reads the same sessions and fails, or leaves out a session it cannot read, as before.
            return null;
        }
    }

    /// <summary>
    /// Completes an info listed without its activity: whether the session runs, and its state, as a listing with its
    /// activity gives them. An info that has them is returned as it is.
    /// </summary>
    public static async Task<AltaSessionInfo> CompleteActivityAsync(AltaCommandContext context, AltaSessionInfo info)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(info);
        if (info.ActivityKnown)
        {
            return info;
        }

        var runtime = context.Services.Get<SessionRuntimeService>();
        var (isRunning, hasActiveSession) = await ReadActivityAsync(runtime, info.Session, context.CancellationToken).ConfigureAwait(false);
        return info with
        {
            IsRunning = isRunning,
            HasActiveSession = hasActiveSession,
            State = ResolveState(info.Session, isRunning, hasActiveSession),
            ActivityKnown = true,
        };
    }

    private static async Task<(bool IsRunning, bool HasActiveSession)> ReadActivityAsync(SessionRuntimeService? runtime, SessionViewDescriptor session,
        CancellationToken cancellationToken)
    {
        var isRunning = runtime is not null && await runtime.HasActiveRunAsync(session, cancellationToken).ConfigureAwait(false);
        var hasActiveSession = isRunning || (runtime is not null && await runtime.HasActiveCoordinatorSessionAsync(session.SessionId, cancellationToken).ConfigureAwait(false));
        return (isRunning, hasActiveSession);
    }

    private static async Task<SessionViewViewState?> TryLoadViewStateAsync(AltaCommandContext context, SessionViewCatalog? sessionCatalog,
        CancellationToken cancellationToken)
    {
        if (sessionCatalog is null)
        {
            return null;
        }

        try
        {
            return await sessionCatalog.LoadViewStateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AltaJsonlWriter.WriteWarning(context.Stderr, context.CorrelationId, "session.viewStateUnavailable", ex.Message);
            return null;
        }
    }

    private static async Task<AltaSessionInfo> BuildSessionInfoAsync(
        SessionRuntimeService? runtime,
        SessionViewCatalog? sessionCatalog,
        SessionViewViewState? viewState,
        KnownPrompts knownPrompts,
        SessionViewDescriptor session,
        bool includeActivity,
        CancellationToken cancellationToken)
    {
        var localState = await TryGetLocalStateAsync(sessionCatalog, viewState, session, cancellationToken).ConfigureAwait(false);
        var preference = TryGetPreference(viewState, session.SessionId);
        var (isRunning, hasActiveSession) = includeActivity
            ? await ReadActivityAsync(runtime, session, cancellationToken).ConfigureAwait(false)
            : (false, false);
        if (localState?.Archived == true)
        {
            session.Status = SessionViewStatus.Archived;
        }

        if (localState?.MessageCount is not null)
        {
            session.MessageCount = localState.MessageCount;
        }

        if (!string.IsNullOrWhiteSpace(localState?.ParentSessionId))
        {
            session.ParentSessionId = localState.ParentSessionId;
        }

        if (localState?.CreatedBy is not null)
        {
            session.CreatedBy = localState.CreatedBy;
        }

        if (!string.IsNullOrWhiteSpace(localState?.ProviderKey))
        {
            session.ProviderKey = localState.ProviderKey;
        }

        if (!string.IsNullOrWhiteSpace(localState?.ModelId))
        {
            session.ModelId = localState.ModelId;
        }

        if (localState?.ReasoningEffort is not null)
        {
            session.ReasoningEffort = localState.ReasoningEffort;
        }

        if (!string.IsNullOrWhiteSpace(localState?.AgentPromptId))
        {
            session.AgentPromptId = knownPrompts.Resolve(session, localState.AgentPromptId);
        }
        else if (!string.IsNullOrWhiteSpace(preference?.AgentPromptId))
        {
            session.AgentPromptId = knownPrompts.Resolve(session, preference.AgentPromptId);
        }

        return new AltaSessionInfo(session, localState, preference, isRunning, hasActiveSession, ResolveState(session, isRunning, hasActiveSession))
        {
            ActivityKnown = includeActivity,
        };
    }

    private static async Task<SessionViewLocalState?> TryGetLocalStateAsync(
        SessionViewCatalog? sessionCatalog,
        SessionViewViewState? viewState,
        SessionViewDescriptor session,
        CancellationToken cancellationToken)
    {
        if (sessionCatalog is not null)
        {
            try
            {
                var journalState = await sessionCatalog.JournalStore
                    .ReadLatestStateAsync(session.SessionId, session.CreatedAt, cancellationToken)
                    .ConfigureAwait(false);
                if (journalState is not null)
                {
                    return journalState;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
            {
            }
        }

        return viewState is not null && viewState.SessionStates.TryGetValue(session.SessionId, out var state) ? state : null;
    }

    private static SessionViewPreference? TryGetPreference(SessionViewViewState? viewState, string sessionId)
        => viewState is not null && viewState.SessionPreferences.TryGetValue(sessionId, out var preference) ? preference : null;

    private static string ResolveState(SessionViewDescriptor session, bool isRunning, bool hasActiveSession)
    {
        if (session.Status == SessionViewStatus.Archived)
        {
            return "archived";
        }

        if (isRunning)
        {
            return "running";
        }

        return hasActiveSession ? "idle" : "inactive";
    }

    // The agent prompts known to one command: the prompt folders of a working folder are read once, not once for
    // each session that works there.
    private sealed class KnownPrompts(AltaCommandContext context, Func<AgentPromptCatalogQuery, IReadOnlyList<AgentPromptDescriptor>> listPrompts)
    {
        private readonly Dictionary<(string? ProjectRoot, bool Trusted), HashSet<string>> _names = [];
        private string? _userCodeAltaRoot;
        private string? _userProfileRoot;

        public string Resolve(SessionViewDescriptor session, string promptId)
        {
            var normalized = string.IsNullOrWhiteSpace(promptId) ? null : promptId.Trim();
            if (normalized is null)
            {
                return AgentPromptCatalog.DefaultPromptName;
            }

            (string? ProjectRoot, bool Trusted) key = (session.WorkingDirectory, !string.IsNullOrWhiteSpace(session.ProjectRef));
            if (!_names.TryGetValue(key, out var names))
            {
                _userProfileRoot ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                _userCodeAltaRoot ??= context.Services.Get<CatalogOptions>()?.GlobalRoot ?? Path.Combine(_userProfileRoot, ".alta");
                var query = new AgentPromptCatalogQuery
                {
                    ProjectRoot = key.ProjectRoot,
                    ProjectPromptResourcesTrusted = key.Trusted,
                    UserCodeAltaRoot = _userCodeAltaRoot,
                    UserProfileRoot = _userProfileRoot,
                };
                names = new HashSet<string>(listPrompts(query).Select(static prompt => prompt.PromptName), StringComparer.OrdinalIgnoreCase);
                _names[key] = names;
            }

            return names.Contains(normalized) ? normalized : AgentPromptCatalog.DefaultPromptName;
        }
    }
}

internal sealed record AltaSessionInfo(
    SessionViewDescriptor Session,
    SessionViewLocalState? LocalState,
    SessionViewPreference? Preference,
    bool IsRunning,
    bool HasActiveSession,
    string State)
{
    // False for an info listed without asking the runtime whether the session runs: IsRunning and HasActiveSession
    // are then false, and State says only whether it is archived. Such an info is completed before it is written.
    public bool ActivityKnown { get; init; } = true;
}
