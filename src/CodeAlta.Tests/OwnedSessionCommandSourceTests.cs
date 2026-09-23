using System.Runtime.CompilerServices;

namespace CodeAlta.Tests;

/// <summary>Named current-source ownership and authorization guards; no host or runtime execution.</summary>
[TestClass]
public sealed class OwnedSessionCommandSourceTests
{
    private const string Owner = "CodeAlta.Orchestration/Runtime/OwnedSessionCommandService.cs";
    private const string Contracts = "CodeAlta.Orchestration/Runtime/OwnedSessionCommandContracts.cs";
    private const string Host = "CodeAlta.Orchestration/Hosting/CodeAltaHost.cs";
    private const string Options = "CodeAlta.Orchestration/Hosting/CodeAltaHostOptions.cs";
    private const string Builtin = "CodeAlta.Catalog/Skills/BuiltInSkillRootProviders.cs";
    private const string Runtime = "CodeAlta.Orchestration/Runtime/SessionRuntimeService.cs";

    [TestMethod]
    public void Requests_AreScalarOnlyAndOwnerDoesNotExposeHost()
    {
        var contracts = Read(Contracts);
        StringAssert.Contains(contracts, "record OwnedTextSendRequest(string ClientRequestId, string SessionId, string Text)");
        StringAssert.Contains(contracts, "record OwnedAbortRequest(string ClientRequestId, Guid TargetOperationId)");
        foreach (var forbidden in new[] { "SessionViewDescriptor", "SessionExecutionOptions", "CodeAltaHost", "OwnedSessionCommandPolicy" })
            Assert.IsFalse(contracts.Contains(forbidden, StringComparison.Ordinal), forbidden);
        var owner = Read(Owner);
        StringAssert.Contains(owner, "internal OwnedSessionCommandService(");
        Assert.IsFalse(owner.Contains("CreateAsync(", StringComparison.Ordinal));
        Assert.IsFalse(owner.Contains("public SessionRuntimeService", StringComparison.Ordinal));
        Assert.IsFalse(owner.Contains("CodeAltaHost", StringComparison.Ordinal));
        StringAssert.Contains(owner, "SessionExecutionPolicy.CaptureSession(");
        StringAssert.Contains(owner, "SessionExecutionPolicy.BuildOptions(");
        Before(owner, "if (!string.Equals(request.SessionId, request.SessionId.Trim(), StringComparison.Ordinal))", "_active.Add(request.SessionId, operation);");
        Before(owner, "if (session is null || !string.Equals(session.SessionId, operation.SessionId, StringComparison.OrdinalIgnoreCase)) return null;", "SessionExecutionPolicy.CaptureSession(");
    }

    [TestMethod]
    [Ignore("Code-shape preservation check; disabled in favor of behavioral coverage.")]
    public void Host_UsesExplicitBuiltInRootWithoutChangingDefaultProviders()
    {
        var host = Read(Host);
        RequireOnce(host, "Commands = new OwnedSessionCommandService(runtimeService, projectCatalog, catalogOptions, ownedCommandReceiptCapacity, reviewOwnedCommandPermissions);");
        RequireOnce(host, "public OwnedSessionCommandService Commands { get; }");
        RequireOnce(host, "        _disposeTask = CreateHostDisposal(\n            DisposeCommandsAndRuntimeAsync,\n            AgentHub.DisposeAsync,");
        RequireOnce(host, "                currentProject,\n                options.OwnedCommandReceiptCapacity,\n                options.ReviewOwnedCommandPermissions);");
        Before(host, "Commands = new OwnedSessionCommandService(runtimeService, projectCatalog, catalogOptions, ownedCommandReceiptCapacity, reviewOwnedCommandPermissions);", "_disposeTask = CreateHostDisposal(");
        Before(host, "ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.OwnedCommandReceiptCapacity);", "Directory.CreateDirectory(globalRoot);");
        Before(host, "new BuiltInCodeAltaSkillRootProvider(options.BuiltInSkillRoot);", "Directory.CreateDirectory(globalRoot);");
        // The current host joins both retained work owners before disposing the runtime, including on failure.
        StringAssert.Contains(host, "() => Commands.DisposeAsync().AsTask(),");
        StringAssert.Contains(host, "() => WorkspaceReads.DisposeAsync().AsTask(),");
        StringAssert.Contains(host, "RuntimeService.DisposeAsync).ConfigureAwait(false);");
        Before(host, "await commands.ConfigureAwait(false)", "var runtime = disposeRuntime();");
        Before(host, "await reads.ConfigureAwait(false)", "var runtime = disposeRuntime();");
        StringAssert.Contains(host, "if (failures.Count > 1) throw new AggregateException(failures);");
        var options = Read(Options);
        StringAssert.Contains(options, "public int OwnedCommandReceiptCapacity { get; init; } = 256;");
        RequireOnce(options, "public bool ReviewOwnedCommandPermissions { get; init; }");
        RequireOnce(options, "public IReadOnlyDictionary<string, string?>? PluginEnvironment { get; init; }");
        RequireOnce(host, "internal static PluginAdapterOperationOptions CreatePluginOperationOptions(");
        StringAssert.Contains(host, "Environment = options.PluginEnvironment is not null\n                ? new Dictionary<string, string?>(options.PluginEnvironment, StringComparer.OrdinalIgnoreCase)\n                : Environment.GetEnvironmentVariables()");
        RequireOnce(host, "var pluginOperationOptions = CreatePluginOperationOptions(options, catalogOptions, currentProject);");
        Before(host, "modelProviderRegistry = new ModelProviderRegistry();", "options.ConfigureModelProviders?.Invoke(modelProviderRegistry);");
        Before(host, "options.ConfigureModelProviders?.Invoke(modelProviderRegistry);", "agentHub = new AgentHub(");
        var builtin = Read(Builtin);
        StringAssert.Contains(builtin, "var rootPath = _rootPath ?? ResolveRootPath();");
        StringAssert.Contains(builtin, "SourceId = \"builtin:codealta\"");
        StringAssert.Contains(builtin, "Precedence = 4");
    }

