using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Plugins.Abstractions.Tests;

[TestClass]
public sealed class PluginAbstractionsTests
{
    [TestMethod]
    public async Task EmptyPluginDefaultsAreSafe()
    {
        var plugin = new EmptyPlugin();

        Assert.ThrowsExactly<InvalidOperationException>(plugin.ReadLoggerBeforeAttach);
        CollectionAssert.AreEqual(Array.Empty<PluginCommandContribution>(), plugin.GetCommands().ToArray());
        CollectionAssert.AreEqual(Array.Empty<PluginAgentToolContribution>(), plugin.GetAgentTools().ToArray());
        CollectionAssert.AreEqual(Array.Empty<PluginAgentBackendContribution>(), plugin.GetAgentBackends().ToArray());
        CollectionAssert.AreEqual(Array.Empty<PluginSystemPromptContribution>(), plugin.GetSystemPromptContributions().ToArray());
        CollectionAssert.AreEqual(Array.Empty<PluginPromptProcessorContribution>(), plugin.GetPromptProcessors().ToArray());
        CollectionAssert.AreEqual(Array.Empty<PluginCompactionContribution>(), plugin.GetCompactionContributions().ToArray());
        CollectionAssert.AreEqual(Array.Empty<PluginUiContribution>(), plugin.GetUiContributions().ToArray());
        CollectionAssert.AreEqual(Array.Empty<PluginResourceContribution>(), plugin.GetResources().ToArray());

        await plugin.InitializeAsync();
        await plugin.OnActivatedAsync();
        await plugin.OnDeactivatingAsync();
        await plugin.DisposeAsync();

        var context = CreatePromptContext("hello");
        Assert.IsNull(await plugin.OnPromptSubmittingAsync(context));
        Assert.IsNull(await plugin.OnBeforeAgentRunAsync(new PluginBeforeAgentRunContext { Plugin = context.Plugin, Services = context.Services }));
    }

    [TestMethod]
    public void RuntimeContextAttachmentExposesServicesAndInvalidation()
    {
        var plugin = new EmptyPlugin();
        var context = CreateRuntimeContext(typeof(EmptyPlugin));

        plugin.AttachRuntimeContext(context);

        Assert.AreSame(context.Logger, plugin.ReadLoggerAfterAttach());
        Assert.AreSame(context.Services, plugin.ReadServicesAfterAttach());
        Assert.AreEqual(PluginScope.Global, plugin.ReadScopeAfterAttach());
        Assert.IsTrue(context.IsValid);
        Assert.IsTrue(context.AppliesToProject("any", Path.Combine(Path.GetTempPath(), "any")));

        context.Invalidate();

        Assert.ThrowsExactly<ObjectDisposedException>(context.ThrowIfInvalid);
    }

    [TestMethod]
    public void ProjectScopedRuntimeContextCarriesRuntimeAssignedScope()
    {
        var plugin = new EmptyPlugin();
        var projectPath = Path.Combine(Path.GetTempPath(), "scope-project");
        var context = CreateRuntimeContext(typeof(EmptyPlugin), PluginScope.Project, "project-1", projectPath);

        plugin.AttachRuntimeContext(context);

        Assert.AreEqual(PluginScope.Project, plugin.ReadScopeAfterAttach());
        Assert.AreEqual("project-1", plugin.ReadScopeProjectIdAfterAttach());
        Assert.AreEqual(projectPath, plugin.ReadScopeProjectPathAfterAttach());
        Assert.IsTrue(context.IsProjectScope);
        Assert.IsFalse(context.IsGlobalScope);
        Assert.IsTrue(context.AppliesToProject("project-1", null));
        Assert.IsTrue(context.AppliesToProject(null, projectPath));
        Assert.IsFalse(context.AppliesToProject("project-2", Path.Combine(Path.GetTempPath(), "other-project")));
    }

