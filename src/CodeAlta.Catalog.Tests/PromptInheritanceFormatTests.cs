namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class PromptInheritanceFormatTests
{
    [TestMethod]
    public void ThePromptsAnAgentPromptInherits_AreReadAsAFlowListABlockListOrIdsSeparatedByCommas()
    {
        static string[] Read(string text) => [.. PromptFileFormat.Parse(PromptResourceKind.Agent, text).Inherits];
        CollectionAssert.AreEqual(new[] { "default", "maui" }, Read("---\nname: A\ninherit: [default, \"maui\", Default]\n---\nBody\n"));
        CollectionAssert.AreEqual(new[] { "plan", "maui" }, Read("---\nname: A\ninherit: plan, maui, not/an/id\n---\nBody\n"));
        CollectionAssert.AreEqual(new[] { "default", "maui" }, Read("---\ninherit:\n  - default\n  - maui\nname: A\n---\nBody\n"));
        // A block list of `disabled_tools` is read the same way, and the keys after it stay.
        var content = PromptFileFormat.Parse(PromptResourceKind.Agent, "---\ndisabled_tools:\n  - webget\n  - shell_command\nname: A\nsystem: custom\n---\nBody\n");
        CollectionAssert.AreEqual(new[] { "webget", "shell_command" }, content.DisabledTools.ToArray());
        Assert.AreEqual("custom", content.SystemPromptName);
        Assert.AreEqual(0, Read("---\nname: A\n---\nBody\n").Length);
    }

    [TestMethod]
    public void ThePromptsAnAgentPromptInherits_SurviveASaveOfItsFile()
    {
        var content = new PromptFileContent("MAUI Default", null, null, "Own.", false) { Inherits = ["default", "maui"] };
        var text = PromptFileFormat.Serialize(PromptResourceKind.Agent, content);
        StringAssert.Contains(text, "inherit: [default, maui]");
        CollectionAssert.AreEqual(new[] { "default", "maui" }, PromptFileFormat.Parse(PromptResourceKind.Agent, text).Inherits.ToArray());
        Assert.IsFalse(PromptFileFormat.Serialize(PromptResourceKind.Agent, content with { Inherits = [] }).Contains("inherit", StringComparison.Ordinal));
        Assert.ThrowsExactly<ArgumentException>(() => PromptFileFormat.Validate(PromptResourceKind.Agent, content with { Inherits = ["../escape"] }));
        Assert.ThrowsExactly<ArgumentException>(() => PromptFileFormat.Validate(PromptResourceKind.Agent,
            content with { Inherits = [.. Enumerable.Range(0, PromptFileFormat.MaximumInherits + 1).Select(static index => $"p{index}")] }));
    }
}