    [TestMethod]
    [Ignore("Code-shape preservation check; disabled in favor of behavioral coverage.")]
    public void Runtime_SplitsWaitAndExecutionTokensWithLiveGuardedCalls()
    {
        var runtime = Read(Runtime);
        RequireOnce(runtime, "AdmitAsync(() => SendOwnedBodyAsync(session, options, sendOptions, cancellationToken, CancellationToken.None), coordinationCancellationToken)");
        StringAssert.Contains(runtime, ".WaitAsync(coordinationCancellationToken)");
        RequireOnce(runtime, "CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, candidate.Attachment.Cancellation.Token)");
        RequireOnce(runtime, "await RunCapturedAsync(sessionHandleId, sendOptions, execution.Token)");
        RequireOnce(runtime, "await PublishRunSubmittedIfStillInFlightAsync(session, runId, runStartedAt, coordinationCancellationToken, candidate)");
    }

    [TestMethod]
    public void Resolver_UsesDirectStoreAndExistingRecoveryHelpers()
    {
        var runtime = Read(Runtime);
        var start = runtime.IndexOf("    private async Task<SessionViewDescriptor?> ResolveOwnedSessionBodyAsync(", StringComparison.Ordinal);
        Assert.IsTrue(start >= 0);
        var end = runtime.IndexOf("\n    }", start, StringComparison.Ordinal);
        var resolver = runtime[start..end];
        Assert.IsFalse(resolver.Contains("ListSessionsAsync", StringComparison.Ordinal));
        StringAssert.Contains(resolver, "TryCreateRecoverableSession(metadata, projects)");
        StringAssert.Contains(resolver, "ApplyCachedSessionLocalState(session, metadata.ViewState)");
        StringAssert.Contains(resolver, "ApplyPersistedSessionLocalStateAsync(session, cancellationToken)");
    }

    [TestMethod]
    [Ignore("Code-shape preservation check; disabled in favor of behavioral coverage.")]
    public void Owner_RetainsPreparationSendAndAbortAndJoinsBeforeHostDisposal()
    {
        var owner = Read(Owner);
        Before(owner, "_active.Add(request.SessionId, operation);", "operation.Work = RunSendAsync(operation);");
        Before(owner, "operation.Work = RunSendAsync(operation);", "operation.Launch.TrySetResult();");
        StringAssert.Contains(owner, "await operation.Launch.Task.ConfigureAwait(false);");
        StringAssert.Contains(owner, "operation.Preparation = PrepareAsync(operation);");
        StringAssert.Contains(owner, "EnsureOwnedCoordinatorSessionAsync(session, options)");
        StringAssert.Contains(owner, "operation.PermissionExecution, operation.Execution.Token");
        Assert.IsFalse(owner.Contains("_runtime.SendAsync(", StringComparison.Ordinal));
        StringAssert.Contains(owner, "await _runtime.AbortAsync(operation.SessionId, CancellationToken.None)");
        StringAssert.Contains(owner, "operation.Control = RunControlAsync(operation);");
        StringAssert.Contains(owner, "operation.Cancellation = operation.Execution.CancelAsync();");
        Assert.IsFalse(owner.Contains("Task.Run(", StringComparison.Ordinal));
        Assert.IsFalse(owner.Contains("ContinueWith", StringComparison.Ordinal));
        Assert.IsFalse(owner.Contains("StreamEventsAsync", StringComparison.Ordinal));
        Before(owner, "_closed = true;", "_disposeTask = DisposeCoreAsync(");
        StringAssert.Contains(owner, "await operation.Work.ConfigureAwait(false);");
        StringAssert.Contains(owner, "await control.ConfigureAwait(false);");
    }

