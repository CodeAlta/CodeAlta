using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class ModelProviderApplicationTests
{
    // The providers of a host say which CodeAlta calls them (the categories of OpenRouter): the host names its frontend.
    [TestMethod]
    [DataRow(PluginFrontends.Desktop, "desktop")]
    [DataRow(PluginFrontends.Terminal, "tui")]
    [DataRow(PluginFrontends.Cli, "cli")]
    [DataRow(PluginFrontends.None, null)]
    public async Task TheRegistryOfAHost_NamesItsApplication(PluginFrontends frontend, string? application)
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeAlta-provider-application-" + Guid.NewGuid().ToString("N"));
        try
        {
            var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
            var project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
            var builtins = Directory.CreateDirectory(Path.Combine(root, "builtin-skills")).FullName;
            var alta = Path.Combine(home, ".alta");
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = alta,
                StateRoot = alta,
                CurrentProjectPath = project,
                DiscoveryScope = new SessionDiscoveryScope(home, project),
                BuiltInSkillRoot = builtins,
                StartPlugins = false,
                OwnsLogging = false,
                PluginFrontend = frontend,
            });

            Assert.AreEqual(application, host.ModelProviderRegistry.Application);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
