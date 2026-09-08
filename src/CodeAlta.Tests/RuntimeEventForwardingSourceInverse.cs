using System.Text;

namespace CodeAlta.Tests;

/// <summary>
/// Literal-only whole-original inverse against parent-supplied 67bb503e anchors. No content reads.
/// Reconstruction remains subject to separate parent execution admission.
/// </summary>
internal static class RuntimeEventForwardingSourceInverse
{
    internal const string Runtime = "CodeAlta.Orchestration/Runtime/SessionRuntimeService.cs";
    internal const string DesktopInverse = "CodeAlta.Tests/DesktopOwnedSessionSourceInverse.cs";
    internal const string DesktopProject = "CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj";
    internal static IReadOnlyList<string> DirectContentPaths => [];
    internal static IReadOnlyList<string> TransitiveContentPaths => [];
    internal static IReadOnlyList<(string Path, string Hash)> Originals =>
    [
        (Runtime, "3E15C035AD91A269837E17316AA7ED3E2EACA58B7418B8D056442003E20189C6"),
        (DesktopInverse, "34A702CE0F64A03D8696385888F400218FD23E2CDD2B155A482E007C4FB9DEA2"),
        (DesktopProject, "98489F9B91D5289D8AB98242322F216AF13F0ADA65E714CF16F3E82234EA67FB"),
    ];

    internal static string Restore(string path, string source)
    {
        source = SourceTestText.DecodeSource(new UTF8Encoding(false, true).GetBytes(source));
        foreach (var edit in Edits(path)) source = Apply(path, source, edit);
        Assert.AreEqual(Originals.Single(item => item.Path == path).Hash, OwnedSessionCommandSourceInverse.Hash(source), path);
        return source;
    }

    internal static string Apply(string path, string source, (string Before, string After, int Count) edit)
    {
        var expected = SourceTestText.Canonicalize(edit.After);
        Assert.IsTrue(expected.Length > 0, path);
        Assert.AreEqual(edit.Count, source.Split(expected, StringSplitOptions.None).Length - 1, path + ": " + expected);
        return source.Replace(expected, SourceTestText.Canonicalize(edit.Before), StringComparison.Ordinal);
    }

    internal static IReadOnlyList<(string Before, string After, int Count)> Edits(string path) => path switch
    {
        DesktopProject => [(DesktopLink, DesktopLink + NewLink, 1)],
        DesktopInverse =>
        [
            ("        WorkspaceTests or Host or OwnerInverse or OwnerTests or Lifetime or DesktopProject or OrchestrationAssemblyInfo\n        ? Restore(path, source) : source;\n", "        WorkspaceTests or Host or OwnerInverse or OwnerTests or Lifetime or DesktopProject or OrchestrationAssemblyInfo\n        ? Restore(path, source)\n        : path == RuntimeEventForwardingSourceInverse.Runtime ? RuntimeEventForwardingSourceInverse.Restore(path, source) : source;\n", 1),
            ("    internal static string Restore(string path, string source)\n    {\n", "    internal static string Restore(string path, string source)\n    {\n        if (path == DesktopProject) source = RuntimeEventForwardingSourceInverse.Restore(path, source);\n", 1),
        ],
        Runtime => [.. Wrappers(), .. SetupRefinements, .. RuntimeEdits, .. OperationEdits, .. TailEdits],
        _ => throw new AssertFailedException("Unknown runtime-forwarding inverse path: " + path),
    };

    internal const string NewLink = "    <Compile Include=\"../CodeAlta.Tests/RuntimeEventForwardingSourceInverse.cs\" Link=\"RuntimeEventForwardingSourceInverse.cs\" />\n";
    private const string DesktopLink = "    <Compile Include=\"../CodeAlta.Tests/DesktopOwnedSessionSourceInverse.cs\" Link=\"DesktopOwnedSessionSourceInverse.cs\" />\n";

    private static (string, string, int) Wrapper(string signature, string forwarding, string bodySignature)
        => (signature + "\n    {\n", signature + "\n" + forwarding + "\n\n" + bodySignature + "\n    {\n", 1);

