using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodeAlta.Plugin.Jira;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.CommandLine;

namespace CodeAlta.Tests;

[TestClass]
public sealed class JiraPluginTests
{
    private const string Issue = """
        {"key":"ALTA-5","fields":{"summary":"A long output is cut","status":{"name":"In Progress","statusCategory":{"key":"indeterminate"}},
          "issuetype":{"name":"Bug"},"priority":{"name":"Medium"},"assignee":{"displayName":"Bo"},"reporter":{"displayName":"Ana"},"labels":["desktop","timeline"],
          "created":"2026-10-07T23:17:18.501+0200","updated":"2026-10-07T23:17:35.008+0200",
          "description":{"type":"doc","version":1,"content":[{"type":"paragraph","content":[{"type":"text","text":"Open the window of a "},{"type":"text","text":"tool call","marks":[{"type":"strong"}]},{"type":"text","text":"."}]}]},
          "comment":{"total":2,"comments":[{"author":{"displayName":"Bo"},"created":"2026-10-07T23:20:00.000+0200","body":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Reproduced."}]}]}}]}}}
        """;

    [TestMethod]
    public void Settings_AreReadFromTheTableOfTheProject_AndNothingElseEnablesJira()
    {
        var settings = JiraSettings.Parse("""
            [plugins.mcp]
            enabled = true

            [plugins.jira]
            site = "https://Example.atlassian.net/"
            project = "alta"
            email = "me@example.com"
            token_env = "MY_TOKEN"
            """);

        Assert.AreEqual(new JiraSettings("example.atlassian.net", "ALTA") { Email = "me@example.com", TokenVariable = "MY_TOKEN" }, settings);
        Assert.AreEqual("https://example.atlassian.net/browse/ALTA-12", settings!.BrowseUrl("ALTA-12"));

        Assert.IsNull(JiraSettings.Parse("[plugins.jira]\nenabled = false\nsite = \"example.atlassian.net\"\nproject = \"ALTA\""), "A project can turn it off.");
        Assert.IsNull(JiraSettings.Parse("[plugins.jira]\nsite = \"example.atlassian.net\""), "Without a project there is nothing to list.");
        Assert.IsNull(JiraSettings.Parse("[plugins.jira]\nsite = \"http://example.atlassian.net\"\nproject = \"ALTA\""), "Only https names a site.");
        Assert.IsNull(JiraSettings.Parse("[plugins.jira]\nsite = \"localhost\"\nproject = \"ALTA\""));
        Assert.IsNull(JiraSettings.Parse("[plugins.jira]\nsite = \"example.atlassian.net\"\nproject = \"AL TA\""));
        Assert.IsNull(JiraSettings.Parse("[plugins]\n"));
        Assert.IsNull(JiraSettings.Parse("not toml ["));
        Assert.IsNull(JiraSettings.Read(Path.Combine(Path.GetTempPath(), "codealta-no-such-project-" + Guid.NewGuid().ToString("N"))));
    }

    [TestMethod]
    public void TheSpace_GivesTheSiteAndTheAccountTheProjectDoesNotWrite_AndWhatTheProjectWritesWins()
    {
        var business = new PluginIdentity { SpaceId = "business", JiraSite = "https://Contoso.atlassian.net", JiraEmail = "alex@contoso.example", JiraTokenVariable = "CONTOSO_JIRA_TOKEN" };

        Assert.AreEqual(new JiraSettings("contoso.atlassian.net", "ALTA") { Email = "alex@contoso.example", TokenVariable = "CONTOSO_JIRA_TOKEN" },
            JiraSettings.Parse("[plugins.jira]\nproject = \"ALTA\"", business));
        Assert.AreEqual(new JiraSettings("example.atlassian.net", "ALTA") { Email = "me@example.com", TokenVariable = "CONTOSO_JIRA_TOKEN" },
            JiraSettings.Parse("[plugins.jira]\nsite = \"example.atlassian.net\"\nproject = \"ALTA\"\nemail = \"me@example.com\"", business));
        Assert.IsNull(JiraSettings.Parse("[plugins.jira]\nsite = \"localhost\"\nproject = \"ALTA\"", business), "A site the project writes wrong is not replaced.");
        Assert.IsNull(JiraSettings.Parse("[plugins.jira]\nproject = \"ALTA\""), "Without a space, a project names its site.");
        Assert.IsNull(JiraSettings.Parse("[plugins.mcp]\nenabled = true", business), "A space does not turn Jira on for a project.");
    }

    [TestMethod]
    public async Task OneUseOfJira_HoldsTheAccountOfTheCli_UntilItIsDone()
    {
        // The CLI has one login: a session of another space waits until the use of this one has run its commands.
        using var root = new TempProject("[plugins.jira]\nsite = \"example.atlassian.net\"\nproject = \"ALTA\"\n");
        var cli = new FakeCli { Account = "example.atlassian.net", Switches = true };
        var plugin = await CreateAsync(cli, root.Path);
        var first = new JiraSettings("example.atlassian.net", "ALTA");
        var second = new JiraSettings("contoso.atlassian.net", "PLAT");

        var use = await plugin.UseAccountAsync(first, default);
        Assert.IsNull(use.Problem);
        var waiting = plugin.UseAccountAsync(second, default).AsTask();
        await Task.Delay(100);
        Assert.IsFalse(waiting.IsCompleted, "The other account waits.");
        Assert.AreEqual("example.atlassian.net", cli.Account);

        use.Dispose();
        using var next = await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsNull(next.Problem);
        Assert.AreEqual("contoso.atlassian.net", cli.Account, "Its turn come, the account is switched.");
        use.Dispose();
    }

