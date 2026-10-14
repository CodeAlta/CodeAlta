namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class SpaceAssignmentsTests
{
    [TestMethod]
    public void AResourceWithoutSpaces_IsEverywhere_AndOneWithSpaces_OnlyThere()
    {
        Assert.IsTrue(SpaceScope.IsAvailable(null, "business"));
        Assert.IsTrue(SpaceScope.IsAvailable([], null));
        Assert.IsTrue(SpaceScope.IsAvailable(["business"], "business"));
        Assert.IsFalse(SpaceScope.IsAvailable(["business"], "personal"));
        // The default space is a space like the others: it is in the list or the resource is not there.
        Assert.IsFalse(SpaceScope.IsAvailable(["business"], "default"));
        Assert.IsFalse(SpaceScope.IsAvailable(["business"], null), "A session of no space is in the default space.");
        Assert.IsTrue(SpaceScope.IsAvailable(["business", "default"], null));
        Assert.IsTrue(SpaceScope.IsAvailable(["business", "default"], " default "));
    }

    [TestMethod]
    public void TurningASpaceOff_ListsTheOtherSpaces_AndTurningItOn_KeepsASharedResourceShared()
    {
        string[] existing = ["default", "business", "personal"];
        CollectionAssert.AreEqual(new[] { "default", "personal" }, SpaceScope.Without(null, "business", existing)!.ToArray(),
            "A shared resource taken out of one space is listed for every other one, the default space included.");
        CollectionAssert.AreEqual(new[] { "default" }, SpaceScope.Without(["business", "default"], "business", existing)!.ToArray());
        Assert.IsNull(SpaceScope.Without(["business"], "business", existing), "No list says 'in no space': the change is refused.");
        Assert.AreEqual(0, SpaceScope.With(null, "business").Count, "A shared resource stays shared.");
        CollectionAssert.AreEqual(new[] { "business", "personal" }, SpaceScope.With(["business"], "personal").ToArray());
        CollectionAssert.AreEqual(new[] { "business" }, SpaceScope.With(["business"], "business").ToArray());
        Assert.ThrowsExactly<ArgumentException>(() => SpaceScope.With(null, "Not An Id"));
    }

    [TestMethod]
    public void TheAssignmentsOfEveryKind_AreReadFromTheGlobalConfiguration()
    {
        var assignments = SpaceAssignments.Parse("""
            [providers.claude-business]
            type = "claude-code"
            spaces = ["business"]

            [providers.codex]
            type = "codex"

            [plugins.mcp.servers.atlassian]
            spaces = ["business", "default", "business", " "]

            [plugins.Jira]
            enabled = true
            spaces = "business"

            [skills]
            disabled = ["old"]

            [skills.spaces]
            terraform = ["business"]
            "my.skill" = []

            [prompts.spaces]
            reviewer = ["business", "default"]
            odd = 3
            """);

        CollectionAssert.AreEqual(new[] { "business" }, assignments.Of(SpaceResourceKind.Provider, "Claude-Business").ToArray());
        Assert.AreEqual(0, assignments.Of(SpaceResourceKind.Provider, "codex").Count);
        CollectionAssert.AreEqual(new[] { "business", "default" }, assignments.Of(SpaceResourceKind.McpServer, "atlassian").ToArray(), "Each space once.");
        CollectionAssert.AreEqual(new[] { "business" }, assignments.Of(SpaceResourceKind.Plugin, "jira").ToArray(), "One string is a list of one space.");
        Assert.IsTrue(assignments.Plugins.ContainsKey("jira"));
        Assert.IsFalse(assignments.Plugins.ContainsKey("mcp"), "The MCP plugin itself is assigned to no space here.");
        CollectionAssert.AreEqual(new[] { "business" }, assignments.Of(SpaceResourceKind.Skill, "terraform").ToArray());
        Assert.AreEqual(0, assignments.Of(SpaceResourceKind.Skill, "my.skill").Count, "An empty list is every space.");
        CollectionAssert.AreEqual(new[] { "business", "default" }, assignments.Of(SpaceResourceKind.Prompt, "reviewer").ToArray());
        Assert.AreEqual(0, assignments.Of(SpaceResourceKind.Prompt, "odd").Count, "What is not a list of spaces is ignored.");

        Assert.IsTrue(assignments.IsAvailable(SpaceResourceKind.Prompt, "reviewer", null));
        Assert.IsFalse(assignments.IsAvailable(SpaceResourceKind.Skill, "terraform", "personal"));
        Assert.IsTrue(assignments.IsAvailable(SpaceResourceKind.Skill, "unknown", "personal"));
        Assert.AreSame(SpaceAssignments.Empty, SpaceAssignments.Parse("  "));
        Assert.ThrowsExactly<InvalidDataException>(() => SpaceAssignments.Parse("[providers"));
    }

    [TestMethod]
    public void WritingTheSpacesOfAResource_ChangesOnlyItsLine()
    {
        using var fixture = new Fixture();
        var original = """
            # My configuration.
            [chat]
            default_provider = "codex" # the one I use most

            [providers.codex]
            type = "codex"
            display_name = "Codex"   # keep this comment

            # Servers.
            [plugins.mcp.servers.atlassian]
            enabled = true
            """.ReplaceLineEndings("\n");
        fixture.Write(original);

        fixture.Store.SaveGlobalSpaceAssignment(SpaceResourceKind.Provider, "codex", ["business", "default"]);
        fixture.Store.SaveGlobalSpaceAssignment(SpaceResourceKind.McpServer, "atlassian", ["business"]);
        Assert.AreEqual(original.Replace(
            "display_name = \"Codex\"   # keep this comment\n",
            "display_name = \"Codex\"   # keep this comment\nspaces = [\"business\", \"default\"]\n").Replace(
            "enabled = true", "enabled = true\nspaces = [\"business\"]\n"), fixture.Read());

        // A new list replaces the old one in place; an empty one removes the line.
        fixture.Store.SaveGlobalSpaceAssignment(SpaceResourceKind.Provider, "Codex", ["personal"]);
        StringAssert.Contains(fixture.Read(), "display_name = \"Codex\"   # keep this comment\nspaces = [\"personal\"]\n");
        fixture.Store.SaveGlobalSpaceAssignment(SpaceResourceKind.Provider, "codex", []);
        fixture.Store.SaveGlobalSpaceAssignment(SpaceResourceKind.McpServer, "atlassian", null);
        Assert.AreEqual(original + "\n", fixture.Read());

        var assignments = fixture.Store.LoadGlobalSpaceAssignments();
        Assert.AreEqual(0, assignments.Providers.Count);
        Assert.AreEqual(0, assignments.McpServers.Count);
    }

    [TestMethod]
    public void WritingTheSpacesOfASkillOrAPrompt_AddsTheirMapWhenItIsMissing()
    {
        using var fixture = new Fixture();
        fixture.Write("[skills]\r\ndisabled = [\"old\"]\r\n");

        fixture.Store.SaveGlobalSpaceAssignment(SpaceResourceKind.Skill, "terraform", ["business"]);
        fixture.Store.SaveGlobalSpaceAssignment(SpaceResourceKind.Skill, "my.skill", ["personal", "default"]);
        fixture.Store.SaveGlobalSpaceAssignment(SpaceResourceKind.Prompt, "reviewer", ["business", "default"]);
        fixture.Store.SaveGlobalSpaceAssignment(SpaceResourceKind.Plugin, "jira", ["business"]);

        Assert.AreEqual(
            "[skills]\r\ndisabled = [\"old\"]\r\n\r\n[skills.spaces]\r\nterraform = [\"business\"]\r\n\"my.skill\" = [\"personal\", \"default\"]\r\n"
            + "\r\n[prompts.spaces]\r\nreviewer = [\"business\", \"default\"]\r\n\r\n[plugins.jira]\r\nspaces = [\"business\"]\r\n",
            fixture.Read(), "The line ends of the file are kept.");
        var assignments = fixture.Store.LoadGlobalSpaceAssignments();
        CollectionAssert.AreEqual(new[] { "personal", "default" }, assignments.Of(SpaceResourceKind.Skill, "my.skill").ToArray());
        CollectionAssert.AreEqual(new[] { "business", "default" }, assignments.Of(SpaceResourceKind.Prompt, "reviewer").ToArray());
        CollectionAssert.AreEqual(new[] { "business" }, assignments.Of(SpaceResourceKind.Plugin, "jira").ToArray());
        CollectionAssert.AreEqual(new[] { "old" }, fixture.Store.LoadGlobalDisabledSkillNames().ToArray(), "The other settings of the table are read as before.");
    }

    [TestMethod]
    public void AProviderThatTheFileDoesNotDefine_IsNotGivenATable_AndWrongValuesAreRefused()
    {
        using var fixture = new Fixture();
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.SaveGlobalSpaceAssignment(SpaceResourceKind.Provider, "copilot", ["business"]));
        Assert.IsFalse(File.Exists(fixture.Options.ConfigPath), "Nothing was written.");
        Assert.ThrowsExactly<ArgumentException>(() => fixture.Store.SaveGlobalSpaceAssignment(SpaceResourceKind.Skill, "terraform", ["Not An Id"]));

        // A table written inline is not rewritten: the user writes its spaces by hand.
        fixture.Write("plugins = { jira = { enabled = true } }\n");
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Store.SaveGlobalSpaceAssignment(SpaceResourceKind.Plugin, "jira", ["business"]));
        Assert.AreEqual("plugins = { jira = { enabled = true } }\n", fixture.Read());

        // Removing what is not written changes nothing.
        fixture.Store.SaveGlobalSpaceAssignment(SpaceResourceKind.Prompt, "reviewer", []);
        Assert.AreEqual("plugins = { jira = { enabled = true } }\n", fixture.Read());
    }

    [TestMethod]
    public void TheSpacesOfAProvider_SurviveASaveOfTheProviderDefinitions()
    {
        using var fixture = new Fixture();
        fixture.Write("[providers.codex]\ntype = \"codex\"\nspaces = [\"business\"]\n");
        var definitions = fixture.Store.LoadGlobalProviderDefinitions(includeDisabled: true);
        fixture.Store.SaveGlobalProviderDefinitions(definitions);
        CollectionAssert.AreEqual(new[] { "business" }, fixture.Store.LoadGlobalSpaceAssignments().Of(SpaceResourceKind.Provider, "codex").ToArray());
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "codealta-space-assignments-" + Guid.NewGuid().ToString("N"));

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Options = new CatalogOptions { GlobalRoot = Root };
            Store = new CodeAltaConfigStore(Options);
        }

        public CatalogOptions Options { get; }

        public CodeAltaConfigStore Store { get; }

        public void Write(string content) => File.WriteAllText(Options.ConfigPath, content);

        public string Read() => File.ReadAllText(Options.ConfigPath);

        public void Dispose() => Directory.Delete(Root, true);
    }
}