    private static IEnumerable<(string, string, int)> Wrappers()
    {
        yield return Wrapper("    public async Task AppendSessionEventAsync(\n        SessionViewDescriptor session,\n        AgentEvent @event,\n        CancellationToken cancellationToken = default)",
            "        => await AdmitAsync(() => AppendSessionEventOwnedBodyAsync(session, @event, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);",
            "    private async Task AppendSessionEventOwnedBodyAsync(SessionViewDescriptor session, AgentEvent @event, CancellationToken cancellationToken)");
        yield return Wrapper("    public async Task<string> GetNotesMarkdownAsync(string sessionId, CancellationToken cancellationToken = default)",
            "        => await AdmitAsync(() => GetNotesMarkdownOwnedBodyAsync(sessionId, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);",
            "    private async Task<string> GetNotesMarkdownOwnedBodyAsync(string sessionId, CancellationToken cancellationToken)");
        yield return Wrapper("    public async Task UpdateNotesAsync(string sessionId, string markdown, AgentNotesUpdateKind kind,\n        Action<AgentNotesEvent> committed, CancellationToken cancellationToken = default)",
            "        => await AdmitAsync(() => UpdateNotesOwnedBodyAsync(sessionId, markdown, kind, committed, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);",
            "    private async Task UpdateNotesOwnedBodyAsync(string sessionId, string markdown, AgentNotesUpdateKind kind,\n        Action<AgentNotesEvent> committed, CancellationToken cancellationToken)");
        yield return Wrapper("    public async Task<SessionViewDescriptor?> TryGetActiveSessionDescriptorAsync(\n        string sessionId,\n        CancellationToken cancellationToken = default)",
            "        => await AdmitAsync(() => TryGetActiveSessionDescriptorOwnedBodyAsync(sessionId, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);",
            "    private async Task<SessionViewDescriptor?> TryGetActiveSessionDescriptorOwnedBodyAsync(string sessionId, CancellationToken cancellationToken)");
        yield return Wrapper("    internal async Task<SessionViewDescriptor?> ResolveOwnedSessionAsync(string sessionId, CancellationToken cancellationToken)",
            "        => await AdmitAsync(() => ResolveOwnedSessionBodyAsync(sessionId, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);",
            "    private async Task<SessionViewDescriptor?> ResolveOwnedSessionBodyAsync(string sessionId, CancellationToken cancellationToken)");
        yield return Wrapper("    public async Task<bool> ReconcileRecoverableSessionCacheAsync(CancellationToken cancellationToken = default)",
            "        => await AdmitAsync(() => ReconcileRecoverableSessionCacheOwnedBodyAsync(cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);",
            "    private async Task<bool> ReconcileRecoverableSessionCacheOwnedBodyAsync(CancellationToken cancellationToken)");
        yield return Wrapper("    public async Task<bool> DeleteSessionAsync(SessionViewDescriptor session, CancellationToken cancellationToken = default)",
            "        => await AdmitAsync(() => DeleteSessionOwnedBodyAsync(session, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);",
            "    private async Task<bool> DeleteSessionOwnedBodyAsync(SessionViewDescriptor session, CancellationToken cancellationToken)");
        yield return Wrapper("    public async Task PersistSessionLocalStateAsync(SessionViewDescriptor session, CancellationToken cancellationToken = default)",
            "        => await AdmitAsync(() => PersistSessionLocalStateOwnedBodyAsync(session, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);",
            "    private async Task PersistSessionLocalStateOwnedBodyAsync(SessionViewDescriptor session, CancellationToken cancellationToken)");
        yield return Wrapper("    public async Task SetActiveSessionAgentPromptIdAsync(\n        string sessionId,\n        string agentPromptId,\n        CancellationToken cancellationToken = default)",
            "        => await AdmitAsync(() => SetActiveSessionAgentPromptIdOwnedBodyAsync(sessionId, agentPromptId, CancellationToken.None), cancellationToken)\n            .WaitAsync(cancellationToken).ConfigureAwait(false);",
            "    private async Task SetActiveSessionAgentPromptIdOwnedBodyAsync(string sessionId, string agentPromptId, CancellationToken cancellationToken)");
        yield return Wrapper("    public async Task<IReadOnlyList<AgentEvent>?> TryReadStoredHistoryAsync(\n        SessionViewDescriptor session,\n        Action<Exception>? onUnavailable,\n        CancellationToken cancellationToken = default)",
            "        => await AdmitAsync(() => TryReadStoredHistoryOwnedBodyAsync(session, onUnavailable, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);",
            "    private async Task<IReadOnlyList<AgentEvent>?> TryReadStoredHistoryOwnedBodyAsync(SessionViewDescriptor session,\n        Action<Exception>? onUnavailable, CancellationToken cancellationToken)");
        yield return Wrapper("    public async Task<SessionViewDescriptor> CreateGlobalSessionAsync(\n        SessionExecutionOptions options,\n        string? title,\n        string? parentSessionId,\n        AltaActorProvenance? createdBy,\n        CancellationToken cancellationToken = default)",
            "        => await AdmitAsync(() => CreateGlobalSessionOwnedBodyAsync(options, title, parentSessionId, createdBy, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);",
            "    private async Task<SessionViewDescriptor> CreateGlobalSessionOwnedBodyAsync(SessionExecutionOptions options, string? title,\n        string? parentSessionId, AltaActorProvenance? createdBy, CancellationToken cancellationToken)");
        yield return Wrapper("    public async Task<SessionViewDescriptor> CreateProjectSessionAsync(\n        ProjectDescriptor project,\n        SessionExecutionOptions options,\n        string? title,\n        string? parentSessionId,\n        AltaActorProvenance? createdBy,\n        CancellationToken cancellationToken = default)",
            "        => await AdmitAsync(() => CreateProjectSessionOwnedBodyAsync(project, options, title, parentSessionId, createdBy, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);",
            "    private async Task<SessionViewDescriptor> CreateProjectSessionOwnedBodyAsync(ProjectDescriptor project, SessionExecutionOptions options,\n        string? title, string? parentSessionId, AltaActorProvenance? createdBy, CancellationToken cancellationToken)");
        yield return Wrapper("    internal async Task<AgentRunId> SendAsync(\n        SessionViewDescriptor session,\n        SessionExecutionOptions options,\n        AgentSendOptions sendOptions,\n        CancellationToken cancellationToken,\n        CancellationToken coordinationCancellationToken)",
            "        => await AdmitAsync(() => SendOwnedBodyAsync(session, options, sendOptions, cancellationToken, CancellationToken.None), coordinationCancellationToken)\n            .WaitAsync(coordinationCancellationToken).ConfigureAwait(false);",
            "    private async Task<AgentRunId> SendOwnedBodyAsync(\n        SessionViewDescriptor session, SessionExecutionOptions options, AgentSendOptions sendOptions,\n        CancellationToken cancellationToken, CancellationToken coordinationCancellationToken)");
        yield return Wrapper("    public async Task<SessionViewQueuedPrompt> QueuePromptAsync(\n        SessionViewDescriptor session,\n        string prompt,\n        string kind,\n        AltaActorProvenance? submittedBy,\n        CancellationToken cancellationToken = default)",
            "        => await AdmitAsync(() => QueuePromptOwnedBodyAsync(session, prompt, kind, submittedBy, CancellationToken.None), cancellationToken)\n            .WaitAsync(cancellationToken).ConfigureAwait(false);",
            "    private async Task<SessionViewQueuedPrompt> QueuePromptOwnedBodyAsync(SessionViewDescriptor session, string prompt, string kind,\n        AltaActorProvenance? submittedBy, CancellationToken cancellationToken)");
        yield return Wrapper("    public async Task<AgentRunId> ActivateSkillAsync(\n        SessionViewDescriptor session,\n        SessionExecutionOptions options,\n        string skillName,\n        CancellationToken cancellationToken = default)",
            "        => await AdmitAsync(() => ActivateSkillOwnedBodyAsync(session, options, skillName, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);",
            "    private async Task<AgentRunId> ActivateSkillOwnedBodyAsync(SessionViewDescriptor session, SessionExecutionOptions options,\n        string skillName, CancellationToken cancellationToken)");
        yield return Wrapper("    public async Task<SkillActivation> CreateSkillActivationAsync(\n        SessionViewDescriptor session,\n        SessionExecutionOptions options,\n        string skillName,\n        CancellationToken cancellationToken = default)",
            "        => await AdmitAsync(() => CreateSkillActivationOwnedBodyAsync(session, options, skillName, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);",
            "    private async Task<SkillActivation> CreateSkillActivationOwnedBodyAsync(SessionViewDescriptor session, SessionExecutionOptions options,\n        string skillName, CancellationToken cancellationToken)");
        yield return Wrapper("    public async Task<AgentRunId> SteerAsync(\n        SessionViewDescriptor session,\n        SessionExecutionOptions options,\n        AgentSteerOptions steerOptions,\n        CancellationToken cancellationToken = default)",
            "        => await AdmitAsync(() => SteerOwnedBodyAsync(session, options, steerOptions, cancellationToken), cancellationToken)\n            .WaitAsync(cancellationToken).ConfigureAwait(false);",
            "    private async Task<AgentRunId> SteerOwnedBodyAsync(SessionViewDescriptor session, SessionExecutionOptions options,\n        AgentSteerOptions steerOptions, CancellationToken cancellationToken)");
        yield return Wrapper("    public async Task<bool> HasActiveRunAsync(SessionViewDescriptor session, CancellationToken cancellationToken = default)",
            "        => await AdmitAsync(() => HasActiveRunOwnedBodyAsync(session, CancellationToken.None), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);",
            "    private async Task<bool> HasActiveRunOwnedBodyAsync(SessionViewDescriptor session, CancellationToken cancellationToken)");
        yield return Wrapper("    public async Task<bool> HasActiveCoordinatorSessionAsync(string sessionId, CancellationToken cancellationToken = default)",
            "        => await AdmitAsync(() => HasActiveCoordinatorSessionOwnedBodyAsync(sessionId, CancellationToken.None), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);",
            "    private async Task<bool> HasActiveCoordinatorSessionOwnedBodyAsync(string sessionId, CancellationToken cancellationToken)");
        yield return Wrapper("    public async Task AbortAsync(string sessionId, CancellationToken cancellationToken = default)",
            "    {\n        cancellationToken.ThrowIfCancellationRequested();\n        if (_forwarding.IsClosed) return;\n        await AdmitAsync(() => AbortOwnedBodyAsync(sessionId, cancellationToken), cancellationToken)\n            .WaitAsync(cancellationToken).ConfigureAwait(false);\n    }",
            "    private async Task AbortOwnedBodyAsync(string sessionId, CancellationToken cancellationToken)");
        yield return Wrapper("    public async Task CompactAsync(\n        SessionViewDescriptor session,\n        SessionExecutionOptions options,\n        CancellationToken cancellationToken = default)",
            "        => await AdmitAsync(() => CompactOwnedBodyAsync(session, options, cancellationToken), cancellationToken)\n            .WaitAsync(cancellationToken).ConfigureAwait(false);",
            "    private async Task CompactOwnedBodyAsync(SessionViewDescriptor session, SessionExecutionOptions options, CancellationToken cancellationToken)");
        yield return Wrapper("    public async Task<IReadOnlyList<AgentEvent>> GetOrResumeHistoryAsync(\n        SessionViewDescriptor session,\n        SessionExecutionOptions options,\n        CancellationToken cancellationToken = default)",
            "        => await AdmitAsync(() => GetOrResumeHistoryOwnedBodyAsync(session, options, cancellationToken), cancellationToken)\n            .WaitAsync(cancellationToken).ConfigureAwait(false);",
            "    private async Task<IReadOnlyList<AgentEvent>> GetOrResumeHistoryOwnedBodyAsync(\n        SessionViewDescriptor session, SessionExecutionOptions options, CancellationToken cancellationToken)");
        yield return Wrapper("    public async Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(string sessionId, CancellationToken cancellationToken = default)",
            "        => await AdmitAsync(() => GetHistoryOwnedBodyAsync(sessionId, cancellationToken), cancellationToken)\n            .WaitAsync(cancellationToken).ConfigureAwait(false);",
            "    private async Task<IReadOnlyList<AgentEvent>> GetHistoryOwnedBodyAsync(string sessionId, CancellationToken cancellationToken)");
    }

