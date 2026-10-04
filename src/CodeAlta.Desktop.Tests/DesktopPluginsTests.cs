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
        CollectionAssert.AreEqual(new[] { "github", "mcp", "statistics" }, DesktopPlugins.BuiltIns.Select(static plugin => plugin.Id).ToArray());
        foreach (var definition in DesktopPlugins.BuiltIns)
        {
            var plugin = definition.Factory();
            Assert.AreEqual(definition.PluginType, plugin.GetType());
            // The terminal's dialogs and pickers are commands and prompt-editor contributions of its own composition.
            Assert.AreEqual(0, plugin.GetCommands().Count());
            Assert.AreEqual(0, plugin.GetPromptEditorContributions().Count());
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
                PluginServices = new DesktopPluginServices(pluginAlta),
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
    public void PluginWorkspace_NamesNoProjectOutsideAToolCall()
    {
        var workspace = new DesktopPluginServices(new PluginAltaServiceBridge()).Workspace;

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