    [TestMethod]
    public async Task AnAccountOfAnotherEmail_IsSignedInAgain_WithTheTokenOfTheSpace()
    {
        using var root = new TempProject("[plugins.jira]\nproject = \"ALTA\"\n");
        var cli = new FakeCli { Account = "contoso.atlassian.net" };
        var identity = new PluginIdentity { SpaceId = "business", JiraSite = "contoso.atlassian.net", JiraEmail = "alex@contoso.example", JiraTokenVariable = "CONTOSO_JIRA_TOKEN" };
        var plugin = await CreateAsync(cli, root.Path, name => name == "CONTOSO_JIRA_TOKEN" ? "secret-token" : null, identity);

        // The session of the space finds the Jira of its project on the site of the space.
        var tracker = (await plugin.GetTrackersAsync(root.Path, "session-1", default)).Single();
        Assert.AreEqual("https://contoso.atlassian.net/browse/ALTA", tracker.WebUrl);
        cli.Answers["workitem search"] = _ => new(0, "[" + Issue + "]", string.Empty);
        Assert.IsNull((await tracker.ListAsync(new(TrackedItemKind.Issue, TrackedItemFilter.Open, null, 10), default)).Problem);

        // The CLI was signed in to the site as me@example.com: the account of the space signs in with its token.
        Assert.IsTrue(cli.Runs.Contains("jira auth login --site contoso.atlassian.net --email alex@contoso.example --token"), string.Join("\n", cli.Runs));
        CollectionAssert.AreEqual(new[] { "secret-token\n" }, cli.Inputs);
    }

    [TestMethod]
    public void AProject_CanNameSeveralJiraProjects_AndTheFirstIsWhereNewIssuesGo()
    {
        var one = JiraSettings.Parse("[plugins.jira]\nsite = \"example.atlassian.net\"\nproject = \"ALTA\"")!;
        CollectionAssert.AreEqual(new[] { "ALTA" }, one.Projects.ToArray());
        Assert.IsFalse(one.HasSeveralProjects);
        Assert.AreEqual("https://example.atlassian.net/browse/ALTA", one.IssuesUrl());

        // `project` comes first, whatever the list says; a key is listed once, and what is not a key is left out.
        var several = JiraSettings.Parse("[plugins.jira]\nsite = \"example.atlassian.net\"\nproject = \"alta\"\nprojects = [\"plat\", \"ALTA\", \"TE AM\", 7, \"Team\", \"PLAT\"]")!;
        Assert.AreEqual("ALTA", several.Project);
        CollectionAssert.AreEqual(new[] { "ALTA", "PLAT", "TEAM" }, several.Projects.ToArray());
        Assert.IsTrue(several.HasSeveralProjects && several.Has("PLAT") && !several.Has("OTHER"));
        Assert.AreEqual("https://example.atlassian.net/issues/?jql=project%20in%20%28ALTA%2C%20PLAT%2C%20TEAM%29%20ORDER%20BY%20updated%20DESC", several.IssuesUrl());
        Assert.AreNotEqual(one, several);
        Assert.AreEqual(several, several with { });

        // Without `project`, the first key of the list is the one new issues go to.
        var listed = JiraSettings.Parse("[plugins.jira]\nsite = \"example.atlassian.net\"\nprojects = [\"PLAT\", \"TEAM\"]")!;
        Assert.AreEqual("PLAT", listed.Project);
        CollectionAssert.AreEqual(new[] { "PLAT", "TEAM" }, listed.Projects.ToArray());

        Assert.IsNull(JiraSettings.Parse("[plugins.jira]\nsite = \"example.atlassian.net\"\nprojects = []"), "No Jira project is named.");
        Assert.IsNull(JiraSettings.Parse("[plugins.jira]\nsite = \"example.atlassian.net\"\nproject = \"AL TA\"\nprojects = [\"PLAT\"]"), "A project that is written wrong is not replaced by another.");
        var many = JiraSettings.Parse("[plugins.jira]\nsite = \"example.atlassian.net\"\nprojects = [" + string.Join(", ", Enumerable.Range(0, 40).Select(static index => $"\"P{index}\"")) + "]")!;
        Assert.AreEqual(JiraSettings.MaximumProjects, many.Projects.Count);
    }