    private static IReadOnlyList<(string Before, string After, int Count)> SetupRefinements =>
    [
        ("        ValidateDiscoveryPaths(session, options, project);\n" + OldPendingLocals + "\n        var effectiveAgentPromptId = selectedPrompt;",
            "        ValidateDiscoveryPaths(session, options, project);\n\n        var effectiveAgentPromptId = selectedPrompt;", 1),
        ("        await UpdateSessionLocalStateAsync(session, cancellationToken).ConfigureAwait(false);\n        if (startNewSession)", SetupStateUpdate + "        if (startNewSession)", 1),
        ("        RuntimeSessionEntry? entry = null;\n        var actor = _sessionActors.GetOrCreate(session.SessionId);\n        var projector = new EventProjector(",
            "        RuntimeSessionEntry? entry = null;\n        var projector = new EventProjector(", 1),
    ];

    private const string OldPendingLocals = """
            RuntimeSessionEntry? existing = null;
            var pendingAgentPromptId = default(string?);
            if (!string.IsNullOrWhiteSpace(session.SessionId) &&
                _entries.TryGetValue(session.SessionId, out existing) &&
                !existing.IsTerminated)
            {
                pendingAgentPromptId = NormalizeOptionalText(existing.PendingAgentPromptId);
            }
    """ + "\n";

    private const string SetupStateUpdate = """
            var actor = GetActorForWork(session.SessionId);
            // Queue mutations share this mailbox. Keep the durable read/modify/append indivisible
            // with respect to them, without joining setup or retirement from inside the actor.
            await actor.QueryAsync(async actorCancellationToken =>
            {
                await UpdateSessionLocalStateAsync(session, actorCancellationToken).ConfigureAwait(false);
                return true;
            }, CancellationToken.None).ConfigureAwait(false);
    """ + "\n";

    private static IReadOnlyList<(string Before, string After, int Count)> RuntimeEdits =>
    [
        ("    private bool _disposed;\n", Fields + "    private bool _disposed;\n" + Admission, 1),
        (EnsureSignature + OldEnsure, EnsureSignature + NewEnsure, 1),
        ("    private async ValueTask<AgentSessionHandleId> EnsureCoordinatorSessionCoreAsync(\n        SessionViewDescriptor session,\n", Preparation + "    private async ValueTask<AgentSessionHandleId> CreateCoordinatorSessionAsync(\n        SessionViewDescriptor session,\n", 1),
        ("        SessionExecutionOptions options,\n        CancellationToken cancellationToken = default)\n    {\n        ArgumentNullException.ThrowIfNull(session);\n        ArgumentNullException.ThrowIfNull(options);\n        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);", "        SessionExecutionOptions options,\n        Task ticket,\n        RuntimeSessionEntry? previousEntry,\n        string? selectedPrompt,\n        CancellationToken cancellationToken = default)\n    {\n        ArgumentNullException.ThrowIfNull(session);\n        ArgumentNullException.ThrowIfNull(options);\n        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);", 1),
        ("        var effectiveAgentPromptId = pendingAgentPromptId\n            ?? NormalizeOptionalText(options.AgentPromptId)\n            ?? NormalizeOptionalText(session.AgentPromptId);\n        session.AgentPromptId = effectiveAgentPromptId;", "        var effectiveAgentPromptId = selectedPrompt;\n        session.AgentPromptId = effectiveAgentPromptId;", 1),
        (OldReplacement + "\n        var requestedSessionId = NormalizeOptionalText(session.SessionId);\n", "        AgentSessionHandleId sessionHandleId;\n        bool startNewSession;\n        lock (_identityGate) startNewSession = _newSessionIds.Remove(session.SessionId);\n\n        var requestedSessionId = NormalizeOptionalText(session.SessionId);\n", 1),
        ("        };\n\n        var startNewSession = previousSessionId is null;\n\n        if (startNewSession)\n", "        };\n\n        if (startNewSession)\n", 1),
        ("        session.ProviderId = options.ProviderId.Value;\n", Registration + "        session.ProviderId = options.ProviderId.Value;\n", 1),
        (Subscription + "        entry = new RuntimeSessionEntry(\n", "        entry = new RuntimeSessionEntry(\n", 1),
        ("            projector,\n            subscription);\n\n        _entries[session.SessionId] = entry;\n\n        return sessionHandleId;\n    }\n", "            projector,\n" + Publication, 1),
        ("                    actorCancellationToken.ThrowIfCancellationRequested();\n                    if (_entries.TryGetValue(sessionId, out var entry) && !entry.IsTerminated)\n", "                    actorCancellationToken.ThrowIfCancellationRequested();\n                    if (_entries.TryGetValue(sessionId, out var entry) && (!entry.IsTerminated || _transitions.ContainsKey(sessionId)))\n", 1),
    ];