    [TestMethod]
    [Ignore("Code-shape preservation check; disabled in favor of behavioral coverage.")]
    public void OwnedPermission_UsesReceiptAndCapturedAttachmentWithoutChangingPreparationPolicy()
    {
        var owner = Read(Owner);
        StringAssert.Contains(owner, "operation.Receipt.OperationId, prepared.Session.SessionId, operation.Execution.Token");
        StringAssert.Contains(owner, "_runtime.Permissions.OwnedDefaultPermissionHandler");
        StringAssert.Contains(owner, "_runtime.Permissions.OwnedDefaultUserInputHandler");
        var permissions = Read("CodeAlta.Orchestration/Runtime/SessionPermissionService.cs");
        StringAssert.Contains(permissions, "static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny))");
        StringAssert.Contains(permissions, "static (_, _) => Task.FromCanceled<AgentUserInputResponse>(new CancellationToken(true))");
        Before(owner, "await _runtime.Permissions.InvalidateOwnedOperationAsync(operation.Receipt.OperationId)", "attached = await operation.Attachment.Task");
        Before(owner, "await _runtime.Permissions.CloseOwnedAdmissionAsync()", "await operation.Work.ConfigureAwait(false)");
        var runtime = Read(Runtime);
        StringAssert.Contains(runtime, "Permissions.BindOwnedExecutionAsync(permissionExecution, _runtimeInstanceId,");
        StringAssert.Contains(runtime, "candidate.Attachment, candidate.ProviderId)");
        StringAssert.Contains(runtime, "OnPermissionRequest = Permissions.CreateOwnedCommandHandler(permissionExecution)");
        StringAssert.Contains(runtime, "OnPermissionRequest = options.OnPermissionRequest,");
        StringAssert.Contains(runtime, "await Permissions.CloseOwnedExecutionAsync(permissionExecution)");
        StringAssert.Contains(runtime, "ReferenceEquals(candidate.OnPermissionRequest, Permissions.OwnedDefaultPermissionHandler)");
        StringAssert.Contains(runtime, "ReferenceEquals(candidate.OnUserInputRequest, Permissions.OwnedDefaultUserInputHandler)");
        var send = runtime[runtime.IndexOf("    private async Task<AgentRunId> SendOwnedBodyAsync(", StringComparison.Ordinal)..
            runtime.IndexOf("    internal Task<AgentRunId> SendOwnedCommandAsync(", StringComparison.Ordinal)];
        Before(send, "ownedDefaultsRejected = true;", "candidate.PendingAgentPromptId = null;");
        Before(send, "ownedDefaultsRejected = true;", "capturedEntry = candidate;");
        Before(send, "ownedDefaultsRejected = true;", "session.MarkStarted(");
        StringAssert.Contains(send, "catch (OperationCanceledException) when (!ownedDefaultsRejected)");
        StringAssert.Contains(send, "catch (Exception ex) when (!ownedDefaultsRejected && ex is not OperationCanceledException)");
        var preparation = runtime[runtime.IndexOf("    private async ValueTask<CoordinatorPreparation> EnsureCoordinatorSessionCoreAsync(", StringComparison.Ordinal)..
            runtime.IndexOf("    private async ValueTask<AgentSessionHandleId> CreateCoordinatorSessionAsync(", StringComparison.Ordinal)];
        Before(preparation, "!HasOwnedCommandDefaults(existing)", "session.AgentPromptId = prompt;");
        Before(preparation, "!HasOwnedCommandDefaults(existing)", "existing.PendingAgentPromptId = null;");
    }

    private static void RequireOnce(string source, string fragment)
        => Assert.AreEqual(1, source.Split(SourceTestText.Canonicalize(fragment), StringSplitOptions.None).Length - 1, fragment);

    private static void Before(string source, string first, string second)
    {
        RequireOnce(source, first);
        RequireOnce(source, second);
        Assert.IsTrue(source.IndexOf(first, StringComparison.Ordinal) < source.IndexOf(second, StringComparison.Ordinal));
    }

    private static string Read(string path, [CallerFilePath] string caller = "")
        => SourceTestText.DecodeSource(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(caller)!, "..", path)));
}
