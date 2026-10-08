using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.Tests;

/// <summary>
/// What the running host says of the packages of its plugin folders, whatever state a folder, a source file or a
/// configuration file is in: a settings page and <c>alta plugin status</c> list from it.
/// </summary>
[TestClass]
public sealed class PluginRuntimePackagesTests
{
    [TestMethod]
    public async Task AProjectWhoseAltaFolderIsTheGlobalOne_HasOnePluginFolder_AndEachPackageOnce()
    {
        using var temp = new TestTempDirectory();
        // CodeAlta started in the home folder: `<project>/.alta/plugins` is `~/.alta/plugins`.
        var home = Directory.CreateDirectory(Path.Combine(temp.Path, "home")).FullName;
        var global = Path.Combine(home, ".alta");
        await using var runtime = new PluginRuntimeManager();
        await runtime.StartAsync(new PluginRuntimeManagerOptions
        {
            GlobalRoot = global, IsHeadless = true,
            // Written as another process may name it: with a separator at its end.
            ProjectContext = new PluginProjectContext { ProjectId = "home", ProjectPath = home + Path.DirectorySeparatorChar },
        });
        WritePlugin(Path.Combine(global, "plugins"), "notes", "// not built: written after the start\n");

        Assert.AreEqual(PluginScope.Global, runtime.Roots.Single().Scope);
        var package = runtime.GetPackages().Single();
        Assert.AreEqual(("notes", PluginScope.Global), (package.Package.PackageId, package.Package.Root.Scope));
    }

    [TestMethod]
    public async Task AProjectElsewhere_KeepsItsOwnPluginFolder()
    {
        using var temp = new TestTempDirectory();
        var global = Path.Combine(temp.Path, "home", ".alta");
        var project = Directory.CreateDirectory(Path.Combine(temp.Path, "project")).FullName;
        await using var runtime = new PluginRuntimeManager();
        await runtime.StartAsync(new PluginRuntimeManagerOptions { GlobalRoot = global, IsHeadless = true, ProjectContext = new PluginProjectContext { ProjectId = "project", ProjectPath = project } });
        WritePlugin(Path.Combine(global, "plugins"), "notes", "// global\n");
        WritePlugin(Path.Combine(project, ".alta", "plugins"), "notes", "// project\n");

        CollectionAssert.AreEqual(new[] { PluginScope.Global, PluginScope.Project }, runtime.Roots.Select(static root => root.Scope).ToArray());
        CollectionAssert.AreEqual(new[] { PluginScope.Global, PluginScope.Project }, runtime.GetPackages().Select(static package => package.Package.Root.Scope).ToArray());
    }

    [TestMethod]
    public async Task AnIncludeLineThatNamesNoPath_LeavesThePackageListed()
    {
        using var temp = new TestTempDirectory();
        var global = Path.Combine(temp.Path, "home");
        await using var runtime = new PluginRuntimeManager();
        await runtime.StartAsync(new PluginRuntimeManagerOptions { GlobalRoot = global, IsHeadless = true });
        WritePlugin(Path.Combine(global, "plugins"), "notes", "#:include \"more\0.cs\"\n// the rest\n");
        WritePlugin(Path.Combine(global, "plugins"), "other", "// a plugin beside it\n");

        var packages = runtime.GetPackages();

        CollectionAssert.AreEqual(new[] { "notes", "other" }, packages.Select(static package => package.Package.PackageId).ToArray());
        Assert.IsFalse(packages[0].SourceChanged, "A source that cannot be read is not said to have changed.");
    }

    [TestMethod]
    public async Task AConfigurationFileThatCannotBeRead_LeavesThePackagesListed_AndIsNamed()
    {
        using var temp = new TestTempDirectory();
        var global = Directory.CreateDirectory(Path.Combine(temp.Path, "home")).FullName;
        var config = Path.Combine(global, "config.toml");
        File.WriteAllText(config, "[plugins.notes]\nenabled = true\n");
        await using var runtime = new PluginRuntimeManager();
        await runtime.StartAsync(new PluginRuntimeManagerOptions { GlobalRoot = global, IsHeadless = true });
        WritePlugin(Path.Combine(global, "plugins"), "notes", "// written after the start\n");

        // Another program is writing the file: on Windows it cannot be read meanwhile.
        using var writer = new FileStream(config, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var package = runtime.GetPackages().Single();

        Assert.AreEqual("notes", package.Package.PackageId);
        if (!OperatingSystem.IsWindows()) return;
        Assert.IsFalse(package.Enabled, "Enablement is not guessed from a file that was not read.");
        var diagnostic = package.Diagnostics.Single(static diagnostic => diagnostic.Source == PluginRuntimeDiagnosticSource.Config);
        Assert.AreEqual((PluginDiagnosticSeverity.Error, config), (diagnostic.Severity, diagnostic.Path));
    }

    private static void WritePlugin(string root, string id, string source)
        => File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(root, id)).FullName, "plugin.cs"), source);
}
