using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.Tests;

[TestClass]
public sealed class PluginContributionRegistryTests
{
    [TestMethod]
    public void RegisterCreatesDeterministicHandlesAndRemovesByPlugin()
    {
        var registry = new PluginContributionRegistry();
        var descriptor = new PluginDescriptor
        {
            RuntimeKey = "assembly:plugin",
            TypeName = "Sample.Plugin",
            AssemblyName = "Sample",
        };
        var commands = new object[]
        {
            new PluginCommandContribution { Name = "hello", Handler = static (_, _) => ValueTask.FromResult(PluginCommandResult.Handled) },
        };

        var registrations = registry.Register(descriptor, PluginScope.Global, null, null, PluginPoint.Command, commands, 1);

        Assert.AreEqual(1, registrations.Count);
        Assert.AreEqual("hello", registrations[0].Handle.NaturalName);
        Assert.AreEqual(1, registry.GetSnapshot().Count);
        Assert.AreEqual(1, registry.RemoveByPlugin(descriptor.RuntimeKey).Count);
        Assert.AreEqual(0, registry.GetSnapshot().Count);
    }

    [TestMethod]
    public void ProjectScopedRegistrationAppliesOnlyToMatchingProject()
    {
        var registration = new PluginContributionRegistration
        {
            Handle = PluginContributionHandle.Create("plugin", "Plugin", PluginPoint.Command, "hello", 0, 1),
            Contribution = new object(),
            Scope = PluginScope.Project,
            ScopeProjectId = "project-a",
            ScopeProjectPath = Path.Combine(Path.GetTempPath(), "project-a"),
        };

        Assert.IsTrue(PluginContributionRegistry.AppliesToProject(registration, "project-a", null));
        Assert.IsFalse(PluginContributionRegistry.AppliesToProject(registration, "project-b", Path.Combine(Path.GetTempPath(), "project-b")));
    }

    [TestMethod]
    public void SnapshotOrdersByScopeContributionOrderPluginAndOrdinal()
    {
        var registry = new PluginContributionRegistry();
        var globalLate = CreateDescriptor("global:late");
        var globalEarly = CreateDescriptor("global:early");
        var project = CreateDescriptor("project:early");

        registry.Register(globalLate, PluginScope.Global, null, null, PluginPoint.Command,
        [
            new PluginCommandContribution { Name = "late", Order = 10, Handler = static (_, _) => ValueTask.FromResult(PluginCommandResult.Handled) },
        ], 1);
        registry.Register(project, PluginScope.Project, "project", null, PluginPoint.Command,
        [
            new PluginCommandContribution { Name = "project", Order = -10, Handler = static (_, _) => ValueTask.FromResult(PluginCommandResult.Handled) },
        ], 1);
        registry.Register(globalEarly, PluginScope.Global, null, null, PluginPoint.Command,
        [
            new PluginCommandContribution { Name = "early", Order = -10, Handler = static (_, _) => ValueTask.FromResult(PluginCommandResult.Handled) },
        ], 1);

        var snapshot = registry.GetSnapshot();

        Assert.AreEqual("early", snapshot[0].Handle.NaturalName);
        Assert.AreEqual("late", snapshot[1].Handle.NaturalName);
        Assert.AreEqual("project", snapshot[2].Handle.NaturalName);
    }

    [TestMethod]
    public void BuiltInContributionsOrderBeforeDynamicAndConflictsAreDiagnosed()
    {
        var registry = new PluginContributionRegistry();
        var source = CreateDescriptor("source:plugin");
        var builtIn = CreateDescriptor("builtin:plugin") with
        {
            Metadata = new Dictionary<string, string> { ["PluginKind"] = PluginLoadUnitKind.BuiltIn.ToString() },
        };

        registry.Register(source, PluginScope.Global, null, null, PluginPoint.Command,
        [
            new PluginCommandContribution { Name = "hello", Handler = static (_, _) => ValueTask.FromResult(PluginCommandResult.Handled) },
        ], 1);
        registry.Register(builtIn, PluginScope.Global, null, null, PluginPoint.Command,
        [
            new PluginCommandContribution { Name = "hello", Handler = static (_, _) => ValueTask.FromResult(PluginCommandResult.Handled) },
        ], 1);

        var snapshot = registry.GetSnapshot();

        Assert.AreEqual(PluginLoadUnitKind.BuiltIn, snapshot[0].LoadUnitKind);
        Assert.AreEqual(PluginLoadUnitKind.Source, snapshot[1].LoadUnitKind);
        Assert.IsTrue(registry.GetDiagnostics().Any(diagnostic =>
            diagnostic.Source == PluginRuntimeDiagnosticSource.Contribution &&
            diagnostic.Metadata.ContainsKey("ShadowedPluginRuntimeKey")));
        Assert.AreEqual(2, registry.GetContributionSummaries().Count);
    }

    [TestMethod]
    public void ConflictDiagnosticsCoverEffectiveRuntimeKeysForConcreteContributionKinds()
    {
        var registry = new PluginContributionRegistry();
        var first = CreateDescriptor("source:first");
        var second = CreateDescriptor("source:second");

        RegisterConcreteConflictSet(registry, first);
        RegisterConcreteConflictSet(registry, second);

        var conflictKinds = registry.GetDiagnostics()
            .Select(diagnostic => diagnostic.Metadata.TryGetValue("ConflictKind", out var kind) ? kind : string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        CollectionAssert.IsSubsetOf(
            new[] { "command", "keybinding", "tool", "prompt-part", "resource", "ui-region" },
            conflictKinds.ToArray());
    }

    private static PluginDescriptor CreateDescriptor(string runtimeKey)
        => new()
        {
            RuntimeKey = runtimeKey,
            TypeName = runtimeKey,
            AssemblyName = "Tests",
        };

    private static void RegisterConcreteConflictSet(PluginContributionRegistry registry, PluginDescriptor descriptor)
    {
        registry.Register(descriptor, PluginScope.Global, null, null, PluginPoint.Command,
        [
            new PluginCommandContribution
            {
                Name = "same-command",
                KeyBinding = new PluginKeyBinding { DisplayText = "Ctrl+X" },
                Handler = static (_, _) => ValueTask.FromResult(PluginCommandResult.Handled),
            },
        ], 1);
        registry.Register(descriptor, PluginScope.Global, null, null, PluginPoint.AgentTool,
        [
            new PluginAgentToolContribution
            {
                Definition = new AgentToolDefinition(
                    new AgentToolSpec("same-tool", "same", JsonSerializer.SerializeToElement(new Dictionary<string, object?>())),
                    static (_, _) => Task.FromResult(new AgentToolResult(true, []))),
            },
        ], 1);
        registry.Register(descriptor, PluginScope.Global, null, null, PluginPoint.SystemPrompt,
        [
            new PluginSystemPromptContribution
            {
                Title = "same-prompt",
                Channel = PluginPromptChannel.System,
                Content = static (_, _) => ValueTask.FromResult<string?>("prompt"),
            },
        ], 1);
        registry.Register(descriptor, PluginScope.Global, null, null, PluginPoint.Resource,
        [
            new PluginResourceContribution { Kind = PluginResourceKind.SkillRoot, Path = "skills" },
        ], 1);
        registry.Register(descriptor, PluginScope.Global, null, null, PluginPoint.Ui,
        [
            new PluginStatusContribution
            {
                Region = PluginUiRegion.SessionStatus,
                Name = "same-status",
                GetStatus = static _ => new PluginStatusItem { Label = "same", Text = "same" },
            },
        ], 1);
    }
}
