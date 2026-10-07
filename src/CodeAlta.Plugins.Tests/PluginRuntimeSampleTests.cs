namespace CodeAlta.Plugins.Tests;

[TestClass]
public sealed class PluginRuntimeSampleTests
{
    private static readonly string[] ExpectedSamples =
    [
        "hello-command",
        "prompt-guidance",
        "ui-status",
        "ui-all-regions",
        "skill-root",
        "package-reference",
        "background-task",
        "instruction-path-normalizer",
        "multi-plugin-assembly",
        "desktop-and-terminal",
        "agent-tool",
        "alta-command",
        "timeline-card",
        "saved-data",
        "report-dialog",
        "todo",
    ];

    [TestMethod]
    public void BuiltInPluginRuntimeSkillContainsRealSamplePluginInputs()
    {
        var sampleRoot = GetSampleRoot();
        foreach (var sampleName in ExpectedSamples)
        {
            var samplePath = Path.Combine(sampleRoot, sampleName);
            var pluginPath = Path.Combine(samplePath, "plugin.cs");
            var readmePath = Path.Combine(samplePath, "README.md");
            Assert.IsTrue(Directory.Exists(samplePath), sampleName);
            Assert.IsTrue(File.Exists(pluginPath), pluginPath);
            Assert.IsTrue(File.ReadAllText(pluginPath).Contains("PluginBase", StringComparison.Ordinal), pluginPath);
            Assert.IsTrue(File.Exists(readmePath), readmePath);
            Assert.IsTrue(File.ReadAllText(readmePath).Length > 80, readmePath);
        }
    }

    [TestMethod]
    [TestCategory("RequiresDotNet10FileBuild")]
    public async Task BuiltInPluginRuntimeSkillSamplesBuildLoadAndUnload()
    {
        //CodeAltaPluginRuntimeStartup.RegisterMsBuildDefaults();
        using var temp = new TestTempDirectory();
        foreach (var sampleName in ExpectedSamples)
        {
            CopySample(sampleName, Path.Combine(temp.Path, sampleName));
        }

        var root = new PluginRoot { RootPath = temp.Path, Scope = CodeAlta.Plugins.Abstractions.PluginScope.Global };
        var generation = await new PluginRootBuildFileGenerator().GenerateAsync(root, new PluginRootBuildFileOptions
        {
            // This suite deliberately includes native terminal UI samples.
            AuthoringProfile = PluginAuthoringProfile.Terminal,
            CodeAltaExeFolder = AppContext.BaseDirectory,
            GlobalJsonContent = """
{
  "sdk": {
    "version": "10.0.100",
    "rollForward": "latestMinor",
    "allowPrerelease": false
  }
}
""",
            PackageVersions = LoadPluginPackageVersions(),
        });
        Assert.IsTrue(generation.Succeeded, string.Join(Environment.NewLine, generation.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var packages = new SourcePluginDiscoveryService().Discover(root).OrderBy(static package => package.PackageId, StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEquivalent(ExpectedSamples, packages.Select(static package => package.PackageId).ToArray());
        foreach (var package in packages)
        {
            var buildResult = await new PluginBuildService().BuildAsync(new PluginBuildRequest { Package = package, ForceRebuild = true });
            AssertIfFileBasedBuildIsUnsupported(buildResult);
            Assert.IsTrue(buildResult.Succeeded, BuildFailureMessage(buildResult));
            var unloadReference = LoadDiscoverAndStartUnload(buildResult);
            Assert.IsTrue(PluginAssemblyLoader.VerifyUnload(unloadReference), package.PackageId);
        }
    }

    private static void AssertIfFileBasedBuildIsUnsupported(PluginBuildResult buildResult)
    {
        if (!buildResult.Succeeded && BuildFailureMessage(buildResult).Contains("The project file could not be loaded", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive("The installed .NET SDK did not accept `dotnet build plugin.cs` file-based builds in this environment.");
        }
    }

    private static WeakReference LoadDiscoverAndStartUnload(PluginBuildResult buildResult)
    {
        var loadResult = new PluginAssemblyLoader(PluginAuthoringProfile.Terminal).Load(buildResult);
        Assert.IsTrue(loadResult.Succeeded, string.Join(Environment.NewLine, loadResult.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.IsNotNull(loadResult.LoadContext);
        Assert.IsTrue(new PluginTypeDiscoveryService().Discover(loadResult).Count > 0, buildResult.Package.PackageId);
        return PluginAssemblyLoader.CreateUnloadWeakReference(loadResult.LoadContext);
    }

    private static string BuildFailureMessage(PluginBuildResult buildResult)
        => string.Join(Environment.NewLine,
            buildResult.RuntimeDiagnostics.Select(static diagnostic => diagnostic.Message)
                .Concat(buildResult.Diagnostics.Select(static diagnostic => diagnostic.Message))
                .Concat([buildResult.StandardOutput, buildResult.StandardError]));

    private static IReadOnlyList<PluginPackageVersion> LoadPluginPackageVersions()
        => PluginPackageVersionProvider.ExtractPluginPackageVersionsFromFile(PluginTestPaths.DirectoryPackagesPropsPath);

    private static void CopySample(string sampleName, string destination)
    {
        var source = Path.Combine(GetSampleRoot(), sampleName);
        Directory.CreateDirectory(destination);
        foreach (var sourceFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var targetFile = Path.Combine(destination, Path.GetRelativePath(source, sourceFile));
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(sourceFile, targetFile, overwrite: true);
        }
    }

    private static string GetSampleRoot()
    {
        var sampleRoot = PluginTestPaths.PluginRuntimeSampleRoot;
        if (Directory.Exists(sampleRoot))
        {
            return sampleRoot;
        }

        throw new DirectoryNotFoundException("Could not find built-in plugin runtime sample folders.");
    }
}
