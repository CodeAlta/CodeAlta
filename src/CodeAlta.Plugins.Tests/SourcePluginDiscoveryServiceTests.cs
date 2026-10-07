using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.Tests;

[TestClass]
public sealed class SourcePluginDiscoveryServiceTests
{
    [TestMethod]
    public void DiscoverReturnsEmptyForMissingRoot()
    {
        var service = new SourcePluginDiscoveryService();
        var packages = service.Discover(new PluginRoot
        {
            RootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            Scope = PluginScope.Global,
        });

        Assert.AreEqual(0, packages.Count);
    }

    [TestMethod]
    public void DiscoverFindsSourcePackageAndSidecars()
    {
        using var temp = new TestTempDirectory();
        var packageDirectory = Path.Combine(temp.Path, "HelloWorld");
        Directory.CreateDirectory(Path.Combine(packageDirectory, "skills"));
        File.WriteAllText(Path.Combine(packageDirectory, "plugin.cs"), "public sealed class Plugin {}" );
        File.WriteAllText(Path.Combine(packageDirectory, "readme.md"), "# Hello" );

        var root = new PluginRoot { RootPath = temp.Path, Scope = PluginScope.Global };
        var packages = new SourcePluginDiscoveryService().Discover(root);

        Assert.AreEqual(1, packages.Count);
        Assert.AreEqual("HelloWorld", packages[0].PackageId);
        Assert.AreEqual(PluginScope.Global, packages[0].Root.Scope);
        Assert.IsNotNull(packages[0].Sidecars.ReadmePath);
        Assert.IsNotNull(packages[0].Sidecars.SkillsDirectory);
        Assert.AreEqual(0, packages[0].Diagnostics.Count);
    }

    [TestMethod]
    public void TheReadmeOfAPackage_IsNamedAsItIsOnDisk()
    {
        using var temp = new TestTempDirectory();
        string Package(string id, params string[] files)
        {
            var directory = Directory.CreateDirectory(Path.Combine(temp.Path, id)).FullName;
            File.WriteAllText(Path.Combine(directory, "plugin.cs"), "public sealed class Plugin {}");
            foreach (var file in files) File.WriteAllText(Path.Combine(directory, file), "# " + id);
            return directory;
        }

        // What `alta plugin create` writes, a name in another case, and a package without one.
        var upper = Package("upper", "README.md", "notes.md");
        var mixed = Package("mixed", "Readme.md");
        Package("none", "readme.txt");

        var packages = new SourcePluginDiscoveryService().Discover(new PluginRoot { RootPath = temp.Path, Scope = PluginScope.Global })
            .ToDictionary(static package => package.PackageId, static package => package.Sidecars.ReadmePath);

        // The exact name: a path in another case opens the file on Windows and names no file for git.
        Assert.AreEqual(Path.Combine(upper, "README.md"), packages["upper"]);
        Assert.AreEqual(Path.Combine(mixed, "Readme.md"), packages["mixed"]);
        Assert.IsNull(packages["none"]);
    }

    [TestMethod]
    public void DiscoverReturnsEmptyForEmptyRoot()
    {
        using var temp = new TestTempDirectory();

        var packages = new SourcePluginDiscoveryService().Discover(new PluginRoot { RootPath = temp.Path, Scope = PluginScope.Global });

        Assert.AreEqual(0, packages.Count);
    }