    [TestMethod]
    public void ADocumentOfJira_BecomesMarkdown()
    {
        using var document = JsonDocument.Parse("""
            {"type":"doc","version":1,"content":[
              {"type":"heading","attrs":{"level":2},"content":[{"type":"text","text":"Steps"}]},
              {"type":"paragraph","content":[{"type":"text","text":"Run "},{"type":"text","text":"alta --dev","marks":[{"type":"code"}]},{"type":"text","text":" and see "},
                {"type":"text","text":"the docs","marks":[{"type":"link","attrs":{"href":"https://example.com/a(b)"}},{"type":"em"}]},{"type":"hardBreak"},{"type":"text","text":"2 * 3 < 7"}]},
              {"type":"orderedList","content":[{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"First"}]},
                {"type":"bulletList","content":[{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"Nested"}]}]}]}]},
                {"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"Second"}]}]}]},
              {"type":"codeBlock","attrs":{"language":"csharp"},"content":[{"type":"text","text":"var fence = \"```\";"}]},
              {"type":"panel","attrs":{"panelType":"info"},"content":[{"type":"paragraph","content":[{"type":"text","text":"Noted by "},{"type":"mention","attrs":{"text":"@Ana"}}]}]},
              {"type":"table","content":[{"type":"tableRow","content":[{"type":"tableHeader","content":[{"type":"paragraph","content":[{"type":"text","text":"Name"}]}]},{"type":"tableHeader","content":[{"type":"paragraph","content":[{"type":"text","text":"a|b"}]}]}]},
                {"type":"tableRow","content":[{"type":"tableCell","content":[{"type":"paragraph","content":[{"type":"text","text":"x"}]}]},{"type":"tableCell","content":[{"type":"paragraph","content":[{"type":"text","text":"y"}]}]}]}]},
              {"type":"mediaSingle","content":[{"type":"media","attrs":{"id":"1"}}]},
              {"type":"rule"}]}
            """);

        var markdown = JiraDocument.ToMarkdown(document.RootElement).ReplaceLineEndings("\n");

        StringAssert.StartsWith(markdown, "## Steps\n\nRun `alta --dev` and see [*the docs*](https://example.com/a(b%29)  \n2 \\* 3 \\< 7\n\n");
        StringAssert.Contains(markdown, "1. First\n\n   - Nested\n2. Second\n");
        StringAssert.Contains(markdown, "````csharp\nvar fence = \"```\";\n````\n", "The fence is longer than the backticks of the code.");
        StringAssert.Contains(markdown, "> Noted by @Ana\n");
        StringAssert.Contains(markdown, "| Name | a\\|b |\n| --- | --- |\n| x | y |\n");
        StringAssert.EndsWith(markdown, "---");

        using var text = JsonDocument.Parse("\"Plain text\"");
        Assert.AreEqual("Plain text", JiraDocument.ToMarkdown(text.RootElement));
        Assert.AreEqual(string.Empty, JiraDocument.ToMarkdown(default));
    }

    [TestMethod]
    public void AListing_IsAQueryOfTheProject_ByStateAndByWhatIsTyped()
    {
        Assert.AreEqual("project = \"ALTA\" AND statusCategory != Done ORDER BY updated DESC", JiraTracker.Query(["ALTA"], TrackedItemFilter.Open, null));
        Assert.AreEqual("project = \"ALTA\" AND statusCategory = Done AND text ~ \"scroll \\\"position\\\"\" ORDER BY updated DESC", JiraTracker.Query(["ALTA"], TrackedItemFilter.Closed, " scroll \"position\" "));
        Assert.AreEqual("project = \"ALTA\" AND key = \"ALTA-12\" ORDER BY updated DESC", JiraTracker.Query(["ALTA"], TrackedItemFilter.All, "12"));
        Assert.AreEqual("project = \"ALTA\" AND key = \"OTHER-7\" ORDER BY updated DESC", JiraTracker.Query(["ALTA"], TrackedItemFilter.All, "other-7"));

        // Several projects are asked together; a number is the one of an issue of any of them.
        string[] projects = ["ALTA", "PLAT"];
        Assert.AreEqual("project in (\"ALTA\", \"PLAT\") AND statusCategory != Done ORDER BY updated DESC", JiraTracker.Query(projects, TrackedItemFilter.Open, null));
        Assert.AreEqual("project in (\"ALTA\", \"PLAT\") AND text ~ \"scroll\" ORDER BY updated DESC", JiraTracker.Query(projects, TrackedItemFilter.All, "scroll"));
        Assert.AreEqual("project in (\"ALTA\", \"PLAT\") AND key in (\"ALTA-12\", \"PLAT-12\") ORDER BY updated DESC", JiraTracker.Query(projects, TrackedItemFilter.All, "12"));
        Assert.AreEqual("project in (\"ALTA\", \"PLAT\") AND key = \"PLAT-7\" ORDER BY updated DESC", JiraTracker.Query(projects, TrackedItemFilter.All, "plat-7"));
        Assert.IsTrue(JiraClient.IsKey("ALTA-12") && !JiraClient.IsKey("ALTA-") && !JiraClient.IsKey("12") && !JiraClient.IsKey("ALTA-12; DROP"));
        Assert.AreEqual(new DateTimeOffset(2026, 10, 7, 23, 17, 18, 501, TimeSpan.FromHours(2)), JiraClient.Date("2026-10-07T23:17:18.501+0200"));
    }