    [TestMethod]
    public void DescriptorFactorySupportsBareAttributedAndDependencies()
    {
        var bare = PluginDescriptorFactory.FromType(typeof(EmptyPlugin));
        var attributed = PluginDescriptorFactory.FromType(typeof(AttributedPlugin), readmePath: "readme.md");

        Assert.AreEqual(nameof(EmptyPlugin), bare.DisplayName);
        Assert.IsNull(bare.ReadmePath);
        Assert.AreEqual("CodeAlta.Plugins.Abstractions.Tests:sample-key", attributed.RuntimeKey);
        Assert.AreEqual("Sample Plugin", attributed.DisplayName);
        Assert.AreEqual("readme.md", attributed.ReadmePath);
        Assert.AreEqual("docs", attributed.ReadmeAnchor);
        CollectionAssert.AreEqual(new[] { "one", "two" }, attributed.Tags.ToArray());
        Assert.AreEqual(1, attributed.Dependencies.Count);
        Assert.AreEqual("other", attributed.Dependencies[0].PluginKey);
        Assert.IsTrue(attributed.Dependencies[0].Optional);
    }

    [TestMethod]
    public void DescriptorValidationFindsNonFatalIssues()
    {
        var descriptor = new PluginDescriptor
        {
            RuntimeKey = "plugin",
            TypeName = "T",
            AssemblyName = "A",
            Tags = ["dup", "Dup"],
            Dependencies =
            [
                new PluginDependency { PluginKey = "other" },
                new PluginDependency { PluginKey = "OTHER" },
            ],
        };

        var diagnostics = PluginDescriptorFactory.Validate(descriptor);

        Assert.IsTrue(diagnostics.Any(diagnostic => diagnostic.Field == "DisplayName"));
        Assert.IsTrue(diagnostics.Any(diagnostic => diagnostic.Field == "Tags"));
        Assert.IsTrue(diagnostics.Any(diagnostic => diagnostic.Field == "Dependencies"));
    }

    [TestMethod]
    public async Task HelperFactoriesProduceLowCeremonyContributions()
    {
        var command = Command.Prompt(
            "hello_plugin",
            "Say hello.",
            static _ => new ValueTask<PluginCommandResult>(PluginCommandResult.Handled),
            ["hello"]);
        var prompt = Prompt.Developer("Always mention the plugin.");
        var visual = PluginUi.Visual(PluginUiRegion.ThreadFooter, new Markup("hello"));
        var visualFactory = PluginUi.Visual(PluginUiRegion.SidebarSection, static _ => new Markup("dynamic"));
        var status = PluginUi.Status("Plugin", static _ => "ready");
        var inputDialog = PluginUi.InputDialog("Input", "seed");
        var confirmDialog = PluginUi.ConfirmDialog("Confirm", "Continue?");
        var resource = Resources.SkillRoot("skills");
        var flag = CommandLine.Flag("plugin-test", "Enable plugin test mode.");
        var option = CommandLine.Option("plugin-value", "Set a plugin value.");
        var listOption = CommandLine.ListOption("plugin-list", "Set repeated plugin values.");
        var startup = Startup.Resources("early-resources", [resource]);
        var attachment = Attachments.File("src/Program.cs", "Program.cs");
        var tool = Tool.FromDefinition(CreateToolDefinition(), promptSnippet: "Use hello_tool.");
        var backend = PluginBackend.FromFactory("fake", static (_, _) => new ValueTask<IAgentBackend>(new FakeBackend()));

        Assert.AreEqual(PluginCommandKind.Prompt, command.Kind);
        Assert.AreEqual("Hello Plugin", command.Label);
        CollectionAssert.Contains(command.Aliases.ToArray(), "hello");
        Assert.AreEqual(PluginPromptChannel.Developer, prompt.Channel);
        Assert.AreEqual("Always mention the plugin.", await prompt.Content(CreateSystemPromptContext(), CancellationToken.None));
        Assert.IsNotNull(visual.Visual);
        Assert.IsNotNull(visualFactory.CreateVisual!(CreateVisualContext()));
        Assert.AreEqual("ready", status.GetStatus(CreateStatusContext())?.Text);
        Assert.AreEqual(PluginDialogKind.Input, inputDialog.Kind);
        Assert.AreEqual("seed", inputDialog.InitialText);
        Assert.AreEqual(2, confirmDialog.Buttons.Count);
        Assert.AreEqual(PluginResourceKind.SkillRoot, resource.Kind);
        Assert.IsTrue(resource.IsPackageRelative);
        Assert.AreEqual(PluginCommandLineValueKind.Boolean, flag.ValueKind);
        Assert.AreEqual(PluginCommandLineValueKind.String, option.ValueKind);
        Assert.AreEqual(PluginCommandLineValueKind.StringList, listOption.ValueKind);
        Assert.AreEqual(resource, startup.Resources[0]);
        Assert.AreEqual(PluginPromptAttachmentKind.File, attachment.Kind);
        Assert.AreEqual("hello_tool", tool.Definition.Spec.Name);
        Assert.AreEqual("Use hello_tool.", tool.PromptSnippet);
        Assert.AreEqual("fake", backend.Name);
        Assert.IsInstanceOfType(await backend.Factory(CreateBackendContext(), CancellationToken.None), typeof(FakeBackend));
    }

