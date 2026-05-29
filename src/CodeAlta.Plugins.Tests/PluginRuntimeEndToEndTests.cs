using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.Tests;

[TestClass]
public sealed class PluginRuntimeEndToEndTests
{
    [TestMethod]
    [TestCategory("RequiresDotNet10FileBuild")]
    public async Task EnabledSourcePluginBuildsLoadsActivatesAndDeactivates()
    {
        CodeAltaPluginRuntimeStartup.RegisterMsBuildDefaults();
        using var temp = new TestTempDirectory();
        var root = new PluginRoot { RootPath = temp.Path, Scope = PluginScope.Global };
        CopySample("hello-command", Path.Combine(temp.Path, "hello-command"));
        var generation = await new PluginRootBuildFileGenerator().GenerateAsync(root, new PluginRootBuildFileOptions
        {
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
        var package = new SourcePluginDiscoveryService().Discover(root).Single();
        var buildResult = await new PluginBuildService(new PluginBuildManifestStore(Path.Combine(temp.Path, ".cache"), "codealta-test", "sdk-test"))
            .BuildAsync(new PluginBuildRequest { Package = package, ForceRebuild = true });
        AssertIfFileBasedBuildIsUnsupported(buildResult);

        Assert.IsTrue(buildResult.Succeeded, BuildFailureMessage(buildResult));

        var registry = new PluginContributionRegistry();
        var activePlugin = await LoadAndActivateAsync(buildResult, package, registry, temp.Path);
        AssertHelloContribution(registry);

        var deactivationDiagnostics = await activePlugin.DeactivateAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(0, deactivationDiagnostics.Count, string.Join(Environment.NewLine, deactivationDiagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.AreEqual(0, registry.GetSnapshot().Count);
        Assert.AreEqual(PluginRuntimeState.Unloaded, activePlugin.State);
    }

    [TestMethod]
    public void ConfigDisabledSourcePluginIsNotBuiltByEnablementPolicy()
    {
        using var temp = new TestTempDirectory();
        var packageDirectory = Path.Combine(temp.Path, "disabled");
        Directory.CreateDirectory(packageDirectory);
        File.WriteAllText(Path.Combine(packageDirectory, "plugin.cs"), "public sealed class Plugin { }");
        var package = new SourcePluginDiscoveryService()
            .Discover(new PluginRoot { RootPath = temp.Path, Scope = PluginScope.Global })
            .Single();
        var config = new CodeAlta.Catalog.CodeAltaConfigDocument
        {
            Plugins = new Dictionary<string, CodeAlta.Catalog.CodeAltaPluginSettingsDocument>
            {
                ["disabled"] = new() { Enabled = false },
            },
        };

        var enablement = new PluginRuntimeConfigResolver().ResolveSourcePlugin(package, config);

        Assert.IsFalse(enablement.Enabled);
    }

    [TestMethod]
    [TestCategory("RequiresDotNet10FileBuild")]
    public async Task SourcePluginCanReloadAndUnloadRepeatedly()
    {
        CodeAltaPluginRuntimeStartup.RegisterMsBuildDefaults();
        using var temp = new TestTempDirectory();
        var (package, buildResult) = await BuildHelloCommandSampleAsync(temp);

        for (var i = 0; i < 2; i++)
        {
            var registry = new PluginContributionRegistry();
            var activePlugin = await LoadAndActivateAsync(buildResult, package, registry, temp.Path);
            AssertHelloContribution(registry);

            var diagnostics = await activePlugin.DeactivateAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(0, diagnostics.Count, string.Join(Environment.NewLine, diagnostics.Select(static diagnostic => diagnostic.Message)));
            Assert.AreEqual(0, registry.GetSnapshot().Count);
            Assert.AreEqual(PluginRuntimeState.Unloaded, activePlugin.State);
        }
    }

    [TestMethod]
    [TestCategory("RequiresDotNet10FileBuild")]
    public async Task PrivatePackageDependencyBuildsLoadsAndExecutesThroughEnableDynamicLoading()
    {
        CodeAltaPluginRuntimeStartup.RegisterMsBuildDefaults();
        using var temp = new TestTempDirectory();
        var (package, buildResult) = await BuildSampleAsync(temp, "package-reference");
        var registry = new PluginContributionRegistry();
        var activePlugin = await LoadAndActivateAsync(buildResult, package, registry, temp.Path);
        var adapter = new PluginContributionAdapterService(registry);

        var (result, diagnostics) = await ExecuteSingleCommandAsync(registry, adapter, activePlugin);

        Assert.AreEqual(PluginCommandDisposition.Handled, result.Disposition);
        Assert.AreEqual("Humanizer says: Sample plugin", result.UserMessage);
        Assert.AreEqual(0, diagnostics.Count, string.Join(Environment.NewLine, diagnostics.Select(static diagnostic => diagnostic.Message)));
        var deactivationDiagnostics = await activePlugin.DeactivateAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, deactivationDiagnostics.Count, string.Join(Environment.NewLine, deactivationDiagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    private static string BuildFailureMessage(PluginBuildResult buildResult)
        => string.Join(Environment.NewLine,
            buildResult.RuntimeDiagnostics.Select(static diagnostic => diagnostic.Message)
                .Concat(buildResult.Diagnostics.Select(static diagnostic => diagnostic.Message))
                .Concat([buildResult.StandardOutput, buildResult.StandardError]));

    private static async Task<(PluginCommandResult Result, IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics)> ExecuteSingleCommandAsync(
        PluginContributionRegistry registry,
        PluginContributionAdapterService adapter,
        ActivePluginInstance activePlugin)
    {
        var command = (PluginCommandContribution)registry.GetSnapshot().Single(static registration => registration.Contribution is PluginCommandContribution).Contribution;
        return await adapter.ExecuteCommandAsync([activePlugin], command);
    }

    private static async Task<(SourcePluginPackage Package, PluginBuildResult BuildResult)> BuildHelloCommandSampleAsync(TestTempDirectory temp)
        => await BuildSampleAsync(temp, "hello-command");

    private static async Task<(SourcePluginPackage Package, PluginBuildResult BuildResult)> BuildSampleAsync(TestTempDirectory temp, string sampleName)
    {
        var root = new PluginRoot { RootPath = temp.Path, Scope = PluginScope.Global };
        CopySample(sampleName, Path.Combine(temp.Path, sampleName));
        var generation = await new PluginRootBuildFileGenerator().GenerateAsync(root, new PluginRootBuildFileOptions
        {
            CodeAltaExeFolder = AppContext.BaseDirectory,
            GlobalJsonContent = DefaultGlobalJsonContent,
            PackageVersions = LoadPluginPackageVersions(),
        });
        Assert.IsTrue(generation.Succeeded, string.Join(Environment.NewLine, generation.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var package = new SourcePluginDiscoveryService().Discover(root).Single();
        var buildResult = await new PluginBuildService(new PluginBuildManifestStore(Path.Combine(temp.Path, ".cache"), "codealta-test", "sdk-test"))
            .BuildAsync(new PluginBuildRequest { Package = package, ForceRebuild = true });

        AssertIfFileBasedBuildIsUnsupported(buildResult);
        Assert.IsTrue(buildResult.Succeeded, BuildFailureMessage(buildResult));
        return (package, buildResult);
    }

    private static void AssertIfFileBasedBuildIsUnsupported(PluginBuildResult buildResult)
    {
        if (!buildResult.Succeeded && BuildFailureMessage(buildResult).Contains("The project file could not be loaded", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive("The installed .NET SDK did not accept `dotnet build plugin.cs` file-based builds in this environment.");
        }
    }

    private const string DefaultGlobalJsonContent = """
{
  "sdk": {
    "version": "10.0.100",
    "rollForward": "latestMinor",
    "allowPrerelease": false
  }
}
""";

    private static async Task<ActivePluginInstance> LoadAndActivateAsync(
        PluginBuildResult buildResult,
        SourcePluginPackage package,
        PluginContributionRegistry registry,
        string tempPath)
    {
        var loadResult = new PluginAssemblyLoader().Load(buildResult);
        Assert.IsTrue(loadResult.Succeeded, string.Join(Environment.NewLine, loadResult.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var discoveredType = new PluginTypeDiscoveryService().Discover(loadResult).Single();
        var activationResult = await new PluginRuntimeActivator(registry).ActivateAsync(
            discoveredType,
            package,
            loadResult.LoadContext,
            new PluginActivationOptions { HostInfo = CreateHostInfo(tempPath) });

        Assert.IsTrue(activationResult.Succeeded, string.Join(Environment.NewLine, activationResult.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.IsNotNull(activationResult.ActivePlugin);
        return activationResult.ActivePlugin;
    }

    private static void AssertHelloContribution(PluginContributionRegistry registry)
    {
        var snapshot = registry.GetSnapshot();
        Assert.AreEqual(1, snapshot.Count);
        Assert.AreEqual("hello", snapshot[0].Handle.NaturalName);
    }

    private static PluginHostInfo CreateHostInfo(string tempPath)
        => new()
        {
            ApplicationName = "CodeAlta.Tests",
            Version = "1.0.0",
            HostApiVersion = "1.0.0",
            UserDataDirectory = tempPath,
            IsHeadless = true,
        };

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
