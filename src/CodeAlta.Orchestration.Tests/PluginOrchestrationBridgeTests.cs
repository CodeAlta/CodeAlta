using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.Plugins;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class PluginOrchestrationBridgeTests
{
    [TestMethod]
    public async Task ProcessPromptSubmittingAsync_UsesHeadlessDefaultsWithoutActivePlugins()
    {
        var bridge = CreateBridge(new PluginContributionRegistry());

        var result = await bridge.ProcessPromptSubmittingAsync("hello", cancellationToken: CancellationToken.None);

        Assert.AreEqual(PluginPromptDisposition.Replace, result.Result.Disposition);
        Assert.AreEqual("hello", result.Result.ReplacementText);
        Assert.AreEqual(0, result.Diagnostics.Count);
    }

    [TestMethod]
    public void GetAgentTools_ReturnsHeadlessApplicableToolContributions()
    {
        var registry = new PluginContributionRegistry();
        var tool = new PluginAgentToolContribution
        {
            Definition = new AgentToolDefinition(
                new AgentToolSpec("tool_1", "Tool", JsonDocument.Parse("{}").RootElement.Clone()),
                static (_, _) => Task.FromResult(new AgentToolResult(Success: true, []))),
        };
        registry.Register(
            new PluginDescriptor
            {
                RuntimeKey = "plugin-1",
                TypeName = "Plugin",
                AssemblyName = "PluginAssembly",
            },
            PluginScope.Global,
            scopeProjectId: null,
            scopeProjectPath: null,
            PluginPoint.AgentTool,
            [tool],
            activationGeneration: 1);
        var bridge = CreateBridge(registry);

        var tools = bridge.GetAgentTools(new PluginAdapterOperationOptions { HasInteractiveUi = true });

        Assert.AreEqual(1, tools.Count);
        Assert.AreSame(tool, tools[0]);
    }

    [TestMethod]
    public async Task ATool_OfAPluginThatIsBuiltAgain_CallsTheVersionThatRuns()
    {
        var registry = new PluginContributionRegistry();
        static PluginAgentToolContribution Tool(string name, string version) => new()
        {
            Definition = new AgentToolDefinition(
                new AgentToolSpec(name, "Says which version runs.", JsonDocument.Parse("{}").RootElement.Clone()),
                (_, _) => Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text(version)]))),
        };
        static PluginDescriptor Plugin(string key) => new() { RuntimeKey = key, TypeName = "Plugin", AssemblyName = "plugin" };
        void Start(string key, int generation, params PluginAgentToolContribution[] tools)
            => registry.Register(Plugin(key), PluginScope.Global, scopeProjectId: null, scopeProjectPath: null, PluginPoint.AgentTool, tools, generation);
        static async Task<string> CallAsync(AgentToolDefinition tool)
        {
            var result = await tool.Handler(new AgentToolInvocation(new ModelProviderId("provider-1"), "session-1", "call-1", tool.Spec.Name, JsonDocument.Parse("{}").RootElement.Clone()), CancellationToken.None);
            return (result.Success ? "ok: " : "failed: ") + string.Concat(result.Items.OfType<AgentToolResultItem.Text>().Select(static item => item.Value));
        }

        Start("plugin:notes", 1, Tool("note_version", "v1"), Tool("note_count", "three"));
        Start("plugin:other", 1, Tool("other_tool", "other"));
        var bridge = CreateBridge(registry);

        // The tools of one plugin, as a run gets them; and those of every plugin.
        var tools = bridge.CreateAgentTools(pluginRuntimeKeys: ["plugin:notes"]);
        CollectionAssert.AreEqual(new[] { "note_version", "note_count" }, tools.Select(static tool => tool.Spec.Name).ToArray());
        Assert.AreEqual(3, bridge.CreateAgentTools().Count);
        Assert.AreEqual(0, bridge.CreateAgentTools(pluginRuntimeKeys: []).Count);
        Assert.AreEqual("ok: v1", await CallAsync(tools[0]));

        // The plugin is built again. A session keeps the tool it was given, which looks the same: it calls the new one.
        registry.RemoveByPlugin("plugin:notes");
        Start("plugin:notes", 2, Tool("note_version", "v2"));
        Assert.AreEqual("ok: v2", await CallAsync(tools[0]));
        Assert.AreEqual("ok: v2", await CallAsync(bridge.CreateAgentTools(pluginRuntimeKeys: ["plugin:notes"]).Single()));

        // A tool the new version no longer has, and the tools of a plugin that was stopped, say so.
        Assert.AreEqual("failed: The plugin that provides the tool 'note_count' is not running.", await CallAsync(tools[1]));
        registry.RemoveByPlugin("plugin:notes");
        Assert.AreEqual("failed: The plugin that provides the tool 'note_version' is not running.", await CallAsync(tools[0]));
    }

    [TestMethod]
    public async Task RunCompactionAsync_ReturnsEmptyResultWhenNoCompactionContributionsApply()
    {
        var bridge = CreateBridge(new PluginContributionRegistry());

        var result = await bridge.RunCompactionAsync(cancellationToken: CancellationToken.None);

        Assert.AreEqual(0, result.BeforeResults.Count);
        Assert.AreEqual(0, result.InstructionResults.Count);
        Assert.AreEqual(0, result.ReducerResults.Count);
        Assert.AreEqual(0, result.Diagnostics.Count);
    }

    [TestMethod]
    public async Task BuildAgentRunAugmentationAsync_MaterializesHeadlessPromptAndTools()
    {
        var registry = new PluginContributionRegistry();
        var activator = new PluginRuntimeActivator(registry);
        var activation = await activator.ActivateAsync(
            CreateDiscovered<RunAugmentationFixturePlugin>(),
            sourcePackage: null,
            loadContext: null,
            new PluginActivationOptions { HostInfo = CreateHeadlessHostInfo() });
        Assert.IsTrue(activation.Succeeded, string.Join(Environment.NewLine, activation.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.IsNotNull(activation.ActivePlugin);
        var active = activation.ActivePlugin;
        var bridge = new PluginOrchestrationBridge(new PluginContributionAdapterService(registry), () => [active]);
        var executionOptions = new SessionExecutionOptions
        {
            ProviderId = new ModelProviderId("provider-1"),
            WorkingDirectory = Environment.CurrentDirectory,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        };

        var result = await bridge.BuildAgentRunAugmentationAsync(
            executionOptions,
            AgentInput.Text("hello"),
            new PluginAdapterOperationOptions
            {
                ProjectId = "project-1",
                ProjectPath = Environment.CurrentDirectory,
                SessionId = "session-1",
                ProviderId = "provider-1",
                Model = "model-1",
                IsCodeAltaManagedProvider = true,
            });

        Assert.IsNull(result.CancelReason);
        Assert.IsNotNull(result.Tools);
        Assert.IsTrue(result.Tools.Any(static tool => tool.Spec.Name == "mcp__fixture__read"));
        Assert.IsNotNull(result.AdditionalSystemMessage);
        Assert.IsNotNull(result.AdditionalDeveloperInstructions);
        StringAssert.Contains(result.AdditionalSystemMessage!, "fixture system prompt");
        StringAssert.Contains(result.AdditionalDeveloperInstructions!, "fixture agent prompt");
        CollectionAssert.Contains(result.PreferredToolNames.ToArray(), "mcp__fixture__read");
        StringAssert.Contains(string.Join("\n", result.Input!.Items.OfType<AgentInputItem.Text>().Select(static item => item.Value)), "fixture per-turn context");
        Assert.IsNotNull(result.InstructionProcessor);
        var processed = await result.InstructionProcessor!(new SessionInstructionProcessingRequest
        {
            SessionId = "session-1",
            ProviderId = "provider-1",
            Model = "model-1",
            SystemMessage = "system secret",
            DeveloperInstructions = "developer secret",
            Manifest = new Dictionary<string, string> { ["agentPromptId"] = "default" },
        }, CancellationToken.None);
        Assert.AreEqual("system [filtered]", processed.SystemMessage);
        Assert.AreEqual("developer [filtered]", processed.DeveloperInstructions);
        Assert.AreEqual(1, processed.Transformations.Count);
        Assert.AreEqual("filtered fixture marker", processed.Transformations[0].ChangeSummary);

        await active.DeactivateAsync(TimeSpan.FromSeconds(5));
    }

    private static PluginOrchestrationBridge CreateBridge(PluginContributionRegistry registry)
        => new(new PluginContributionAdapterService(registry), static () => []);

    private static DiscoveredPluginType CreateDiscovered<TPlugin>()
        where TPlugin : PluginBase
        => new()
        {
            Type = typeof(TPlugin),
            Descriptor = PluginDescriptorFactory.FromType(typeof(TPlugin)),
        };

    private static PluginHostInfo CreateHeadlessHostInfo()
        => new()
        {
            ApplicationName = "CodeAlta.Orchestration.Tests",
            Version = "1.0.0",
            HostApiVersion = "1.0.0",
            UserDataDirectory = Path.GetTempPath(),
            IsHeadless = true,
            HasInteractiveUi = false,
        };

    public sealed class RunAugmentationFixturePlugin : PluginBase
    {
        public override IEnumerable<PluginSystemPromptContribution> GetSystemPromptContributions()
        {
            yield return new PluginSystemPromptContribution
            {
                Title = "Fixture System",
                Channel = PluginPromptChannel.System,
                Content = static (_, _) => ValueTask.FromResult<string?>("fixture system prompt"),
            };
            yield return new PluginSystemPromptContribution
            {
                Title = "Fixture Developer",
                Channel = PluginPromptChannel.Developer,
                Content = static (_, _) => ValueTask.FromResult<string?>("fixture agent prompt"),
            };
        }

        public override ValueTask<PluginBeforeAgentRunResult?> OnBeforeAgentRunAsync(PluginBeforeAgentRunContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<PluginBeforeAgentRunResult?>(new PluginBeforeAgentRunResult
            {
                AdditionalMessages = [new PluginPromptMessage { Role = PluginPromptMessageRole.Developer, Content = "fixture per-turn context" }],
                PreferredToolNames = ["mcp__fixture__read"],
                AdditionalTools =
                [
                    new AgentToolDefinition(
                        new AgentToolSpec("mcp__fixture__read", "Fixture MCP tool", JsonDocument.Parse("{}").RootElement.Clone()),
                        static (_, _) => Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text("fixture tool result")]))),
                ],
            });

        public override IEnumerable<PluginInstructionProcessorContribution> GetInstructionProcessors()
        {
            yield return new PluginInstructionProcessorContribution
            {
                Name = "fixture-filter",
                Handler = static (context, _) => ValueTask.FromResult(PluginInstructionProcessingResult.Replace(
                    systemMessage: context.Instructions.SystemMessage?.Replace("secret", "[filtered]", StringComparison.Ordinal),
                    developerInstructions: context.Instructions.DeveloperInstructions?.Replace("secret", "[filtered]", StringComparison.Ordinal),
                    changeSummary: "filtered fixture marker")),
            };
        }
    }
}