    private const string Fields = "    private readonly OwnedProviderEventForwarding _forwarding = new();\n    private readonly ConcurrentDictionary<string, Task> _transitions = new(StringComparer.OrdinalIgnoreCase);\n    private readonly object _identityGate = new();\n    private readonly HashSet<string> _newSessionIds = new(StringComparer.OrdinalIgnoreCase);\n";
    private const string Admission = "\n" + """
        private Task<T> AdmitAsync<T>(Func<Task<T>> body, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _forwarding.RunAsync(body);
        }

        private Task AdmitAsync(Func<Task> body, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _forwarding.RunAsync(body);
        }

        private SessionActor GetActorForWork(string sessionId)
        {
            lock (_identityGate)
            {
                if (_sessionActors.TryGet(sessionId, out var existing)) return existing;
                ObjectDisposedException.ThrowIf(_disposed, this);
                var actor = _sessionActors.GetOrCreate(sessionId);
                return actor;
            }
        }
    """ + "\n";
    private const string EnsureSignature = "    public async Task<AgentSessionHandleId> EnsureCoordinatorSessionAsync(\n        SessionViewDescriptor session,\n        SessionExecutionOptions options,\n        CancellationToken cancellationToken = default)\n    {\n";
    private const string OldEnsure = """
            ArgumentNullException.ThrowIfNull(session);
            if (string.IsNullOrWhiteSpace(session.SessionId))
            {
                return await EnsureCoordinatorSessionCoreAsync(session, options, cancellationToken).ConfigureAwait(false);
            }

            var actor = _sessionActors.GetOrCreate(session.SessionId);
            return await actor.QueryAsync(
                    actorCancellationToken => EnsureCoordinatorSessionCoreAsync(session, options, actorCancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    """ + "\n";
    private const string NewEnsure = """
            return await AdmitAsync(async () =>
            {
                var entry = await ResolveCoordinatorEntryAsync(session, options).ConfigureAwait(false);
                return entry.SessionHandleId;
            }, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    """ + "\n";
    private const string Preparation = """
        private void ReserveSessionIdentity(SessionViewDescriptor session)
        {
            ArgumentNullException.ThrowIfNull(session);
            lock (_identityGate)
            {
                if (!string.IsNullOrWhiteSpace(session.SessionId)) return;
                session.SessionId = Guid.CreateVersion7().ToString();
                _newSessionIds.Add(session.SessionId);
            }
        }

        private async Task<RuntimeSessionEntry> ResolveCoordinatorEntryAsync(SessionViewDescriptor session, SessionExecutionOptions options, bool history = false)
        {
            ReserveSessionIdentity(session);
            ArgumentNullException.ThrowIfNull(options);
            while (true)
            {
                ObjectDisposedException.ThrowIf(_forwarding.IsClosed, this);
                var actor = GetActorForWork(session.SessionId);
                var prepared = await actor.QueryAsync(
                    actorCancellationToken => history && _entries.TryGetValue(session.SessionId, out var active) && !active.IsTerminated && !active.Attachment.IsRetiring
                        ? ValueTask.FromResult(new CoordinatorPreparation(active, null))
                        : EnsureCoordinatorSessionCoreAsync(session, options, actorCancellationToken),
                    CancellationToken.None).ConfigureAwait(false);
                if (prepared.Entry is not null) return prepared.Entry;
                await prepared.Transition!.ConfigureAwait(false);
            }
        }

        // Actor prepare only: callers join the returned ticket outside the mailbox.
        private async ValueTask<CoordinatorPreparation> EnsureCoordinatorSessionCoreAsync(
            SessionViewDescriptor session, SessionExecutionOptions options, CancellationToken actorCancellationToken)
        {
            actorCancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_forwarding.IsClosed, this);
            if (_transitions.TryGetValue(session.SessionId, out var transition))
                return new CoordinatorPreparation(null, transition);
            _entries.TryGetValue(session.SessionId, out var existing);
            var prompt = NormalizeOptionalText(existing?.PendingAgentPromptId) ?? NormalizeOptionalText(options.AgentPromptId) ?? NormalizeOptionalText(session.AgentPromptId);
            // Preserve validation/instruction-build-before-retirement behavior. The body is retained.
            ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);
            ValidateDiscoveryPaths(session, options);
            var project = await ResolveProjectAsync(session, actorCancellationToken).ConfigureAwait(false);
            ValidateDiscoveryPaths(session, options, project);
            session.AgentPromptId = prompt;
            _instructionTemplateProvider.BuildCoordinatorInstructions(session, project, options.Model, prompt);
            if (existing is not null && !existing.Attachment.IsRetiring && existing.Matches(options, prompt))
            {
                existing.PendingAgentPromptId = null;
                return new CoordinatorPreparation(existing, null);
            }
            var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var retirement = existing is null ? Task.CompletedTask : _forwarding.RetireAsync(existing.Attachment);
            Task? ticket = null;
            transition = _forwarding.RunAsync(async () =>
            {
                await launch.Task.ConfigureAwait(false);
                try
                {
                    await retirement.ConfigureAwait(false);
                    ObjectDisposedException.ThrowIf(_forwarding.IsClosed, this);
                    await CreateCoordinatorSessionAsync(session, options, ticket!, existing, prompt, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    await GetActorForWork(session.SessionId).QueryAsync(_ =>
                    {
                        if (_transitions.TryGetValue(session.SessionId, out var current) && ReferenceEquals(current, ticket))
                            _transitions.TryRemove(session.SessionId, out var completedTransition);
                        return ValueTask.FromResult(true);
                    }, CancellationToken.None).ConfigureAwait(false);
                }
            }, external: false);
            _transitions[session.SessionId] = transition;
            ticket = transition;
            launch.TrySetResult();
            return new CoordinatorPreparation(null, transition);
        }

        private sealed record CoordinatorPreparation(RuntimeSessionEntry? Entry, Task? Transition);
    """ + "\n\n";
    private const string OldReplacement = """
            RuntimeSessionEntry? previousEntry = null;
            AgentSessionHandleId sessionHandleId;

            if (existing is not null && existing.Matches(options, session.AgentPromptId))
            {
                existing.PendingAgentPromptId = null;
                return existing.SessionHandleId;
            }

            if (!string.IsNullOrWhiteSpace(session.SessionId))
            {
                _entries.TryRemove(session.SessionId, out previousEntry);
            }

            if (previousEntry is not null)
            {
                await previousEntry.DisposeAsync(_agentHub).ConfigureAwait(false);
            }

            var previousSessionId = string.IsNullOrWhiteSpace(session.SessionId) ? null : session.SessionId;
            if (previousSessionId is null)
            {
                session.SessionId = Guid.CreateVersion7().ToString();
            }
    """ + "\n";
    private const string Registration = """
            var attachment = _forwarding.RegisterAttachment(
                session.SessionId, sessionHandleId.ToString(),
                () => _agentHub.AbortAsync(sessionHandleId, CancellationToken.None),
                () => _agentHub.StopSessionAsync(sessionHandleId, CancellationToken.None));
            try
            {
    """ + "\n";

    private static IReadOnlyList<(string Before, string After, int Count)> OperationEdits =>
    [
        ("        CancellationToken cancellationToken)\n    {\n        if (await MarkActiveRunIfStillInFlightAsync", "        CancellationToken cancellationToken,\n        RuntimeSessionEntry capturedEntry)\n    {\n        if (await MarkActiveRunIfStillInFlightAsync", 1),
        ("            PublishRunSubmittedEvent(session.SessionId, runId, runStartedAt);\n        }\n    }\n", "            PublishRunSubmittedEvent(session.SessionId, runId, runStartedAt);\n        }\n" + CapturedCompletion + "    }\n" + CapturedClear, 1),
        ("        try\n        {\n            var sessionStateUpdated = false;", "        RuntimeSessionEntry? capturedEntry = null;\n        OwnedProviderEventForwarding.Use? handleUse = null;\n        try\n        {\n            while (true)\n            {\n            var candidate = await ResolveCoordinatorEntryAsync(session, options).ConfigureAwait(false);\n            GetActorForWork(session.SessionId);\n            var sessionStateUpdated = false;", 1),
        ("                        var ensuredHandleId = await EnsureCoordinatorSessionCoreAsync(session, options, actorCancellationToken).ConfigureAwait(false);\n                        session.MarkStarted(DateTimeOffset.UtcNow);\n", SendAcquisition + "                        session.MarkStarted(DateTimeOffset.UtcNow);\n", 1),
        ("                        return ensuredHandleId;\n                    },\n                    coordinationCancellationToken)", "                        return candidate.SessionHandleId;\n                    },\n                    coordinationCancellationToken)", 1),
        ("\n            if (sessionStateUpdated)\n", "            if (handleUse is null) continue;\n\n            if (sessionStateUpdated)\n", 1),
        ("            var runId = await _agentHub.RunAsync(sessionHandleId, sendOptions, cancellationToken).ConfigureAwait(false);\n            await PublishRunSubmittedIfStillInFlightAsync(session, runId, runStartedAt, coordinationCancellationToken).ConfigureAwait(false);\n\n            return runId;\n", "            using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, candidate.Attachment.Cancellation.Token);\n            var runId = await RunCapturedAsync(sessionHandleId, sendOptions, execution.Token).ConfigureAwait(false);\n            await PublishRunSubmittedIfStillInFlightAsync(session, runId, runStartedAt, coordinationCancellationToken, candidate).ConfigureAwait(false);\n\n            return runId;\n            }\n", 1),
        ("            var activeRunId = await ClearActiveRunAsync(session.SessionId, CancellationToken.None).ConfigureAwait(false);\n            PublishRunFinishedEvent(", "            var activeRunId = await ClearCapturedRunAsync(capturedEntry).ConfigureAwait(false);\n            PublishRunFinishedEvent(", 1),
        ("            await ClearActiveRunAsync(session.SessionId, CancellationToken.None).ConfigureAwait(false);\n            PublishRuntimeFailureEvent(session, ex);", "            await ClearCapturedRunAsync(capturedEntry).ConfigureAwait(false);\n            PublishRuntimeFailureEvent(session, ex);", 1),
        ("            PublishRuntimeFailureEvent(session, ex);\n            throw;\n        }\n    }\n", "            PublishRuntimeFailureEvent(session, ex);\n            throw;\n        }\n        finally { handleUse?.Dispose(); }\n    }\n" + RunCaptured, 1),
        (OldSteer, NewSteer, 1),
        ("        SessionActorCommandResult result;\n        try\n", "        SessionActorCommandResult result;\n        RuntimeSessionEntry? capturedEntry = null;\n        OwnedProviderEventForwarding.Use? handleUse = null;\n        try\n", 1),
        ("                        var entry = await GetEntryAsync(sessionId, actorCancellationToken).ConfigureAwait(false);\n" + OldAbortControl, "                        var entry = await GetEntryAsync(sessionId, actorCancellationToken).ConfigureAwait(false);\n" + NewAbortControl, 1),
        ("            return;\n        }\n\n        if (!result.Succeeded)\n", "            return;\n        }\n        finally { handleUse?.Dispose(); }\n\n        if (!result.Succeeded)\n", 1),
        (OldDetach, NewDetach, 1),
        ("        AgentSessionHandleId? sessionHandleId = null;\n        var result = await _sessionActors.GetOrCreate(session.SessionId).ExecuteAsync(", "        ReserveSessionIdentity(session);\n        GetActorForWork(session.SessionId);\n        var result = await _sessionActors.GetOrCreate(session.SessionId).ExecuteAsync(", 1),
        ("                        $\"Manual compaction requested for '{session.Title}'.\"));\n\n                    sessionHandleId = await EnsureCoordinatorSessionCoreAsync(session, options, actorCancellationToken).ConfigureAwait(false);\n                },\n                cancellationToken)\n", "                        $\"Manual compaction requested for '{session.Title}'.\"));\n\n                    await Task.CompletedTask.ConfigureAwait(false);\n                },\n                CancellationToken.None)\n", 1),
        (OldCompactTail + "\n    private static bool ShouldPublishHostCompactionOutcome(AgentCompactionOutcome outcome)\n", NewCompactTail + "\n    private static bool ShouldPublishHostCompactionOutcome(AgentCompactionOutcome outcome)\n", 1),
        (OldResumeHistory, NewResumeHistory, 1),
        (OldActiveHistory, NewActiveHistory, 1),
        ("        var history = await _agentHub.GetSessionHistoryAsync(entry.SessionHandleId, cancellationToken).ConfigureAwait(false);\n        return entry.Projector.ProjectHistory(history);\n", "        var history = await _agentHub.GetSessionHistoryAsync(entry.SessionHandleId, cancellationToken).ConfigureAwait(false);\n        return await GetActorForWork(entry.SessionId).QueryAsync(async _ =>\n        {\n            await Task.CompletedTask.ConfigureAwait(false);\n            return entry.Projector.ProjectHistory(history);\n        }, CancellationToken.None).ConfigureAwait(false);\n", 1),
        ("    /// <inheritdoc />\n" + OldDispose, "    /// <inheritdoc />\n" + NewDispose, 1),
    ];

