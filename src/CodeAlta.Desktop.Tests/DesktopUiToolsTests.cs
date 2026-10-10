using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Mcp;
using CodeAlta.Desktop.Ui;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Plugins;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The tools that see and drive the window: what they are, where they save a file, how a session gets them and
/// what a client of the MCP server runs.
/// </summary>
[TestClass]
public sealed class DesktopUiToolsTests
{
    [TestMethod]
    public void Tools_AreTheOnesOfTheWindow_WithoutTheOnesForBrowserWindows()
    {
        var tools = DesktopUiAutomation.Describe(
        [
            Tool("take_snapshot", """{"type":"object","properties":{"verbose":{"type":"boolean"},"filePath":{"type":"string","description":"A path relative to the first allowed directory."}}}"""),
            Tool("new_page"), Tool("click"), Tool("close_page"),
            Tool("list_pages", readOnly: true), Tool("navigate_page"),
            Tool("take_screenshot", """{"type":"object","properties":{"format":{"type":"string"},"filePath":{"type":"string","description":"A path relative to the first allowed directory."}}}"""),
        ]);

        // The pages are the windows of the application: it opens and closes them itself.
        CollectionAssert.AreEqual(new[] { "click", "list_pages", "navigate_page", "take_screenshot", "take_snapshot" }, tools.Select(static tool => tool.Name).ToArray());
        Assert.AreEqual("Description of click.", tools[0].Description);
        Assert.IsTrue(tools[1].ReadOnly);
        StringAssert.EndsWith(tools[1].Description, "The pages are the windows of CodeAlta Desktop.");
        StringAssert.Contains(tools[2].Description, "use the type \"reload\"");
        // A file is saved where the caller works, not in a folder of the automation.
        foreach (var tool in tools.Skip(3))
        {
            var filePath = tool.InputSchema.GetProperty("properties").GetProperty("filePath");
            StringAssert.Contains(filePath.GetProperty("description").GetString(), "relative to the working folder of the caller");
            Assert.AreEqual("string", filePath.GetProperty("type").GetString());
        }

        Assert.AreEqual("string", tools[3].InputSchema.GetProperty("properties").GetProperty("format").GetProperty("type").GetString());
    }

    [TestMethod]
    public void AFile_IsSavedWhereItsCallerMayWrite()
    {
        using var temp = new TempFolder();
        var work = Directory.CreateDirectory(Path.Combine(temp.Path, "project")).FullName;
        var tools = Directory.CreateDirectory(Path.Combine(temp.Path, "ui")).FullName;
        var files = new DesktopUiFiles(work, [work, tools]);

        // A relative path starts from the folder the caller works in.
        Assert.IsTrue(DesktopUiAutomation.TryResolveFile(Path.Combine("site", "img", "shot.png"), files, out var path, out _));
        Assert.AreEqual(Path.Combine(work, "site", "img", "shot.png"), path);
        Assert.IsTrue(DesktopUiAutomation.TryResolveFile(Path.Combine(tools, "shot.png"), files, out path, out _));
        Assert.AreEqual(Path.Combine(tools, "shot.png"), path);

        foreach (var outside in new[] { Path.Combine("..", "elsewhere.png"), Path.Combine(temp.Path, "other", "shot.png"), work + "-2" + Path.DirectorySeparatorChar + "shot.png" })
        {
            Assert.IsFalse(DesktopUiAutomation.TryResolveFile(outside, files, out path, out var refusal), outside);
            Assert.IsNull(path);
            StringAssert.Contains(refusal, "outside the folders a file can be saved in");
            StringAssert.Contains(refusal, work);
        }

        // A caller with many folders is told what they are.
        Assert.IsFalse(DesktopUiAutomation.TryResolveFile(Path.Combine(temp.Path, "other", "shot.png"), files with { Description = "the folder of a project" }, out _, out var described));
        Assert.AreEqual("The path of \"filePath\" is outside the folders a file can be saved in: the folder of a project.", described);
        Assert.IsFalse(DesktopUiAutomation.TryResolveFile(" ", files, out _, out var blank));
        StringAssert.Contains(blank, "must be a file path");
        Assert.IsFalse(DesktopUiAutomation.TryResolveFile(work, files, out _, out var folder));
        StringAssert.Contains(folder, "is a folder");
        Assert.IsFalse(DesktopUiAutomation.TryResolveFile("bad\0name.png", files, out _, out var invalid));
        StringAssert.Contains(invalid, "not a valid file path");
    }

