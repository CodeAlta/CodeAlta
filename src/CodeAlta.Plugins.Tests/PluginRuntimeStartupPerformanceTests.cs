using System.Diagnostics;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.Tests;

[TestClass]
public sealed class PluginRuntimeStartupPerformanceTests
{
    [TestMethod]
    [TestCategory("RequiresDotNet10FileBuild")]
    public async Task StartupMeasuresStaleAndUpToDateEnabledPluginPaths()
    {
        //CodeAltaPluginRuntimeStartup.RegisterMsBuildDefaults();
        using var temp = new TestTempDirectory();
        var globalRoot = Path.Combine(temp.Path, "home");
        var projectRoot = Path.Combine(temp.Path, "project");
        Directory.CreateDirectory(globalRoot);
        Directory.CreateDirectory(Path.Combine(projectRoot, ".alta"));
        File.WriteAllText(Path.Combine(projectRoot, ".alta", "config.toml"), """
[plugins."hello-command"]
enabled = true
""");
        CopyDirectory(
            FindSampleDirectory("hello-command"),
            Path.Combine(projectRoot, ".alta", "plugins", "hello-command"));

        var stale = await MeasureStartupAsync(globalRoot, projectRoot);
        if (stale.Result.BuildResults.Count == 0 && stale.Result.Diagnostics.Any(diagnostic => diagnostic.Message.Contains("skipped", StringComparison.OrdinalIgnoreCase)))
        {
            Assert.Inconclusive("The enabled sample plugin was skipped in this environment.");
        }

        if (stale.Result.BuildResults.Any(IsFileBasedBuildUnsupported))
        {
            Assert.Inconclusive("The installed .NET SDK did not accept `dotnet build plugin.cs` file-based builds in this environment.");
        }

        Assert.IsTrue(stale.Result.BuildResults.All(static result => result.Succeeded), FormatDiagnostics(stale.Result));
        Assert.IsTrue(stale.Result.ActivePlugins.Any(static plugin => plugin.Descriptor.RuntimeKey.Contains("hello-command", StringComparison.OrdinalIgnoreCase)), FormatDiagnostics(stale.Result));
        Assert.IsTrue(stale.Elapsed < TestTime.Minutes(2), $"Stale plugin startup took {stale.Elapsed}.");

        var upToDate = await MeasureStartupAsync(globalRoot, projectRoot);
        Assert.IsTrue(upToDate.Result.BuildResults.All(static result => result.Succeeded), FormatDiagnostics(upToDate.Result));
        Assert.IsTrue(upToDate.Result.BuildResults.Any(static result => result.IsUpToDate), FormatDiagnostics(upToDate.Result));
        Assert.IsTrue(upToDate.Elapsed < TestTime.Seconds(20), $"Up-to-date plugin startup took {upToDate.Elapsed}.");
    }

    private static async Task<(PluginRuntimeManagerStartResult Result, TimeSpan Elapsed)> MeasureStartupAsync(string globalRoot, string projectRoot)
    {
        await using var runtime = new PluginRuntimeManager();
        var stopwatch = Stopwatch.StartNew();
        var result = await runtime.StartAsync(new PluginRuntimeManagerOptions
        {
            GlobalRoot = globalRoot,
            ProjectContext = new PluginProjectContext
            {
                ProjectId = "perf-test",
                ProjectPath = projectRoot,
            },
            IsHeadless = true,
            RawArguments = [],
        });
        stopwatch.Stop();
        return (result, stopwatch.Elapsed);
    }

    private static string FormatDiagnostics(PluginRuntimeManagerStartResult result)
        => string.Join(Environment.NewLine,
            result.Diagnostics.Select(static diagnostic => diagnostic.Message)
                .Concat(result.BuildResults.SelectMany(static build => build.RuntimeDiagnostics.Select(static diagnostic => diagnostic.Message)))
                .Concat(result.BuildResults.SelectMany(static build => build.Diagnostics.Select(static diagnostic => diagnostic.Message))));

    private static bool IsFileBasedBuildUnsupported(PluginBuildResult result)
        => !result.Succeeded && FormatDiagnostics(result).Contains("The project file could not be loaded", StringComparison.OrdinalIgnoreCase);

    private static string FormatDiagnostics(PluginBuildResult result)
        => string.Join(Environment.NewLine,
            result.RuntimeDiagnostics.Select(static diagnostic => diagnostic.Message)
                .Concat(result.Diagnostics.Select(static diagnostic => diagnostic.Message))
                .Concat([result.StandardOutput, result.StandardError]));

    private static string FindSampleDirectory(string name)
    {
        var candidate = Path.Combine(PluginTestPaths.PluginRuntimeSampleRoot, name);
        if (Directory.Exists(candidate))
        {
            return candidate;
        }

        throw new DirectoryNotFoundException($"Could not find sample plugin '{name}'.");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