    private const string CapturedCompletion = "\n" + """
            async Task<bool> MarkActiveRunIfStillInFlightAsync(string sessionId, AgentRunId id, DateTimeOffset started, CancellationToken token)
                => await GetActorForWork(sessionId).QueryAsync(_ =>
                    ValueTask.FromResult(capturedEntry.MarkActiveRunIfStillInFlight(id, started)), CancellationToken.None).ConfigureAwait(false);
    """ + "\n";
    private const string CapturedClear = "\n" + """
        private async Task<AgentRunId?> ClearCapturedRunAsync(RuntimeSessionEntry? entry)
            => entry is null ? null : await GetActorForWork(entry.SessionId).QueryAsync(_ =>
                ValueTask.FromResult(entry.ClearActiveRun()), CancellationToken.None).ConfigureAwait(false);
    """ + "\n";
    private const string SendAcquisition = """
                            await Task.CompletedTask.ConfigureAwait(false);
                            if (!_entries.TryGetValue(session.SessionId, out var current) || !ReferenceEquals(current, candidate)
                                || !candidate.Matches(options, NormalizeOptionalText(candidate.PendingAgentPromptId)
                                    ?? NormalizeOptionalText(options.AgentPromptId) ?? NormalizeOptionalText(session.AgentPromptId)))
                                return default(AgentSessionHandleId);
                            handleUse = candidate.Attachment.TryAcquireHandleUse();
                            if (handleUse is null) return default(AgentSessionHandleId);
                            candidate.PendingAgentPromptId = null;
                            capturedEntry = candidate;
    """ + "\n";
    private const string RunCaptured = "\n" + """
        private async Task<AgentRunId> RunCapturedAsync(AgentSessionHandleId sessionHandleId, AgentSendOptions sendOptions, CancellationToken cancellationToken)
        {
            var runId = await _agentHub.RunAsync(sessionHandleId, sendOptions, cancellationToken).ConfigureAwait(false);
            return runId;
        }
    """ + "\n";
    private const string OldSteer = """
            var sessionHandleId = await _sessionActors.GetOrCreate(session.SessionId).QueryAsync(
                    async actorCancellationToken =>
                    {
                        var entry = await GetActiveRuntimeSessionForSteeringAsync(session, options, actorCancellationToken).ConfigureAwait(false);
                        return entry.SessionHandleId;
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            return await _agentHub.SteerAsync(sessionHandleId, steerOptions, cancellationToken).ConfigureAwait(false);
        }
    """ + "\n";
    private const string NewSteer = """
            OwnedProviderEventForwarding.Use? handleUse = null;
            try
            {
            GetActorForWork(session.SessionId);
            var sessionHandleId = await _sessionActors.GetOrCreate(session.SessionId).QueryAsync(
                    async actorCancellationToken =>
                    {
                        var entry = await GetActiveRuntimeSessionForSteeringAsync(session, options, actorCancellationToken).ConfigureAwait(false);
                        handleUse = entry.Attachment.TryAcquireHandleUse()
                            ?? throw new InvalidOperationException("The coordinator attachment is retiring.");
                        return entry.SessionHandleId;
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);

            using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, handleUse!.Attachment.Cancellation.Token);
            return await SteerCapturedAsync(sessionHandleId, steerOptions, execution.Token).ConfigureAwait(false);
            }
            finally { handleUse?.Dispose(); }
        }

        private async Task<AgentRunId> SteerCapturedAsync(AgentSessionHandleId sessionHandleId, AgentSteerOptions steerOptions, CancellationToken cancellationToken)
        {
            return await _agentHub.SteerAsync(sessionHandleId, steerOptions, cancellationToken).ConfigureAwait(false);
        }
    """ + "\n";
    private const string OldAbortControl = """
                            await _agentHub.AbortAsync(entry.SessionHandleId, actorCancellationToken).ConfigureAwait(false);
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
    """ + "\n";
    private const string NewAbortControl = """
                            handleUse = entry.Attachment.TryAcquireHandleUse()
                                ?? throw new InvalidOperationException("The coordinator attachment is retiring.");
                            capturedEntry = entry;
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (result.Succeeded)
                {
                    using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, capturedEntry!.Attachment.Cancellation.Token);
                    await _agentHub.AbortAsync(capturedEntry.SessionHandleId, execution.Token).ConfigureAwait(false);
                }
    """ + "\n";
    private const string OldDetach = """
        public async Task<bool> DetachRuntimeSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

            var actor = _sessionActors.GetOrCreate(sessionId);
            var detached = await actor.QueryAsync(
                    async actorCancellationToken =>
                    {
                        await Task.CompletedTask.ConfigureAwait(false);
                        _entries.TryRemove(sessionId, out var entry);

                        if (entry is null)
                        {
                            return false;
                        }

                        await entry.DisposeAsync(_agentHub).ConfigureAwait(false);
                        return true;
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (detached)
            {
                await _sessionActors.RemoveAsync(sessionId, cancelPending: false).ConfigureAwait(false);
            }

            return detached;
        }
    """ + "\n";
    private const string NewDetach = """
        public async Task<bool> DetachRuntimeSessionAsync(string sessionId, CancellationToken cancellationToken = default)
            => await AdmitAsync(() => DetachOwnedBodyAsync(sessionId), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

        private async Task<bool> DetachOwnedBodyAsync(string sessionId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
            var actor = GetActorForWork(sessionId);
            while (true)
            {
                var preparation = await actor.QueryAsync(_ =>
                {
                    if (_transitions.TryGetValue(sessionId, out var current))
                        return ValueTask.FromResult((Claimed: false, Work: (Task?)current));
                    if (!_entries.TryGetValue(sessionId, out var entry))
                        return ValueTask.FromResult((Claimed: false, Work: (Task?)null));
                    var retirement = _forwarding.RetireAsync(entry.Attachment);
                    var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    Task? ticket = null;
                    var work = _forwarding.RunAsync(async () =>
                    {
                        await launch.Task.ConfigureAwait(false);
                        try { await retirement.ConfigureAwait(false); }
                        finally
                        {
                            await actor.QueryAsync(token =>
                            {
                                if (_entries.TryGetValue(sessionId, out var active) && ReferenceEquals(active, entry))
                                    _entries.TryRemove(sessionId, out var removedEntry);
                                if (_transitions.TryGetValue(sessionId, out var currentTicket) && ReferenceEquals(currentTicket, ticket))
                                    _transitions.TryRemove(sessionId, out var removedTransition);
                                return ValueTask.FromResult(true);
                            }, CancellationToken.None).ConfigureAwait(false);
                        }
                    }, external: false);
                    _transitions[sessionId] = work;
                    ticket = work;
                    launch.TrySetResult();
                    return ValueTask.FromResult((Claimed: true, Work: (Task?)work));
                }, CancellationToken.None).ConfigureAwait(false);
                if (preparation.Work is null) return false;
                await preparation.Work.ConfigureAwait(false);
                if (preparation.Claimed) return true;
            }
        }
    """ + "\n";
    private const string OldCompactTail = """
            if (sessionHandleId is not { } handleId)
            {
                throw new InvalidOperationException($"Failed to resolve coordinator session for '{session.SessionId}'.");
            }

            var outcome = await _agentHub.CompactAsync(handleId, cancellationToken).ConfigureAwait(false);
            if (outcome is not null && ShouldPublishHostCompactionOutcome(outcome))
            {
                _events.TryPublish(new SessionHostEvent(
                    session.SessionId,
                    DateTimeOffset.UtcNow,
                    AgentSessionUpdateKind.CompactionCompleted,
                    outcome.Message ?? (outcome.Success ? "Manual compaction completed." : "Manual compaction failed.")));
            }
        }
    """ + "\n";
    private const string NewCompactTail = """
            OwnedProviderEventForwarding.Use? handleUse = null;
            try
            {
                while (handleUse is null)
                {
                    var entry = await ResolveCoordinatorEntryAsync(session, options).ConfigureAwait(false);
                    handleUse = await GetActorForWork(session.SessionId).QueryAsync(_ =>
                    {
                        var use = _entries.TryGetValue(session.SessionId, out var current) && ReferenceEquals(current, entry)
                            && entry.Matches(options, NormalizeOptionalText(entry.PendingAgentPromptId)
                                ?? NormalizeOptionalText(options.AgentPromptId) ?? NormalizeOptionalText(session.AgentPromptId))
                            ? entry.Attachment.TryAcquireHandleUse() : null;
                        if (use is not null) entry.PendingAgentPromptId = null;
                        return ValueTask.FromResult(use);
                    }, CancellationToken.None).ConfigureAwait(false);
                    if (handleUse is null) continue;
                    using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, entry.Attachment.Cancellation.Token);
                    var outcome = await _agentHub.CompactAsync(entry.SessionHandleId, execution.Token).ConfigureAwait(false);
                    if (outcome is not null && ShouldPublishHostCompactionOutcome(outcome))
                    {
                        _events.TryPublish(new SessionHostEvent(
                            session.SessionId,
                            DateTimeOffset.UtcNow,
                            AgentSessionUpdateKind.CompactionCompleted,
                            outcome.Message ?? (outcome.Success ? "Manual compaction completed." : "Manual compaction failed.")));
                    }
                }
            }
            finally { handleUse?.Dispose(); }
        }
    """ + "\n";
    private const string OldResumeHistory = """
            ArgumentNullException.ThrowIfNull(options);

            if (string.IsNullOrWhiteSpace(session.SessionId))
            {
                await EnsureCoordinatorSessionCoreAsync(session, options, cancellationToken).ConfigureAwait(false);
                var newEntry = await GetEntryAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
                return await GetProjectedHistoryAsync(newEntry, cancellationToken).ConfigureAwait(false);
            }

            var actor = _sessionActors.GetOrCreate(session.SessionId);
            return await actor.QueryAsync(
                    async actorCancellationToken =>
                    {
                        if (!_entries.TryGetValue(session.SessionId, out var entry) || entry.IsTerminated)
                        {
                            await EnsureCoordinatorSessionCoreAsync(session, options, actorCancellationToken).ConfigureAwait(false);
                            entry = await GetEntryAsync(session.SessionId, actorCancellationToken).ConfigureAwait(false);
                        }

                        return await GetProjectedHistoryAsync(entry, actorCancellationToken).ConfigureAwait(false);
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
    """ + "\n";
    private const string NewResumeHistory = """
            ArgumentNullException.ThrowIfNull(options);
            while (true)
            {
                var entry = await ResolveCoordinatorEntryAsync(session, options, history: true).ConfigureAwait(false);
                var use = await GetActorForWork(session.SessionId).QueryAsync(_ =>
                    ValueTask.FromResult(_entries.TryGetValue(session.SessionId, out var current) && ReferenceEquals(current, entry)
                        ? entry.Attachment.TryAcquireHandleUse() : null), CancellationToken.None).ConfigureAwait(false);
                if (use is null) continue;
                using (use)
                using (var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, entry.Attachment.Cancellation.Token))
                    return await GetProjectedHistoryAsync(entry, execution.Token).ConfigureAwait(false);
            }
        }
    """ + "\n";
    private const string OldActiveHistory = """
            var actor = _sessionActors.GetOrCreate(sessionId);
            return await actor.QueryAsync(
                    async actorCancellationToken =>
                    {
                        var entry = await GetEntryAsync(sessionId, actorCancellationToken).ConfigureAwait(false);
                        return await GetProjectedHistoryAsync(entry, actorCancellationToken).ConfigureAwait(false);
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
    """ + "\n";
    private const string NewActiveHistory = """
            var actor = GetActorForWork(sessionId);
            var selected = await actor.QueryAsync(
                    async actorCancellationToken =>
                    {
                        var entry = await GetEntryAsync(sessionId, actorCancellationToken).ConfigureAwait(false);
                        var use = entry.Attachment.TryAcquireHandleUse()
                            ?? throw new InvalidOperationException("The coordinator attachment is retiring.");
                        return (Entry: entry, Use: use);
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            using (selected.Use)
            using (var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, selected.Entry.Attachment.Cancellation.Token))
                return await GetProjectedHistoryAsync(selected.Entry, execution.Token).ConfigureAwait(false);
        }
    """ + "\n";
    private const string OldDispose = """
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            // Approval waits must finish before joining actors/runs that may be awaiting their callbacks.
            await Permissions.DisposeAsync().ConfigureAwait(false);
            _events.Complete();
            await _sessionActors.DisposeAsync().ConfigureAwait(false);

            foreach (var entry in _entries.Values)
            {
                await entry.DisposeAsync(_agentHub).ConfigureAwait(false);
            }

            _entries.Clear();
        }
    """ + "\n";
    private const string NewDispose = """
        public ValueTask DisposeAsync()
        {
            lock (_identityGate)
            {
            _disposed = true;
            return new ValueTask(_forwarding.CloseAsync(
                () => Permissions.DisposeAsync().AsTask(),
                async () =>
                {
                    await _sessionActors.DisposeAsync().ConfigureAwait(false);
                    _entries.Clear();
                }, _events.Complete));
            }
        }
    """ + "\n";