    [TestMethod]
    public async Task PromptToolCompactionAndDiagnosticsResultsHaveExpectedShapes()
    {
        using var document = JsonDocument.Parse("{\"value\":1}");
        var replacePrompt = PluginPromptResult.Replace("new text", [new PluginPromptAttachment { Kind = PluginPromptAttachmentKind.Text, Text = "attachment" }]);
        var cancelRun = PluginBeforeAgentRunResult.CancelRun("blocked");
        var replaceToolCall = PluginToolCallResult.ReplaceArguments(document.RootElement.Clone());
        var blockedToolCall = PluginToolCallResult.Block("no");
        var replaceToolResult = PluginToolResult.Replace(new AgentToolResult(true, [new AgentToolResultItem.Text("ok")]));
        var compactionCancel = PluginBeforeCompactionResult.CancelWithReason("not now");
        var compactionText = PluginCompactionReducerResult.FromText("compact");
        var diagnostic = PluginDiagnostics.Tool("plugin", "hello_tool", "failed", new InvalidOperationException("boom"));
        var rendererResult = PluginRenderResult.FromMarkdown("**ok**");

        Assert.AreEqual(PluginPromptDisposition.Replace, replacePrompt.Disposition);
        Assert.AreEqual("new text", replacePrompt.ReplacementText);
        Assert.IsTrue(cancelRun.Cancel);
        Assert.AreEqual(PluginToolCallDisposition.ReplaceArguments, replaceToolCall.Disposition);
        Assert.AreEqual(PluginToolCallDisposition.Block, blockedToolCall.Disposition);
        Assert.AreEqual(PluginToolResultDisposition.Replace, replaceToolResult.Disposition);
        Assert.IsTrue(compactionCancel.Cancel);
        Assert.IsTrue(compactionText.Handled);
        Assert.AreEqual(PluginDiagnosticSource.Tool, diagnostic.Source);
        Assert.AreEqual("System.InvalidOperationException", diagnostic.Exception?.TypeName);
        Assert.AreEqual("**ok**", rendererResult.Markdown);

        var processor = new PluginPromptProcessorContribution
        {
            Handler = static (context, _) => new ValueTask<PluginPromptResult>(PluginPromptResult.Handled(context.Text)),
        };
        Assert.AreEqual("hello", (await processor.Handler(CreatePromptContext("hello"), CancellationToken.None)).UserMessage);
    }

