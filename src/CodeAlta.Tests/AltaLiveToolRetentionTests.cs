using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.Plugins;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.CommandLine;
using Command = XenoAtom.CommandLine.Command;

namespace CodeAlta.Tests;

public sealed partial class AltaLiveToolTests
{
    [TestMethod]
    [DataRow("send")]
    [DataRow("queue")]
    [DataRow("message")]
    [DataRow("request")]
    [DataRow("steer")]
    [DataRow("compact")]
    [DataRow("set_agent")]
    [DataRow("abort")]
    [DataRow("reminder")]
    [DataRow("job")]
    [DataRow("ask")]
    [DataRow("skill")]
    [DataRow("notes-set")]
    [DataRow("notes-clear")]
    public async Task Retention_RefusesLiveTargetBeforeResolution(string route)
    {
        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        var provider = new StatefulProviderRuntime(new ModelProviderId("retention-fixture"));
        await using var runtime = CreateRuntime(options, provider);
        var protection = ReadyLiveRetention();
        var query = new HeldRetentionQuery();
        query.Release.TrySetResult();
        var services = new AltaServiceCollection().Add(options).Add(runtime)
            .Add<ISessionRetentionAdmission>(protection).Add<IAltaSessionQueryService>(query);
        await using var reminders = new AltaReminderService(services);
        services.Add(reminders);
        var dispatcher = CreateDispatcher(services);
        using var reservation = protection.TryReserveRetirement("target");
        Assert.IsNotNull(reservation);

        var caller = route.StartsWith("notes-", StringComparison.Ordinal)
            ? new AltaCallerIdentity { Kind = "agent", SourceSessionId = "target" }
            : AltaCallerIdentity.Cli;
        var result = await dispatcher.InvokeAsync(RetentionArgs(route), caller: caller);

        Assert.AreEqual(AltaExitCodes.Failure, result.ExitCode, result.Transcript);
        StringAssert.Contains(result.Transcript, "retirement");
        Assert.IsFalse(query.Started.Task.IsCompleted, "Refusal must precede session resolution.");
        Assert.IsEmpty(provider.CreatedOptions);
        Assert.IsEmpty(provider.ResumedOptions);
        Assert.IsEmpty(provider.SentOptions);
        Assert.IsEmpty(reminders.List(null, true));
    }

    [TestMethod]
    public async Task Retention_LivePreparationKeepsClaimAfterWaiterCancellation()
    {
        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        await using var runtime = CreateRuntime(options, new StatefulProviderRuntime(new ModelProviderId("retention-fixture")));
        var protection = ReadyLiveRetention();
        var query = new HeldRetentionQuery();
        var dispatcher = CreateDispatcher(new AltaServiceCollection().Add(options).Add(runtime)
            .Add<ISessionRetentionAdmission>(protection).Add<IAltaSessionQueryService>(query));
        var original = dispatcher.InvokeAsync(RetentionArgs("send"), caller: AltaCallerIdentity.Cli).AsTask();
        try
        {
            await query.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var cancellation = new CancellationTokenSource();
            var waiter = original.WaitAsync(cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => waiter);
            Assert.IsFalse(original.IsCompleted);
            Assert.IsNull(protection.TryReserveRetirement("target"));
            using var unrelated = protection.TryReserveRetirement("unrelated");
            Assert.IsNotNull(unrelated);
        }
        finally
        {
            query.Release.TrySetResult();
        }
        await original.WaitAsync(TimeSpan.FromSeconds(10));
        using var reservation = protection.TryReserveRetirement("target");
        Assert.IsNotNull(reservation);
    }

    [TestMethod]
    public async Task Retention_CreateRefusesUnknownTargetBeforeProjectOrProviderPreparation()
    {
        var protection = ReadyLiveRetention();
        using var reservation = protection.TryReserveRetirement("potential-parent");
        Assert.IsNotNull(reservation);
        var dispatcher = CreateDispatcher(new AltaServiceCollection().Add<ISessionRetentionAdmission>(protection));

        var result = await dispatcher.InvokeAsync(["session", "create", "--global"], caller: AltaCallerIdentity.Cli);

        Assert.AreEqual(AltaExitCodes.Failure, result.ExitCode, result.Transcript);
        StringAssert.Contains(result.Transcript, "retirement");
    }