    [TestMethod]
    public async Task TheTracker_ListsAndReadsIssues_ThroughTheCli()
    {
        using var root = new TempProject("[plugins.jira]\nsite = \"example.atlassian.net\"\nproject = \"ALTA\"\n");
        var cli = new FakeCli { Account = "example.atlassian.net" };
        cli.Answers["workitem search"] = _ => new(0, "[" + Issue + ",{\"key\":\"ALTA-4\",\"fields\":{\"summary\":\"Done one\",\"status\":{\"name\":\"Done\",\"statusCategory\":{\"key\":\"done\"}}}}]", string.Empty);
        cli.Answers["workitem view"] = _ => new(0, Issue, string.Empty);
        var plugin = await CreateAsync(cli, root.Path);

        var tracker = (await plugin.GetTrackersAsync(root.Path, default)).Single();
        Assert.AreEqual(("jira", "Jira", "ALTA", "https://example.atlassian.net/browse/ALTA"), (tracker.Service, tracker.DisplayName, tracker.Location, tracker.WebUrl));
        CollectionAssert.AreEqual(new[] { TrackedItemKind.Issue }, tracker.Kinds.ToArray());

        var page = await tracker.ListAsync(new(TrackedItemKind.Issue, TrackedItemFilter.All, null, 1), default);
        Assert.IsNull(page.Problem);
        Assert.IsTrue(page.More, "One more than asked was found.");
        var item = page.Items.Single();
        Assert.AreEqual(("ALTA-5", "A long output is cut", "https://example.atlassian.net/browse/ALTA-5", TrackedItemState.Open, "In Progress", "Bug", "Medium", "Ana", "Bo"),
            (item.Id, item.Title, item.Url, item.State, item.StateText, item.Type, item.Priority, item.Author, item.Assignees.Single()));
        CollectionAssert.AreEqual(new[] { "desktop", "timeline" }, item.Labels.Select(static label => label.Name).ToArray());
        CollectionAssert.Contains(cli.Runs, "jira workitem search --jql project = \"ALTA\" ORDER BY updated DESC --fields " + JiraClient.ListFields + " --limit 2 --json");

        var detail = await tracker.ReadAsync(TrackedItemKind.Issue, "alta-5", default);
        Assert.AreEqual(("Open the window of a **tool call**.", "Reproduced.", "Bo", true, 2), (detail!.Body, detail.Comments.Single().Body, detail.Comments[0].Author, detail.MoreComments, detail.Item.CommentCount));
        Assert.AreEqual(new DateTimeOffset(2026, 10, 7, 21, 17, 35, 8, TimeSpan.Zero), detail.Item.UpdatedAt!.Value.ToUniversalTime());
        Assert.IsNull(await tracker.ReadAsync(TrackedItemKind.Issue, "ALTA-5 OR 1=1", default));
        Assert.IsEmpty((await tracker.ListAsync(new(TrackedItemKind.PullRequest, TrackedItemFilter.Open, null, 10), default)).Items);

        // A project that does not name its Jira has no tracker, and nothing is run for it.
        using var plain = new TempProject("[plugins.mcp]\nenabled = true\n");
        Assert.IsEmpty(await plugin.GetTrackersAsync(plain.Path, default));
    }

    [TestMethod]
    public async Task TheTracker_ListsTheIssuesOfSeveralProjectsTogether_AndLeavesOutAProjectTheAccountDoesNotSee()
    {
        using var root = new TempProject("[plugins.jira]\nsite = \"example.atlassian.net\"\nproject = \"ALTA\"\nprojects = [\"PLAT\", \"GONE\", \"TEAM\"]\n");
        var cli = new FakeCli { Account = "example.atlassian.net" };
        // Jira refuses the whole query for one project it does not show, and names it in lower case.
        cli.Answers["workitem search"] = arguments => arguments[4].Contains("\"GONE\"", StringComparison.Ordinal)
            ? new(1, string.Empty, "✗ Error: failed to parse JQL query: the value 'gone' does not exist for the field 'project'.")
            : new(0, "[" + Issue + ",{\"key\":\"PLAT-4\",\"fields\":{\"summary\":\"Of the platform\"}}]", string.Empty);
        var plugin = await CreateAsync(cli, root.Path);

        var tracker = (await plugin.GetTrackersAsync(root.Path, default)).Single();
        Assert.AreEqual(("jira", "ALTA, PLAT, GONE, TEAM"), (tracker.Service, tracker.Location));
        CollectionAssert.AreEqual(new[] { "ALTA", "PLAT", "GONE", "TEAM" }, tracker.Parts.ToArray(), "Each Jira project can be listed alone.");
        StringAssert.StartsWith(tracker.WebUrl, "https://example.atlassian.net/issues/?jql=project%20in%20");

        var page = await tracker.ListAsync(new(TrackedItemKind.Issue, TrackedItemFilter.Open, null, 10), default);
        CollectionAssert.AreEqual(new[] { "ALTA-5", "PLAT-4" }, page.Items.Select(static item => item.Id).ToArray());
        Assert.AreEqual("https://example.atlassian.net/browse/PLAT-4", page.Items[1].Url);
        Assert.AreEqual("Jira does not show the project GONE to this account: its issues are not listed. Check `projects` in .alta/config.toml.", page.Problem);
        Assert.IsFalse(page.NeedsSignIn);
        var searches = cli.Runs.Where(static run => run.StartsWith("jira workitem search", StringComparison.Ordinal)).ToArray();
        Assert.AreEqual(2, searches.Length);
        StringAssert.Contains(searches[0], "--jql project in (\"ALTA\", \"PLAT\", \"GONE\", \"TEAM\") AND statusCategory != Done ORDER BY updated DESC");
        StringAssert.Contains(searches[1], "--jql project in (\"ALTA\", \"PLAT\", \"TEAM\") AND statusCategory != Done ORDER BY updated DESC");

        // One of the projects is asked alone, and a key the settings do not name is not asked.
        cli.Runs.Clear();
        var part = await tracker.ListAsync(new(TrackedItemKind.Issue, TrackedItemFilter.All, "7", 10) { Part = "plat" }, default);
        Assert.IsNull(part.Problem);
        StringAssert.Contains(cli.Runs.Single(), "--jql project = \"PLAT\" AND key = \"PLAT-7\" ORDER BY updated DESC");
        cli.Runs.Clear();
        var other = await tracker.ListAsync(new(TrackedItemKind.Issue, TrackedItemFilter.All, null, 10) { Part = "OTHER" }, default);
        Assert.AreEqual((0, "Jira: this project names no Jira project OTHER.", 0), (other.Items.Count, other.Problem, cli.Runs.Count));

        // An issue of any project of the site is read by its key.
        cli.Answers["workitem view"] = _ => new(0, Issue, string.Empty);
        Assert.IsNotNull(await tracker.ReadAsync(TrackedItemKind.Issue, "PLAT-4", default));

        // What happened lately is asked of all of them at once, and a project that is not shown stops nothing.
        cli.Runs.Clear();
        var created = await plugin.ReadEventsAsync(root.Path, "jira", TrackedEventKind.Created, TimeSpan.FromMinutes(5), default);
        Assert.AreEqual(("ALTA, PLAT, GONE, TEAM", (string?)null, 2), (created!.Location, created.Problem, created.Events.Count));
        Assert.IsTrue(cli.Runs.Any(static run => run.Contains("--jql project in (\"ALTA\", \"PLAT\", \"TEAM\") AND created >= -6m ORDER BY created ASC", StringComparison.Ordinal)));

        // Another failure is said as it is, and so is the one of a project that is alone.
        cli.Answers["workitem search"] = _ => new(1, string.Empty, "✗ Error: the service is down");
        var failed = await tracker.ListAsync(new(TrackedItemKind.Issue, TrackedItemFilter.Open, null, 10), default);
        Assert.AreEqual((0, "Jira: the service is down"), (failed.Items.Count, failed.Problem));
        using var alone = new TempProject("[plugins.jira]\nsite = \"example.atlassian.net\"\nproject = \"GONE\"\n");
        cli.Answers["workitem search"] = _ => new(1, string.Empty, "✗ Error: failed to parse JQL query: the value 'gone' does not exist for the field 'project'.");
        var single = await (await plugin.GetTrackersAsync(alone.Path, default)).Single().ListAsync(new(TrackedItemKind.Issue, TrackedItemFilter.Open, null, 10), default);
        StringAssert.Contains(single.Problem, "the value 'gone' does not exist");
        Assert.IsEmpty((await plugin.GetTrackersAsync(alone.Path, default)).Single().Parts, "One Jira project is not a choice.");
    }

