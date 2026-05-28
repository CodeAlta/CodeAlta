using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Catalog.Skills;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.Tests;

[TestClass]
public sealed class PluginContributionAdapterServiceTests
{
    [TestMethod]
    public async Task ExecuteCommandAsync_InvokesMatchingContributionWithOperationContext()
    {
        var (registry, active) = await ActivateAsync<ComprehensivePlugin>();
        var adapter = new PluginContributionAdapterService(registry);

        var (result, diagnostics) = await adapter.ExecuteCommandAsync(
            [active],
            "sample_alias",
            ["one", "two"],
            "/sample one two",
            new PluginAdapterOperationOptions { ProjectId = "project", ProjectPath = Environment.CurrentDirectory, SessionId = "session" });

        Assert.AreEqual(0, diagnostics.Count, string.Join(Environment.NewLine, diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.AreEqual(PluginCommandDisposition.Handled, result.Disposition);
        Assert.AreEqual("command:one,two", result.UserMessage);
        Assert.AreEqual("project", ComprehensivePlugin.LastCommandProjectId);
        Assert.AreEqual("/sample one two", ComprehensivePlugin.LastCommandRawText);
        await active.DeactivateAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task PromptAndSystemPromptAdapters_MaterializeContributionsAndCallbacksInOrder()
    {
        var (registry, active) = await ActivateAsync<ComprehensivePlugin>();
        var adapter = new PluginContributionAdapterService(registry);

        var prompt = await adapter.ProcessPromptSubmittingAsync([active], "hello", options: new PluginAdapterOperationOptions { ProviderId = "local", IsCodeAltaManagedProvider = true });
        var (parts, diagnostics) = await adapter.BuildSystemPromptPartsAsync([active], PluginPromptChannel.System, supportsDirectInjection: true);

        Assert.AreEqual(0, prompt.Diagnostics.Count, string.Join(Environment.NewLine, prompt.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.AreEqual(PluginPromptDisposition.Replace, prompt.Result.Disposition);
        Assert.AreEqual("hello processed callback", prompt.Result.ReplacementText);
        Assert.AreEqual(0, diagnostics.Count, string.Join(Environment.NewLine, diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.AreEqual(1, parts.Count);
        Assert.AreEqual("system:True", parts[0].Content);
        await active.DeactivateAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task RuntimeCallbackAdapters_InvokeBeforeRunToolsResultsAndEvents()
    {
        var (registry, active) = await ActivateAsync<ComprehensivePlugin>();
        var adapter = new PluginContributionAdapterService(registry);
        var invocation = new AgentToolInvocation(new ModelProviderId("local"), "session", "call", "sample", JsonSerializer.SerializeToElement(new { value = 1 }));
        var toolResult = new AgentToolResult(true, [new AgentToolResultItem.Text("original")]);

        var before = await adapter.BeforeAgentRunAsync([active], CreateBeforeRunTemplate(active, "prompt"));
        var (call, callDiagnostics) = await adapter.OnToolCallAsync([active], CreateToolCallTemplate(active, invocation));
        var (result, resultDiagnostics) = await adapter.OnToolResultAsync([active], CreateToolResultTemplate(active, invocation, toolResult));
        var eventDiagnostics = await adapter.ObserveAgentEventAsync([active], CreateAgentEventTemplate(active));

        Assert.AreEqual(0, before.Diagnostics.Count, string.Join(Environment.NewLine, before.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.AreEqual("before", before.Result.AdditionalMessages.Single().Content);
        Assert.AreEqual("sample_tool", before.Result.PreferredToolNames.Single());
        Assert.AreEqual("before_tool", before.Result.AdditionalTools.Single().Spec.Name);
        Assert.AreEqual(0, callDiagnostics.Count, string.Join(Environment.NewLine, callDiagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.AreEqual(PluginToolCallDisposition.Block, call!.Disposition);
        Assert.AreEqual("blocked", call.BlockReason);
        Assert.AreEqual(0, resultDiagnostics.Count, string.Join(Environment.NewLine, resultDiagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.AreEqual(PluginToolResultDisposition.Replace, result!.Disposition);
        Assert.AreEqual("replacement", ((AgentToolResultItem.Text)result.ReplacementResult!.Items.Single()).Value);
        Assert.AreEqual(0, eventDiagnostics.Count, string.Join(Environment.NewLine, eventDiagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.AreEqual(1, ComprehensivePlugin.AgentEventsObserved);
        await active.DeactivateAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task ContributionAdapters_ExposeToolsResourcesUiRenderersCompactionAndStartup()
    {
        var (registry, active) = await ActivateAsync<ComprehensivePlugin>();
        var adapter = new PluginContributionAdapterService(registry);
        var managedOptions = new PluginAdapterOperationOptions { IsCodeAltaManagedProvider = true, HasInteractiveUi = true, ConfigurationPaths = ["config.toml"], Environment = new Dictionary<string, string?> { ["A"] = "B" } };

        var startup = await adapter.RunStartupAsync([active], ["--test"], managedOptions);
        var tools = adapter.GetAgentTools(managedOptions);
        var resources = adapter.GetResources([active]);
        var status = adapter.GetStatusItems([active], managedOptions);
        var (renderResults, renderDiagnostics) = await adapter.RenderAsync([active], PluginUiRegion.SessionFooter, "sample", new { value = 1 }, managedOptions);
        var compaction = await adapter.RunCompactionAsync(
            before: CreateBeforeCompactionTemplate(active),
            instructions: CreateInstructionTemplate(active),
            reducer: CreateReducerTemplate(active),
            after: CreateAfterCompactionTemplate(active));

        Assert.AreEqual(0, startup.Diagnostics.Count, string.Join(Environment.NewLine, startup.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.IsTrue(ComprehensivePlugin.StartupInvoked);
        Assert.AreEqual(1, startup.Resources.Count);
        Assert.AreEqual(2, tools.Count);
        Assert.AreEqual(1, adapter.GetAgentTools(new PluginAdapterOperationOptions { IsCodeAltaManagedProvider = false }).Count);
        Assert.AreEqual(1, resources.Count);
        Assert.AreEqual(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "skills")), resources[0].Path);
        var pluginSkillRoots = await new PluginSkillRootProvider(() => resources).GetRootsAsync(new SkillDiscoveryContext());
        Assert.AreEqual(SkillSourceKind.Plugin, pluginSkillRoots.Single().SourceKind);
        Assert.AreEqual(resources[0].Path, pluginSkillRoots.Single().RootPath);
        Assert.AreEqual("sample", status.Single().Label);
        Assert.AreEqual(0, renderDiagnostics.Count, string.Join(Environment.NewLine, renderDiagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.AreEqual("rendered", renderResults.Single().Markdown);
        Assert.AreEqual(0, compaction.Diagnostics.Count, string.Join(Environment.NewLine, compaction.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.IsFalse(compaction.BeforeResults.Single().Cancel);
        Assert.AreEqual("compact instructions", compaction.InstructionResults.Single().Instructions);
        Assert.AreEqual("reduced", compaction.ReducerResults.Single().CompactedText);
        Assert.IsTrue(ComprehensivePlugin.AfterCompactionInvoked);
        await active.DeactivateAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task UiOnlyContributions_AreIgnoredInHeadlessMode()
    {
        var (registry, active) = await ActivateAsync<ComprehensivePlugin>();
        var adapter = new PluginContributionAdapterService(registry);
        var headlessOptions = new PluginAdapterOperationOptions { IsHeadless = true, HasInteractiveUi = false };

        var status = adapter.GetStatusItems([active], headlessOptions);
        var visuals = adapter.CreateVisuals([active], PluginUiRegion.CommandBar, headlessOptions);
        var (renderResults, renderDiagnostics) = await adapter.RenderAsync([active], PluginUiRegion.SessionFooter, "sample", new { value = 1 }, headlessOptions);

        Assert.AreEqual(0, status.Count);
        Assert.AreEqual(0, visuals.Count);
        Assert.AreEqual(0, renderResults.Count);
        Assert.AreEqual(0, renderDiagnostics.Count);
        await active.DeactivateAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task ContributionFailures_AreStoredAsRuntimeDiagnosticsAndDoNotThrow()
    {
        var registry = new PluginContributionRegistry();
        var diagnostics = new PluginRuntimeDiagnosticStore();
        var activator = new PluginRuntimeActivator(registry);
        var result = await activator.ActivateAsync(CreateDiscovered<FailingCommandPlugin>(), null, null, new PluginActivationOptions { HostInfo = CreateHostInfo() });
        Assert.IsTrue(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.IsNotNull(result.ActivePlugin);
        var adapter = new PluginContributionAdapterService(registry, diagnostics);

        var (commandResult, commandDiagnostics) = await adapter.ExecuteCommandAsync([result.ActivePlugin], "fail");

        Assert.AreEqual(PluginCommandDisposition.NotHandled, commandResult.Disposition);
        Assert.AreEqual(1, commandDiagnostics.Count);
        Assert.AreEqual(1, diagnostics.GetSnapshot().Count);
        Assert.AreEqual(PluginRuntimeDiagnosticSource.Callback, commandDiagnostics[0].Source);
        await result.ActivePlugin.DeactivateAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task AgentEventObservers_RunInPluginOrderAndContinueAfterDiagnostics()
    {
        var recorder = new AgentEventOrderRecorder();
        var services = new AgentEventOrderPluginServices(recorder);
        FailingAgentEventPlugin.Reset();
        var registry = new PluginContributionRegistry();
        var activator = new PluginRuntimeActivator(registry);
        var first = await activator.ActivateAsync(CreateDiscovered<AgentEventOrderPlugin>(), null, null, new PluginActivationOptions { HostInfo = CreateHostInfo(), Services = services, ActivationGeneration = 1 });
        var failing = await activator.ActivateAsync(CreateDiscovered<FailingAgentEventPlugin>(), null, null, new PluginActivationOptions { HostInfo = CreateHostInfo(), Services = services, ActivationGeneration = 2 });
        var last = await activator.ActivateAsync(CreateDiscovered<AgentEventOrderPlugin>(), null, null, new PluginActivationOptions { HostInfo = CreateHostInfo(), Services = services, ActivationGeneration = 3 });
        Assert.IsNotNull(first.ActivePlugin);
        Assert.IsNotNull(failing.ActivePlugin);
        Assert.IsNotNull(last.ActivePlugin);
        var adapter = new PluginContributionAdapterService(registry);

        var diagnostics = await adapter.ObserveAgentEventAsync(
            [first.ActivePlugin, failing.ActivePlugin, last.ActivePlugin],
            CreateAgentEventTemplate(first.ActivePlugin));

        CollectionAssert.AreEqual(new[] { first.ActivePlugin.Descriptor.RuntimeKey, last.ActivePlugin.Descriptor.RuntimeKey }, recorder.Calls.ToArray());
        Assert.AreEqual(1, diagnostics.Count);
        StringAssert.Contains(diagnostics[0].Message, "Agent-event callback failed.");
        await first.ActivePlugin.DeactivateAsync(TimeSpan.FromSeconds(5));
        await failing.ActivePlugin.DeactivateAsync(TimeSpan.FromSeconds(5));
        await last.ActivePlugin.DeactivateAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task AgentEventObservers_PropagateCancellation()
    {
        var (registry, active) = await ActivateAsync<CancellingAgentEventPlugin>();
        var adapter = new PluginContributionAdapterService(registry);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var exception = await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await adapter.ObserveAgentEventAsync([active], CreateAgentEventTemplate(active), cancellationToken: cancellation.Token));

        Assert.IsTrue(exception.CancellationToken.IsCancellationRequested);
        await active.DeactivateAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task DeactivationRemovesPluginOwnedContributionsFromAdapters()
    {
        var (registry, active) = await ActivateAsync<ComprehensivePlugin>();
        var adapter = new PluginContributionAdapterService(registry);
        Assert.AreNotEqual(0, registry.GetSnapshot().Count);

        await active.DeactivateAsync(TimeSpan.FromSeconds(5));
        var (result, diagnostics) = await adapter.ExecuteCommandAsync([active], "sample");

        Assert.AreEqual(0, diagnostics.Count, string.Join(Environment.NewLine, diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.AreEqual(0, registry.GetSnapshot().Count);
        Assert.AreEqual(0, adapter.GetAgentTools(new PluginAdapterOperationOptions { IsCodeAltaManagedProvider = true }).Count);
        Assert.AreEqual(0, adapter.GetResources([active]).Count);
        Assert.AreEqual(PluginCommandDisposition.NotHandled, result.Disposition);
        Assert.IsFalse(active.RuntimeContext.IsValid);
    }

    private static async Task<(PluginContributionRegistry Registry, ActivePluginInstance Active)> ActivateAsync<TPlugin>()
        where TPlugin : PluginBase
    {
        ComprehensivePlugin.Reset();
        var registry = new PluginContributionRegistry();
        var activator = new PluginRuntimeActivator(registry);
        var result = await activator.ActivateAsync(CreateDiscovered<TPlugin>(), null, null, new PluginActivationOptions { HostInfo = CreateHostInfo() });
        Assert.IsTrue(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.IsNotNull(result.ActivePlugin);
        return (registry, result.ActivePlugin);
    }

    private static DiscoveredPluginType CreateDiscovered<TPlugin>()
        where TPlugin : PluginBase
        => new()
        {
            Type = typeof(TPlugin),
            Descriptor = PluginDescriptorFactory.FromType(typeof(TPlugin)),
        };

    private static PluginHostInfo CreateHostInfo()
        => new()
        {
            ApplicationName = "CodeAlta.Tests",
            Version = "1.0.0",
            HostApiVersion = "1.0.0",
            UserDataDirectory = Path.GetTempPath(),
            IsHeadless = true,
        };

    private static PluginBeforeAgentRunContext CreateBeforeRunTemplate(ActivePluginInstance active, string prompt)
        => new()
        {
            Plugin = active.Descriptor,
            Services = active.RuntimeContext.Services,
            PromptText = prompt,
            ActiveToolNames = ["existing"],
        };

    private static PluginToolCallContext CreateToolCallTemplate(ActivePluginInstance active, AgentToolInvocation invocation)
        => new()
        {
            Plugin = active.Descriptor,
            Services = active.RuntimeContext.Services,
            Invocation = invocation,
        };

    private static PluginToolResultContext CreateToolResultTemplate(ActivePluginInstance active, AgentToolInvocation invocation, AgentToolResult result)
        => new()
        {
            Plugin = active.Descriptor,
            Services = active.RuntimeContext.Services,
            Invocation = invocation,
            Result = result,
        };

    private static PluginAgentEventContext CreateAgentEventTemplate(ActivePluginInstance active)
        => new()
        {
            Plugin = active.Descriptor,
            Services = active.RuntimeContext.Services,
            Event = new AgentActivityEvent(new ModelProviderId("local"), "session", DateTimeOffset.UtcNow, null, AgentActivityKind.Turn, AgentActivityPhase.Started, "activity", null, "name", "message"),
        };

    private static PluginBeforeCompactionContext CreateBeforeCompactionTemplate(ActivePluginInstance active)
        => new()
        {
            Plugin = active.Descriptor,
            Services = active.RuntimeContext.Services,
            PlanSummary = "plan",
        };

    private static PluginCompactionInstructionContext CreateInstructionTemplate(ActivePluginInstance active)
        => new()
        {
            Plugin = active.Descriptor,
            Services = active.RuntimeContext.Services,
            PreferredMaximumCharacters = 100,
        };

    private static PluginCompactionReducerContext CreateReducerTemplate(ActivePluginInstance active)
        => new()
        {
            Plugin = active.Descriptor,
            Services = active.RuntimeContext.Services,
            Payload = "payload",
            PayloadKind = "sample",
        };

    private static PluginAfterCompactionContext CreateAfterCompactionTemplate(ActivePluginInstance active)
        => new()
        {
            Plugin = active.Descriptor,
            Services = active.RuntimeContext.Services,
            Succeeded = true,
            Summary = "summary",
        };

    public sealed class ComprehensivePlugin : PluginBase
    {
        public static string? LastCommandProjectId { get; private set; }

        public static string? LastCommandRawText { get; private set; }

        public static bool StartupInvoked { get; private set; }

        public static bool AfterCompactionInvoked { get; private set; }

        public static int AgentEventsObserved { get; private set; }

        public static void Reset()
        {
            LastCommandProjectId = null;
            LastCommandRawText = null;
            StartupInvoked = false;
            AfterCompactionInvoked = false;
            AgentEventsObserved = 0;
        }

        public override IEnumerable<PluginStartupContribution> GetStartupContributions()
        {
            yield return new PluginStartupContribution
            {
                Name = "startup",
                Resources = [new PluginResourceContribution { Kind = PluginResourceKind.TemplateRoot, Path = "templates" }],
                Handler = static (context, _) =>
                {
                    StartupInvoked = context.RawArguments.Single() == "--test" && context.ConfigurationPaths.Single() == "config.toml";
                    return ValueTask.CompletedTask;
                },
            };
        }

        public override IEnumerable<PluginCommandContribution> GetCommands()
        {
            yield return new PluginCommandContribution
            {
                Name = "sample",
                Aliases = ["sample_alias"],
                Handler = static (context, _) =>
                {
                    LastCommandProjectId = context.ProjectId;
                    LastCommandRawText = context.RawText;
                    return ValueTask.FromResult(PluginCommandResult.Message($"command:{string.Join(',', context.Arguments)}"));
                },
            };
        }

        public override IEnumerable<PluginAgentToolContribution> GetAgentTools()
        {
            yield return new PluginAgentToolContribution { Definition = CreateTool("always"), ActivationPolicy = PluginToolActivationPolicy.Default };
            yield return new PluginAgentToolContribution { Definition = CreateTool("managed"), ActivationPolicy = PluginToolActivationPolicy.CodeAltaManagedOnly };
        }

        public override IEnumerable<PluginSystemPromptContribution> GetSystemPromptContributions()
        {
            yield return new PluginSystemPromptContribution
            {
                Title = "sample system",
                Channel = PluginPromptChannel.System,
                Content = static (context, _) => ValueTask.FromResult<string?>($"system:{context.SupportsDirectInjection}"),
            };
        }

        public override IEnumerable<PluginPromptProcessorContribution> GetPromptProcessors()
        {
            yield return new PluginPromptProcessorContribution
            {
                Handler = static (context, _) => ValueTask.FromResult(PluginPromptResult.Replace(context.Text + " processed")),
            };
        }

        public override IEnumerable<PluginCompactionContribution> GetCompactionContributions()
        {
            yield return new PluginCompactionContribution
            {
                Capabilities = PluginCompactionCapabilities.Observe | PluginCompactionCapabilities.Instructions | PluginCompactionCapabilities.Reduce,
                BeforeCompaction = static (_, _) => ValueTask.FromResult(PluginBeforeCompactionResult.Continue),
                Instructions = static (_, _) => ValueTask.FromResult(new PluginCompactionInstructionResult { Title = "sample", Instructions = "compact instructions" }),
                Reducer = static (_, _) => ValueTask.FromResult(PluginCompactionReducerResult.FromText("reduced")),
                AfterCompaction = static (_, _) =>
                {
                    AfterCompactionInvoked = true;
                    return ValueTask.CompletedTask;
                },
            };
        }

        public override IEnumerable<PluginUiContribution> GetUiContributions()
        {
            yield return new PluginStatusContribution
            {
                Region = PluginUiRegion.SessionStatus,
                GetStatus = static _ => new PluginStatusItem { Label = "sample", Text = "ready", Tone = PluginStatusTone.Success },
            };
            yield return new PluginRendererContribution
            {
                Region = PluginUiRegion.SessionFooter,
                Target = "sample",
                Renderer = static (_, _) => ValueTask.FromResult<PluginRenderResult?>(PluginRenderResult.FromMarkdown("rendered")),
            };
        }

        public override IEnumerable<PluginResourceContribution> GetResources()
        {
            yield return new PluginResourceContribution { Kind = PluginResourceKind.SkillRoot, Path = "skills" };
        }

        public override ValueTask<PluginPromptResult?> OnPromptSubmittingAsync(PluginPromptSubmittingContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<PluginPromptResult?>(PluginPromptResult.Replace(context.Text + " callback"));

        public override ValueTask<PluginBeforeAgentRunResult?> OnBeforeAgentRunAsync(PluginBeforeAgentRunContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<PluginBeforeAgentRunResult?>(new PluginBeforeAgentRunResult
            {
                AdditionalMessages = [new PluginPromptMessage { Role = PluginPromptMessageRole.Developer, Content = "before" }],
                PreferredToolNames = ["sample_tool"],
                AdditionalTools = [CreateTool("before_tool")],
            });

        public override ValueTask<PluginToolCallResult?> OnToolCallAsync(PluginToolCallContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<PluginToolCallResult?>(PluginToolCallResult.Block("blocked"));

        public override ValueTask<PluginToolResult?> OnToolResultAsync(PluginToolResultContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<PluginToolResult?>(PluginToolResult.Replace(new AgentToolResult(true, [new AgentToolResultItem.Text("replacement")])));

        public override ValueTask OnAgentEventAsync(PluginAgentEventContext context, CancellationToken cancellationToken = default)
        {
            AgentEventsObserved++;
            return ValueTask.CompletedTask;
        }

        private static AgentToolDefinition CreateTool(string name)
            => new(
                new AgentToolSpec(name, "sample", JsonSerializer.SerializeToElement(new Dictionary<string, object?>())),
                static (_, _) => Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text("tool")])));
    }

    public sealed class FailingCommandPlugin : PluginBase
    {
        public override IEnumerable<PluginCommandContribution> GetCommands()
        {
            yield return new PluginCommandContribution
            {
                Name = "fail",
                Handler = static (_, _) => throw new InvalidOperationException("boom"),
            };
        }
    }

    public sealed class AgentEventOrderPlugin : PluginBase
    {
        public override ValueTask OnAgentEventAsync(PluginAgentEventContext context, CancellationToken cancellationToken = default)
        {
            if (context.Services.State is AgentEventOrderRecorder recorder)
            {
                recorder.Record(context.Plugin.RuntimeKey);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class AgentEventOrderRecorder : IPluginStateStore
    {
        private readonly List<string> _calls = [];

        public IReadOnlyList<string> Calls => _calls;

        public string GetDirectory(PluginStateScope scope) => Path.GetTempPath();

        public ValueTask<T?> ReadJsonAsync<T>(PluginStateScope scope, string name, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<T?>(default);

        public ValueTask WriteJsonAsync<T>(PluginStateScope scope, string name, T value, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DeleteAsync(PluginStateScope scope, string name, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public void Record(string runtimeKey) => _calls.Add(runtimeKey);
    }

    private sealed class AgentEventOrderPluginServices : IPluginServices
    {
        private readonly NoopPluginServices _inner = NoopPluginServices.Create();

        public AgentEventOrderPluginServices(IPluginStateStore state)
        {
            State = state;
        }

        public XenoAtom.Logging.Logger Logger => _inner.Logger;

        public IPluginUiService Ui => _inner.Ui;

        public IPluginStateStore State { get; }

        public IPluginWorkspaceService Workspace => _inner.Workspace;

        public IPluginSessionService Sessions => _inner.Sessions;

        public IPluginPromptService Prompts => _inner.Prompts;

        public IPluginAgentService Agents => _inner.Agents;

        public IPluginTaskService Tasks => _inner.Tasks;

        public IPluginAltaService Alta => _inner.Alta;
    }

    public sealed class FailingAgentEventPlugin : PluginBase
    {
        public static void Reset()
        {
        }

        public override ValueTask OnAgentEventAsync(PluginAgentEventContext context, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("observer failed");
    }

    public sealed class CancellingAgentEventPlugin : PluginBase
    {
        public override ValueTask OnAgentEventAsync(PluginAgentEventContext context, CancellationToken cancellationToken = default)
            => throw new OperationCanceledException(cancellationToken);
    }

}
