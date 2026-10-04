using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.Tests;

[TestClass]
public sealed class PluginBuildServiceTests
{
    [TestMethod]
    public async Task BuildAsyncHonorsAlreadyCanceledToken()
    {
        //CodeAltaPluginRuntimeStartup.RegisterMsBuildDefaults();
        using var temp = new TestTempDirectory();
        var package = CreatePackage(temp.Path, "public sealed class Plugin { }");
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            new PluginBuildService().BuildAsync(new PluginBuildRequest { Package = package }, cancellationTokenSource.Token).AsTask());
    }

    [TestMethod]
    public void BuildRunsWithoutAConsoleWindowOfItsOwn()
    {
        using var temp = new TestTempDirectory();
        var startInfo = PluginBuildService.CreateFileBuildStartInfo(CreatePackage(temp.Path, "public sealed class Plugin { }"));

        // The desktop has no console: a build that created one would put a console window over the application.
        Assert.IsTrue(startInfo.CreateNoWindow);
        Assert.IsFalse(startInfo.UseShellExecute);
    }

    [TestMethod]
    public async Task BuildAsyncReportsMissingSdkFromGlobalJsonMismatch()
    {
        //CodeAltaPluginRuntimeStartup.RegisterMsBuildDefaults();
        using var temp = new TestTempDirectory();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "global.json"), """
        {
          "sdk": {
            "version": "99.99.999",
            "rollForward": "disable"
          }
        }
        """);
        var package = CreatePackage(temp.Path, "public sealed class Plugin { }");

        var result = await new PluginBuildService().BuildAsync(new PluginBuildRequest { Package = package, ForceRebuild = true });

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(BuildFailureMessage(result).Contains("99.99.999", StringComparison.OrdinalIgnoreCase), BuildFailureMessage(result));
    }

    [TestMethod]
    [TestCategory("RequiresDotNet10FileBuild")]
    public async Task BuildAsyncRetainsCompilerErrorsInCapturedOutput()
    {
        //CodeAltaPluginRuntimeStartup.RegisterMsBuildDefaults();
        using var temp = new TestTempDirectory();
        await GenerateBuildFilesAsync(temp.Path);
        var package = CreatePackage(temp.Path, "public sealed class Plugin { public void Broken( } ");

        var result = await new PluginBuildService().BuildAsync(new PluginBuildRequest { Package = package, ForceRebuild = true });
        AssertIfFileBasedBuildIsUnsupported(result);

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(BuildFailureMessage(result).Contains("error", StringComparison.OrdinalIgnoreCase), BuildFailureMessage(result));
    }

    [TestMethod]
    [TestCategory("RequiresDotNet10FileBuild")]
    public async Task BuildAsyncRetainsCompilerWarningsInCapturedOutput()
    {
        //CodeAltaPluginRuntimeStartup.RegisterMsBuildDefaults();
        using var temp = new TestTempDirectory();
        await GenerateBuildFilesAsync(temp.Path);
        var package = CreatePackage(temp.Path, "#warning sample plugin warning\npublic sealed class Plugin { }");

        var result = await new PluginBuildService().BuildAsync(new PluginBuildRequest { Package = package, ForceRebuild = true });
        AssertIfFileBasedBuildIsUnsupported(result);

        Assert.IsTrue(result.Succeeded, BuildFailureMessage(result));
        Assert.IsTrue(BuildFailureMessage(result).Contains("warning", StringComparison.OrdinalIgnoreCase), BuildFailureMessage(result));
    }

    [TestMethod]
    [TestCategory("RequiresDotNet10FileBuild")]
    public async Task BuildAsyncUsesNativeFileBuildWithoutGeneratedProject()
    {
        //CodeAltaPluginRuntimeStartup.RegisterMsBuildDefaults();
        using var temp = new TestTempDirectory();
        await GenerateBuildFilesAsync(temp.Path);
        var package = CreatePackage(temp.Path, "public sealed class Plugin { }");

        var result = await new PluginBuildService().BuildAsync(new PluginBuildRequest { Package = package, ForceRebuild = true });
        AssertIfFileBasedBuildIsUnsupported(result);

        Assert.IsTrue(result.Succeeded, BuildFailureMessage(result));
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.OutputAssemblyPath));
        Assert.IsTrue(File.Exists(result.OutputAssemblyPath), result.OutputAssemblyPath);
        Assert.IsTrue(result.TargetOutputs.Any(static output => string.Equals(output.TargetName, "CodeAltaPluginTargetPath", StringComparison.Ordinal)));
        Assert.IsFalse(File.Exists(Path.Combine(package.PackageDirectory, ".codealta.plugin.csproj")));
        Assert.IsFalse(File.Exists(Path.Combine(package.PackageDirectory, ".codealta.plugin.g.cs")));
        var runtimeDirectory = Path.Combine(Path.GetDirectoryName(result.OutputAssemblyPath)!, "runtimes");
        Assert.IsFalse(Directory.Exists(runtimeDirectory) && Directory.EnumerateFiles(runtimeDirectory, "*", SearchOption.AllDirectories).Any());
    }

    private static SourcePluginPackage CreatePackage(string rootPath, string source)
    {
        var packageDirectory = Path.Combine(rootPath, "sample");
        Directory.CreateDirectory(packageDirectory);
        var entryPath = Path.Combine(packageDirectory, "plugin.cs");
        File.WriteAllText(entryPath, source);
        return new SourcePluginPackage
        {
            PackageId = "sample",
            Root = new PluginRoot { RootPath = rootPath, Scope = PluginScope.Global },
            PackageDirectory = packageDirectory,
            EntryFilePath = entryPath,
        };
    }

    private static async Task GenerateBuildFilesAsync(string rootPath)
    {
        var generation = await new PluginRootBuildFileGenerator().GenerateAsync(
            new PluginRoot { RootPath = rootPath, Scope = PluginScope.Global },
            new PluginRootBuildFileOptions
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
    }

    private static void AssertIfFileBasedBuildIsUnsupported(PluginBuildResult result)
    {
        if (!result.Succeeded && BuildFailureMessage(result).Contains("The project file could not be loaded", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive("The installed .NET SDK did not accept `dotnet build plugin.cs` file-based builds in this environment.");
        }
    }

    private static string BuildFailureMessage(PluginBuildResult buildResult)
        => string.Join(Environment.NewLine,
            buildResult.RuntimeDiagnostics.Select(static diagnostic => diagnostic.Message)
                .Concat(buildResult.Diagnostics.Select(static diagnostic => diagnostic.Message))
                .Concat([buildResult.StandardOutput, buildResult.StandardError]));

    private static IReadOnlyList<PluginPackageVersion> LoadPluginPackageVersions()
        => PluginPackageVersionProvider.ExtractPluginPackageVersionsFromFile(PluginTestPaths.DirectoryPackagesPropsPath);
}