    private static IReadOnlyList<(string Before, string After, int Count)> TailEdits =>
    [
        ("    private async Task PostAgentEventToActorAsync(\n        SessionActor actor,\n        string sessionId,\n        EventProjector projector,\n        AgentEvent @event)\n    {\n", "    private Task PostAgentEventToActorAsync(\n        SessionActor actor,\n        string sessionId,\n        EventProjector projector,\n        AgentEvent @event)\n        => _forwarding.Forward(projector.Entry!.Attachment,\n            use => PostAgentEventToActorCoreAsync(actor, sessionId, projector, @event, use));\n\n    private async Task PostAgentEventToActorCoreAsync(\n        SessionActor actor, string sessionId, EventProjector projector, AgentEvent @event,\n        OwnedProviderEventForwarding.Use projectionUse)\n    {\n", 1),
        ("                    var sanitized = projector.Project(@event);\n                    var notifications = _entries.TryGetValue(sessionId, out var entry)\n                        ? entry.TakeParentNotifications(sanitized)\n                        : Array.Empty<ParentNotificationWork>();\n", "                    var sanitized = projector.Project(@event);\n                    var notifications = projector.Entry!.TakeParentNotifications(sanitized);\n", 1),
        ("            foreach (var notification in parentNotifications)\n", "            projectionUse.Dispose();\n            foreach (var notification in parentNotifications)\n", 1),
        (OldQueuedRun, NewQueuedRun, 1),
        ("    private async Task<QueuedPromptDrainWork?> TryMarkNextQueuedPromptSubmittingAsync(string sessionId)\n    {\n        var actor = GetActorForWork(sessionId);\n        return await actor.QueryAsync(\n", "    private async Task<QueuedPromptDrainWork?> TryMarkNextQueuedPromptSubmittingAsync(string sessionId)\n    {\n        var actor = GetActorForWork(sessionId);\n        while (!_disposed)\n        {\n        Task? transition = null;\n        var work = await actor.QueryAsync(\n", 1),
        ("                async actorCancellationToken =>\n                {\n                    if (!_entries.TryGetValue(sessionId, out var entry) || entry.IsTerminated || entry.HasActiveRun || entry.QueueDrainInProgress)\n", "                async actorCancellationToken =>\n                {\n                    if (_transitions.TryGetValue(sessionId, out transition)) return null;\n                    if (!_entries.TryGetValue(sessionId, out var entry) || entry.IsTerminated || entry.HasActiveRun || entry.QueueDrainInProgress)\n", 1),
        (OldQueuedEnsure, NewQueuedEnsure, 1),
        ("                    var timestamp = DateTimeOffset.UtcNow;\n                    item.State = \"submitting\";\n", "                    var use = entry.Attachment.TryAcquireHandleUse();\n                    if (use is null) return null;\n                    try\n                    {\n                    var timestamp = DateTimeOffset.UtcNow;\n                    item.State = \"submitting\";\n", 1),
        ("                    PublishQueueChanged(sessionId, localState, item, timestamp, isEnqueued: false);\n                    return new QueuedPromptDrainWork(sessionHandleId, CloneQueuedPrompt(item));\n", "                    PublishQueueChanged(sessionId, localState, item, timestamp, isEnqueued: false);\n                    return new QueuedPromptDrainWork(sessionHandleId, CloneQueuedPrompt(item), entry, use);\n                    }\n                    catch { use.Dispose(); throw; }\n", 1),
        ("            .ConfigureAwait(false);\n    }\n\n    private async Task MarkQueuedPromptSubmittedAsync(\n", "            .ConfigureAwait(false);\n        if (transition is null) return work;\n        await transition.ConfigureAwait(false);\n        // Re-read durable queue state after transition; never reuse the earlier state/item.\n        }\n        return null;\n    }\n\n    private async Task MarkQueuedPromptSubmittedAsync(\n", 1),
        ("    private async Task MarkQueuedPromptSubmittedAsync(\n        string sessionId,\n", "    private async Task MarkQueuedPromptSubmittedAsync(\n        RuntimeSessionEntry capturedEntry,\n", 1),
        ("    private async Task MarkQueuedPromptFailedAsync(string sessionId, string queueItemId, string error, DateTimeOffset timestamp)\n        => await UpdateQueuedPromptStateAsync(\n", "    private async Task MarkQueuedPromptFailedAsync(RuntimeSessionEntry capturedEntry, string queueItemId, string error, DateTimeOffset timestamp)\n        => await UpdateQueuedPromptStateAsync(\n", 1),
        ("        => await UpdateQueuedPromptStateAsync(\n                sessionId,\n                queueItemId,\n", "        => await UpdateQueuedPromptStateAsync(\n                capturedEntry,\n                queueItemId,\n", 2),
        ("    private async Task UpdateQueuedPromptStateAsync(\n        string sessionId,\n", "    private async Task UpdateQueuedPromptStateAsync(\n        RuntimeSessionEntry currentEntry,\n", 1),
        ("        Action<RuntimeSessionEntry>? updateEntry)\n    {\n        var actor = GetActorForWork(sessionId);\n", "        Action<RuntimeSessionEntry>? updateEntry)\n    {\n        var sessionId = currentEntry.SessionId;\n        var actor = GetActorForWork(sessionId);\n", 1),
        ("                        if (!_entries.TryGetValue(sessionId, out var currentEntry))\n                        {\n                            return false;\n                        }\n\n                        var localState = await ReadLatestLocalStateAsync(sessionId, currentEntry.CreatedAt, actorCancellationToken).ConfigureAwait(false);\n", "                        var localState = await ReadLatestLocalStateAsync(sessionId, currentEntry.CreatedAt, actorCancellationToken).ConfigureAwait(false);\n", 1),
        ("                        return true;\n                    }\n                    finally\n                    {\n                        if (updateEntry is not null && _entries.TryGetValue(sessionId, out var entry))\n                        {\n                            updateEntry(entry);\n                        }\n                    }\n", "                        return true;\n                    }\n                    finally\n                    {\n                        updateEntry?.Invoke(currentEntry);\n                    }\n", 1),
        ("                CreatedAt = DateTimeOffset.UtcNow,\n            };\n\n            if (await HasActiveRunAsync(parent, CancellationToken.None).ConfigureAwait(false))\n            {\n                try\n", "                CreatedAt = DateTimeOffset.UtcNow,\n            };\n\n            if (await HasActiveRunOwnedBodyAsync(parent, CancellationToken.None).ConfigureAwait(false))\n            {\n                try\n", 1),
        ("                    var runId = await SteerAsync(\n                            parent,\n                            CreateParentDeliveryExecutionOptions(parent),\n", "                    var runId = await SteerOwnedBodyAsync(\n                            parent,\n                            CreateParentDeliveryExecutionOptions(parent),\n", 1),
        ("            await QueuePromptAsync(parent, prompt, \"parent-notify\", submittedBy, CancellationToken.None).ConfigureAwait(false);\n            await TryDrainNextQueuedPromptAsync(parent.SessionId).ConfigureAwait(false);\n", "            await QueuePromptOwnedBodyAsync(parent, prompt, \"parent-notify\", submittedBy, CancellationToken.None).ConfigureAwait(false);\n            await TryDrainNextQueuedPromptAsync(parent.SessionId).ConfigureAwait(false);\n", 1),
        ("        SessionViewDescriptor? session = null;\n        try\n        {\n" + OldParentLookup, "        SessionViewDescriptor? session = null;\n        try\n        {\n            session = await ResolveParentFromCachedStoreAsync(sessionId, cancellationToken).ConfigureAwait(false);\n", 1),
        ("    private async Task ApplyLocalSessionStateAsync(SessionViewDescriptor session, CancellationToken cancellationToken)\n", CachedParentLookup + "    private async Task ApplyLocalSessionStateAsync(SessionViewDescriptor session, CancellationToken cancellationToken)\n", 1),
        ("            EventProjector projector,\n            IDisposable subscription)\n", "            EventProjector projector,\n            OwnedProviderEventForwarding.Attachment attachment)\n", 1),
        ("            Projector = projector;\n            Subscription = subscription;\n", "            Projector = projector;\n            Attachment = attachment;\n", 1),
        ("        public IDisposable Subscription { get; }\n\n        public EventProjector Projector { get; }", "        public OwnedProviderEventForwarding.Attachment Attachment { get; }\n\n        public EventProjector Projector { get; }", 1),
        ("        public void CompleteQueueDrain()\n            => QueueDrainInProgress = false;\n\n" + OldEntryDispose + "    }\n", "        public void CompleteQueueDrain()\n            => QueueDrainInProgress = false;\n\n    }\n", 1),
        ("    private sealed record QueuedPromptDrainWork(AgentSessionHandleId SessionHandleId, SessionViewQueuedPrompt Prompt);\n\n    private sealed record ParentNotificationPayload(string Kind, string Body);\n", "    private sealed record QueuedPromptDrainWork(AgentSessionHandleId SessionHandleId, SessionViewQueuedPrompt Prompt,\n        RuntimeSessionEntry Entry, OwnedProviderEventForwarding.Use Use);\n\n    private sealed record ParentNotificationPayload(string Kind, string Body);\n", 1),
        ("        private readonly Action<AgentEvent> _observeRuntimeSessionEvent;\n", "        private readonly Action<AgentEvent> _observeRuntimeSessionEvent;\n        public RuntimeSessionEntry? Entry { get; set; }\n", 1),
        // Remaining lookups are Abort, queue claim/update, queue append, and provenance append.
        ("            var actor = _sessionActors.GetOrCreate(sessionId);\n            result = await actor.ExecuteReservedAsync(", "            var actor = GetActorForWork(sessionId);\n            result = await actor.ExecuteReservedAsync(", 1),
        ("        var actor = _sessionActors.GetOrCreate(sessionId);\n        return await actor.QueryAsync(", "        var actor = GetActorForWork(sessionId);\n        return await actor.QueryAsync(", 1),
        ("        var actor = _sessionActors.GetOrCreate(sessionId);\n        await actor.QueryAsync(", "        var actor = GetActorForWork(sessionId);\n        await actor.QueryAsync(", 1),
        ("        var actor = _sessionActors.GetOrCreate(session.SessionId);\n        return await actor.QueryAsync(", "        var actor = GetActorForWork(session.SessionId);\n        return await actor.QueryAsync(", 1),
        ("        var actor = _sessionActors.GetOrCreate(session.SessionId);\n        await actor.QueryAsync(", "        var actor = GetActorForWork(session.SessionId);\n        await actor.QueryAsync(", 1),
    ];