    [TestMethod]
    public async Task AgentTools_GiveTheModelTheTextAndThePicturesOfATool_AndSaveWhereTheSessionWorks()
    {
        using var temp = new TempFolder();
        var ui = new FakeUi(temp.Path);
        var sessions = new DesktopUiSessions();
        var project = Directory.CreateDirectory(Path.Combine(temp.Path, "project")).FullName;
        var worktree = Directory.CreateDirectory(Path.Combine(temp.Path, "worktree")).FullName;
        var tools = DesktopUiPlugin.CreateTools(ui, sessions, project);

        // The tools are the same for every session: name, description and schema.
        CollectionAssert.AreEqual(ui.Tools.Select(static tool => (tool.Name, tool.Description, tool.InputSchema.GetRawText())).ToArray(),
            tools.Select(static tool => (tool.Spec.Name, tool.Spec.Description, tool.Spec.InputSchema.GetRawText())).ToArray());

        ui.Answer = _ => new DesktopUiToolResult("Took a screenshot.", [new DesktopUiImage(new byte[] { 1, 2, 3 }, "image/png"), new DesktopUiImage(new byte[] { 4 }, "image/jpeg")], false);
        var shot = await Invoke(tools, "take_screenshot", "session-1", """{"fullPage":null}""");
        Assert.IsTrue(shot.Success);
        Assert.IsNull(shot.Error);
        CollectionAssert.AreEqual(new AgentToolResultItem[]
        {
            new AgentToolResultItem.Text("Took a screenshot."),
            new AgentToolResultItem.Image("AQID", "image/png", "screenshot"),
            new AgentToolResultItem.Image("BA==", "image/jpeg", "screenshot-2"),
        }, shot.Items.ToArray());
        // The arguments reach the tool as the model gave them, and the session saves in the folder of its project.
        Assert.AreEqual(("take_screenshot", """{"fullPage":null}"""), (ui.Calls[0].Name, ui.Calls[0].Arguments));
        Assert.AreEqual(project, ui.Calls[0].Files.BaseDirectory);
        CollectionAssert.AreEqual(new[] { project, ui.FilesDirectory }, ui.Calls[0].Files.Roots.ToArray());

        // A session that works in a git worktree saves there.
        sessions.WorkFolder = (sessionId, _) => ValueTask.FromResult<string?>(sessionId == "session-2" ? worktree : null);
        await Invoke(tools, "click", "session-2", "{}");
        Assert.AreEqual(worktree, ui.Calls[1].Files.BaseDirectory);
        await Invoke(tools, "click", "session-1", "{}");
        Assert.AreEqual(project, ui.Calls[2].Files.BaseDirectory);
        // A session nobody knows, and one whose folder is gone, save in the folder of the tools.
        sessions.WorkFolder = (_, _) => throw new InvalidOperationException("unknown session");
        await Invoke(DesktopUiPlugin.CreateTools(ui, sessions, Path.Combine(temp.Path, "gone")), "click", "session-3", "{}");
        Assert.AreEqual(ui.FilesDirectory, ui.Calls[3].Files.BaseDirectory);
        CollectionAssert.AreEqual(new[] { ui.FilesDirectory }, ui.Calls[3].Files.Roots.ToArray());

        // A tool that cannot do what it was asked says why, as a failed call.
        ui.Answer = _ => DesktopUiToolResult.Error("No element has the uid \"9_9\".");
        var failed = await Invoke(tools, "click", "session-1", """{"uid":"9_9"}""");
        Assert.IsFalse(failed.Success);
        Assert.AreEqual("No element has the uid \"9_9\".", failed.Error);
        CollectionAssert.AreEqual(new AgentToolResultItem[] { new AgentToolResultItem.Text("No element has the uid \"9_9\".") }, failed.Items.ToArray());
    }