    [TestMethod]
    public async Task AnIssueIsCreated_InTheProjectOfTheSettings_OrInAnotherOneTheProjectNames()
    {
        using var root = new TempProject("[plugins.jira]\nsite = \"example.atlassian.net\"\nproject = \"ALTA\"\nprojects = [\"PLAT\"]\n");
        var cli = new FakeCli { Account = "example.atlassian.net" };
        cli.Answers["workitem create"] = arguments => new(0, "{\"key\":\"" + arguments[4] + "-31\"}", string.Empty);
        cli.Answers["workitem comment"] = _ => new(0, string.Empty, string.Empty);
        var plugin = await CreateAsync(cli, root.Path);

        var (code, output, _) = await RunAsync(plugin, root.Path, "jira", "create", "--summary", "The list loses its place", "--type", "Bug");
        Assert.AreEqual(0, code);
        using (var created = JsonDocument.Parse(output))
        {
            Assert.AreEqual(("alta.jira.created", "ALTA-31", "ALTA", "https://example.atlassian.net/browse/ALTA-31"),
                (created.RootElement.GetProperty("type").GetString(), created.RootElement.GetProperty("key").GetString(), created.RootElement.GetProperty("project").GetString(), created.RootElement.GetProperty("url").GetString()));
        }

        CollectionAssert.Contains(cli.Runs, "jira workitem create --project ALTA --type Bug --summary The list loses its place --json");

        (code, output, _) = await RunAsync(plugin, root.Path, "jira", "create", "--jira-project", "plat", "--summary", "For the platform");
        Assert.AreEqual(0, code);
        StringAssert.Contains(output, "\"key\":\"PLAT-31\"");
        CollectionAssert.Contains(cli.Runs, "jira workitem create --project PLAT --type Task --summary For the platform --json");

        // Never in a project the settings do not name.
        var runs = cli.Runs.Count;
        (code, _, var error) = await RunAsync(plugin, root.Path, "jira", "create", "--jira-project", "OTHER", "--summary", "Elsewhere");
        Assert.AreEqual(2, code);
        StringAssert.Contains(error, "usage.unknownProject");
        StringAssert.Contains(error, "Its Jira projects are ALTA, PLAT.");
        Assert.IsFalse(cli.Runs.Skip(runs).Any(static run => run.Contains("workitem create", StringComparison.Ordinal)));

        // A number alone is an issue of the project new issues go to.
        (code, output, _) = await RunAsync(plugin, root.Path, "jira", "comment", "12", "--body", "Seen.");
        Assert.AreEqual(0, code);
        StringAssert.Contains(output, "\"key\":\"ALTA-12\"");
        CollectionAssert.Contains(cli.Runs, "jira workitem comment create --key ALTA-12 --body Seen.");

        (code, output, _) = await RunAsync(plugin, root.Path, "jira", "status");
        Assert.AreEqual(0, code);
        using var status = JsonDocument.Parse(output);
        Assert.AreEqual("ALTA", status.RootElement.GetProperty("project").GetString());
        CollectionAssert.AreEqual(new[] { "ALTA", "PLAT" }, status.RootElement.GetProperty("projects").EnumerateArray().Select(static project => project.GetString()).ToArray());
    }

