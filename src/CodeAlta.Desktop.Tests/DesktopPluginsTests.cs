using CodeAlta.Catalog;
using CodeAlta.Catalog.Issues;
using CodeAlta.Desktop;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.CommandLine;
using Command = XenoAtom.CommandLine.Command;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopPluginsTests
{
    [TestMethod]
    public async Task Identities_AreTheUsualOnesUntilTheHostRuns_ThenTheSpaceOfTheWork()
    {
        var identities = new DesktopPluginIdentities();
        Assert.AreSame(PluginIdentity.Usual, await identities.GetIdentityAsync("session-1", default));

        var business = new SpaceDescriptor
        {
            Id = "business",
            Name = "Business",
            Settings = new SpaceSettings(Identity: new SpaceIdentity(new SpaceGitIdentity(Email: "alex@contoso.example"), new SpaceGitHubIdentity("gh-contoso"),
                new SpaceJiraIdentity("contoso.atlassian.net", "alex@contoso.example", "CONTOSO_JIRA_TOKEN"))),
        };
        var resolver = new CodeAlta.Orchestration.Runtime.SessionIdentityResolver(null);
        var asked = new List<string?>();
        identities.Attach((sessionId, _) =>
        {
            asked.Add(sessionId);
            return Task.FromResult(resolver.Of(business));
        }, (_, _) => Task.FromResult(resolver.Of(SpaceDescriptor.CreateDefault())));

        var session = await identities.GetIdentityAsync("session-1", default);
        Assert.AreEqual(("business", "contoso.atlassian.net", "alex@contoso.example", "CONTOSO_JIRA_TOKEN"), (session.SpaceId, session.JiraSite, session.JiraEmail, session.JiraTokenVariable));
        Assert.AreEqual("gh-contoso", session.Environment.Get("GH_CONFIG_DIR"));
        Assert.AreEqual("alex@contoso.example", session.Environment.Get("GIT_AUTHOR_EMAIL"));
        await identities.GetIdentityAsync(null, default);
        CollectionAssert.AreEqual(new string?[] { "session-1", null }, asked);

        var project = await identities.GetProjectIdentityAsync("C:/repo", default);
        Assert.AreEqual(("default", true), (project.SpaceId, project.Environment.IsEmpty), "A space without identity changes nothing.");
        var window = await identities.GetWindowIdentityAsync("C:/repo", default);
        Assert.AreEqual("gh-contoso", window.Environment.Get("GH_CONFIG_DIR"), "Without the identity of a project, the window has its own.");
        CollectionAssert.AreEqual(new string?[] { "session-1", null, null }, asked);

        // The window works on a project with the space it shows and the CLI profiles of that project.
        var paths = new List<string>();
        identities.Attach((_, _) => Task.FromResult(resolver.Of(business)), (_, _) => Task.FromResult(resolver.Of(SpaceDescriptor.CreateDefault())), (path, _) =>
        {
            paths.Add(path);
            return Task.FromResult(resolver.Of(business, [new SpaceCliProfile("azure", [new("AZURE_CONFIG_DIR", "/home/alex/.azure-contoso")])]));
        });
        window = await identities.GetWindowIdentityAsync("C:/repo", default);
        Assert.AreEqual(("business", "gh-contoso", "/home/alex/.azure-contoso"),
            (window.SpaceId, window.Environment.Get("GH_CONFIG_DIR"), window.Environment.Get("AZURE_CONFIG_DIR")));
        CollectionAssert.AreEqual(new[] { "C:/repo" }, paths);
    }

    [TestMethod]
    public void BuiltIns_AreTheTerminalPluginsWithoutTheirTerminalPresentation()
    {
        // The ids are what [plugins.<id>] in the configuration names, in both frontends.
        CollectionAssert.AreEqual(new[] { "git", "jira", "mcp", "statistics", "acp", "codex-cli" }, DesktopPlugins.BuiltIns.Select(static plugin => plugin.Id).ToArray());
        foreach (var definition in DesktopPlugins.BuiltIns)
        {
            var plugin = definition.Factory();
            Assert.AreEqual(definition.PluginType, plugin.GetType());
            // The terminal's dialogs and pickers are commands and prompt-editor contributions of its own composition.
            // Jira has two commands of its own, for both applications: signing in, and its status.
            Assert.AreEqual(definition.Id == "jira" ? 2 : 0, plugin.GetCommands().Count());
            Assert.AreEqual(0, plugin.GetPromptEditorContributions().Count());
        }
    }

    [TestMethod]
    public void BuiltIns_ThatNeedNoWindow_SupportTheCommandLineApplication()
    {
        string[] headless = ["git", "jira", "mcp", "acp", "codex-cli"];
        BuiltInPluginDefinition[] definitions = [.. DesktopPlugins.BuiltIns, DesktopPlugins.MobileDefinition, DesktopLandingPlugin.Definition()];
        foreach (var definition in definitions)
        {
            var expected = headless.Contains(definition.Id) ? PluginFrontends.All | PluginFrontends.Cli : definition.Id == "mobile" ? PluginFrontends.Desktop : PluginFrontends.All;
            Assert.AreEqual(expected, definition.CreateDescriptor().Frontends, definition.Id);
            // The attribute of the plugin says the same as its definition.
            Assert.AreEqual(expected, PluginDescriptorFactory.FromType(definition.PluginType!).Frontends, definition.Id);
        }

        // The plugins of the window alone never run in the command-line application.
        foreach (var type in new[] { typeof(Search.DesktopSearchPlugin), typeof(Ui.DesktopUiPlugin) })
        {
            Assert.IsFalse(PluginDescriptorFactory.FromType(type).Frontends.Supports(PluginFrontends.Cli), type.Name);
        }
    }

    [TestMethod]
    public async Task TheJournalsOfTheStatistics_AreTheOnesOfTheStateRootOfTheInstance()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeAlta-desktop-journals-" + Guid.NewGuid().ToString("N"));
        try
        {
            // The developer instance shares the global root and keeps its sessions under a state root of its own.
            var global = Path.Combine(root, "global");
            var state = Path.Combine(global, "dev");
            Directory.CreateDirectory(Path.Combine(global, "sessions", "2026", "10", "09"));
            Directory.CreateDirectory(Path.Combine(state, "sessions", "2026", "10", "09"));
            await File.WriteAllTextAsync(Path.Combine(global, "sessions", "2026", "10", "09", "normal-session.jsonl"), "{}\n");
            await File.WriteAllTextAsync(Path.Combine(state, "sessions", "2026", "10", "09", "dev-session.jsonl"), "{}\n");

            var normal = DesktopPlugins.CreateJournalCatalog(new CodeAlta.Catalog.CatalogOptions { GlobalRoot = global });
            var developer = DesktopPlugins.CreateJournalCatalog(new CodeAlta.Catalog.CatalogOptions { GlobalRoot = global, StateRoot = state });

            Assert.AreEqual("normal-session", (await normal.ListAsync().ToListAsync()).Single().SessionId);
            Assert.AreEqual("dev-session", (await developer.ListAsync().ToListAsync()).Single().SessionId);
            Assert.IsNull(await developer.GetAsync("normal-session"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task AltaTool_RunsTheCommandsOfTheHostsPlugins_AndPluginsCanRunAltaCommands()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeAlta-desktop-plugins-" + Guid.NewGuid().ToString("N"));
        var global = Directory.CreateDirectory(Path.Combine(root, "global")).FullName;
        var project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
        try
        {
            var pluginAlta = new PluginAltaServiceBridge();
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = project, IsHeadless = true,
                PluginBuiltIns = [new BuiltInPluginDefinition { Id = "fixture", DisplayName = "Fixture", PluginType = typeof(FixturePlugin), Factory = static () => new FixturePlugin() }],
                PluginServices = new DesktopPluginServices(pluginAlta, new DesktopPluginUi()),
            }, CancellationToken.None);
            var before = await pluginAlta.InvokeAsync(["fixture-echo"]);

            DesktopAltaTools.Attach(host, new AltaReminderService(new AltaServiceCollection()), pluginAlta);
            var tool = host.Commands.SessionTools!(new("session-1", host.CurrentProject.Id, project, "provider")).Single(static t => t.Spec.Name == "alta");
            using var arguments = System.Text.Json.JsonDocument.Parse("""{"args":["fixture-echo"]}""");
            var fromSession = await tool.Handler(new(new("provider"), "session-1", "call", "alta", arguments.RootElement.Clone()), default);
            var fromPlugin = await pluginAlta.InvokeAsync(["fixture-echo"], options: new PluginAltaInvocationOptions { SourceSessionId = "session-2" });

            // Before the alta services exist a plugin gets a refusal, never an exception.
            Assert.AreEqual(AltaExitCodes.ServiceUnavailable, before.ExitCode);
            Assert.IsTrue(fromSession.Success, fromSession.Error);
            StringAssert.Contains(string.Concat(fromSession.Items.OfType<CodeAlta.Agent.AgentToolResultItem.Text>().Select(static item => item.Value)), "fixture:session-1");
            Assert.AreEqual(0, fromPlugin.ExitCode, fromPlugin.Error);
            StringAssert.Contains(fromPlugin.TranscriptJsonl, "fixture:session-2");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }

    [TestMethod]
    public async Task AltaIssue_ReadsTheTrackersOfTheProject_WhateverKeepsThem()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeAlta-desktop-issues-" + Guid.NewGuid().ToString("N"));
        var global = Directory.CreateDirectory(Path.Combine(root, "global")).FullName;
        var project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
        try
        {
            var pluginAlta = new PluginAltaServiceBridge();
            // What happens while the tracker gives its filters: the user saves in the window of the views.
            Action? duringFilters = null;
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = project, IsHeadless = true,
                PluginBuiltIns = [new BuiltInPluginDefinition { Id = "trackers", DisplayName = "Trackers", PluginType = typeof(TrackersPlugin), Factory = () => new TrackersPlugin(() => duringFilters?.Invoke()) }],
                PluginServices = new DesktopPluginServices(pluginAlta, new DesktopPluginUi()),
            }, CancellationToken.None);
            DesktopAltaTools.Attach(host, new AltaReminderService(new AltaServiceCollection()), pluginAlta);
            var listed = await host.ProjectCatalog.UpsertFromPathAsync(project);
            async Task<(int Code, string Text)> Run(params string[] arguments)
            {
                var result = await pluginAlta.InvokeAsync([.. arguments, "--project", listed.Id]);
                return (result.ExitCode, result.TranscriptJsonl + result.Error);
            }

            var trackers = await Run("issue", "trackers");
            StringAssert.Contains(trackers.Text, "\"tracker\":\"github\",\"name\":\"GitHub\",\"location\":\"o/r\"");
            StringAssert.Contains(trackers.Text, "\"tracker\":\"jira\",\"name\":\"Jira\",\"location\":\"ALTA\",\"url\":\"https://example.atlassian.net/browse/ALTA\",\"kinds\":[\"issue\"]");

            // Without a tracker named, the first one that has the kind answers; both names of the command are the same command.
            var issues = await Run("issues", "list", "--text", "#crash", "--state", "all");
            Assert.AreEqual(0, issues.Code, issues.Text);
            StringAssert.Contains(issues.Text, "\"tracker\":\"github\",\"kind\":\"issue\",\"id\":\"12\",\"title\":\"github issue All crash\"");
            var pulls = await Run("issue", "list", "--kind", "pr", "--state", "merged");
            StringAssert.Contains(pulls.Text, "\"kind\":\"pull_request\",\"id\":\"12\",\"title\":\"github pr Merged \"");
            StringAssert.Contains(pulls.Text, "\"type\":\"alta.issue.summary\"");
            var jira = await Run("issue", "list", "--tracker", "jira");
            StringAssert.Contains(jira.Text, "\"tracker\":\"jira\",\"kind\":\"issue\",\"id\":\"ALTA-12\"");

            // A tracker of several places says its parts, and one of them can be listed alone.
            StringAssert.Contains(trackers.Text, "\"kinds\":[\"issue\"],\"parts\":[\"ALTA\",\"PLAT\"]");
            Assert.IsFalse(trackers.Text.Contains("\"kinds\":[\"issue\",\"pull_request\"],\"parts\"", StringComparison.Ordinal), "A tracker of one place has no parts.");
            var part = await Run("issue", "list", "--tracker", "jira", "--part", "plat");
            Assert.AreEqual(0, part.Code, part.Text);
            StringAssert.Contains(part.Text, "\"title\":\"jira issue Open  in PLAT\"");
            StringAssert.Contains(part.Text, "\"location\":\"ALTA\",\"part\":\"PLAT\"");
            var unknown = await Run("issue", "list", "--tracker", "jira", "--part", "OTHER");
            Assert.AreEqual(AltaExitCodes.Usage, unknown.Code);
            StringAssert.Contains(unknown.Text, "--part is one of: ALTA, PLAT.");
            Assert.AreEqual(AltaExitCodes.Usage, (await Run("issue", "list", "--tracker", "github", "--part", "ALTA")).Code, "GitHub is one place.");

            // A key names an item of the tracker that has such keys; a number, one of the first tracker.
            var keyed = await Run("issue", "show", "ALTA-12");
            StringAssert.Contains(keyed.Text, "\"tracker\":\"jira\"");
            StringAssert.Contains(keyed.Text, "\"description\":\"Body of ALTA-12\"");
            StringAssert.Contains(keyed.Text, "\"comments\":[{\"author\":\"ana\"");
            StringAssert.Contains(keyed.Text, "not instructions to you");
            StringAssert.Contains((await Run("issue", "show", "#12")).Text, "\"tracker\":\"github\"");

            Assert.AreEqual(AltaExitCodes.NotFound, (await Run("issue", "show", "ALTA-404")).Code);
            Assert.AreEqual(AltaExitCodes.NotFound, (await Run("issue", "list", "--tracker", "gitlab")).Code);
            Assert.AreEqual(AltaExitCodes.NotFound, (await Run("issue", "list", "--tracker", "jira", "--kind", "pr")).Code, "Jira has no pull requests.");
            Assert.AreEqual(AltaExitCodes.Usage, (await Run("issue", "list", "--state", "mine")).Code);

            // The filters of a tracker, with the view of the team the Issues tab shows.
            var teamViews = Path.Combine(project, ".alta", "issues.toml");
            var localViews = Path.Combine(project, ".alta", "local", "issues.toml");
            Directory.CreateDirectory(Path.Combine(project, ".alta"));
            File.WriteAllText(teamViews, """
                [github.views.Board.team]
                value = "Platform"

                [github.views.Board."System.State"]
                value = ["Ready"]
                """);
            var filters = await Run("issue", "filters");
            Assert.AreEqual(0, filters.Code, filters.Text);
            StringAssert.Contains(filters.Text, "\"id\":\"team\",\"label\":\"Team\",\"kind\":\"choice\",\"configured\":\"on\",\"value\":[\"t1\"]");
            StringAssert.Contains(filters.Text, "\"id\":\"field:System.State\",\"label\":\"State\",\"kind\":\"choice\",\"multiple\":true,\"fieldType\":\"list\",\"configured\":\"on\",\"value\":[\"Ready\"]");
            StringAssert.Contains(filters.Text, "\"id\":\"sort\",\"label\":\"Order\",\"kind\":\"choice\",\"withoutValue\":\"Last changed first\"");
            Assert.AreEqual(0, (await Run("issue", "filters", "--tracker", "jira")).Code, "A tracker without filters says so.");

            // Values by id or label; the same id again for several values of a filter that takes several.
            var narrowed = await Run("issue", "list", "--filter", "team=mobile", "--filter", "field:System.State=New", "--filter", "field:System.State=On Hold", "--filter", "sort=backlog");
            Assert.AreEqual(0, narrowed.Code, narrowed.Text);
            StringAssert.Contains(narrowed.Text, "github issue Open  [field:System.State=New+On Hold;sort=backlog;team=t2]");
            StringAssert.Contains(narrowed.Text, "\"filters\":{\"team\":[\"t2\"],\"field:System.State\":[\"New\",\"On Hold\"],\"sort\":[\"backlog\"]}");
            // What the user's tab shows: the active filters of the view stay whatever is asked; --filter adds the others.
            StringAssert.Contains((await Run("issue", "list", "--configured")).Text, "github issue Open  [field:System.State=Ready;team=t1]");
            StringAssert.Contains((await Run("issue", "list", "--configured", "--filter", "team=t2", "--filter", "field:System.State=", "--filter", "sort=backlog")).Text,
                "github issue Open  [field:System.State=Ready;sort=backlog;team=t1]");
            StringAssert.Contains((await Run("issue", "list")).Text, "\"title\":\"github issue Open \"", "Without --configured nothing is filtered.");

            var wrongFilter = await Run("issue", "list", "--filter", "colour=red");
            Assert.AreEqual(AltaExitCodes.Usage, wrongFilter.Code);
            StringAssert.Contains(wrongFilter.Text, "It has: team, sprint, field:System.State, sort");
            var wrongValue = await Run("issue", "list", "--filter", "field:System.State=Done");
            Assert.AreEqual(AltaExitCodes.Usage, wrongValue.Code);
            StringAssert.Contains(wrongValue.Text, "It takes: New, Ready, On Hold.");
            Assert.AreEqual(AltaExitCodes.Usage, (await Run("issue", "list", "--filter", "team=t1", "--filter", "team=t2")).Code, "A team is one.");
            // A sprint is checked against the sprints of the team asked with it, not those of the first team.
            var sprint = await Run("issue", "list", "--filter", "team=Mobile", "--filter", "sprint=Sprint of Mobile");
            Assert.AreEqual(0, sprint.Code, sprint.Text);
            StringAssert.Contains(sprint.Text, "[sprint=s-t2;team=t2]");
            Assert.AreEqual(AltaExitCodes.Usage, (await Run("issue", "list", "--filter", "team=Platform", "--filter", "sprint=Sprint of Mobile")).Code);
            StringAssert.Contains((await Run("issue", "list", "--filter", "type=x", "--filter", "field:System.State=new", "--filter", "field:System.State=New")).Text, "no such filter",
                "An unknown filter is named first.");
            var twice = await Run("issue", "list", "--filter", "field:System.State=new", "--filter", "field:System.State=New");
            Assert.AreEqual(0, twice.Code, "The same value twice, in any case, is one value: " + twice.Text);
            Assert.AreEqual(AltaExitCodes.Usage, (await Run("issue", "list", "--filter", "nothing")).Code);
            Assert.AreEqual(AltaExitCodes.Usage, (await Run("issue", "list", "--kind", "pr", "--filter", "team=t1")).Code, "Pull requests have no filters.");

            // The views of the project: the team's is the current one, since the user chose none.
            var views = await Run("issue", "views");
            Assert.AreEqual(0, views.Code, views.Text);
            StringAssert.Contains(views.Text, "\"name\":\"Board\",\"source\":\"team\",\"current\":true");
            StringAssert.Contains(views.Text, "\"current\":\"Board\"");

            // An agent changes the filters as the user does in the tab: the team's view is copied into one of the user's, which the tab shows.
            var set = await Run("issue", "filter", "set", "field:System.State", "--value", "ready", "--value", "On Hold");
            Assert.AreEqual(0, set.Code, set.Text);
            StringAssert.Contains(set.Text, "\"id\":\"field:System.State\",\"view\":\"Mine\",\"source\":\"personal\"");
            StringAssert.Contains(set.Text, "\"current\":\"Mine\",\"mode\":\"on\",\"value\":[\"Ready\",\"On Hold\"]");
            var local = File.ReadAllText(localViews);
            StringAssert.Contains(local, "value = [\"Ready\", \"On Hold\"]", "Kept by their names, as the page keeps them.");
            StringAssert.Contains(local, "[github.views.Mine.team]", "The copy has the filters of the team's view.");
            StringAssert.Contains(File.ReadAllText(teamViews), "value = [\"Ready\"]", "The team's view changes only when it is named.");
            Assert.AreEqual(0, (await Run("issue", "filter", "set", "team", "--value", "Mobile", "--label", "Squad")).Code);
            StringAssert.Contains((await Run("issue", "list", "--configured")).Text, "github issue Open  [field:System.State=Ready+On Hold;team=t2]");
            // Turned off, a filter keeps its value in the view, unused.
            var off = await Run("issue", "filter", "set", "field:System.State", "--mode", "off");
            Assert.AreEqual(0, off.Code, off.Text);
            StringAssert.Contains(off.Text, "\"mode\":\"off\",\"value\":[\"Ready\",\"On Hold\"]");
            StringAssert.Contains(File.ReadAllText(localViews), "mode = \"off\"");
            StringAssert.Contains((await Run("issue", "list", "--configured")).Text, "github issue Open  [team=t2]");
            Assert.AreEqual(0, (await Run("issue", "filter", "set", "field:System.State", "--mode", "on")).Code);
            StringAssert.Contains((await Run("issue", "list", "--configured")).Text, "github issue Open  [field:System.State=Ready+On Hold;team=t2]");
            // A filter the tab may change keeps the value of the view, and a listing of the agent may change it as the user does.
            var pick = await Run("issue", "filter", "set", "team", "--pick", "on");
            Assert.AreEqual(0, pick.Code, pick.Text);
            StringAssert.Contains(pick.Text, "\"mode\":\"on\",\"pick\":true");
            StringAssert.Contains(File.ReadAllText(localViews), "pick = true");
            StringAssert.Contains((await Run("issue", "filters")).Text, "\"configured\":\"on\",\"pick\":true");
            StringAssert.Contains((await Run("issue", "list", "--configured", "--filter", "team=t1")).Text, "github issue Open  [field:System.State=Ready+On Hold;team=t1]");
            Assert.AreEqual(AltaExitCodes.Usage, (await Run("issue", "filter", "set", "team", "--pick", "maybe")).Code);
            StringAssert.Contains((await Run("issue", "filters")).Text, "\"id\":\"team\",\"label\":\"Squad\"");
            // The sprint a view is set to is one of the team of that view.
            var viewSprint = await Run("issue", "filter", "set", "sprint", "--value", "Sprint of Mobile");
            Assert.AreEqual(0, viewSprint.Code, viewSprint.Text);
            Assert.AreEqual(0, (await Run("issue", "filter", "reset", "sprint")).Code);
            StringAssert.Contains((await Run("issue", "list", "--configured", "--view", "Board")).Text, "github issue Open  [field:System.State=Ready;team=t1]");

            var team = await Run("issue", "filter", "set", "sort", "--view", "Board", "--value", "Backlog order");
            Assert.AreEqual(0, team.Code, team.Text);
            StringAssert.Contains(File.ReadAllText(teamViews), "[github.views.Board.sort]", "The team's view, when it is named.");
            StringAssert.Contains(team.Text, "\"current\":\"Mine\"");
            var made = await Run("issue", "filter", "set", "sort", "--value", "backlog", "--view", "Bugs");
            Assert.AreEqual(0, made.Code, made.Text);
            StringAssert.Contains(made.Text, "\"view\":\"Bugs\",\"source\":\"personal\"");
            StringAssert.Contains(made.Text, "\"current\":\"Bugs\"", "A view made for a change becomes the current one.");

            Assert.AreEqual(AltaExitCodes.Usage, (await Run("issue", "filter", "set", "field:System.State", "--value", "Done")).Code, "Only what the tracker offers.");
            Assert.AreEqual(AltaExitCodes.Usage, (await Run("issue", "filter", "set", "colour", "--value", "red")).Code);
            Assert.AreEqual(AltaExitCodes.Usage, (await Run("issue", "filter", "set", "team", "--value", "Platform", "--value", "Mobile")).Code, "A team is one.");
            Assert.AreEqual(AltaExitCodes.Usage, (await Run("issue", "filter", "set", "team", "--mode", "always")).Code);
            Assert.AreEqual(AltaExitCodes.Usage, (await Run("issue", "filter", "set", "team")).Code, "Nothing to set.");
            Assert.AreEqual(AltaExitCodes.Usage, (await Run("issue", "filter", "set", "team", "--value", "Platform", "--view", new string('x', 65))).Code);

            // A save made in the window of the views while the tracker answers stays: the change is made to the view as its file holds it.
            duringFilters = () =>
            {
                duringFilters = null;
                new IssueViewStore().EditView(IssueViewSource.Personal, "github", project, "Bugs", existing => new([.. existing!.Filters, new("sprint", true, "@current")]));
            };
            var meanwhile = await Run("issue", "filter", "set", "field:System.State", "--label", "Status");
            Assert.AreEqual(0, meanwhile.Code, meanwhile.Text);
            Assert.IsNull(duringFilters, "The tracker was asked after the view was read.");
            StringAssert.Contains(meanwhile.Text, "\"view\":\"Bugs\"", "The current view changes.");
            var kept = new IssueViewStore().ReadViews("github", project).Single(static view => view.Name == "Bugs").Filters;
            Assert.AreEqual(("@current", "Status"), (kept.Find("sprint")?.Value, kept.Find("field:System.State")?.Label));
            Assert.AreEqual(0, (await Run("issue", "filter", "reset", "sprint")).Code);
            var resetMissing = await Run("issue", "filter", "reset", "team", "--view", "Nowhere");
            Assert.AreEqual(AltaExitCodes.NotFound, resetMissing.Code, "reset changes only a view that exists.");
            StringAssert.Contains(resetMissing.Text, "issue.viewNotFound");

            // Views are chosen, made, renamed and removed as in the Issues tab.
            StringAssert.Contains((await Run("issue", "view", "select", "Mine")).Text, "\"current\":\"Mine\"");
            Assert.AreEqual(AltaExitCodes.NotFound, (await Run("issue", "view", "select", "Gone")).Code);
            Assert.AreEqual(AltaExitCodes.Usage, (await Run("issue", "view", "create", "Mine")).Code, "A name is one view.");
            StringAssert.Contains((await Run("issue", "view", "create", "Empty", "--empty")).Text, "\"current\":\"Empty\"");
            Assert.AreEqual(0, (await Run("issue", "view", "rename", "Empty", "Later")).Code);
            StringAssert.Contains(File.ReadAllText(localViews), "current = \"Later\"", "The choice follows the name.");
            Assert.AreEqual(0, (await Run("issue", "view", "remove", "Later")).Code);
            Assert.IsFalse(File.ReadAllText(localViews).Contains("Later", StringComparison.Ordinal));
            Assert.AreEqual(0, (await Run("issue", "view", "select", "Mine")).Code);

            var reset = await Run("issue", "filter", "reset", "team");
            Assert.AreEqual(0, reset.Code, reset.Text);
            Assert.IsFalse(reset.Text.Contains("\"mode\"", StringComparison.Ordinal), "The view says nothing of the team any more.");
            Assert.AreEqual(AltaExitCodes.NotFound, (await Run("issue", "filter", "reset", "team")).Code, "Nothing left to reset in that view.");
            var all = await Run("issue", "filter", "reset", "--all", "--view", "Board");
            Assert.AreEqual(0, all.Code, all.Text);
            Assert.IsFalse(File.ReadAllText(teamViews).Contains("[github.views.Board.", StringComparison.Ordinal));

            // A file of views that does not parse is said, not taken for a project without views.
            File.WriteAllText(localViews, "[github.views.Mine\n");
            var broken = await Run("issue", "views");
            Assert.AreEqual(0, broken.Code, broken.Text);
            StringAssert.Contains(broken.Text, "issues.toml' is not valid TOML");
            Assert.IsFalse(broken.Text.Contains("This project has no view", StringComparison.Ordinal), broken.Text);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }

    public sealed class TrackersPlugin(Action? onFilters) : PluginBase, IIssueTrackerSource
    {
        public TrackersPlugin()
            : this(null)
        {
        }

        public ValueTask<IReadOnlyList<IIssueTracker>> GetTrackersAsync(string projectPath, CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<IIssueTracker>>([new Tracker("github", "GitHub", "o/r", null, [TrackedItemKind.Issue, TrackedItemKind.PullRequest], onFilters),
                new Tracker("jira", "Jira", "ALTA", "https://example.atlassian.net/browse/ALTA", [TrackedItemKind.Issue], onFilters)]);

        private sealed class Tracker(string service, string name, string location, string? url, TrackedItemKind[] kinds, Action? onFilters) : IIssueTracker
        {
            public string Service => service;
            public string DisplayName => name;
            public string Location => location;
            public string? WebUrl => url;
            public IReadOnlyList<TrackedItemKind> Kinds => kinds;
            public IReadOnlyList<string> Parts => service == "jira" ? ["ALTA", "PLAT"] : [];

            public ValueTask<TrackedItemPage> ListAsync(TrackedItemQuery query, CancellationToken cancellationToken)
                => ValueTask.FromResult(new TrackedItemPage([Item(query.Kind, $"{service} {(query.Kind == TrackedItemKind.PullRequest ? "pr" : "issue")} {query.Filter} {query.Text}{(query.Part is null ? string.Empty : " in " + query.Part)}"
                    + (query.Filters.Count == 0 ? string.Empty : " [" + string.Join(";", query.Filters.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                        .Select(static pair => pair.Key + "=" + string.Join("+", TrackedFilters.Split(pair.Value)))) + "]"))]));

            // The filters of the repository: a team, the states (several at once), and an order.
            public ValueTask<IReadOnlyList<TrackedFilterDescriptor>> GetFiltersAsync(TrackedFilterRequest request, CancellationToken cancellationToken)
            {
                onFilters?.Invoke();
                return ValueTask.FromResult<IReadOnlyList<TrackedFilterDescriptor>>(service != "github" ? [] : [
                    new("team", "Team", [new("t1", "Platform"), new("t2", "Mobile")]),
                    // The sprints are those of the team asked, Platform's without one.
                    new("sprint", "Sprint", [new("@current", "Current sprint"), new(request.Selections.GetValueOrDefault("team") == "t2" ? "s-t2" : "s-t1",
                        request.Selections.GetValueOrDefault("team") == "t2" ? "Sprint of Mobile" : "Sprint of Platform")]) { DependsOn = ["team"] },
                    new("field:System.State", "State", [new("New", "New"), new("Ready", "Ready"), new("On Hold", "On Hold")]) { Multiple = true, FieldType = "list" },
                    new(TrackedFilters.Sort, "Order", [new("backlog", "Backlog order")]) { AllLabel = "Last changed first" },
                ]);
            }

            public ValueTask<TrackedItemDetail?> ReadAsync(TrackedItemKind kind, string id, CancellationToken cancellationToken)
                => ValueTask.FromResult(id == Id ? new TrackedItemDetail(Item(kind, "Read"), "Body of " + id, [new("ana", null, "Seen.")]) : null);

            private string Id => service == "jira" ? "ALTA-12" : "12";

            private TrackedItem Item(TrackedItemKind kind, string title) => new(kind, Id, title, "https://example.com/" + Id, TrackedItemState.Open);
        }
    }

    [TestMethod]
    public void PluginWorkspace_NamesNoProjectOutsideAToolCall()
    {
        var workspace = new DesktopPluginServices(new PluginAltaServiceBridge(), new DesktopPluginUi()).Workspace;

        Assert.IsNull(workspace.SelectedProjectId);
        Assert.IsNull(workspace.SelectedProjectPath);
        Assert.AreEqual(0, workspace.ProjectPaths.Count);
        Assert.IsNull(workspace.GetSelectedProjectPath("file.txt"));
        Assert.IsFalse(workspace.IsInsideSelectedProject(Path.GetTempPath()));
    }

    public sealed class FixturePlugin : PluginBase
    {
        public override IEnumerable<PluginAltaCommandContribution> GetAltaCommands()
        {
            yield return new PluginAltaCommandContribution
            {
                Path = "fixture-echo",
                Description = "Writes the calling session.",
                CreateCommandNode = static context =>
                {
                    var command = new Command("fixture-echo", "Writes the calling session.");
                    command.Add((_, _) =>
                    {
                        context.Stdout.WriteLine("{\"type\":\"fixture.echo\",\"version\":1,\"value\":\"fixture:" + context.SourceSessionId + "\"}");
                        return ValueTask.FromResult(0);
                    });
                    return command;
                },
            };
        }
    }
}