    [TestMethod]
    [DataRow("send", false)]
    [DataRow("send", true)]
    [DataRow("create", true)]
    public async Task Retention_ProtectsProviderPreparationUntilOriginalReturns(string route, bool cancelCommand)
    {
        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        var provider = new StatefulProviderRuntime(new ModelProviderId("retention-provider"))
        {
            Models = [new AgentModelInfo("synthetic-model")],
            ModelsStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ModelsBlocker = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var registry = new ModelProviderRegistry();
        registry.RegisterOrReplaceSessionRuntime(new ModelProviderDescriptor(provider.ProviderId, "Synthetic"), () => provider);
        await using var runtime = CreateRuntime(options, registry);
        var session = await runtime.CreateGlobalSessionAsync(new SessionExecutionOptions
        {
            ProviderId = provider.ProviderId, ProviderKey = provider.ProviderId.Value, WorkingDirectory = root.Path, ProjectRoots = [],
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        }, "Synthetic retention target");
        var protection = ReadyLiveRetention();
        var dispatcher = CreateDispatcher(new AltaServiceCollection().Add(options).Add(runtime)
            .Add(new SessionViewCatalog(options)).Add(new ProjectCatalog(options))
            .Add<IReadOnlyList<ModelProviderDescriptor>>(registry.ListProviders())
            .Add<ISessionRetentionAdmission>(protection)
            .Add<IModelProviderInitializationService>(new ModelProviderInitializationService(registry)));
        using var commandCancellation = new CancellationTokenSource();
        string[] args = route == "create"
            ? ["session", "create", "--global", "--provider", provider.ProviderId.Value, "--parent", session.SessionId]
            : ["session", "send", session.SessionId, "--message", "synthetic"];
        var original = dispatcher.InvokeAsync(args, caller: AltaCallerIdentity.Cli,
            cancellationToken: commandCancellation.Token).AsTask();
        try
        {
            await provider.ModelsStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsNull(protection.TryReserveRetirement(session.SessionId));
            using var cancellation = new CancellationTokenSource();
            var waiter = original.WaitAsync(cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => waiter);
            if (cancelCommand)
            {
                commandCancellation.Cancel();
                await Assert.ThrowsAsync<TimeoutException>(() => original.WaitAsync(TimeSpan.FromMilliseconds(100)));
            }
            Assert.IsFalse(original.IsCompleted, "Cancellation must not detach the model refresh's original work.");
            Assert.IsNull(protection.TryReserveRetirement(session.SessionId));
            Assert.IsEmpty(provider.SentOptions);
        }
        finally
        {
            provider.ModelsBlocker.TrySetResult();
        }
        var result = await original.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(cancelCommand ? AltaExitCodes.TimeoutOrCancellation : AltaExitCodes.Success, result.ExitCode, result.Transcript);
        if (cancelCommand) Assert.IsEmpty(provider.SentOptions);
        using var reservation = protection.TryReserveRetirement(session.SessionId);
        Assert.IsNotNull(reservation, "The command's own preparation claim ends with the original command, not its observer.");
    }

    [TestMethod]
    public async Task Retention_PluginPreparationIsRefusedBeforeEffectsAndKeepsOriginalOnCancellation()
    {
        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        var provider = new StatefulProviderRuntime(new ModelProviderId("retention-plugin"));
        await using var runtime = CreateRuntime(options, provider);
        var session = await runtime.CreateGlobalSessionAsync(new SessionExecutionOptions
        {
            ProviderId = provider.ProviderId, ProviderKey = provider.ProviderId.Value, WorkingDirectory = root.Path,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        }, "Synthetic plugin target");
        var registry = new PluginContributionRegistry();
        var activation = await new PluginRuntimeActivator(registry).ActivateAsync(
            CreateDiscovered<HeldRetentionPlugin>(), sourcePackage: null, loadContext: null,
            new PluginActivationOptions { HostInfo = CreateHeadlessPluginHostInfo(root.Path) });
        Assert.IsTrue(activation.Succeeded);
        var active = activation.ActivePlugin!;
        var plugin = (HeldRetentionPlugin)active.Instance!;
        var protection = ReadyLiveRetention();
        var dispatcher = CreateDispatcher(new AltaServiceCollection().Add(options).Add(runtime)
            .Add(new SessionViewCatalog(options)).Add<ISessionRetentionAdmission>(protection)
            .Add(new PluginOrchestrationBridge(new PluginContributionAdapterService(registry), () => [active])));
        using var cancellation = new CancellationTokenSource();
        Task<AltaCommandResult>? original = null;
        try
        {
            using (var reservation = protection.TryReserveRetirement(session.SessionId))
            {
                Assert.IsNotNull(reservation);
                var refused = await dispatcher.InvokeAsync(["session", "send", session.SessionId, "--message", "synthetic"], caller: AltaCallerIdentity.Cli);
                Assert.AreEqual(AltaExitCodes.Failure, refused.ExitCode);
                Assert.IsFalse(plugin.Started.Task.IsCompleted);
            }
            original = dispatcher.InvokeAsync(["session", "send", session.SessionId, "--message", "synthetic"],
                caller: AltaCallerIdentity.Cli, cancellationToken: cancellation.Token).AsTask();
            await plugin.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAsync<TimeoutException>(() => original.WaitAsync(TimeSpan.FromMilliseconds(100)));
            Assert.IsNull(protection.TryReserveRetirement(session.SessionId));
            Assert.IsEmpty(provider.SentOptions);
        }
        finally
        {
            plugin.Release.TrySetResult();
            if (original is not null) await original.WaitAsync(TimeSpan.FromSeconds(10));
            await active.DeactivateAsync(TimeSpan.FromSeconds(5));
        }
        Assert.AreEqual(AltaExitCodes.TimeoutOrCancellation, (await original!).ExitCode);
        using var retired = protection.TryReserveRetirement(session.SessionId);
        Assert.IsNotNull(retired);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Retention_PluginCommandFactoryAndOriginalCallbackOwnUnknownTargets(bool mutating)
    {
        var protection = ReadyLiveRetention();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factories = 0;
        var catalog = new FakeAltaPluginCatalog(new AltaPluginCommandContribution
        {
            Plugin = CreatePluginDescriptor("retention-command"), Services = NoopPluginServices.Create(), Scope = PluginScope.Global,
            Command = new PluginAltaCommandContribution
            {
                Path = "retention-command",
                Policy = new PluginAltaCommandPolicy { IsMutating = mutating },
                CreateCommandNode = _ =>
                {
                    factories++;
                    Assert.IsNull(protection.TryReserveRetirement("any-target"), "Admission must precede the factory itself.");
                    var command = new Command("retention-command", "Synthetic callback");
                    command.Add(async (_, _) =>
                    {
                        started.TrySetResult();
                        await release.Task;
                        return AltaExitCodes.Success;
                    });
                    return command;
                },
            },
        });
        var services = new AltaServiceCollection()
            .Add<ISessionRetentionAdmission>(protection).Add<IAltaPluginCatalog>(catalog);
        var dispatcher = CreateDispatcher(services);
        using var output = new StringWriter();
        var unownedContext = new AltaCommandContext
        {
            Caller = AltaCallerIdentity.Cli, Services = services, Stdin = TextReader.Null,
            Stdout = output, Stderr = output, CorrelationId = "synthetic",
        };
        Assert.Throws<InvalidOperationException>(() => new AltaCommandRegistry().CreateCommandApp(unownedContext));
        Assert.AreEqual(0, factories, "A raw external command tree has no original invocation owner.");
        using (var retirement = protection.TryReserveRetirement("any-target"))
        {
            Assert.IsNotNull(retirement);
            var refused = await dispatcher.InvokeAsync(["retention-command"], caller: AltaCallerIdentity.Cli);
            Assert.AreEqual(AltaExitCodes.Failure, refused.ExitCode, refused.Transcript);
            Assert.AreEqual(0, factories);
        }

        using var cancellation = new CancellationTokenSource();
        var original = dispatcher.InvokeAsync(["retention-command"], caller: AltaCallerIdentity.Cli, cancellationToken: cancellation.Token).AsTask();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAsync<TimeoutException>(() => original.WaitAsync(TimeSpan.FromMilliseconds(100)));
            Assert.IsNull(protection.TryReserveRetirement("any-target"));
        }
        finally
        {
            release.TrySetResult();
        }
        await original.WaitAsync(TimeSpan.FromSeconds(10));
        StringAssert.Contains(AltaHelpText.RenderRootHelp(services), "retention-command");
        using var reservation = protection.TryReserveRetirement("any-target");
        Assert.IsNotNull(reservation);
    }

    public sealed class HeldRetentionPlugin : PluginBase
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<PluginBeforeAgentRunResult?> OnBeforeAgentRunAsync(
            PluginBeforeAgentRunContext context, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task;
            return null;
        }
    }

    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(true, false, false)]
    [DataRow(true, true, false)]
    [DataRow(true, true, true)]
    public async Task Retention_PluginCommandInspectsOriginalAggregateBeforeReleasing(bool retained, bool pluginMarker, bool nestedMarker)
    {
        var protection = ReadyLiveRetention();
        var original = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        original.SetException(RetentionFaults(retained, pluginMarker, nestedMarker));
        var catalog = new FakeAltaPluginCatalog(new AltaPluginCommandContribution
        {
            Plugin = CreatePluginDescriptor("aggregate-command"), Services = NoopPluginServices.Create(), Scope = PluginScope.Global,
            Command = new PluginAltaCommandContribution
            {
                Path = "aggregate-command", Policy = new PluginAltaCommandPolicy { IsMutating = false },
                CreateCommandNode = _ => new Command("aggregate-command") { Action = (_, _) => new ValueTask<int>(original.Task) },
            },
        });
        var dispatcher = CreateDispatcher(new AltaServiceCollection()
            .Add<ISessionRetentionAdmission>(protection).Add<IAltaPluginCatalog>(catalog));

        var result = await dispatcher.InvokeAsync(["aggregate-command"], caller: AltaCallerIdentity.Cli);

        Assert.AreNotEqual(AltaExitCodes.Success, result.ExitCode, result.Transcript);
        using var reservation = protection.TryReserveRetirement("any-target");
        Assert.AreEqual(retained, reservation is null, "A sibling dependency marker must retain the original callback's claim.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Retention_NotesOriginalAggregateKeepsTargetButNotUnrelatedSession(bool retained)
    {
        var protection = ReadyLiveRetention();
        var original = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        original.SetException(RetentionFaults(retained));
        var dispatcher = CreateDispatcher(new AltaServiceCollection().Add<ISessionRetentionAdmission>(protection)
            .Add<IAltaNotesService>(new FaultedRetentionNotes(original.Task)));

        var result = await dispatcher.InvokeAsync(["notes", "clear"], caller: new AltaCallerIdentity { Kind = "agent", SourceSessionId = "target" });

        Assert.AreNotEqual(AltaExitCodes.Success, result.ExitCode, result.Transcript);
        using var reservation = protection.TryReserveRetirement("target");
        Assert.AreEqual(retained, reservation is null);
        using var unrelated = protection.TryReserveRetirement("unrelated");
        Assert.IsNotNull(unrelated);
    }

    private static Exception[] RetentionFaults(bool retained, bool pluginMarker = false, bool nestedMarker = false)
    {
        Exception sibling = !retained ? new Exception("ordinary sibling")
            : pluginMarker
                ? new PluginEventDependencyException(new Exception("creation"), new Exception("barrier"), new object())
                : new AgentDependencyRetentionException("synthetic", "unconfirmed dependency", [new Exception("dependency")], new object());
        if (nestedMarker) sibling = new InvalidOperationException("wrapper", new AggregateException(new Exception("ordinary nested fault"), sibling));
        return [new InvalidOperationException("ordinary first fault"), sibling];
    }

    [TestMethod]
    public async Task Retention_ModelPreparationInspectsAlternateServiceOriginalAggregate()
    {
        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        var provider = new StatefulProviderRuntime(new ModelProviderId("retention-model-aggregate"));
        await using var runtime = CreateRuntime(options, provider);
        var protection = ReadyLiveRetention();
        var original = new TaskCompletionSource<IReadOnlyList<AgentModelInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        original.SetException(RetentionFaults(retained: true));
        var dispatcher = CreateDispatcher(new AltaServiceCollection().Add(options).Add(runtime)
            .Add(new ProjectCatalog(options)).Add<ISessionRetentionAdmission>(protection)
            .Add<IReadOnlyList<ModelProviderDescriptor>>([new ModelProviderDescriptor(provider.ProviderId, "Synthetic")])
            .Add<IModelProviderInitializationService>(new FaultedRetentionModels(original.Task)));

        var result = await dispatcher.InvokeAsync(["session", "create", "--global", "--provider", provider.ProviderId.Value], caller: AltaCallerIdentity.Cli);

        Assert.AreEqual(AltaExitCodes.Failure, result.ExitCode, result.Transcript);
        Assert.IsEmpty(provider.CreatedOptions);
        Assert.IsNull(protection.TryReserveRetirement("unknown-target"));
    }

    private sealed class FaultedRetentionModels(Task<IReadOnlyList<AgentModelInfo>> original) : IModelProviderInitializationService
    {
        public IReadOnlyList<ModelProviderStateSnapshot> CurrentStates => [];
        public IAsyncEnumerable<ModelProviderStateChanged> StreamStateChangesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task InitializeAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RefreshProviderAsync(ModelProviderId providerId, CancellationToken cancellationToken = default) => original;
        public Task<IReadOnlyList<AgentModelInfo>> GetModelsAsync(ModelProviderId providerId, CancellationToken cancellationToken = default) => original;
    }

    private sealed class FaultedRetentionNotes(Task original) : IAltaNotesService
    {
        public event EventHandler<AltaNotesChangedEventArgs>? Changed { add { } remove { } }
        public AltaCallerIdentity CaptureCaller(AltaCallerIdentity caller) => caller;
        public ValueTask<string> GetMarkdownAsync(AltaCallerIdentity caller, CancellationToken cancellationToken = default) => ValueTask.FromResult(string.Empty);
        public ValueTask SetMarkdownAsync(string markdown, AltaCallerIdentity caller, CancellationToken cancellationToken = default) => new(original);
        public ValueTask ClearAsync(AltaCallerIdentity caller, CancellationToken cancellationToken = default) => new(original);
    }

    private static string[] RetentionArgs(string route) => route switch
    {
        "reminder" => ["reminder", "create", "--session", " target ", "--duration", "60", "--content", "synthetic"],
        "job" => ["job", "start", "--session", " target ", "--command", "must-not-start"],
        "ask" => ["ask", "--session", " target ", "--stdin"],
        "skill" => ["skill", "activate", "synthetic", "--session", " target "],
        "notes-set" => ["notes", "set", "--stdin"],
        "notes-clear" => ["notes", "clear"],
        "compact" or "abort" => ["session", route, " target "],
        "set_agent" => ["session", route, " target ", "--prompt-id", "default"],
        _ => ["session", route, " target ", "--message", "synthetic"],
    };

    private static SessionRetentionProtection ReadyLiveRetention()
    {
        var protection = new SessionRetentionProtection();
        protection.SealParticipants(["fixture"]);
        Assert.IsTrue(protection.Publish("fixture", [], ready: true));
        return protection;
    }

    private sealed class HeldRetentionQuery : IAltaSessionQueryService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<AltaSessionInfo> LoadAsync(AltaCommandContext context)
        {
            Started.TrySetResult();
            await Release.Task; // Deliberately ignore caller cancellation: test original ownership.
            yield break;
        }
    }
}