    [TestMethod]
    public void AnAgent_IsToldTheProjectsOfJira_AndWhereANewIssueGoes()
    {
        var one = JiraPlugin.Guidance(new JiraSettings("example.atlassian.net", "ALTA"));
        StringAssert.Contains(one, "project `ALTA` on `example.atlassian.net`");
        Assert.IsFalse(one.Contains("--jira-project", StringComparison.Ordinal));

        var several = JiraPlugin.Guidance(new JiraSettings("example.atlassian.net", "ALTA") { Projects = ["ALTA", "PLAT", "TEAM"] });
        StringAssert.Contains(several, "in several projects: `ALTA`, `PLAT`, `TEAM`.");
        StringAssert.Contains(several, "(`--part PLAT` for one of them)");
        StringAssert.Contains(several, "A new issue goes to `ALTA`; it goes to another of these projects only when the user names that project: `alta jira create --jira-project PLAT`.");
        StringAssert.Contains(several, "A number alone names an issue of `ALTA`.");
    }

    [TestMethod]
    public async Task Jira_IsMadeReady_ByDownloadingTheCliAndSigningInWithTheTokenOfTheEnvironment()
    {
        var settings = new JiraSettings("example.atlassian.net", "ALTA") { TokenVariable = "MY_TOKEN", Email = "me@example.com" };
        var cli = new FakeCli { Installed = false };
        var plugin = await CreateAsync(cli, Path.GetTempPath(), name => name == "MY_TOKEN" ? " secret-token " : null);

        Assert.AreEqual(((string?)null, false), await plugin.EnsureReadyAsync(settings, default));

        Assert.AreEqual(1, cli.Downloads, "The CLI is downloaded when the project first needs it.");
        CollectionAssert.Contains(cli.Runs, "jira auth login --site example.atlassian.net --email me@example.com --token");
        Assert.AreEqual("secret-token\n", cli.Inputs.Single(), "The token goes to the standard input of the CLI, never on its command line.");
        Assert.IsFalse(cli.Runs.Any(static run => run.Contains("secret-token", StringComparison.Ordinal)));

        // What was checked is not checked again for a while.
        var runs = cli.Runs.Count;
        await plugin.EnsureReadyAsync(settings, default);
        Assert.AreEqual(runs, cli.Runs.Count);

        // Signed in to another site, the account of the project is chosen when the CLI has it.
        var other = new FakeCli { Account = "other.atlassian.net", Switches = true };
        var switching = await CreateAsync(other, Path.GetTempPath());
        Assert.IsNull((await switching.EnsureReadyAsync(settings with { TokenVariable = null, Email = null }, default)).Problem);
        CollectionAssert.Contains(other.Runs, "jira auth switch --site example.atlassian.net");
    }

    [TestMethod]
    public async Task WithoutAnAccountOrAToken_TheUserIsToldHowToSignIn_AndAFailedDownloadSaysSo()
    {
        var settings = new JiraSettings("example.atlassian.net", "ALTA");
        var plugin = await CreateAsync(new FakeCli(), Path.GetTempPath());

        var (problem, needsSignIn) = await plugin.EnsureReadyAsync(settings, default);
        Assert.IsTrue(needsSignIn);
        StringAssert.Contains(problem, "Jira is not signed in for example.atlassian.net");
        StringAssert.Contains(problem, "Jira: Sign in");

        using var root = new TempProject("[plugins.jira]\nsite = \"example.atlassian.net\"\nproject = \"ALTA\"\n");
        var page = await (await plugin.GetTrackersAsync(root.Path, default)).Single().ListAsync(new(TrackedItemKind.Issue, TrackedItemFilter.Open, null, 10), default);
        Assert.AreEqual((0, true), (page.Items.Count, page.NeedsSignIn));

        var missing = await CreateAsync(new FakeCli { Installed = false, DownloadProblem = "The Atlassian CLI could not be downloaded." }, Path.GetTempPath());
        Assert.AreEqual(("The Atlassian CLI could not be downloaded.", false), await missing.EnsureReadyAsync(settings, default));

        var refused = new FakeCli();
        refused.Answers["auth login"] = _ => new(1, string.Empty, "✗ Error: invalid token");
        var token = await CreateAsync(refused, Path.GetTempPath(), name => name is "JIRA_API_TOKEN" ? "bad" : name is "JIRA_EMAIL" ? "me@example.com" : null);
        var answer = await token.EnsureReadyAsync(settings, default);
        Assert.IsTrue(answer.NeedsSignIn);
        StringAssert.Contains(answer.Problem, "invalid token");
    }

