using CodeAlta.Catalog.Issues;
using Tomlyn;
using Tomlyn.Model;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class IssueViewStoreTests
{
    [TestMethod]
    public void TheViewsOfAProject_AreTheUsersThenTheTeams_AndTheCurrentOneIsTheUsersChoice()
    {
        var project = Path.Combine(Path.GetTempPath(), "alta-issue-views-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(project, ".alta", "local"));
            File.WriteAllText(Path.Combine(project, ".alta", "issues.toml"), """
                [azure_devops.views."Team Core".team]
                value = "Core"

                [azure_devops.views."Team Core"."System.State"]
                mode = "off"
                value = ["Ready", "Active"]

                [azure_devops.views."Team Core".sort]

                [azure_devops.views."Team Core".sprint]
                value = "@current"
                pick = true
                """);
            File.WriteAllText(Path.Combine(project, ".alta", "local", "issues.toml"), """
                [azure_devops]
                current = "Bugs"

                [azure_devops.views.Mine.assigned_to]
                mode = "anything"
                value = "@me"
                label = "Wer"

                [azure_devops.views.Bugs.type]
                value = "Bug"
                """);
            var store = new IssueViewStore();

            var views = store.ReadViews("azure_devops", project);

            CollectionAssert.AreEqual(new[] { "Mine:Personal", "Bugs:Personal", "Team Core:Team" }, views.Select(static view => $"{view.Name}:{view.Source}").ToArray());
            Assert.AreEqual(new IssueFilterSetting("assigned_to", true, "@me", "Wer"), views[0].Filters.Filters.Single(), "A mode other than off is on.");
            Assert.AreEqual(new IssueFilterSetting("field:System.State", false, "Ready\u001FActive"), views[2].Filters.Find("field:System.State"),
                "A field is written by its name and read as a filter of fields; several values are a list; off keeps its value.");
            Assert.AreEqual(new IssueFilterSetting("sort"), views[2].Filters.Find("sort"), "A table alone names an active filter of every item.");
            Assert.AreEqual(new IssueFilterSetting("sprint", true, "@current", Pick: true), views[2].Filters.Find("sprint"), "The tab may change the value of this one.");
            Assert.AreEqual("Bugs", store.ReadCurrent("azure_devops", project)!.Name);
            Assert.AreEqual(0, store.ReadViews("jira", project).Count, "Each service has its own views.");

            store.SetCurrent("azure_devops", project, "Team Core");
            Assert.AreEqual((IssueViewSource.Team, "Team Core"), (store.ReadCurrent("azure_devops", project)!.Source, store.ReadCurrent("azure_devops", project)!.Name));
            store.SetCurrent("azure_devops", project, "Gone");
            Assert.AreEqual("Mine", store.ReadCurrent("azure_devops", project)!.Name, "A choice no view answers falls back to the user's first view.");
        }
        finally { if (Directory.Exists(project)) Directory.Delete(project, true); }
    }

    [TestMethod]
    public void SaveRenameRemove_WriteTheFileOfTheView_AndTheUsersFolderIsKeptOutOfGit()
    {
        var project = Path.Combine(Path.GetTempPath(), "alta-issue-views-save-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(project);
            var store = new IssueViewStore();

            store.SaveView(IssueViewSource.Personal, "azure_devops", project, "Meine", new([
                new("team", true, "O'Brien \"Core\""),
                new("field:System.State", false, "Ready\u001FActive", "Status"),
                new("sort"),
                new("sprint", true, "@current", Pick: true),
            ]));
            store.SaveView(IssueViewSource.Personal, "azure_devops", project, "Leer", IssueFilterSettings.Empty);
            store.SetCurrent("azure_devops", project, "Meine");

            var file = Path.Combine(project, ".alta", "local", "issues.toml");
            var text = File.ReadAllText(file);
            StringAssert.Contains(text, "[azure_devops.views.Meine.\"System.State\"]");
            StringAssert.Contains(text, "value = [\"Ready\", \"Active\"]");
            StringAssert.Contains(text, "mode = \"off\"");
            Assert.AreEqual(1, text.Split("mode =").Length - 1, "Only a filter that is off says its mode.");
            StringAssert.Contains(text, "[azure_devops.views.Meine.sort]", "A filter without a value is a table of its own.");
            Assert.AreEqual(1, text.Split("pick = true").Length - 1, "Only a filter the tab may change says so.");
            Assert.AreEqual("*\n", File.ReadAllText(Path.Combine(project, ".alta", "local", ".gitignore")));
            Assert.IsFalse(File.Exists(Path.Combine(project, ".alta", "issues.toml")), "The team's file is not written for the user's view.");
            var views = store.ReadViews("azure_devops", project);
            CollectionAssert.AreEqual(new[] { "Meine", "Leer" }, views.Select(static view => view.Name).ToArray(), "An empty view is a view.");
            Assert.AreEqual(new IssueFilterSetting("team", true, "O'Brien \"Core\""), views[0].Filters.Find("team"));
            CollectionAssert.AreEqual(new[] { "team", "field:System.State", "sort", "sprint" }, views[0].Filters.Filters.Select(static filter => filter.Id).ToArray());
            Assert.IsTrue(views[0].Filters.Find("sprint")!.Pick);

            store.SaveView(IssueViewSource.Personal, "azure_devops", project, "Meine", new([new("sprint", true, "@current")]));
            Assert.AreEqual(new IssueFilterSetting("sprint", true, "@current"), store.ReadCurrent("azure_devops", project)!.Filters.Filters.Single(),
                "What the view said before is replaced.");

            Assert.IsTrue(store.RenameView(IssueViewSource.Personal, "azure_devops", project, "Meine", "Mein Sprint"));
            Assert.AreEqual("Mein Sprint", store.ReadCurrent("azure_devops", project)!.Name, "The choice follows the view.");
            Assert.ThrowsExactly<IssueViewNameTakenException>(() => store.RenameView(IssueViewSource.Personal, "azure_devops", project, "Mein Sprint", "Leer"));
            Assert.IsTrue(store.RemoveView(IssueViewSource.Personal, "azure_devops", project, "Leer"));
            Assert.IsFalse(store.RemoveView(IssueViewSource.Personal, "azure_devops", project, "Leer"));
            CollectionAssert.AreEqual(new[] { "Mein Sprint" }, store.ReadViews("azure_devops", project).Select(static view => view.Name).ToArray());
            store.SaveView(IssueViewSource.Personal, "azure_devops", project, "Kurz", IssueFilterSettings.Empty);
            store.SetCurrent("azure_devops", project, "Kurz");
            Assert.IsTrue(store.RemoveView(IssueViewSource.Personal, "azure_devops", project, "Kurz"));
            Assert.IsFalse(File.ReadAllText(Path.Combine(project, ".alta", "local", "issues.toml")).Contains("Kurz", StringComparison.Ordinal), "The choice of a view that is gone is forgotten.");
            Assert.AreEqual("Mein Sprint", store.ReadCurrent("azure_devops", project)!.Name);

            store.SaveView(IssueViewSource.Team, "azure_devops", project, "Team", new([new("sort", true, "backlog")]));
            Assert.AreEqual(IssueViewSource.Team, store.ReadViews("azure_devops", project).Last().Source);
            Assert.IsTrue(File.Exists(Path.Combine(project, ".alta", "issues.toml")));
            Assert.ThrowsExactly<IssueViewNameTakenException>(() => store.SaveView(IssueViewSource.Personal, "azure_devops", project, "Team", IssueFilterSettings.Empty),
                "A name is one view: the user's view does not take the name of the team's.");
            Assert.ThrowsExactly<IssueViewNameTakenException>(() => store.RenameView(IssueViewSource.Team, "azure_devops", project, "Team", "Mein Sprint"));
            Assert.ThrowsExactly<ArgumentException>(() => store.SaveView(IssueViewSource.Personal, "azure_devops", project, "a\nb", IssueFilterSettings.Empty));
            Assert.ThrowsExactly<ArgumentException>(() => store.SaveView(IssueViewSource.Personal, "azure_devops", project, "X", new([new("team"), new("team")])));
        }
        finally { if (Directory.Exists(project)) Directory.Delete(project, true); }
    }

    [TestMethod]
    public void AFileThatCannotBeRead_HasNoViews()
    {
        var project = Path.Combine(Path.GetTempPath(), "alta-issue-views-invalid-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(project, ".alta"));
            File.WriteAllText(Path.Combine(project, ".alta", "issues.toml"), "[azure_devops.views\nfilters = 3");

            Assert.AreEqual(0, new IssueViewStore().ReadViews("azure_devops", project).Count);
            Assert.IsNull(new IssueViewStore().ReadCurrent("azure_devops", project));
        }
        finally { if (Directory.Exists(project)) Directory.Delete(project, true); }
    }

    [TestMethod]
    public void RemoveTable_RemovesTheHeaderAndItsSettings_Only()
    {
        const string text = "[a]\nx = 1\n\n[a.b]\ny = 2 # kept with it\n\n# Of c.\n[c]\nz = 3\n";

        Assert.AreEqual("[a]\nx = 1\n\n# Of c.\n[c]\nz = 3\n", ConfigTableSyntax.RemoveTable(text, ["a", "b"]));
        Assert.AreEqual("[a]\nx = 1\n\n[a.b]\ny = 2 # kept with it\n\n# Of c.\n", ConfigTableSyntax.RemoveTable(text, ["c"]));
        Assert.AreEqual(text, ConfigTableSyntax.RemoveTable(text, ["d"]));
        Assert.AreEqual("# Of c.\n[c]\nz = 3\n", ConfigTableSyntax.RemoveTables(text, ["a"]), "A table and every table under it.");
        Assert.AreEqual(text + "\n[d.e]\n", ConfigTableSyntax.EnsureTable(text, ["d", "e"]));
        Assert.AreEqual(text, ConfigTableSyntax.EnsureTable(text, ["a", "b"]));
    }

    [TestMethod]
    public void SaveAndRename_KeepTheViewInItsPlace_AndAValueLongerThanTheFileKeepsIsRefused()
    {
        var project = Path.Combine(Path.GetTempPath(), "alta-issue-views-order-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(project);
            var store = new IssueViewStore();
            store.SaveView(IssueViewSource.Team, "azure_devops", project, "A", new([new("team", true, "Core")]));
            store.SaveView(IssueViewSource.Team, "azure_devops", project, "B", new([new("sort", true, "backlog")]));
            var file = Path.Combine(project, ".alta", "issues.toml");
            File.WriteAllText(file, File.ReadAllText(file) + "\n# The views of the next release.\n[azure_devops.views.C]\n");

            store.SaveView(IssueViewSource.Team, "azure_devops", project, "A", new([new("team", true, "Mobile"), new("sprint", true, "@current")]));
            CollectionAssert.AreEqual(new[] { "A", "B", "C" }, store.ReadViews("azure_devops", project).Select(static view => view.Name).ToArray(), "A saved view stays where it was.");
            Assert.IsTrue(store.RenameView(IssueViewSource.Team, "azure_devops", project, "A", "Z"));
            CollectionAssert.AreEqual(new[] { "Z", "B", "C" }, store.ReadViews("azure_devops", project).Select(static view => view.Name).ToArray(), "A renamed view too.");
            Assert.AreEqual(new IssueFilterSetting("team", true, "Mobile"), store.ReadViews("azure_devops", project)[0].Filters.Find("team"));
            StringAssert.Contains(File.ReadAllText(file), "# The views of the next release.\n[azure_devops.views.C]", "A comment of another view stays with it.");

            Assert.ThrowsExactly<ArgumentException>(() => store.SaveView(IssueViewSource.Team, "azure_devops", project, "B", new([new("team", true, new string('x', IssueViewStore.MaximumText + 1))])));
            Assert.ThrowsExactly<ArgumentException>(() => store.SaveView(IssueViewSource.Team, "azure_devops", project, "B", new([new("team", true, "Core", new string('x', IssueViewStore.MaximumText + 1))])));
            Assert.IsTrue(IssueViewStore.IsFilterText(string.Join(IssueViewStore.ValueSeparator, Enumerable.Repeat(new string('x', IssueViewStore.MaximumText), 3))), "Each value of several may be as long.");
        }
        finally { if (Directory.Exists(project)) Directory.Delete(project, true); }
    }

    [TestMethod]
    public void TheViewsOfJira_AreWrittenBesideItsSettings_WithoutTouchingThem()
    {
        var project = Path.Combine(Path.GetTempPath(), "alta-issue-views-jira-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(project, ".alta", "local"));
            var team = Path.Combine(project, ".alta", "issues.toml");
            var local = Path.Combine(project, ".alta", "local", "issues.toml");
            File.WriteAllText(team, "[jira]\nsite = \"example.atlassian.net\"\nproject = \"ALTA\"\nprojects = [\"PLAT\"]\n");
            File.WriteAllText(local, "[jira]\nemail = \"me@example.com\"\ntoken_env = \"MY_JIRA_TOKEN\"\n");
            var store = new IssueViewStore();

            store.SaveView(IssueViewSource.Team, "jira", project, "Board", new([new("status", true, "To Do")]));
            store.SaveView(IssueViewSource.Personal, "jira", project, "Mine", new([new("assignee", true, "@me")]));
            store.SetCurrent("jira", project, "Mine");
            Assert.IsTrue(store.RenameView(IssueViewSource.Team, "jira", project, "Board", "Sprint"));
            store.SaveView(IssueViewSource.Team, "jira", project, "Sprint", new([new("status", false, "Done")]));
            Assert.IsTrue(store.RemoveView(IssueViewSource.Personal, "jira", project, "Mine"));

            static TomlTable Jira(string path) => (TomlTable)TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path))!["jira"];
            var teamJira = Jira(team);
            Assert.AreEqual(("example.atlassian.net", "ALTA"), ((string)teamJira["site"], (string)teamJira["project"]), "The settings of Jira stay as they were.");
            CollectionAssert.AreEqual(new[] { "PLAT" }, ((TomlArray)teamJira["projects"]).OfType<string>().ToArray());
            Assert.AreEqual(("me@example.com", "MY_JIRA_TOKEN"), ((string)Jira(local)["email"], (string)Jira(local)["token_env"]));
            Assert.IsFalse(Jira(local).ContainsKey("current"), "The choice of a view that is gone is forgotten, the account stays.");
            CollectionAssert.AreEqual(new[] { "Sprint" }, store.ReadViews("jira", project).Select(static view => view.Name).ToArray());
        }
        finally { if (Directory.Exists(project)) Directory.Delete(project, true); }
    }
}
