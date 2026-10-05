using CodeAlta.Catalog;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Tui.App;
using CodeAlta.Tui.Frontend.Commands;

namespace CodeAlta.Tests;

/// <summary>How the terminal application hosts its plugins: the services they start with and what its plugin dialog reports.</summary>
[TestClass]
public sealed class TerminalPluginHostTests
{
    [TestMethod]
    public async Task PluginsStartedBeforeTheApplication_KeepTheServicesItBindsLater()
    {
        var root = Directory.CreateTempSubdirectory("CodeAlta-terminal-plugins-").FullName;
        try
        {
            var services = new CodeAltaPluginServices();
            await using var runtime = new PluginRuntimeManager();
            Assert.IsNull(runtime.HostServices);

            var started = await runtime.StartAsync(new PluginRuntimeManagerOptions
            {
                GlobalRoot = root, Frontend = PluginFrontends.Terminal, Services = services,
                BuiltIns = [new BuiltInPluginDefinition { Id = "fixture", DisplayName = "Fixture", PluginType = typeof(FixturePlugin), Factory = static () => new FixturePlugin() }],
            });

            // The application finds them on the runtime it borrows, and attaches its window to the same objects.
            Assert.AreSame(services, runtime.HostServices);
            var plugin = started.ActivePlugins.Single().RuntimeContext.Services;
            Assert.AreSame(services.TerminalUi, plugin.Ui);
            Assert.AreSame(services.TerminalUi, plugin.Sessions);
            Assert.AreSame(services.TerminalUi, plugin.Prompts);
            Assert.IsFalse(plugin.Ui.HasInteractiveUi, "no window yet");
            Assert.AreSame(services.AltaBridge, services.Alta);

            var bridge = new PluginHostBridge(runtime, static () => null, services.AltaBridge, services.TerminalUi);
            Assert.AreSame(services.TerminalUi, bridge.Ui);
            // Without a window a queued prompt is not taken, and the command adapter sends it instead.
            Assert.IsFalse(await new PluginHostCommandService(bridge).TryEnqueuePromptAsync("prompt"));
            Assert.IsFalse(await new PluginHostCommandService(null).TryEnqueuePromptAsync("prompt"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }

    [TestMethod]
    public void PluginDialog_ReportsWhatTheRuntimeDidWithAPlugin()
    {
        var root = Directory.CreateTempSubdirectory("CodeAlta-terminal-plugins-").FullName;
        try
        {
            foreach (var id in new[] { "broken", "desktop-only", "fine" })
            {
                File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(root, "plugins", id)).FullName, "plugin.cs"), "// plugin");
            }

            PluginRuntimeDiagnostic[] diagnostics =
            [
                PluginRuntimeDiagnostic.Error(PluginRuntimeDiagnosticSource.Build, "Plugin build failed: plugin.cs(1,1): error CS1002: ; expected", "broken"),
                PluginRuntimeManager.CreateUnsupportedFrontendDiagnostic(
                    new PluginDescriptor { RuntimeKey = "source:DesktopOnly", TypeName = "DesktopOnly", AssemblyName = "plugin", Frontends = PluginFrontends.Desktop },
                    PluginFrontends.Terminal, "desktop-only", null)!,
            ];
            var service = new PluginManagementService(new CatalogOptions { GlobalRoot = root }, static () => null, () => diagnostics);

            var entries = service.LoadSnapshot().Entries.Where(static entry => entry.LoadUnitKind == PluginLoadUnitKind.Source).ToDictionary(static entry => entry.PluginId!);

            Assert.AreEqual(PluginManagementState.Failed, entries["broken"].State);
            StringAssert.Contains(entries["broken"].Diagnostics.Single().Message, "error CS1002");
            Assert.AreEqual(PluginManagementState.Enabled, entries["desktop-only"].State);
            Assert.AreEqual("Plugin 'source:DesktopOnly' was not started: it does not support the terminal application.", entries["desktop-only"].Diagnostics.Single().Message);
            Assert.AreEqual(0, entries["fine"].Diagnostics.Count);
            Assert.IsTrue(new PluginManagementService(new CatalogOptions { GlobalRoot = root }, static () => null).LoadSnapshot().Entries.All(static entry => entry.Diagnostics.Count == 0));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }

    public sealed class FixturePlugin : PluginBase;
}