    [TestMethod]
    public async Task WhatHappenedLately_IsAskedInMinutes_AndAnIssueThatWasOnlyCreatedWasNotUpdated()
    {
        using var root = new TempProject("[plugins.jira]\nsite = \"example.atlassian.net\"\nproject = \"ALTA\"\n");
        var cli = new FakeCli { Account = "example.atlassian.net" };
        cli.Answers["workitem search"] = _ => new(0, "[" + Issue + ",{\"key\":\"ALTA-6\",\"fields\":{\"summary\":\"New\"}}]", string.Empty);
        cli.Answers["workitem view"] = arguments => arguments.Contains("ALTA-6")
            ? new(0, "{\"key\":\"ALTA-6\",\"fields\":{\"summary\":\"New\",\"created\":\"2026-10-07T10:00:00.000+0000\",\"updated\":\"2026-10-07T10:00:01.000+0000\"}}", string.Empty)
            : new(0, Issue, string.Empty);
        var plugin = await CreateAsync(cli, root.Path);

        var created = await plugin.ReadEventsAsync(root.Path, "jira", TrackedEventKind.Created, TimeSpan.FromMinutes(5.2), default);
        Assert.AreEqual(("Jira", "ALTA"), (created!.DisplayName, created.Location));
        CollectionAssert.AreEqual(new[] { ("ALTA-5", "ALTA-5"), ("ALTA-6", "ALTA-6") }, created.Events.Select(static item => (item.Item.Id, item.Stamp)).ToArray());
        Assert.IsTrue(cli.Runs.Any(static run => run.Contains("--jql project = \"ALTA\" AND created >= -7m ORDER BY created ASC", StringComparison.Ordinal)));

        var updated = await plugin.ReadEventsAsync(root.Path, "jira", TrackedEventKind.Updated, TimeSpan.FromMinutes(5), default);
        var item = updated!.Events.Single();
        Assert.AreEqual(("ALTA-5", "2026-10-07T21:17:35.0080000+00:00"), (item.Item.Id, item.Stamp));

        Assert.IsNull(await plugin.ReadEventsAsync(root.Path, "github", TrackedEventKind.Created, TimeSpan.FromMinutes(5), default), "The events of another service are not Jira's.");
        Assert.IsNull(await plugin.ReadEventsAsync(Path.GetTempPath(), "jira", TrackedEventKind.Created, TimeSpan.FromMinutes(5), default));
    }