    [TestMethod]
    public async Task NoopServicesArePredictableAndHeadless()
    {
        var services = NoopPluginServices.Create();

        Assert.IsFalse(services.Ui.HasInteractiveUi);
        await services.Ui.NotifyAsync("hello");
        Assert.IsFalse(await services.Ui.ConfirmAsync("title", "message"));
        Assert.IsNull(await services.Ui.InputAsync("title"));
        Assert.IsNull(await services.Ui.SelectAsync("title", new[] { new PluginSelectItem<string> { Label = "A", Value = "a" } }));
        Assert.IsNull(await services.Ui.ShowDialogForResultAsync(PluginUi.NotifyDialog("title", "message")));
        Assert.IsTrue(services.State.GetDirectory(PluginStateScope.User).Contains("Noop", StringComparison.Ordinal));
        Assert.IsNull(await services.State.ReadJsonAsync<string>(PluginStateScope.User, "state"));
        await services.State.WriteJsonAsync(PluginStateScope.User, "state", new { Value = 1 });
        await services.State.DeleteAsync(PluginStateScope.User, "state");
        Assert.IsNull(services.Workspace.GetSelectedProjectPath("file.txt"));
        Assert.IsFalse(services.Workspace.IsInsideSelectedProject("file.txt"));
        await services.Threads.SendPromptAsync("hello");
        Assert.IsFalse(await services.Threads.TrySteerAsync("stop"));
        await services.Prompts.SetDraftTextAsync("draft");
        await services.Prompts.AddAttachmentAsync(new PluginPromptAttachment { Kind = PluginPromptAttachmentKind.File, Path = "file.txt" });
        Assert.AreEqual(0, (await services.Prompts.GetAttachmentsAsync()).Count);
        Assert.IsFalse(services.Agents.HasCapability("tools"));
    }

    [TestMethod]
    public void ContributionHandlesAreStableForStableInputs()
    {
        var first = PluginContributionHandle.Create("plugin", "Type", PluginPoint.Command, "hello", 0, 1);
        var second = PluginContributionHandle.Create("plugin", "Type", PluginPoint.Command, "hello", 0, 1);

        Assert.AreEqual(first, second);
        Assert.AreEqual("hello", first.NaturalName);
        Assert.AreEqual("plugin:Type:Command:hello:0:1", first.RuntimeContributionKey);
    }

    [TestMethod]
    public void EventAndOperationContextsCarryTypedDataAndCanInvalidate()
    {
        using var document = JsonDocument.Parse("{\"event\":true}");
        var context = new PluginAgentEventContext
        {
            Plugin = CreateDescriptor(typeof(EmptyPlugin)),
            Services = NoopPluginServices.Create(),
            Scope = PluginScope.Project,
            ScopeProjectId = "project",
            Event = new AgentRawEvent(new AgentBackendId("fake"), "session", DateTimeOffset.UtcNow, "raw", document.RootElement.Clone()),
            ProjectId = "project",
            ThreadId = "thread",
        };

        Assert.AreEqual("project", context.ProjectId);
        Assert.AreEqual("fake", context.Event.BackendId.Value);
        Assert.IsTrue(context.AppliesToCurrentProject());
        context.Invalidate();
        Assert.ThrowsExactly<ObjectDisposedException>(context.ThrowIfInvalid);
    }

    [TestMethod]
    public void DiscoveryFindsOnlyPublicConcreteParameterlessPluginTypes()
    {
        var assembly = typeof(PublicDiscoverablePlugin).Assembly;
        var discovered = PluginDiscovery.DiscoverPluginTypes(assembly);

        CollectionAssert.Contains(discovered.ToArray(), typeof(PublicDiscoverablePlugin));
        CollectionAssert.DoesNotContain(discovered.ToArray(), typeof(PublicPluginWithoutDefaultConstructor));
        CollectionAssert.DoesNotContain(discovered.ToArray(), typeof(PublicGenericPlugin<>));
        Assert.IsTrue(PluginDiscovery.IsDiscoverablePluginType(typeof(PublicDiscoverablePlugin)));
        Assert.IsFalse(PluginDiscovery.IsDiscoverablePluginType(typeof(PublicPluginWithoutDefaultConstructor)));
        Assert.IsFalse(PluginDiscovery.IsDiscoverablePluginType(typeof(PublicGenericPlugin<>)));
    }

