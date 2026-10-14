namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class DisabledToolsConfigTests
{
    [TestMethod]
    public void TheToolsEverySessionDoesWithout_AreReadAndWrittenInTheGlobalConfiguration()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-disabled-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root });
            Assert.AreEqual(0, store.LoadGlobalDisabledTools().Count, "No file turns nothing off.");
            File.WriteAllText(store.ConfigPath, "# my settings\n[skills]\ndisabled = [\"x\"]\n\n[tools]\ndisabled = [\"shell_command\", \"not a tool\", \"webget\"]\n");
            CollectionAssert.AreEquivalent(new[] { "shell_command", "webget" }, store.LoadGlobalDisabledTools().ToArray(), "What is no tool name is left out.");

            store.SaveGlobalDisabledTools(["shell_command"]);
            CollectionAssert.AreEquivalent(new[] { "shell_command" }, store.LoadGlobalDisabledTools().ToArray());
            CollectionAssert.AreEquivalent(new[] { "x" }, store.LoadGlobalDisabledSkillNames().ToArray(), "The other settings stay.");
            store.SaveGlobalDisabledTools([]);
            Assert.AreEqual(0, store.LoadGlobalDisabledTools().Count);
            Assert.DoesNotContain("[tools]", File.ReadAllText(store.ConfigPath));
            Assert.ThrowsExactly<ArgumentException>(() => store.SaveGlobalDisabledTools(["rm -rf"]));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void TheToolsAnAgentPromptTurnsOff_AreReadFromItsFrontMatter()
    {
        static IReadOnlyList<string> Read(string value) => PromptFileFormat.ParseDisabledTools(new Dictionary<string, string> { ["disabled_tools"] = value });
        CollectionAssert.AreEqual(new[] { "shell_command", "webget" }, Read("[shell_command, \"webget\", shell_command]").ToArray());
        CollectionAssert.AreEqual(new[] { "shell_command" }, Read("shell_command").ToArray());
        CollectionAssert.AreEqual(new[] { "a", "b" }, Read("a, b, not a tool").ToArray());
        Assert.AreEqual(0, PromptFileFormat.ParseDisabledTools(new Dictionary<string, string>()).Count);
    }
}
