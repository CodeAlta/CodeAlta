using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.Tests;

[TestClass]
public sealed class PluginRuntimeLifecycleTests
{
    [TestMethod]
    public async Task DeactivateCancelsAndWaitsForTrackedPluginTasks()
    {
        var registry = new PluginContributionRegistry();
        var activator = new PluginRuntimeActivator(registry);
        TrackingPlugin.Reset();
        var discovered = new DiscoveredPluginType
        {
            Type = typeof(TrackingPlugin),
            Descriptor = PluginDescriptorFactory.FromType(typeof(TrackingPlugin)),
        };

        var result = await activator.ActivateAsync(discovered, null, null, new PluginActivationOptions { HostInfo = CreateHostInfo() });

        Assert.IsTrue(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.IsNotNull(result.ActivePlugin);
        await TrackingPlugin.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(result.ActivePlugin.RuntimeContext.Services.Tasks.HasRunningTasks);

        var diagnostics = await result.ActivePlugin.DeactivateAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(0, diagnostics.Count, string.Join(Environment.NewLine, diagnostics.Select(static diagnostic => diagnostic.Message)));
        await TrackingPlugin.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(result.ActivePlugin.RuntimeContext.IsValid);
        Assert.IsFalse(result.ActivePlugin.RuntimeContext.Services.Tasks.HasRunningTasks);
        Assert.IsNull(result.ActivePlugin.Instance);
    }

    [TestMethod]
    public async Task ActivationFailureRollsBackContributionsAndReportsDiagnostic()
    {
        var registry = new PluginContributionRegistry();
        var activator = new PluginRuntimeActivator(registry);
        var discovered = new DiscoveredPluginType
        {
            Type = typeof(FailingContributionPlugin),
            Descriptor = PluginDescriptorFactory.FromType(typeof(FailingContributionPlugin)),
        };

        var result = await activator.ActivateAsync(discovered, null, null, new PluginActivationOptions { HostInfo = CreateHostInfo() });

        Assert.IsFalse(result.Succeeded);
        Assert.IsNull(result.ActivePlugin);
        Assert.AreEqual(0, registry.GetSnapshot().Count);
        Assert.IsTrue(result.Diagnostics.Any(diagnostic => diagnostic.Source == PluginRuntimeDiagnosticSource.Activation));
    }

    [TestMethod]
    public async Task InitializationFailureReportsDiagnostic()
    {
        var activator = new PluginRuntimeActivator(new PluginContributionRegistry());
        var discovered = new DiscoveredPluginType
        {
            Type = typeof(FailingInitializePlugin),
            Descriptor = PluginDescriptorFactory.FromType(typeof(FailingInitializePlugin)),
        };

        var result = await activator.ActivateAsync(discovered, null, null, new PluginActivationOptions { HostInfo = CreateHostInfo() });

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.Diagnostics.Any(diagnostic => diagnostic.Message.Contains("Plugin activation failed", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ActivatorCollectsSessionEventProjectionContributions()
    {
        var registry = new PluginContributionRegistry();
        var activator = new PluginRuntimeActivator(registry);
        var discovered = new DiscoveredPluginType
        {
            Type = typeof(SessionEventProjectionPlugin),
            Descriptor = PluginDescriptorFactory.FromType(typeof(SessionEventProjectionPlugin)),
        };

        var result = await activator.ActivateAsync(discovered, null, null, new PluginActivationOptions { HostInfo = CreateHostInfo() });

        Assert.IsTrue(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var registration = registry.GetSnapshot().Single(static item => item.Handle.Point == PluginPoint.SessionEventProjection);
        Assert.IsInstanceOfType<PluginSessionEventProjectionContribution>(registration.Contribution);
        await result.ActivePlugin!.DeactivateAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task PluginAltaService_UsesRuntimeOwnedPluginKeyAndProjectScope()
    {
        var registry = new PluginContributionRegistry();
        var activator = new PluginRuntimeActivator(registry);
        var alta = new CapturingPluginAltaService();
        AltaInvokingPlugin.Reset();
        var discovered = new DiscoveredPluginType
        {
            Type = typeof(AltaInvokingPlugin),
            Descriptor = PluginDescriptorFactory.FromType(typeof(AltaInvokingPlugin)),
        };
        var sourcePackage = new SourcePluginPackage
        {
            PackageId = "alta-invoker-package",
            Root = new PluginRoot
            {
                RootPath = Path.Combine(Path.GetTempPath(), "project", ".alta", "plugins"),
                Scope = PluginScope.Project,
                ProjectId = "project-real",
                ProjectPath = Path.Combine(Path.GetTempPath(), "project"),
            },
            PackageDirectory = Path.Combine(Path.GetTempPath(), "project", ".alta", "plugins", "alta-invoker-package"),
            EntryFilePath = "Plugin.cs",
        };

        var result = await activator.ActivateAsync(
            discovered,
            sourcePackage,
            null,
            new PluginActivationOptions
            {
                HostInfo = CreateHostInfo(),
                Services = new TestPluginServices(alta),
            });

        Assert.IsTrue(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var commandResult = await AltaInvokingPlugin.Result.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, commandResult.ExitCode);
        Assert.IsFalse(alta.UntrustedInvokeCalled);
        Assert.AreEqual(discovered.Descriptor.RuntimeKey, alta.PluginRuntimeKey);
        CollectionAssert.AreEqual(new[] { "version" }, alta.Args.ToArray());
        Assert.AreEqual("project-real", alta.Options?.SourceProjectId);
        await result.ActivePlugin!.DeactivateAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task DeactivateReportsFailedUnloadDiagnosticsWhenLoadContextIsStillReferenced()
    {
        var registry = new PluginContributionRegistry();
        var activator = new PluginRuntimeActivator(registry);
        var discovered = new DiscoveredPluginType
        {
            Type = typeof(EmptyPlugin),
            Descriptor = PluginDescriptorFactory.FromType(typeof(EmptyPlugin)),
        };
        var heldLoadContext = new PluginAssemblyLoadContext(typeof(PluginRuntimeLifecycleTests).Assembly.Location);
        var result = await activator.ActivateAsync(discovered, null, heldLoadContext, new PluginActivationOptions { HostInfo = CreateHostInfo() });
        Assert.IsNotNull(result.ActivePlugin);

        var diagnostics = await result.ActivePlugin.DeactivateAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(PluginRuntimeState.Failed, result.ActivePlugin.State);
        Assert.IsTrue(diagnostics.Any(diagnostic => diagnostic.Source == PluginRuntimeDiagnosticSource.Unload));
        GC.KeepAlive(heldLoadContext);
    }

    private static PluginHostInfo CreateHostInfo()
        => new()
        {
            ApplicationName = "CodeAlta.Tests",
            Version = "1.0.0",
            HostApiVersion = "1.0.0",
            UserDataDirectory = Path.GetTempPath(),
            IsHeadless = true,
        };

    public sealed class TrackingPlugin : PluginBase
    {
        public static TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static TaskCompletionSource Cancelled { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static void Reset()
        {
            Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public override ValueTask InitializeAsync(CancellationToken cancellationToken = default)
        {
            Tasks.Run("wait", async token =>
            {
                Started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Cancelled.TrySetResult();
                }
            });
            return ValueTask.CompletedTask;
        }
    }

    [Plugin("alta-invoker")]
    public sealed class AltaInvokingPlugin : PluginBase
    {
        public static TaskCompletionSource<PluginAltaCommandResult> Result { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static void Reset()
            => Result = new TaskCompletionSource<PluginAltaCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
        {
            var result = await Services.Alta.InvokeAsync(
                ["version"],
                options: new PluginAltaInvocationOptions { SourceProjectId = "forged-project" },
                cancellationToken: cancellationToken);
            Result.TrySetResult(result);
        }
    }

    private sealed class CapturingPluginAltaService : IPluginAltaRuntimeService
    {
        public bool UntrustedInvokeCalled { get; private set; }

        public string? PluginRuntimeKey { get; private set; }

        public IReadOnlyList<string> Args { get; private set; } = [];

        public PluginAltaInvocationOptions? Options { get; private set; }

        public ValueTask<PluginAltaCommandResult> InvokeAsync(
            IReadOnlyList<string> args,
            string? stdin = null,
            PluginAltaInvocationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            UntrustedInvokeCalled = true;
            return InvokeAsync(string.Empty, args, stdin, options, cancellationToken);
        }

        public ValueTask<PluginAltaCommandResult> InvokeAsync(
            string pluginRuntimeKey,
            IReadOnlyList<string> args,
            string? stdin = null,
            PluginAltaInvocationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            PluginRuntimeKey = pluginRuntimeKey;
            Args = args.ToArray();
            Options = options;
            return ValueTask.FromResult(new PluginAltaCommandResult
            {
                ExitCode = 0,
                TranscriptJsonl = "{\"type\":\"alta.result\",\"version\":1,\"exitCode\":0}\n",
            });
        }
    }

    private sealed class TestPluginServices(IPluginAltaService alta) : IPluginServices
    {
        private readonly NoopPluginServices _inner = NoopPluginServices.Create();

        public XenoAtom.Logging.Logger Logger => _inner.Logger;

        public IPluginUiService Ui => _inner.Ui;

        public IPluginStateStore State => _inner.State;

        public IPluginDatabase Database => _inner.Database;

        public IPluginWorkspaceService Workspace => _inner.Workspace;

        public IPluginSessionService Sessions => _inner.Sessions;

        public IPluginPromptService Prompts => _inner.Prompts;

        public IPluginAgentService Agents => _inner.Agents;

        public IPluginTaskService Tasks => _inner.Tasks;

        public IPluginAltaService Alta { get; } = alta;
    }

    public sealed class FailingContributionPlugin : PluginBase
    {
        public override IEnumerable<PluginCommandContribution> GetCommands()
        {
            yield return new PluginCommandContribution { Name = "before-failure", Handler = static (_, _) => ValueTask.FromResult(PluginCommandResult.Handled) };
            throw new InvalidOperationException("Contribution failure.");
        }
    }

    public sealed class FailingInitializePlugin : PluginBase
    {
        public override ValueTask InitializeAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Initialization failure.");
    }

    public sealed class EmptyPlugin : PluginBase
    {
    }

    public sealed class SessionEventProjectionPlugin : PluginBase
    {
        public override IEnumerable<PluginSessionEventProjectionContribution> GetSessionEventProjections()
        {
            yield return new PluginSessionEventProjectionContribution
            {
                Name = "stats",
                ProjectAsync = static (_, _) => ValueTask.FromResult<IReadOnlyList<PluginDerivedSessionEvent>>([]),
            };
        }
    }
}