    [TestMethod]
    public void DiscoverReportsInvalidPackageIds()
    {
        using var temp = new TestTempDirectory();
        var packageDirectory = Path.Combine(temp.Path, "_bad");
        Directory.CreateDirectory(packageDirectory);
        File.WriteAllText(Path.Combine(packageDirectory, "plugin.cs"), "public sealed class Plugin {}" );

        var packages = new SourcePluginDiscoveryService().Discover(new PluginRoot { RootPath = temp.Path, Scope = PluginScope.Global });

        Assert.AreEqual(1, packages.Count);
        Assert.IsTrue(packages[0].Diagnostics.Any(diagnostic =>
            diagnostic.Severity == PluginDiagnosticSeverity.Error &&
            diagnostic.Message.Contains("unsupported characters", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void DiscoverReportsDuplicatePackageIdsAcrossSameScopeRoots()
    {
        using var temp = new TestTempDirectory();
        var firstRoot = Path.Combine(temp.Path, "first");
        var secondRoot = Path.Combine(temp.Path, "second");
        CreatePackage(firstRoot, "hello");
        CreatePackage(secondRoot, "hello");

        var packages = new SourcePluginDiscoveryService().Discover(
        [
            new PluginRoot { RootPath = firstRoot, Scope = PluginScope.Global },
            new PluginRoot { RootPath = secondRoot, Scope = PluginScope.Global },
        ]);

        Assert.AreEqual(2, packages.Count);
        Assert.IsTrue(packages[1].Diagnostics.Any(diagnostic =>
            diagnostic.Severity == PluginDiagnosticSeverity.Error &&
            diagnostic.Message.Contains("Duplicate global plugin package id", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void DiscoverReportsUnsupportedPackageBuildFiles()
    {
        using var temp = new TestTempDirectory();
        var packageDirectory = Path.Combine(temp.Path, "BadPackage");
        Directory.CreateDirectory(packageDirectory);
        File.WriteAllText(Path.Combine(packageDirectory, "plugin.cs"), "public sealed class Plugin {}" );
        File.WriteAllText(Path.Combine(packageDirectory, "Directory.Build.props"), "<Project />" );

        var packages = new SourcePluginDiscoveryService().Discover(new PluginRoot { RootPath = temp.Path, Scope = PluginScope.Project });

        Assert.AreEqual(1, packages.Count);
        Assert.AreEqual(1, packages[0].UnsupportedBuildFiles.Count);
        Assert.IsTrue(packages[0].Diagnostics.Any(diagnostic => diagnostic.Severity == PluginDiagnosticSeverity.Error));
    }

    [TestMethod]
    public void PathServiceDoesNotReturnMissingProjectRootByDefault()
    {
        using var temp = new TestTempDirectory();
        var service = new PluginRuntimePathService(
            Path.Combine(temp.Path, "global"),
            new PluginProjectContext { ProjectId = "project", ProjectPath = Path.Combine(temp.Path, "project") });

        var roots = service.GetCandidateRoots();

        Assert.AreEqual(0, roots.Count);
    }

    [TestMethod]
    public void PathServiceAssignsGlobalAndProjectScopes()
    {
        using var temp = new TestTempDirectory();
        var globalCodeAltaRoot = Path.Combine(temp.Path, "global");
        var globalRoot = Path.Combine(globalCodeAltaRoot, "plugins");
        var projectRoot = Path.Combine(temp.Path, "project", ".alta", "plugins");
        Directory.CreateDirectory(globalRoot);
        Directory.CreateDirectory(projectRoot);
        var service = new PluginRuntimePathService(
            globalCodeAltaRoot,
            new PluginProjectContext { ProjectId = "project", ProjectPath = Path.Combine(temp.Path, "project") });

        var roots = service.GetCandidateRoots();

        Assert.AreEqual(2, roots.Count);
        Assert.IsTrue(roots.Any(root => root.Scope == PluginScope.Global && string.Equals(root.RootPath, PluginRuntimePathService.NormalizeDirectory(globalRoot), StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(roots.Any(root => root.Scope == PluginScope.Project && root.ProjectId == "project"));
    }

    private static void CreatePackage(string rootPath, string packageId)
    {
        var packageDirectory = Path.Combine(rootPath, packageId);
        Directory.CreateDirectory(packageDirectory);
        File.WriteAllText(Path.Combine(packageDirectory, "plugin.cs"), "public sealed class Plugin {}" );
    }
}