    [TestMethod]
    public async Task ASession_GetsTheToolsWhenItAsksForThem_InTheRunningTurnAndOnItsNextRuns()
    {
        using var temp = new TempFolder();
        var global = Directory.CreateDirectory(Path.Combine(temp.Path, "global")).FullName;
        var project = Directory.CreateDirectory(Path.Combine(temp.Path, "project")).FullName;
        var ui = new FakeUi(temp.Path);
        var sessions = new DesktopUiSessions();
        var provider = new ScriptedProvider();
        var pluginAlta = new PluginAltaServiceBridge();
        await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
        {
            GlobalRoot = global, CurrentProjectPath = project, IsHeadless = true, AutoApproveOwnedPermissions = true, OwnedCommandReceiptCapacity = 32,
            PluginBuiltIns = [DesktopUiPlugin.Definition(ui, sessions)],
            PluginServices = new DesktopPluginServices(pluginAlta, new DesktopPluginUi()),
            ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor, () => provider),
        }, CancellationToken.None);
        var commands = DesktopAltaTools.Attach(host, new AltaReminderService(new AltaServiceCollection()), pluginAlta);
        var session = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Looks at the window");
        var other = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Does not");

        // A session does not carry the tools until it asks: its instructions say how.
        provider.Script(Say("nothing to look at"));
        await SendAsync(host, session, "one");
        var first = provider.Requests.Single();
        Assert.IsFalse(first.Tools.Contains("take_snapshot"));
        CollectionAssert.Contains(first.Tools, "alta");
        StringAssert.Contains(first.Instructions, DesktopUiPlugin.Guidance(active: false));
        Assert.IsFalse(sessions.IsActive(session.SessionId));

        // It asks with an alta command: the tools join the running turn, and it uses one in its next step.
        provider.Requests.Clear();
        provider.Script(Call("alta", """{"args":["ui","activate"]}"""), Call("take_snapshot", """{"verbose":false}"""), Say("the window shows a list"));
        await SendAsync(host, session, "two");
        Assert.AreEqual(3, provider.Requests.Count);
        Assert.IsFalse(provider.Requests[0].Tools.Contains("take_snapshot"));
        foreach (var tool in ui.Tools) CollectionAssert.Contains(provider.Requests[1].Tools, tool.Name);
        var activation = provider.Requests[1].LastToolResult!;
        StringAssert.Contains(activation, "\"type\":\"alta.ui.activate\"");
        StringAssert.Contains(activation, "\"toolsAvailable\":\"now\"");
        StringAssert.Contains(activation, $"\"registeredToolCount\":{ui.Tools.Count}");
        Assert.AreEqual("snapshot of the window", provider.Requests[2].LastToolResult);
        Assert.AreEqual(("take_snapshot", """{"verbose":false}""", project), (ui.Calls.Single().Name, ui.Calls.Single().Arguments, ui.Calls.Single().Files.BaseDirectory));
        Assert.IsTrue(sessions.IsActive(session.SessionId));

        // Its next runs start with them, and its instructions say that it has them. Another session has none.
        provider.Requests.Clear();
        provider.Script(Say("still there"));
        await SendAsync(host, session, "three");
        CollectionAssert.Contains(provider.Requests.Single().Tools, "take_snapshot");
        StringAssert.Contains(provider.Requests.Single().Instructions, DesktopUiPlugin.Guidance(active: true));
        provider.Requests.Clear();
        provider.Script(Say("no window for me"));
        await SendAsync(host, other, "one");
        Assert.IsFalse(provider.Requests.Single().Tools.Contains("take_snapshot"));
        StringAssert.Contains(provider.Requests.Single().Instructions, DesktopUiPlugin.Guidance(active: false));

        // It gives them back the same way.
        provider.Requests.Clear();
        provider.Script(Call("alta", """{"args":["ui","deactivate"]}"""), Say("done"));
        await SendAsync(host, session, "four");
        StringAssert.Contains(provider.Requests[1].LastToolResult, "\"type\":\"alta.ui.deactivate\"");
        Assert.IsFalse(sessions.IsActive(session.SessionId));
        provider.Requests.Clear();
        provider.Script(Say("gone"));
        await SendAsync(host, session, "five");
        Assert.IsFalse(provider.Requests.Single().Tools.Contains("take_snapshot"));

        // A caller that is no session cannot turn them on: there is nobody to give them to.
        var refused = await commands.InvokeAsync(["ui", "activate"], caller: AltaCallerIdentity.Cli);
        Assert.AreEqual(AltaExitCodes.Failure, refused.ExitCode);
        StringAssert.Contains(refused.Transcript, "ui.noSession");
        var status = await commands.InvokeAsync(["ui", "status"], caller: new AltaCallerIdentity { Kind = "agent", SourceSessionId = session.SessionId });
        StringAssert.Contains(status.Transcript, "\"active\":false");
        StringAssert.Contains(status.Transcript, "\"take_snapshot\"");
    }

    [TestMethod]
    public async Task ASessionWhoseCommandsAreReviewed_DoesNotDriveTheWindow()
    {
        using var temp = new TempFolder();
        var global = Directory.CreateDirectory(Path.Combine(temp.Path, "global")).FullName;
        var project = Directory.CreateDirectory(Path.Combine(temp.Path, "project")).FullName;
        var ui = new FakeUi(temp.Path);
        var reviewed = false;
        var sessions = new DesktopUiSessions { Reviewed = _ => Volatile.Read(ref reviewed) };
        var provider = new ScriptedProvider();
        var pluginAlta = new PluginAltaServiceBridge();
        await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
        {
            GlobalRoot = global, CurrentProjectPath = project, IsHeadless = true, AutoApproveOwnedPermissions = true, OwnedCommandReceiptCapacity = 32,
            PluginBuiltIns = [DesktopUiPlugin.Definition(ui, sessions)],
            PluginServices = new DesktopPluginServices(pluginAlta, new DesktopPluginUi()),
            ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor, () => provider),
        }, CancellationToken.None);
        var commands = DesktopAltaTools.Attach(host, new AltaReminderService(new AltaServiceCollection()), pluginAlta);
        var session = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Asks first");
        var caller = new AltaCallerIdentity { Kind = "agent", SourceSessionId = session.SessionId };

        // It could answer the review itself: the command that turns the tools on refuses, and its instructions say so.
        Volatile.Write(ref reviewed, true);
        provider.Script(Call("alta", """{"args":["ui","activate"]}"""), Say("no window for me"));
        await SendAsync(host, session, "one");
        StringAssert.Contains(provider.Requests[0].Instructions, DesktopUiPlugin.ReviewedGuidance);
        StringAssert.Contains(provider.Requests[1].LastToolResult, "ui.activateDenied");
        Assert.IsFalse(provider.Requests[1].Tools.Contains("take_snapshot"));
        Assert.IsFalse(sessions.IsActive(session.SessionId));
        var refused = await commands.InvokeAsync(["ui", "activate"], caller: caller);
        Assert.AreEqual(AltaExitCodes.PolicyDenied, refused.ExitCode, refused.Transcript);

        // A session that has the tools runs without them from the moment its commands are reviewed.
        Volatile.Write(ref reviewed, false);
        Assert.AreEqual(AltaExitCodes.Success, (await commands.InvokeAsync(["ui", "activate"], caller: caller)).ExitCode);
        Volatile.Write(ref reviewed, true);
        provider.Requests.Clear();
        provider.Script(Say("still none"));
        await SendAsync(host, session, "two");
        Assert.IsFalse(provider.Requests.Single().Tools.Contains("take_snapshot"));
        StringAssert.Contains(provider.Requests.Single().Instructions, DesktopUiPlugin.ReviewedGuidance);
        // A tool it was given before answers the same: the mode can change while a turn runs.
        var click = await Invoke(DesktopUiPlugin.CreateTools(ui, sessions, project), "click", session.SessionId, """{"uid":"1_2"}""");
        Assert.IsFalse(click.Success);
        Assert.AreEqual(DesktopUiPlugin.ReviewedRefusal, click.Error);
        Assert.HasCount(0, ui.Calls);

        // It has them again when its commands are not reviewed any more.
        Volatile.Write(ref reviewed, false);
        provider.Requests.Clear();
        provider.Script(Say("there they are"));
        await SendAsync(host, session, "three");
        CollectionAssert.Contains(provider.Requests.Single().Tools, "take_snapshot");
        StringAssert.Contains(provider.Requests.Single().Instructions, DesktopUiPlugin.Guidance(active: true));
    }

    [TestMethod]
    public async Task ASessionAnAgentCreates_DoesNotAsk_UnlessTheUserChoseThatItAsksWhatItsCreatorAsks()
    {
        using var temp = new TempFolder();
        var global = Directory.CreateDirectory(Path.Combine(temp.Path, "global")).FullName;
        var project = Directory.CreateDirectory(Path.Combine(temp.Path, "project")).FullName;
        var review = false;
        var inherit = false;
        var provider = new ScriptedProvider();
        await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
        {
            GlobalRoot = global, CurrentProjectPath = project, IsHeadless = true, OwnedCommandReceiptCapacity = 32,
            // The host of the desktop: the mode of a session comes first, and the settings of the user are read when needed.
            SessionPermissionModes = true, ReviewOwnedPermissionsPolicy = () => Volatile.Read(ref review), InheritPermissionModePolicy = () => Volatile.Read(ref inherit),
            ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor, () => provider),
        }, CancellationToken.None);
        var commands = DesktopAltaTools.Attach(host, new AltaReminderService(new AltaServiceCollection()));
        var runtime = host.RuntimeService;
        var creator = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Creator");
        provider.Script(Say("ready"));
        await SendAsync(host, creator, "one");
        var caller = new AltaCallerIdentity { Kind = "agent", SourceSessionId = creator.SessionId, SourceProjectId = host.CurrentProject.Id };
        async Task<string> CreateAsync(string title)
        {
            var created = await commands.InvokeAsync(["session", "create", "--project", host.CurrentProject.Id, "--title", title, "--provider", provider.Descriptor.ProviderId.Value], caller: caller);
            Assert.AreEqual(AltaExitCodes.Success, created.ExitCode, created.Transcript);
            using var record = JsonDocument.Parse(created.Transcript.Split('\n').First(static line => line.Contains("\"alta.session.created\"", StringComparison.Ordinal)));
            return record.RootElement.GetProperty("sessionId").GetString()!;
        }

        // The user asked to be asked first, and the creator is: by default the session it creates is not.
        Volatile.Write(ref review, true);
        Assert.AreEqual(SessionPermissionPolicy.Review, runtime.GetPermissionPolicy(creator.SessionId));
        var free = await CreateAsync("Does not ask");
        Assert.AreEqual(SessionPermissionPolicy.Approve, runtime.GetPermissionPolicy(free));
        // The mode is saved with the session, like one chosen in the composer: it is the one it is attached with again.
        Assert.AreEqual(SessionPermissionModes.Bypass, (await host.Commands.GetSelectionChoicesAsync(free))!.Current.PermissionMode);
        // No session runs, so a steer goes no further than the question of who may hand a prompt to whom.
        async Task<(int ExitCode, string Transcript)> SteerAsync(string sessionId, AltaCallerIdentity from)
        {
            var result = await commands.InvokeAsync(["session", "steer", sessionId, "--message", "work"], caller: from);
            return (result.ExitCode, result.Transcript);
        }
        Assert.IsFalse((await SteerAsync(free, caller)).Transcript.Contains("session.promptDenied", StringComparison.Ordinal));

        // The user chose that it asks what its creator asks: the next one does, in a host that bypasses permissions too.
        Volatile.Write(ref inherit, true);
        Volatile.Write(ref review, false);
        Assert.AreEqual(SessionPermissionPolicy.Approve, runtime.GetPermissionPolicy(await CreateAsync("Bypasses as its creator")));
        Volatile.Write(ref review, true);
        var asks = await CreateAsync("Asks as its creator");
        Volatile.Write(ref review, false);
        Assert.AreEqual(SessionPermissionPolicy.Review, runtime.GetPermissionPolicy(asks));
        Assert.AreEqual(SessionPermissionModes.Ask, (await host.Commands.GetSelectionChoicesAsync(asks))!.Current.PermissionMode);

        // The creator, which asks, hands a prompt to the one that asks as much, not to the one that asks less.
        Volatile.Write(ref review, true);
        Assert.IsFalse((await SteerAsync(asks, caller)).Transcript.Contains("session.promptDenied", StringComparison.Ordinal));
        var denied = await SteerAsync(free, caller);
        Assert.AreEqual(AltaExitCodes.PolicyDenied, denied.ExitCode, denied.Transcript);
        StringAssert.Contains(denied.Transcript, "session.promptDenied");
        // A caller that is no session is the user's own client.
        Assert.IsFalse((await SteerAsync(free, AltaCallerIdentity.Cli)).Transcript.Contains("session.promptDenied", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ThePlugin_IsTurnedOffLikeAnyOther()
    {
        using var temp = new TempFolder();
        var project = Directory.CreateDirectory(Path.Combine(temp.Path, "project")).FullName;
        var ui = new FakeUi(temp.Path);
        foreach (var (name, configuration, active) in new[] { ("on", "", true), ("off", "[plugins.ui]\nenabled = false\n", false) })
        {
            var global = Directory.CreateDirectory(Path.Combine(temp.Path, name)).FullName;
            File.WriteAllText(Path.Combine(global, "config.toml"), configuration);
            var pluginAlta = new PluginAltaServiceBridge();
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = project, IsHeadless = true,
                PluginBuiltIns = [DesktopUiPlugin.Definition(ui, new DesktopUiSessions())],
                PluginServices = new DesktopPluginServices(pluginAlta, new DesktopPluginUi()),
            }, CancellationToken.None);
            var commands = DesktopAltaTools.Attach(host, new AltaReminderService(new AltaServiceCollection()), pluginAlta);

            var plugins = await commands.InvokeAsync(["plugin", "list"], caller: AltaCallerIdentity.Cli);
            Assert.AreEqual(active, plugins.Transcript.Contains("builtin:ui", StringComparison.Ordinal), plugins.Transcript);
            // Without the plugin there is no command to turn the tools on.
            var status = await commands.InvokeAsync(["ui", "status"], caller: AltaCallerIdentity.Cli);
            Assert.AreEqual(active, status.ExitCode == AltaExitCodes.Success, status.Transcript);
        }
    }

    [TestMethod]
    public void AHostThatReviewsCommands_GivesNoSessionTheTools()
    {
        using var temp = new TempFolder();
        var ui = new FakeUi(temp.Path);
        var sessions = new DesktopUiSessions();

        CollectionAssert.AreEqual(new[] { "git", "jira", "mcp", "statistics", "landing", "ui" }, DesktopPlugins.ForWindow(ui, sessions, reviewsCommands: false).Select(static plugin => plugin.Id).ToArray());
        // A session that drives the window could answer the review itself.
        CollectionAssert.AreEqual(new[] { "git", "jira", "mcp", "statistics", "landing" }, DesktopPlugins.ForWindow(ui, sessions, reviewsCommands: true).Select(static plugin => plugin.Id).ToArray());
        var definition = DesktopPlugins.ForWindow(ui, sessions, reviewsCommands: false)[^1];
        Assert.AreEqual(typeof(DesktopUiPlugin), definition.PluginType);
        Assert.IsInstanceOfType<DesktopUiPlugin>(definition.Factory());

        // With the journals of the instance the Statistics plugin keeps the statistics of the sessions, in the same place of the list.
        var journals = DesktopPlugins.CreateJournalCatalog(new CodeAlta.Catalog.CatalogOptions { GlobalRoot = temp.Path });
        CollectionAssert.AreEqual(new[] { "git", "jira", "mcp", "statistics", "landing", "ui" }, DesktopPlugins.ForWindow(ui, sessions, reviewsCommands: false, journals).Select(static plugin => plugin.Id).ToArray());
        var statistics = DesktopPlugins.ForWindow(ui, sessions, reviewsCommands: true, journals).Single(static plugin => plugin.Id == "statistics");
        Assert.AreEqual(typeof(CodeAlta.Plugin.Statistics.StatisticsPlugin), statistics.PluginType);
        Assert.IsNull(((CodeAlta.Plugin.Statistics.StatisticsPlugin)statistics.Factory()).Statistics, "Nothing runs before the plugin is activated.");
    }

    [TestMethod]
    public async Task AClientOfTheMcpServer_RunsAltaCommandsAsACallerOfItsOwn_AndTheToolsOfTheWindow()
    {
        using var temp = new TempFolder();
        var global = Directory.CreateDirectory(Path.Combine(temp.Path, "global")).FullName;
        var project = Directory.CreateDirectory(Path.Combine(temp.Path, "project")).FullName;
        await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions { GlobalRoot = global, CurrentProjectPath = project, IsHeadless = true }, CancellationToken.None);
        var registered = await host.ProjectCatalog.UpsertFromPathAsync(project);
        var commands = DesktopAltaTools.Compose(host, new AltaReminderService(new AltaServiceCollection()));
        var ran = 0;
        var alta = DesktopMcpTools.Alta(commands, project, () => ran++);

        // The tool is the one the sessions have.
        var forSessions = AltaSessionToolFactory.Create(commands, new AltaSessionToolOptions());
        Assert.AreEqual((forSessions.Spec.Name, forSessions.Spec.Description, forSessions.Spec.InputSchema.GetRawText()), (alta.Name, alta.Description, alta.InputSchema.GetRawText()));
        Assert.IsFalse(alta.ReadOnly);

        var help = await alta.CallAsync(Json("""{"args":["--help"]}"""), CancellationToken.None);
        Assert.IsFalse(help.IsError);
        StringAssert.StartsWith(help.Text, "Usage: alta");
        // A relative path is resolved from the folder the application was started in.
        var current = await alta.CallAsync(Json("""{"args":["project","current"]}"""), CancellationToken.None);
        Assert.IsFalse(current.IsError, current.Text);
        StringAssert.Contains(current.Text, registered.Id);
        // The caller belongs to no session, and a command that needs one says so.
        var session = await alta.CallAsync(Json("""{"args":["session","current"]}"""), CancellationToken.None);
        Assert.IsTrue(session.IsError);
        StringAssert.Contains(session.Text, "usage.missingCurrentSession");
        var invalid = await alta.CallAsync(Json("""{"args":"session"}"""), CancellationToken.None);
        Assert.IsTrue(invalid.IsError);
        StringAssert.Contains(invalid.Text, "usage.invalidToolArguments");
        // The window is told after each command: a client creates sessions without the window asking.
        Assert.AreEqual(4, ran);

        // The tools of the window: a client saves a file in the folder of the tools or where it is allowed to.
        var ui = new FakeUi(temp.Path);
        var window = DesktopMcpTools.Window(ui, _ => ValueTask.FromResult(new DesktopUiFiles(ui.FilesDirectory, [ui.FilesDirectory, project])));
        CollectionAssert.AreEqual(ui.Tools.Select(static tool => (tool.Name, tool.Description, tool.ReadOnly)).ToArray(), window.Select(static tool => (tool.Name, tool.Description, tool.ReadOnly)).ToArray());
        ui.Answer = name => new DesktopUiToolResult("answer of " + name, [], false);
        var clicked = await window.Single(static tool => tool.Name == "click").CallAsync(Json("""{"uid":"1_2"}"""), CancellationToken.None);
        Assert.AreEqual(new DesktopUiToolResult("answer of click", [], false).Text, clicked.Text);
        Assert.AreEqual(("click", """{"uid":"1_2"}""", ui.FilesDirectory), (ui.Calls.Single().Name, ui.Calls.Single().Arguments, ui.Calls.Single().Files.BaseDirectory));
        CollectionAssert.AreEqual(new[] { ui.FilesDirectory, project }, ui.Calls.Single().Files.Roots.ToArray());
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static DesktopUiTool Tool(string name, string schema = """{"type":"object","properties":{}}""", bool readOnly = false)
        => new(name, $"Description of {name}.", Json(schema), readOnly);

    private static Task<AgentToolResult> Invoke(IReadOnlyList<AgentToolDefinition> tools, string name, string sessionId, string arguments)
        => tools.Single(tool => tool.Spec.Name == name).Handler(new AgentToolInvocation(new ModelProviderId("provider"), sessionId, "call", name, Json(arguments)), CancellationToken.None);

    internal static async Task SendAsync(CodeAltaHost host, SessionViewDescriptor session, string text)
    {
        var receipt = host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), session.SessionId, text)).Receipt;
        Assert.IsNotNull(receipt);
        var result = await receipt.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, result.Outcome, result.Code);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        // The next send may attach the session again, which the runtime only does for an idle session.
        while (await host.RuntimeService.HasActiveRunAsync(session, timeout.Token)) await Task.Delay(20, timeout.Token);
    }

    internal static Func<AgentTurnRequest, AgentTurnResponse> Call(string tool, string arguments)
        => _ => new AgentTurnResponse
        {
            AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.ToolCall("call-" + Guid.NewGuid().ToString("N"), tool, Json(arguments))]),
        };

    internal static Func<AgentTurnRequest, AgentTurnResponse> Say(string text)
        => _ => new AgentTurnResponse { AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text(text)]) };

    /// <summary>What the model was offered and told in one of its requests, and the result of the tool it called before.</summary>
    internal sealed record Request(string[] Tools, string Instructions, string? LastToolResult);

    // The window of the tests: it has a few tools, records what is asked of them and answers what a test says.
    private sealed class FakeUi : IDesktopUi
    {
        public FakeUi(string root)
        {
            FilesDirectory = Directory.CreateDirectory(Path.Combine(root, "ui")).FullName;
            Tools = DesktopUiAutomation.Describe(
            [
                Tool("click", """{"type":"object","properties":{"uid":{"type":"string"}},"required":["uid"]}"""),
                Tool("list_pages", readOnly: true),
                Tool("take_screenshot", """{"type":"object","properties":{"fullPage":{"type":"boolean"},"filePath":{"type":"string"}}}"""),
                Tool("take_snapshot", """{"type":"object","properties":{"verbose":{"type":"boolean"},"filePath":{"type":"string"}}}"""),
            ]);
        }

        public IReadOnlyList<DesktopUiTool> Tools { get; }

        public string FilesDirectory { get; }

        public List<(string Name, string Arguments, DesktopUiFiles Files)> Calls { get; } = [];

        public Func<string, DesktopUiToolResult>? Answer { get; set; }

        public Task<DesktopUiToolResult> CallAsync(string name, JsonElement arguments, DesktopUiFiles files, CancellationToken cancellationToken)
        {
            lock (Calls) Calls.Add((name, arguments.GetRawText(), files));
            return Task.FromResult(Answer?.Invoke(name) ?? new DesktopUiToolResult(name == "take_snapshot" ? "snapshot of the window" : "done", [], false));
        }
    }

    // A model that says and calls what a test scripted, and records what it was offered.
    internal sealed class ScriptedProvider : IAgentModelProviderRuntime, IModelProviderTurnExecutor
    {
        private readonly Queue<Func<AgentTurnRequest, AgentTurnResponse>> _steps = new();

        public List<Request> Requests { get; } = [];

        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId("fake-ui"), "Fake UI") { DefaultModelId = "fake-model" };

        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = "test", ProviderKey = "fake-ui", DisplayName = "Fake UI", TransportKind = AgentTransportKind.OpenAIResponses,
        };

        public IModelProviderModelCatalog? ModelCatalog => null;

        public void Script(params Func<AgentTurnRequest, AgentTurnResponse>[] steps)
        {
            lock (_steps)
            {
                foreach (var step in steps) _steps.Enqueue(step);
            }
        }

        public AgentRuntimeProviderRegistration CreateProviderRegistration() => new() { Provider = RuntimeDescriptor, TurnExecutor = this };

        public IModelProviderTurnExecutor CreateTurnExecutor() => this;

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelProviderProbeResult
            {
                ProviderId = Descriptor.ProviderId,
                Availability = ModelProviderAvailability.Ready,
                Models = [new AgentModelInfo("fake-model", DisplayName: "Fake Model")],
                SelectedModelId = "fake-model",
            });

        public Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken = default)
        {
            var last = request.Conversation.LastOrDefault(static message => message.Role == AgentConversationRole.Tool)?.Parts.OfType<AgentMessagePart.ToolResult>().LastOrDefault();
            var result = last is null ? null : string.Concat(last.Result.Items.OfType<AgentToolResultItem.Text>().Select(static item => item.Value));
            Func<AgentTurnRequest, AgentTurnResponse> step;
            lock (_steps)
            {
                Requests.Add(new Request([.. request.Tools.Select(static tool => tool.Spec.Name)], string.Join("\n", request.SystemMessage, request.DeveloperInstructions), result));
                step = _steps.Count > 0 ? _steps.Dequeue() : Say("nothing was scripted");
            }

            return Task.FromResult(step(request));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codealta-ui-" + Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }
}
