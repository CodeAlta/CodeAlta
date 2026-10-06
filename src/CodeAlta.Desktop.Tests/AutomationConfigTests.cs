using CodeAlta.Agent;
using CodeAlta.Desktop.Automations;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class AutomationConfigTests
{
    private const string First = "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d77";
    private const string Second = "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d78";
    private static readonly AutomationSource Global = new("config.toml", null, null);
    private static readonly AutomationSource Project = new("project/.alta/config.toml", "project-id", "project");

    [TestMethod]
    public void Read_TakesEveryPartOfAnAutomation()
    {
        var (definitions, faults) = AutomationConfig.Read($$""""
            [chat]
            default_provider = "codex"

            [automations.{{First}}]
            name = " Issue triage "
            enabled = false
            project = "C:/code/CodeAlta"
            model = "codex:gpt-6.1-sol@high"
            agent = "plan"
            catch_up = true
            triggers = [
              { type = "hourly", minute = 15, every = 2 },
              { type = "daily", at = ["17:30", "9:00", "09:00"] },
              { type = "weekly", days = ["thu", "Monday"], at = "08:30" },
              { type = "cron", expression = "0   9 * * 1-5" },
              { type = "issue" },
              { type = "pull_request", event = "updated", authors = "anyone" },
            ]
            prompt = """
            Triage the issues opened since the last run.
              Keep "quotes" and \\ as they are.
            """
            """", Global);

        Assert.AreEqual(0, faults.Count, string.Join("; ", faults.Select(static fault => fault.Message)));
        var automation = definitions.Single();
        Assert.AreEqual(First, automation.Id);
        Assert.AreEqual("Issue triage", automation.Name);
        Assert.IsFalse(automation.Enabled);
        Assert.AreEqual("C:/code/CodeAlta", automation.Project);
        Assert.AreEqual(new AutomationModelRef("codex", "gpt-6.1-sol", AgentReasoningEffort.High), automation.Model);
        Assert.AreEqual("plan", automation.Agent);
        Assert.IsTrue(automation.CatchUp);
        Assert.AreEqual("Triage the issues opened since the last run.\n  Keep \"quotes\" and \\ as they are.", automation.Prompt);
        CollectionAssert.AreEqual(
            new[] { "hourly:15/2", "daily:09:00,17:30", "weekly:mon,thu@08:30", "cron:0 9 * * 1-5", "issue:opened:trusted", "pull_request:updated:anyone" },
            automation.Triggers.Select(static trigger => trigger.Key).ToArray());
    }

    [TestMethod]
    public void Read_ReportsAnAutomationWrittenWrongAndKeepsTheOthers()
    {
        var (definitions, faults) = AutomationConfig.Read($$"""
            [automations.{{First}}]
            name = "Runs by hand"
            prompt = "Review the repository."

            [automations.{{Second}}]
            name = "No prompt"

            [automations.nightly]
            name = "Not a GUID"
            prompt = "x"

            [automations.0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d79]
            name = "Wrong time"
            prompt = "x"
            triggers = [{ type = "daily", at = ["25:00"] }]

            [automations.0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d7a]
            name = "Wrong kind"
            prompt = "x"
            triggers = [{ type = "yearly" }]

            [automations.0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d7b]
            name = "Wrong value"
            prompt = "x"
            enabled = "yes"
            """, Project);

        Assert.AreEqual("Runs by hand", definitions.Single().Name);
        Assert.AreEqual(0, definitions[0].Triggers.Count);
        Assert.IsTrue(definitions[0].Enabled);
        CollectionAssert.AreEqual(
            new[] { Second, "nightly", "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d79", "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d7a", "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d7b" },
            faults.Select(static fault => fault.Key).ToArray());
        StringAssert.Contains(faults[0].Message, "prompt");
        StringAssert.Contains(faults[1].Message, "GUID");
        StringAssert.Contains(faults[2].Message, "25:00");
        StringAssert.Contains(faults[3].Message, "yearly");
        StringAssert.Contains(faults[4].Message, "enabled");
        Assert.IsTrue(faults.All(static fault => fault.Source == Project));
    }

    [TestMethod]
    public void Read_OfAFileThatCannotBeParsedOrHasNoAutomations()
    {
        Assert.AreEqual((0, 0), Count(AutomationConfig.Read("[chat]\ndefault_provider = \"codex\"\n", Global)));
        Assert.AreEqual((0, 0), Count(AutomationConfig.Read(string.Empty, Global)));
        var (definitions, faults) = AutomationConfig.Read($"[automations.{First}]\nname = \"unterminated\n", Global);
        Assert.AreEqual(0, definitions.Count);
        Assert.AreEqual(string.Empty, faults.Single().Key);
        StringAssert.Contains(faults[0].Message, "cannot be read");
        Assert.AreEqual(1, AutomationConfig.Read("automations = 3\n", Global).Faults.Count);

        // The project of a project's own file is the folder of that file.
        var project = AutomationConfig.Read($"[automations.{First}]\nname = \"n\"\nprompt = \"p\"\nproject = \"elsewhere\"\n", Project);
        Assert.IsNull(project.Definitions.Single().Project);

        static (int, int) Count((IReadOnlyList<AutomationDefinition> Definitions, IReadOnlyList<AutomationFault> Faults) read) => (read.Definitions.Count, read.Faults.Count);
    }

    [TestMethod]
    public void Write_AddsAnAutomationAtTheEndAndReadsItBackTheSame()
    {
        var definition = new AutomationDefinition(First, "Weekly \"changelog\"")
        {
            Enabled = false,
            Project = "C:/code/CodeAlta",
            Model = new("copilot", null, AgentReasoningEffort.Low),
            Agent = "plan",
            CatchUp = true,
            Triggers =
            [
                new(AutomationTriggerKind.Weekly) { Days = [DayOfWeek.Monday, DayOfWeek.Friday], At = [new(9, 0), new(17, 5)] },
                new(AutomationTriggerKind.Hourly) { Minute = 5, Every = 6 },
                new(AutomationTriggerKind.Cron) { Expression = "*/30 * * * *" },
                new(AutomationTriggerKind.PullRequest) { Event = "updated", Authors = AutomationAuthors.Anyone },
            ],
            Prompt = "Draft the changelog.\n\tKeep C:\\paths and \"quotes\".",
        };
        Assert.IsNull(AutomationConfig.Validate(definition));

        var existing = "# My settings\r\n[chat]\r\ndefault_provider = \"codex\" # kept\r\n";
        var written = AutomationConfig.Write(existing, definition);
        Assert.IsTrue(written.StartsWith(existing + "\r\n", StringComparison.Ordinal), "What the file held stays as it is, a blank line apart.");
        Assert.IsFalse(written.Replace("\r\n", string.Empty, StringComparison.Ordinal).Contains('\n'), "The file keeps its line endings.");
        StringAssert.Contains(written, "prompt = '''\r\nDraft the changelog.\r\n\tKeep C:\\paths and \"quotes\".\r\n'''");

        var (definitions, faults) = AutomationConfig.Read(written, Global);
        Assert.AreEqual(0, faults.Count, string.Join("; ", faults.Select(static fault => fault.Message)));
        AssertSame(definition, definitions.Single());

        // In a file that does not exist yet, and with a prompt that holds what a literal string cannot.
        var quoted = definition with { Prompt = "Say '''hello''' and \"\"\"bye\"\"\" with a \\ mark." };
        var alone = AutomationConfig.Write(string.Empty, quoted);
        Assert.IsTrue(alone.StartsWith($"[automations.{First}]\n", StringComparison.Ordinal));
        AssertSame(quoted, AutomationConfig.Read(alone, Global).Definitions.Single());

        // A folder of Windows is written with the slashes every system reads, which need no escape.
        var folder = AutomationConfig.Write(string.Empty, definition with { Project = @"C:\code\CodeAlta" });
        StringAssert.Contains(folder, OperatingSystem.IsWindows() ? "project = \"C:/code/CodeAlta\"" : "project = \"C:\\\\code\\\\CodeAlta\"");
    }

    [TestMethod]
    public void Write_ReplacesOnlyTheTextOfItsAutomation()
    {
        var before = $"""
            # Providers
            [providers.codex]
            enabled = true # the one I use

            # Automations of the team
            [automations.{First}]
            name = "Old name"   # renamed below
            prompt = "old"
            unknown = 1

            [[automations.{First}.triggers]]
            type = "daily"
            at = "09:00"

            # The second one stays
            [automations.{Second}]
            name = "Second"
            prompt = "second"

            [skills]
            disabled = ["x"]

            """.ReplaceLineEndings("\n");
        Assert.AreEqual("daily:09:00", AutomationConfig.Read(before, Global).Definitions[0].Triggers.Single().Key, "A trigger may be a table of its own.");

        var after = AutomationConfig.Write(before, new AutomationDefinition(First, "New name") { Prompt = "new" });
        Assert.AreEqual($"""
            # Providers
            [providers.codex]
            enabled = true # the one I use

            # Automations of the team
            [automations.{First}]
            name = "New name"
            enabled = true
            prompt = '''
            new
            '''

            # The second one stays
            [automations.{Second}]
            name = "Second"
            prompt = "second"

            [skills]
            disabled = ["x"]

            """.ReplaceLineEndings("\n"), after);

        var removed = AutomationConfig.Remove(after, Second);
        Assert.AreEqual($"""
            # Providers
            [providers.codex]
            enabled = true # the one I use

            # Automations of the team
            [automations.{First}]
            name = "New name"
            enabled = true
            prompt = '''
            new
            '''

            # The second one stays

            [skills]
            disabled = ["x"]

            """.ReplaceLineEndings("\n"), removed);
        Assert.AreSame(removed, AutomationConfig.Remove(removed, Second), "Nothing to remove changes nothing.");
        Assert.AreEqual(1, AutomationConfig.Read(removed, Global).Definitions.Count);
    }

    [TestMethod]
    public void Write_RefusesAFileItCannotEditSafely()
    {
        var definition = new AutomationDefinition(First, "Name") { Prompt = "prompt" };
        Assert.Throws<InvalidDataException>(() => AutomationConfig.Write("[chat\n", definition));
        // Written as an inline table: there is no table of its own whose text could be replaced.
        var inline = $"[automations]\n{First} = {{ name = \"Inline\", prompt = \"p\" }}\n";
        Assert.AreEqual("Inline", AutomationConfig.Read(inline, Global).Definitions.Single().Name);
        Assert.Throws<InvalidDataException>(() => AutomationConfig.Write(inline, definition));
        Assert.Throws<InvalidDataException>(() => AutomationConfig.Remove(inline, First));
    }

    [TestMethod]
    public void Validate_NamesWhatIsWrong()
    {
        var valid = new AutomationDefinition(First, "Name") { Prompt = "prompt" };
        Assert.IsNull(AutomationConfig.Validate(valid));
        Assert.IsNotNull(AutomationConfig.Validate(valid with { Id = "not-a-guid" }));
        Assert.IsNotNull(AutomationConfig.Validate(valid with { Id = First.ToUpperInvariant() }));
        Assert.IsNotNull(AutomationConfig.Validate(valid with { Name = " " }));
        Assert.IsNotNull(AutomationConfig.Validate(valid with { Name = new string('n', AutomationDefinition.MaximumNameLength + 1) }));
        Assert.IsNotNull(AutomationConfig.Validate(valid with { Name = "two\nlines" }));
        Assert.IsNotNull(AutomationConfig.Validate(valid with { Prompt = "" }));
        Assert.IsNotNull(AutomationConfig.Validate(valid with { Prompt = new string('p', AutomationDefinition.MaximumPromptLength + 1) }));
        Assert.IsNotNull(AutomationConfig.Validate(valid with { Prompt = "half \ud83d" }));
        Assert.IsNotNull(AutomationConfig.Validate(valid with { Triggers = [new(AutomationTriggerKind.Cron) { Expression = "nope" }] }));
        Assert.IsNotNull(AutomationConfig.Validate(valid with { Triggers = [new(AutomationTriggerKind.Issue) { Event = "updated" }] }));
        Assert.IsNotNull(AutomationConfig.Validate(valid with { Triggers = [new(AutomationTriggerKind.Daily) { At = [new(9, 0)] }, new(AutomationTriggerKind.Daily) { At = [new(9, 0)] }] }));
        Assert.IsNotNull(AutomationConfig.Validate(valid with { Triggers = [.. Enumerable.Range(0, AutomationDefinition.MaximumTriggers + 1).Select(static minute => new AutomationTrigger(AutomationTriggerKind.Hourly) { Minute = minute })] }));
    }

    [TestMethod]
    public void ModelRef_ReadsEachPartAndWritesItBack()
    {
        Assert.IsTrue(AutomationModelRef.TryParse(null, out var none, out _));
        Assert.IsTrue(none.IsEmpty);
        Assert.IsNull(none.Format());
        foreach (var text in new[] { "codex", "codex:gpt-6.1-sol", "codex:gpt-6.1-sol@high", "codex@low", "local:org/model:tag@xhigh" })
        {
            Assert.IsTrue(AutomationModelRef.TryParse(" " + text + " ", out var reference, out var error), error);
            Assert.AreEqual(text, reference.Format());
        }

        Assert.AreEqual(new AutomationModelRef("local", "org/model:tag", AgentReasoningEffort.XHigh), Parse("local:org/model:tag@xhigh"));
        foreach (var text in new[] { ":model", "codex:", "codex@fast", "codex@2", "@high" })
            Assert.IsFalse(AutomationModelRef.TryParse(text, out _, out var error), text + " " + error);

        static AutomationModelRef Parse(string text) => AutomationModelRef.TryParse(text, out var reference, out _) ? reference : default;
    }

    [TestMethod]
    public void ModelRef_KeepsAMarkThatBelongsToTheNameOfTheModel()
    {
        Assert.IsTrue(AutomationModelRef.TryParse("vertex:claude-sonnet-4@20250514", out var plain, out _));
        Assert.AreEqual(("vertex", "claude-sonnet-4@20250514", null), (plain.Provider, plain.Model, plain.Effort));
        Assert.AreEqual("vertex:claude-sonnet-4@20250514", plain.Format());
        Assert.IsTrue(AutomationModelRef.TryParse("vertex:claude-sonnet-4@20250514@high", out var effort, out _));
        Assert.AreEqual(("vertex", "claude-sonnet-4@20250514", AgentReasoningEffort.High), (effort.Provider, effort.Model, effort.Effort));
        Assert.AreEqual("vertex:claude-sonnet-4@20250514@high", effort.Format());
        Assert.IsTrue(AutomationModelRef.TryParse("codex@HIGH", out var provider, out _));
        Assert.AreEqual(("codex", null, AgentReasoningEffort.High), (provider.Provider, provider.Model, provider.Effort));
        // Without a model there is nothing the mark could belong to but an effort.
        Assert.IsFalse(AutomationModelRef.TryParse("codex@extreme", out _, out var error));
        Assert.AreEqual("'extreme' is not a reasoning effort.", error);

        // Written in a file, such a model is read back as it was written.
        var definition = new AutomationDefinition(First, "Vertex") { Prompt = "p", Model = plain };
        Assert.AreEqual(plain, AutomationConfig.Read(AutomationConfig.Write(string.Empty, definition), Global).Definitions.Single().Model);
    }

    [TestMethod]
    public void SavingOtherSettings_KeepsTheAutomationsOfTheFile()
    {
        // The settings of the application are saved through a model of the file that does not know the automations.
        var root = Directory.CreateTempSubdirectory("codealta-automation-config-").FullName;
        try
        {
            var definition = new AutomationDefinition(First, "Nightly \"review\"")
            {
                Project = "C:/code/CodeAlta",
                Model = new("codex", "gpt", AgentReasoningEffort.High),
                CatchUp = true,
                Triggers = [new(AutomationTriggerKind.Daily) { At = [new(9, 0)] }, new(AutomationTriggerKind.Issue) { Authors = AutomationAuthors.Anyone }],
                Prompt = "Review what changed.\nKeep C:\\paths, 'quotes' and base_uri = \"x\" as they are.",
            };
            var options = new CodeAlta.Catalog.CatalogOptions { GlobalRoot = root };
            File.WriteAllText(options.ConfigPath, AutomationConfig.Write("[chat]\ndefault_provider = \"codex\"\n\n[providers.codex]\nenabled = true\ntype = \"codex\"\n", definition));
            var store = new CodeAlta.Catalog.CodeAltaConfigStore(options);

            store.SaveGlobalDefaultProvider("copilot");
            store.SaveGlobalPluginEnabled("statistics", false);

            var saved = File.ReadAllText(options.ConfigPath);
            StringAssert.Contains(saved, "copilot");
            var (definitions, faults) = AutomationConfig.Read(saved, Global);
            Assert.AreEqual(0, faults.Count, string.Join("; ", faults.Select(static fault => fault.Message)));
            AssertSame(definition, definitions.Single());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertSame(AutomationDefinition expected, AutomationDefinition actual)
    {
        Assert.AreEqual(expected with { Triggers = [] }, actual with { Triggers = [] });
        CollectionAssert.AreEqual(expected.Triggers.Select(static trigger => trigger.Key).ToArray(), actual.Triggers.Select(static trigger => trigger.Key).ToArray());
    }
}