    private static AgentToolDefinition CreateToolDefinition()
    {
        using var document = JsonDocument.Parse("{\"type\":\"object\"}");
        return new AgentToolDefinition(
            new AgentToolSpec("hello_tool", "Hello tool.", document.RootElement.Clone()),
            static (_, _) => Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text("ok")])));
    }

    private static PluginRuntimeContext CreateRuntimeContext(
        Type pluginType,
        PluginScope scope = PluginScope.Global,
        string? scopeProjectId = null,
        string? scopeProjectPath = null)
    {
        var services = NoopPluginServices.Create();
        return new PluginRuntimeContext
        {
            Plugin = CreateDescriptor(pluginType),
            Host = new PluginHostInfo
            {
                ApplicationName = "CodeAlta",
                Version = "test",
                HostApiVersion = "1",
                UserDataDirectory = Path.GetTempPath(),
                IsHeadless = true,
            },
            Logger = services.Logger,
            Services = services,
            PackageDirectory = Path.GetTempPath(),
            Scope = scope,
            ScopeProjectId = scopeProjectId,
            ScopeProjectPath = scopeProjectPath,
        };
    }

    private static PluginDescriptor CreateDescriptor(Type pluginType) => PluginDescriptorFactory.FromType(pluginType);

    private static PluginPromptSubmittingContext CreatePromptContext(string text)
    {
        var services = NoopPluginServices.Create();
        return new PluginPromptSubmittingContext
        {
            Plugin = CreateDescriptor(typeof(EmptyPlugin)),
            Services = services,
            Text = text,
        };
    }

    private static PluginSystemPromptContext CreateSystemPromptContext()
    {
        var services = NoopPluginServices.Create();
        return new PluginSystemPromptContext
        {
            Plugin = CreateDescriptor(typeof(EmptyPlugin)),
            Services = services,
            Channel = PluginPromptChannel.Developer,
        };
    }

    private static PluginVisualContext CreateVisualContext()
    {
        var services = NoopPluginServices.Create();
        return new PluginVisualContext
        {
            Plugin = CreateDescriptor(typeof(EmptyPlugin)),
            Services = services,
            Region = PluginUiRegion.SidebarSection,
        };
    }

    private static PluginStatusContext CreateStatusContext()
    {
        var services = NoopPluginServices.Create();
        return new PluginStatusContext
        {
            Plugin = CreateDescriptor(typeof(EmptyPlugin)),
            Services = services,
        };
    }

    private static PluginAgentBackendFactoryContext CreateBackendContext()
    {
        var services = NoopPluginServices.Create();
        return new PluginAgentBackendFactoryContext
        {
            Plugin = CreateDescriptor(typeof(EmptyPlugin)),
            Services = services,
            Logger = services.Logger,
            PackageDirectory = Path.GetTempPath(),
        };
    }

    private sealed class EmptyPlugin : PluginBase
    {
        public void ReadLoggerBeforeAttach()
        {
            _ = Logger;
        }

        public XenoAtom.Logging.Logger ReadLoggerAfterAttach() => Logger;

        public IPluginServices ReadServicesAfterAttach() => Services;

        public PluginScope ReadScopeAfterAttach() => Scope;

        public string? ReadScopeProjectIdAfterAttach() => ScopeProjectId;

        public string? ReadScopeProjectPathAfterAttach() => ScopeProjectPath;
    }

    [Plugin("sample-key", DisplayName = "Sample Plugin", Description = "Sample.", Author = "Tester", ProjectUrl = "https://example.com", ReadmeAnchor = "docs", Tags = ["one", "two"])]
    [PluginDependency("other", Optional = true, VersionRange = ">=1.0")]
    private sealed class AttributedPlugin : PluginBase
    {
    }

    private sealed class FakeBackend : IAgentBackend
    {
        public AgentBackendId BackendId => new("fake");

        public string DisplayName => "Fake";

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentModelInfo>>([]);

        public Task<IReadOnlyList<AgentSessionMetadata>> ListSessionsAsync(AgentSessionListFilter? filter = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentSessionMetadata>>([]);

        public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IAgentSession> ResumeSessionAsync(string sessionId, AgentSessionResumeOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class PublicDiscoverablePlugin : PluginBase;

public sealed class PublicPluginWithoutDefaultConstructor(string value) : PluginBase
{
    public string Value { get; } = value;
}

public sealed class PublicGenericPlugin<T> : PluginBase;
