using CodeAlta.Catalog;
using CodeAlta.Frontend.Commands;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.Tests;

[TestClass]
public sealed class McpShellCommandTests
{
    [TestMethod]
    public void Catalog_ContainsMcpManagementCommandPaletteMetadata()
    {
        var metadata = ShellCommandCatalog.Get("CodeAlta.Mcp.Manage");

        Assert.AreEqual("MCP Servers", metadata.Label);
        Assert.AreEqual("mcp", metadata.CommandName);
        CollectionAssert.Contains(metadata.Aliases.ToArray(), "mcp_servers");
        Assert.IsTrue(metadata.ShowInCommandPalette);
        Assert.IsTrue(metadata.ShowInCommandBar);
        Assert.AreEqual(ShellCommandCatalog.McpServersShortcutSequence, metadata.Sequence);
    }

    [TestMethod]
    public async Task DialogCommandHandlers_RouteMcpCommandToDialogService()
    {
        var registry = new ShellCommandRegistry();
        var service = new FakeDialogService();
        DialogCommandHandlers.Register(
            registry,
            _ => Task.CompletedTask,
            () => { },
            _ => { },
            service);
        var dispatcher = new ShellCommandDispatcher(registry);

        await dispatcher.DispatchAsync(new OpenMcpServersCommand());

        Assert.AreEqual(1, service.OpenMcpServersCount);
    }

    private sealed class FakeDialogService : IShellDialogCommandService
    {
        public int OpenMcpServersCount { get; private set; }

        public Rectangle? GetDialogBounds() => null;

        public Visual? GetDialogFocusTarget() => null;

        public IReadOnlyList<ProjectDescriptor> GetProjects() => [];

        public Task OpenFolderAsync(string path, bool trustFolder) => Task.CompletedTask;

        public Task OpenModelProvidersAsync() => Task.CompletedTask;

        public void OpenAbout()
        {
        }

        public void OpenModels()
        {
        }

        public void OpenApplicationLogs()
        {
        }

        public Task OpenFileEditorAsync() => Task.CompletedTask;

        public Task OpenSkillsAsync() => Task.CompletedTask;

        public Task OpenPluginsAsync() => Task.CompletedTask;

        public Task OpenMcpServersAsync()
        {
            OpenMcpServersCount++;
            return Task.CompletedTask;
        }

        public void OpenWorkspaceSettings()
        {
        }

        public void OpenSessionUsage()
        {
        }

        public void OpenSessionInfo()
        {
        }

        public void OpenExpandedPromptEditor()
        {
        }

        public void ToggleCommandBarMultiLine()
        {
        }

        public void ExitApp()
        {
        }
    }
}
