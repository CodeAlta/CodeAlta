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
    public async Task ActivatorCollectsCanvasContributions_AndNamesThemByTheirIdentifier()
    {
        var registry = new PluginContributionRegistry();
        var activator = new PluginRuntimeActivator(registry);
        var discovered = new DiscoveredPluginType
        {
            Type = typeof(CanvasPlugin),
            Descriptor = PluginDescriptorFactory.FromType(typeof(CanvasPlugin)),
        };

        var result = await activator.ActivateAsync(discovered, null, null, new PluginActivationOptions { HostInfo = CreateHostInfo() });

        Assert.IsTrue(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var canvases = registry.GetSnapshot().Where(static item => item.Handle.Point == PluginPoint.Canvas).ToArray();
        // The order of the contributions decides: the lower comes first.
        CollectionAssert.AreEqual(new[] { "first", "second" }, canvases.Select(static item => item.Handle.NaturalName).ToArray());
        Assert.IsTrue(canvases.All(static item => item.Contribution is PluginCanvasContribution));
        await result.ActivePlugin!.DeactivateAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, registry.GetSnapshot().Count(static item => item.Handle.Point == PluginPoint.Canvas), "the canvases go with the plugin");
    }

    [TestMethod]
    public async Task PluginCanvasService_IsTheOneTheHostGivesForTheCallingPlugin()
    {
        var registry = new PluginContributionRegistry();
        var activator = new PluginRuntimeActivator(registry);
        var host = new CapturingCanvasService();
        var discovered = new DiscoveredPluginType
        {
            Type = typeof(CanvasPlugin),
            Descriptor = PluginDescriptorFactory.FromType(typeof(CanvasPlugin)),
        };

        var result = await activator.ActivateAsync(discovered, null, null, new PluginActivationOptions
        {
            HostInfo = CreateHostInfo(),
            Services = new TestPluginServices(new CapturingPluginAltaService(), host),
        });

        Assert.IsTrue(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var services = result.ActivePlugin!.RuntimeContext.Services;
        Assert.AreEqual(discovered.Descriptor.RuntimeKey, host.RequestedFor);
        Assert.IsTrue(services.Canvases.HasInteractiveUi);
        var opened = await services.Canvases.OpenAsync("first");
        Assert.AreEqual(PluginCanvasOpenStatus.Requested, opened.Status);
        Assert.AreEqual(discovered.Descriptor.RuntimeKey, host.Asked.Single().PluginRuntimeKey, "the service a plugin gets is its own");
        await result.ActivePlugin.DeactivateAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task PluginCanvasService_WithoutAWindowSaysNothingCanBeShown()
    {
        var activator = new PluginRuntimeActivator(new PluginContributionRegistry());
        var discovered = new DiscoveredPluginType
        {
            Type = typeof(EmptyPlugin),
            Descriptor = PluginDescriptorFactory.FromType(typeof(EmptyPlugin)),
        };

        var result = await activator.ActivateAsync(discovered, null, null, new PluginActivationOptions { HostInfo = CreateHostInfo(), Services = new TestPluginServices(new CapturingPluginAltaService()) });

        var canvases = result.ActivePlugin!.RuntimeContext.Services.Canvases;
        Assert.IsFalse(canvases.HasInteractiveUi);
        Assert.AreEqual(PluginCanvasOpenStatus.Unavailable, (await canvases.OpenAsync("any")).Status);
        Assert.AreEqual(0, canvases.GetOpen().Count);
        await result.ActivePlugin.DeactivateAsync(TimeSpan.FromSeconds(5));
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

    private sealed class CapturingCanvasService : IPluginCanvasRuntimeService
    {
        public string? RequestedFor { get; private set; }

        public List<(string PluginRuntimeKey, string CanvasId)> Asked { get; } = [];

        public bool HasInteractiveUi => true;

        public IPluginCanvasService ForPlugin(string pluginRuntimeKey)
        {
            RequestedFor = pluginRuntimeKey;
            return new ForOnePlugin(this, pluginRuntimeKey);
        }

        public ValueTask<PluginCanvasOpenResult> OpenAsync(string canvasId, PluginCanvasOpenOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The service of no plugin must not be used by a plugin.");

        public ValueTask<bool> CloseAsync(string instanceId, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

        public ValueTask InvalidateAsync(string canvasId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public IReadOnlyList<PluginCanvasInstanceInfo> GetOpen() => [];

        private sealed class ForOnePlugin(CapturingCanvasService owner, string key) : IPluginCanvasService
        {
            public bool HasInteractiveUi => true;

            public ValueTask<PluginCanvasOpenResult> OpenAsync(string canvasId, PluginCanvasOpenOptions? options = null, CancellationToken cancellationToken = default)
            {
                owner.Asked.Add((key, canvasId));
                return ValueTask.FromResult(new PluginCanvasOpenResult(PluginCanvasOpenStatus.Requested, "instance", null, true));
            }

            public ValueTask<bool> CloseAsync(string instanceId, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

            public ValueTask InvalidateAsync(string canvasId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

            public IReadOnlyList<PluginCanvasInstanceInfo> GetOpen() => [];
        }
    }

    private sealed class TestPluginServices(IPluginAltaService alta, IPluginCanvasService? canvases = null) : IPluginServices
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

        public IPluginCanvasService Canvases { get; } = canvases ?? NoopPluginCanvasService.Instance;
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

    public sealed class CanvasPlugin : PluginBase
    {
        public override IEnumerable<PluginCanvasContribution> GetCanvases()
        {
            yield return new PluginCanvasContribution
            {
                Id = "second", Title = "Second", Order = 2,
                Open = static (_, _) => ValueTask.FromResult(PluginCanvasView.Html("<p>two</p>")),
            };
            yield return new PluginCanvasContribution
            {
                Id = "first", Title = "First", Order = 1, Scope = PluginCanvasScope.Project,
                Open = static (_, _) => ValueTask.FromResult(PluginCanvasView.Html("<p>one</p>")),
            };
        }
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
