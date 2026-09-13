using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Agent;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Tui.App;
using XenoAtom.Logging;
using XenoAtom.CommandLine;

namespace CodeAlta.Tests;

/// <summary>Injected release stages and direct inert instances only; never constructs Host, TUI, providers or a real activator.</summary>
[TestClass]
public sealed class PluginAgentEventShutdownTests
{
    [TestMethod]
    public async Task Barrier_PrecedesEveryRelease_ConcurrentCallsShareOriginal()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stages = new List<string>();
        ValueTask Stage(string name) { stages.Add(name); return ValueTask.CompletedTask; }
        var host = CodeAltaHost.CreateHostDisposal(() => Stage("runtime"), () => Stage("hub"), () => Stage("providers"),
            () => Stage("plugins"), () => Assert.Fail("Host does not own logging."), ownsPluginRuntime: true, ownsLogging: false);
        var owned = CodeAltaOwnedServices.CreateOwnedServicesDisposal(() => new ValueTask(host.Value), () => Stage("metadata"),
            () => stages.Add("logging"), ownsLogging: true);
        var owner = PluginEventDependencyBarrier.CreateDisposal(async () => { entered.SetResult(); await release.Task; },
            new Lazy<Task>(() => ShellFrontendHost.DisposeRemindersThenFrontendAsync(() => Stage("reminders"), async () =>
            {
                await Stage("frontend");
                await owned.Value;
            })));
        var original = owner.Value;
        var outcome = Observe(original);
        Exception? primary = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreSame(original, owner.Value);
            Assert.AreEqual(0, stages.Count);
        }
        catch (Exception ex) { primary = ex; }
        finally { release.TrySetResult(); }
        try { await original.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception cleanup) { throw new RetainedFailure(owner, primary, cleanup); }
        if (primary is not null) throw new RetainedFailure(owner, primary, null);
        Assert.IsNull(await outcome);
        CollectionAssert.AreEqual(new[] { "reminders", "frontend", "runtime", "hub", "providers", "plugins", "metadata", "logging" }, stages);
    }

    [TestMethod]
    public async Task BarrierFailure_PreservesOriginalAndPreventsAllDependencyRelease()
    {
        var failure = new OperationCanceledException("barrier-original");
        var releases = 0;
        var owner = PluginEventDependencyBarrier.CreateDisposal(() => Task.FromException(failure),
            new Lazy<Task>(() => { releases++; return Task.CompletedTask; }));
        Task? original = null;
        var operation = new PluginOwnedOperation(() =>
        {
            original = owner.Value;
            return original;
        });
        // Own the wrapper and outcome observer before entering the synthetic barrier.
        operation.Launch();
        Exception? error;
        try { error = await operation.Outcome.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception waitFailure)
        {
            // This is an observation deadline, not a cleanup or cancellation signal. Keep the same owner/original.
            throw new RetainedFailure(new object[] { owner, operation, failure }, waitFailure, null);
        }
        Assert.AreSame(failure, error);
        Assert.IsNotNull(original);
        Assert.AreSame(original, owner.Value);
        Assert.AreEqual(0, releases);
    }

    [TestMethod]
    public async Task ContributedAltaSelfClose_RejectsBeforeAlreadyMemoizedOwnerAccess()
    {
        // Main-assembly hooks own logging. Preserve their configuration; this logger may be enabled.
        // The exact contributed success route does not call the adapter's callback-failure log tail.
        var logger = LogManager.GetLogger(nameof(PluginAgentEventShutdownTests) + "." + Guid.NewGuid().ToString("N"));
        var services = new NoopPluginServices(logger);
        var manager = new PluginRuntimeManager();
        var descriptor = new PluginDescriptor { RuntimeKey = "inert-command", TypeName = nameof(CommandPlugin), AssemblyName = "inert" };
        var lifetime = new PluginActivationLifetime(default);
        var tasks = new PluginRuntimeTaskService(lifetime.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var check = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releases = 0;
        var commandCalls = 0;
        var owner = PluginEventDependencyBarrier.Wrap(manager, new Lazy<Task>(() => { releases++; return Task.CompletedTask; }));
        var contribution = new AltaPluginCommandContribution
        {
            Plugin = descriptor, Services = services, Scope = PluginScope.Global,
            Command = new PluginAltaCommandContribution
            {
                Path = "inert-close",
                CreateCommandNode = _ =>
                {
                    var command = new XenoAtom.CommandLine.Command("inert-close", "Inert ownership test only.");
                    command.Add(async (_, _) =>
                    {
                        await Task.Yield();
                        commandCalls++;
                        Assert.IsTrue(owner.IsValueCreated);
                        Assert.ThrowsExactly<InvalidOperationException>(() => PluginEventDependencyBarrier.EnterDispose(manager, owner));
                        Assert.ThrowsExactly<InvalidOperationException>(() => manager.DisposeAsync());
                        Assert.ThrowsExactly<InvalidOperationException>(() => manager.QuiesceAgentEventsAsync());
                        Assert.AreEqual(0, releases);
                        return AltaExitCodes.Success;
                    });
                    return command;
                },
            },
        };
        var dispatcher = new AltaCommandDispatcher(new AltaCommandRegistry([new PluginAltaCommandContributor()]), new CommandServices(contribution));
        AltaCommandResult? commandResult = null;
        var plugin = new CommandPlugin(async () =>
        {
            entered.TrySetResult();
            await check.Task;
            commandResult = await dispatcher.InvokeAsync(["inert-close"]);
        });
        var context = new PluginRuntimeContext
        {
            Plugin = descriptor, Services = services, Logger = logger, PackageDirectory = "",
            Host = new PluginHostInfo { ApplicationName = "inert", Version = "0", HostApiVersion = "0", UserDataDirectory = "" },
            LifetimeCancellationToken = lifetime.Token,
        };
        var active = new ActivePluginInstance(plugin, descriptor, null, null, context, [], manager.Registry, tasks, lifetime);
        manager.OwnActivation(active);
        var originals = new List<PluginOwnedOperation>();
        PluginOwnedOperation Keep(Func<Task> body)
        {
            var operation = new PluginOwnedOperation(body);
            originals.Add(operation);
            operation.Launch();
            return operation;
        }
        var delivery = Keep(async () =>
        {
            var diagnostics = await manager.Adapter.ObserveAgentEventAsync([active], new PluginAgentEventContext
            {
                Plugin = descriptor, Services = services,
                Event = new AgentSessionUpdateEvent(new("inert"), "session", DateTimeOffset.UnixEpoch, null, AgentSessionUpdateKind.DiffUpdated, null),
            });
            Assert.AreEqual(0, diagnostics.Count);
        });
        Exception? primary = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var close = owner.Value;
            _ = Keep(() => close);
            check.TrySetResult();
            await delivery.Work.WaitAsync(TimeSpan.FromSeconds(10));
            // Check the returned command result outside the plugin callback so an assertion failure
            // does not itself invoke the production callback-failure logging path.
            Assert.IsNotNull(commandResult);
            Assert.AreEqual(AltaExitCodes.Success, commandResult.ExitCode);
            await close.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, commandCalls);
            Assert.AreEqual(1, releases);
        }
        catch (Exception ex) { primary = ex; }
        finally { check.TrySetResult(); }
        _ = Keep(() => manager.DisposeAsync().AsTask());
        try { await Task.WhenAll(originals.Select(operation => operation.Outcome)).WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception cleanup) { throw new RetainedFailure(new object[] { manager, active, owner, originals }, primary, cleanup); }
        var failures = originals.Select(operation => operation.Outcome.Result).OfType<Exception>().ToArray();
        if (primary is not null || failures.Length != 0)
            throw new RetainedFailure(new object[] { manager, active, owner, originals }, primary, failures.Length == 0 ? null : new AggregateException(failures));
    }

    [TestMethod]
    public async Task NestedRollback_RetainsOuterDependenciesWhenHostWasNeverReturned()
    {
        var primary = new OperationCanceledException("creation-primary");
        var barrier = new InvalidOperationException("barrier-control");
        var innerDependencies = new object();
        var outerDependencies = new object();
        var failure = new PluginEventDependencyException(primary, barrier, innerDependencies);
        var operation = new PluginOwnedOperation(() => PluginEventDependencyBarrier.BeforeRollbackAsync(null, failure, outerDependencies));
        operation.Launch();
        Exception? caught;
        try { caught = await operation.Outcome.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception waitFailure)
        {
            // Preserve creation/control failure identities and both dependency sets while the original is uncertain.
            // Do not start another rollback or release anything after the wait expires.
            throw new RetainedFailure(new object[] { operation, failure, innerDependencies, outerDependencies }, waitFailure, null);
        }
        Assert.AreSame(failure, caught);
        Assert.AreSame(primary, failure.CreationFailure);
        Assert.AreSame(barrier, failure.BarrierFailure);
        Assert.AreSame(innerDependencies, failure.Dependencies);
        Assert.AreSame(outerDependencies, failure.OuterDependencies);
    }

    private static async Task<Exception?> Observe(Task task)
    { try { await task.ConfigureAwait(false); return null; } catch (Exception ex) { return ex; } }

    private sealed class RetainedFailure(object owner, Exception? primary, Exception? cleanup)
        : AggregateException("Shutdown fixture failure; original owner and dependencies retained.", new[] { primary, cleanup }.OfType<Exception>())
    {
        internal object Owner { get; } = owner;
        internal Exception? Primary { get; } = primary;
        internal Exception? Cleanup { get; } = cleanup;
    }

    private sealed class CommandPlugin(Func<Task> callback) : PluginBase
    {
        public override ValueTask OnAgentEventAsync(PluginAgentEventContext context, CancellationToken cancellationToken = default) => new(callback());
    }

    private sealed class CommandServices(AltaPluginCommandContribution contribution) : IServiceProvider, IAltaPluginCatalog
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IAltaPluginCatalog) ? this : null;
        public IReadOnlyList<AltaPluginSummary> ListPlugins() => [];
        public AltaPluginSummary? GetPlugin(string runtimeKey) => null;
        public IReadOnlyList<AltaCommandPolicy> ListCommandPolicies() => [];
        public IReadOnlyList<AltaPluginCommandContribution> ListCommandContributions() => [contribution];
    }
}
