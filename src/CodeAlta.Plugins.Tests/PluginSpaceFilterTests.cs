using CodeAlta.Catalog;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.Tests;

[TestClass]
public sealed class PluginSpaceFilterTests
{
    [TestMethod]
    public void APluginOfOtherSpaces_AddsNothingThere_AndOneOfAProjectFollowsItsProject()
    {
        var registry = new PluginContributionRegistry();
        Register(registry, "builtin:jira", PluginScope.Global, "jira", "issues");
        Register(registry, "builtin:git", PluginScope.Global, "git", "commit");
        Register(registry, "project:notes", PluginScope.Project, "notes", "note", "project-a");
        var adapter = new PluginContributionAdapterService(registry)
        {
            SpaceAssignments = () => SpaceAssignments.Parse("""
                [plugins.jira]
                spaces = ["business"]

                [plugins.notes]
                spaces = ["business"]
                """),
        };

        string[] Commands(string? spaceId) => [.. adapter.GetContributions<PluginCommandContribution>(PluginPoint.Command,
                new PluginAdapterOperationOptions { SpaceId = spaceId, ProjectId = "project-a" })
            .Select(static registration => ((PluginCommandContribution)registration.Contribution).Name)];

        CollectionAssert.AreEquivalent(new[] { "issues", "commit", "note" }, Commands("business"));
        CollectionAssert.AreEquivalent(new[] { "commit", "note" }, Commands("personal"),
            "A plugin of the user that lists other spaces adds nothing; one of a project follows its project.");
        CollectionAssert.AreEquivalent(new[] { "commit", "note" }, Commands(SpaceDescriptor.DefaultId), "The default space has only what lists it.");
        CollectionAssert.AreEquivalent(new[] { "issues", "commit", "note" }, Commands(null), "Without a space, as at startup, every plugin applies.");

        Assert.IsTrue(adapter.IsAvailableInSpace("jira", PluginScope.Global, "business"));
        Assert.IsFalse(adapter.IsAvailableInSpace("jira", PluginScope.Global, "personal"));
        Assert.IsTrue(adapter.IsAvailableInSpace(null, PluginScope.Global, "personal"), "A plugin the configuration does not name is in every space.");
        Assert.IsTrue(new PluginContributionAdapterService(registry).IsAvailableInSpace("jira", PluginScope.Global, "personal"),
            "A host that reads no assignments puts every plugin in every space.");
    }

    [TestMethod]
    public void TheIdOfAPlugin_IsTheFolderOfItsPackage_OrTheNameOfABuiltInPlugin()
    {
        Assert.AreEqual("jira", PluginContributionRegistry.PluginIdOf("builtin:jira", null));
        Assert.AreEqual("my-plugin", PluginContributionRegistry.PluginIdOf("source:abc", "my-plugin"));
        Assert.AreEqual("assembly:x", PluginContributionRegistry.PluginIdOf("assembly:x", null));
    }

    private static void Register(PluginContributionRegistry registry, string runtimeKey, PluginScope scope, string pluginId, string command, string? projectId = null)
        => registry.Register(new PluginDescriptor { RuntimeKey = runtimeKey, TypeName = runtimeKey, AssemblyName = runtimeKey }, scope, projectId, null, PluginPoint.Command,
            [new PluginCommandContribution { Name = command, Handler = static (_, _) => ValueTask.FromResult(PluginCommandResult.Handled) }], 1, pluginId);
}