    [TestMethod]
    public async Task TheCli_IsDownloadedOnceForThisSystem_IntoTheCache()
    {
        Assert.AreEqual("https://acli.atlassian.com/windows/latest/acli_windows_amd64/acli.exe", JiraCli.DownloadUrl(OSPlatform.Windows, Architecture.X64));
        Assert.AreEqual("https://acli.atlassian.com/darwin/latest/acli_darwin_arm64/acli", JiraCli.DownloadUrl(OSPlatform.OSX, Architecture.Arm64));
        Assert.AreEqual("https://acli.atlassian.com/linux/latest/acli_linux_arm64/acli", JiraCli.DownloadUrl(OSPlatform.Linux, Architecture.Arm64));
        Assert.IsNull(JiraCli.DownloadUrl(OSPlatform.Linux, Architecture.X86));
        Assert.IsNull(JiraCli.DownloadUrl(OSPlatform.FreeBSD, Architecture.X64));

        var cache = Path.Combine(Path.GetTempPath(), "codealta-jira-cli-" + Guid.NewGuid().ToString("N"));
        var previous = (Environment.GetEnvironmentVariable("ACLI_PATH"), Environment.GetEnvironmentVariable("PATH"));
        try
        {
            // No CLI of the machine is found: only the cache counts.
            Environment.SetEnvironmentVariable("ACLI_PATH", null);
            Environment.SetEnvironmentVariable("PATH", Path.Combine(cache, "nothing-here"));
            var handler = new DownloadHandler(new byte[2 * 1024 * 1024]);
            using var cli = new JiraCli(cache, handler);
            Assert.IsFalse(cli.IsInstalled);
            Assert.AreEqual(127, (await cli.RunAsync(["jira", "auth", "status"], null, TimeSpan.FromSeconds(5), default)).ExitCode);

            var announced = 0;
            Assert.IsNull(await cli.EnsureInstalledAsync(() => announced++, default));
            Assert.AreEqual((1, 1, true), (announced, handler.Requests.Count, cli.IsInstalled));
            Assert.AreEqual(JiraCli.DownloadUrl(), handler.Requests[0]);
            Assert.AreEqual(2 * 1024 * 1024, new FileInfo(cli.CachedPath).Length);
            Assert.IsEmpty(Directory.GetFiles(cache, "*.tmp"), "The file that was being written is gone.");
            Assert.IsNull(await cli.EnsureInstalledAsync(() => announced++, default));
            Assert.AreEqual((1, 1), (announced, handler.Requests.Count), "What is there is not downloaded again.");

            // What is too small to be the CLI is not kept.
            var small = Path.Combine(cache, "small");
            using var refused = new JiraCli(small, new DownloadHandler(new byte[100]));
            StringAssert.Contains(await refused.EnsureInstalledAsync(null, default), "not what was expected");
            Assert.IsFalse(refused.IsInstalled);
            using var absent = new JiraCli(Path.Combine(cache, "absent"), new DownloadHandler(null));
            StringAssert.Contains(await absent.EnsureInstalledAsync(null, default), "HTTP 404");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ACLI_PATH", previous.Item1);
            Environment.SetEnvironmentVariable("PATH", previous.Item2);
            try { Directory.Delete(cache, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task<JiraPlugin> CreateAsync(FakeCli cli, string userData, Func<string, string?>? environment = null, PluginIdentity? identity = null)
    {
        var plugin = new JiraPlugin(_ => cli, environment ?? (static _ => null));
        var noop = NoopPluginServices.Create();
        IPluginServices services = identity is null ? noop : new IdentityServices(noop, identity);
        plugin.AttachRuntimeContext(new PluginRuntimeContext
        {
            Plugin = PluginDescriptorFactory.FromType(typeof(JiraPlugin)),
            Host = new PluginHostInfo { ApplicationName = "CodeAlta", Version = "test", HostApiVersion = "1", UserDataDirectory = userData, IsHeadless = true },
            Logger = services.Logger,
            Services = services,
            PackageDirectory = Path.GetTempPath(),
        });
        await plugin.InitializeAsync(CancellationToken.None);
        return plugin;
    }

    // Runs an `alta` command of the plugin for the project in a folder.
    private static async Task<(int Code, string Output, string Error)> RunAsync(JiraPlugin plugin, string projectPath, params string[] arguments)
    {
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var app = new CommandApp("alta", "test") { plugin.GetAltaCommands().Single().CreateCommandNode(new PluginAltaCommandContext
        {
            Plugin = PluginDescriptorFactory.FromType(typeof(JiraPlugin)),
            Services = NoopPluginServices.Create(),
            Scope = PluginScope.Project,
            ScopeProjectPath = projectPath,
            CorrelationId = "corr-1",
            Stdin = TextReader.Null,
            Stdout = stdout,
            Stderr = stderr,
        }) };
        var code = await app.RunAsync(arguments, new CommandRunConfig { Out = TextWriter.Null, Error = stderr });
        return (code, stdout.ToString(), stderr.ToString());
    }

    // Host services whose every work has one identity.
    private sealed class IdentityServices(IPluginServices inner, PluginIdentity identity) : IPluginServices, IPluginIdentityService
    {
        public XenoAtom.Logging.Logger Logger => inner.Logger;
        public IPluginUiService Ui => inner.Ui;
        public IPluginStateStore State => inner.State;
        public IPluginWorkspaceService Workspace => inner.Workspace;
        public IPluginSessionService Sessions => inner.Sessions;
        public IPluginPromptService Prompts => inner.Prompts;
        public IPluginAgentService Agents => inner.Agents;
        public IPluginTaskService Tasks => inner.Tasks;
        public IPluginAltaService Alta => inner.Alta;
        public IPluginIdentityService Identity => this;

        public ValueTask<PluginIdentity> GetIdentityAsync(string? sessionId, CancellationToken cancellationToken) => ValueTask.FromResult(identity);

        public ValueTask<PluginIdentity> GetProjectIdentityAsync(string projectPath, CancellationToken cancellationToken) => ValueTask.FromResult(identity);
    }

    // A CLI that answers what a test says, and records what it was asked.
    private sealed class FakeCli : IJiraCli
    {
        public bool Installed { get; set; } = true;
        public string? DownloadProblem { get; init; }
        public int Downloads { get; private set; }
        public string? Account { get; set; }
        public bool Switches { get; init; }
        public List<string> Runs { get; } = [];
        public List<string> Inputs { get; } = [];
        public Dictionary<string, Func<IReadOnlyList<string>, JiraCliResult>> Answers { get; } = [];

        public bool IsInstalled => Installed;

        public string? ExecutablePath => Installed ? "acli" : null;

        public ValueTask<string?> EnsureInstalledAsync(Action? progress, CancellationToken cancellationToken)
        {
            if (Installed) return ValueTask.FromResult<string?>(null);
            progress?.Invoke();
            Downloads++;
            Installed = DownloadProblem is null;
            return ValueTask.FromResult(DownloadProblem);
        }

        public Task<JiraCliResult> RunAsync(IReadOnlyList<string> arguments, string? input, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var line = string.Join(' ', arguments);
            Runs.Add(line);
            if (input is not null) Inputs.Add(input);
            foreach (var (key, answer) in Answers)
            {
                if (line.StartsWith("jira " + key, StringComparison.Ordinal)) return Task.FromResult(answer(arguments));
            }

            if (line == "jira auth status")
                return Task.FromResult(Account is null ? new JiraCliResult(1, string.Empty, "✗ Error: not logged in") : new(0, $"✓ Authenticated\n  Site: {Account}\n  Email: me@example.com\n  Authentication Type: oauth\n", string.Empty));
            if (line.StartsWith("jira auth login", StringComparison.Ordinal))
            {
                Account = arguments[Array.IndexOf([.. arguments], "--site") + 1];
                return Task.FromResult(new JiraCliResult(0, "✓ Authenticated", string.Empty));
            }

            if (line.StartsWith("jira auth switch", StringComparison.Ordinal))
            {
                if (Switches) Account = arguments[^1];
                return Task.FromResult(new JiraCliResult(Switches ? 0 : 1, string.Empty, Switches ? string.Empty : "✗ Error: no such account"));
            }

            return Task.FromResult(new JiraCliResult(1, string.Empty, "✗ Error: unexpected " + line));
        }
    }

    private sealed class DownloadHandler(byte[]? content) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(content is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
        }
    }

    private sealed class TempProject : IDisposable
    {
        public TempProject(string config)
        {
            Path = Directory.CreateTempSubdirectory("codealta-jira-project-").FullName;
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".alta"));
            File.WriteAllText(JiraSettings.ConfigPath(Path), config);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