    private const string OldQueuedRun = """
                var runStartedAt = DateTimeOffset.UtcNow;
                var runId = await _agentHub.RunAsync(
                        work.SessionHandleId,
                        new AgentSendOptions { Input = AgentInput.Text(work.Prompt.Prompt) },
                        CancellationToken.None)
                    .ConfigureAwait(false);
                await MarkQueuedPromptSubmittedAsync(sessionId, work.Prompt.QueueItemId, runId, runStartedAt, DateTimeOffset.UtcNow).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (_disposed && ex is OperationCanceledException)
                {
                    return;
                }

                await MarkQueuedPromptFailedAsync(sessionId, work.Prompt.QueueItemId, ex.Message, DateTimeOffset.UtcNow).ConfigureAwait(false);
            }
    """ + "\n";
    private const string NewQueuedRun = """
                var runStartedAt = DateTimeOffset.UtcNow;
                var runId = await _agentHub.RunAsync(
                        work.SessionHandleId,
                        new AgentSendOptions { Input = AgentInput.Text(work.Prompt.Prompt) },
                        work.Entry.Attachment.Cancellation.Token)
                    .ConfigureAwait(false);
                await MarkQueuedPromptSubmittedAsync(work.Entry, work.Prompt.QueueItemId, runId, runStartedAt, DateTimeOffset.UtcNow).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await MarkQueuedPromptFailedAsync(work.Entry, work.Prompt.QueueItemId, ex.Message, DateTimeOffset.UtcNow).ConfigureAwait(false);
            }
            finally { work.Use.Dispose(); }
    """ + "\n";
    private const string OldQueuedEnsure = """
                            var session = entry.ToDescriptor();
                            sessionHandleId = await EnsureCoordinatorSessionCoreAsync(session, entry.ToExecutionOptions(), actorCancellationToken).ConfigureAwait(false);
                            if (!_entries.TryGetValue(sessionId, out entry) || entry.IsTerminated || entry.HasActiveRun || entry.QueueDrainInProgress)
                            {
                                return null;
                            }
    """ + "\n";
    private const string NewQueuedEnsure = """
                            var session = entry.ToDescriptor();
                            var prepared = await EnsureCoordinatorSessionCoreAsync(session, entry.ToExecutionOptions(), actorCancellationToken).ConfigureAwait(false);
                            if (prepared.Transition is not null)
                            {
                                transition = prepared.Transition;
                                return null;
                            }
                            entry = prepared.Entry!;
                            sessionHandleId = entry.SessionHandleId;
    """ + "\n";
    private const string OldParentLookup = """
                await foreach (var candidate in ListRecoverableSessionsAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (string.Equals(candidate.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                    {
                        session = candidate;
                        break;
                    }
                }
    """ + "\n";
    private const string CachedParentLookup = """
        private async Task<SessionViewDescriptor?> ResolveParentFromCachedStoreAsync(string sessionId, CancellationToken cancellationToken)
        {
            // Finishing path: directly await the shared cached store, never a catalog-list producer.
            var metadata = await _sessionViewCatalog.JournalStore.CreateSessionStore()
                .GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (metadata is null) return null;
            var projects = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
            var session = TryCreateRecoverableSession(metadata, projects);
            if (session is not null)
            {
                if (metadata.ViewState is not null) ApplyCachedSessionLocalState(session, metadata.ViewState);
                else await ApplyPersistedSessionLocalStateAsync(session, cancellationToken).ConfigureAwait(false);
            }
            return session;
        }
    """ + "\n\n";
    private const string OldEntryDispose = """
            public async Task DisposeAsync(AgentHub hub)
            {
                Subscription.Dispose();
                await hub.StopSessionAsync(SessionHandleId).ConfigureAwait(false);
            }
    """ + "\n";
    private const string Subscription = """
            var subscription = await _agentHub.SubscribeSessionEventsAsync(
                    sessionHandleId,
                    @event => _ = PostAgentEventToActorAsync(actor, session.SessionId, projector, @event),
                    cancellationToken)
                .ConfigureAwait(false);
    """ + "\n\n";
    private static string Publication => """
                attachment);

            projector.Entry = entry;
    """ + "\n" + Subscription.TrimEnd('\n') + "\n" + """
            attachment.InstallSubscription(subscription);
            attachment.CompleteSetup();
            await actor.QueryAsync(_ =>
            {
                ObjectDisposedException.ThrowIf(_forwarding.IsClosed || attachment.IsRetiring, this);
                if (!_transitions.TryGetValue(session.SessionId, out var currentTicket) || !ReferenceEquals(currentTicket, ticket))
                    throw new InvalidOperationException("The coordinator transition no longer owns publication.");
                if (previousEntry is not null && !string.Equals(previousEntry.PendingAgentPromptId, selectedPrompt, StringComparison.Ordinal))
                    entry.PendingAgentPromptId = previousEntry.PendingAgentPromptId;
                _entries[session.SessionId] = entry;
                return ValueTask.FromResult(true);
            }, CancellationToken.None).ConfigureAwait(false);

            return sessionHandleId;
            }
            catch
            {
                // Signal setup before joining retirement: retirement may already be awaiting this record.
                attachment.CompleteSetup();
                await _forwarding.RetireAsync(attachment).ConfigureAwait(false);
                throw;
            }
        }
    """ + "\n";
}
