using CodeAlta.Catalog;
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
    public void BuiltIns_AreTheTerminalPluginsWithoutTheirTerminalPresentation()
    {
        // The ids are what [plugins.<id>] in the configuration names, in both frontends.
        CollectionAssert.AreEqual(new[] { "git", "jira", "mcp", "statistics" }, DesktopPlugins.BuiltIns.Select(static plugin => plugin.Id).ToArray());
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
            var tool = host.Commands.SessionTools!(new("session-1", host.CurrentProject.Id, project, "provider")).Single();
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
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = project, IsHeadless = true,
                PluginBuiltIns = [new BuiltInPluginDefinition { Id = "trackers", DisplayName = "Trackers", PluginType = typeof(TrackersPlugin), Factory = static () => new TrackersPlugin() }],
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
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }

    public sealed class TrackersPlugin : PluginBase, IIssueTrackerSource
    {
        public ValueTask<IReadOnlyList<IIssueTracker>> GetTrackersAsync(string projectPath, CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<IIssueTracker>>([new Tracker("github", "GitHub", "o/r", null, [TrackedItemKind.Issue, TrackedItemKind.PullRequest]),
                new Tracker("jira", "Jira", "ALTA", "https://example.atlassian.net/browse/ALTA", [TrackedItemKind.Issue])]);

        private sealed class Tracker(string service, string name, string location, string? url, TrackedItemKind[] kinds) : IIssueTracker
        {
            public string Service => service;
            public string DisplayName => name;
            public string Location => location;
            public string? WebUrl => url;
            public IReadOnlyList<TrackedItemKind> Kinds => kinds;

            public ValueTask<TrackedItemPage> ListAsync(TrackedItemQuery query, CancellationToken cancellationToken)
                => ValueTask.FromResult(new TrackedItemPage([Item(query.Kind, $"{service} {(query.Kind == TrackedItemKind.PullRequest ? "pr" : "issue")} {query.Filter} {query.Text}")]));

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
