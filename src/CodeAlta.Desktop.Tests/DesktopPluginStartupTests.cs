using System.Text;
using CodeAlta.Desktop;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Desktop.Tests;

/// <summary>What the start-up screen and the Plugins page say about plugins that are built, skipped or failed.</summary>
[TestClass]
public sealed class DesktopPluginStartupTests
{
    [TestMethod]
    public void Describe_NamesOnePluginAndCountsSeveral()
    {
        Assert.AreEqual(string.Empty, DesktopPluginStartupFeedback.Describe([], 2, 2));
        Assert.AreEqual("Building plugin notes…", DesktopPluginStartupFeedback.Describe(["notes"], 0, 1));
        Assert.AreEqual("Building plugin notes (2 of 3)…", DesktopPluginStartupFeedback.Describe(["notes"], 1, 3));
        Assert.AreEqual("Building 3 plugins…", DesktopPluginStartupFeedback.Describe(["a", "b", "c"], 0, 3));
        Assert.AreEqual("Building 4 plugins (1 done)…", DesktopPluginStartupFeedback.Describe(["b", "c"], 1, 4));
    }

    [TestMethod]
    public async Task Feedback_ShowsTheBuildsWhileTheyRun_AndNothingAfterwards()
    {
        var status = new DesktopStartupStatus();
        var feedback = new DesktopPluginStartupFeedback(status);
        var seen = new List<string>();

        var result = await feedback.RunAsync<int>([], waitForAcknowledgement: false, (progress, _) =>
        {
            void Report(string id, PluginBuildProgressState state)
            {
                progress!.Report(new PluginBuildProgress { Package = Package(id), Index = 0, Total = 2, State = state });
                seen.Add(status.Text);
            }

            Report("first", PluginBuildProgressState.UpToDate);
            progress!.MarkActivating();
            seen.Add(status.Text);
            Report("second", PluginBuildProgressState.Running);
            Report("second", PluginBuildProgressState.Succeeded);
            progress.MarkActivating();
            seen.Add(status.Text);
            return ValueTask.FromResult(7);
        }, static (_, _) => "summary", CancellationToken.None);

        Assert.AreEqual(7, result);
        // A start where nothing is built says nothing, not even while the plugins start.
        CollectionAssert.AreEqual(new[] { "", "", "Building plugin second (2 of 2)…", "", "Starting plugins…" }, seen);
        Assert.AreEqual(string.Empty, status.Text);
        Assert.AreEqual("""{"text":""}""", Encoding.UTF8.GetString(status.ToJson()));

        status.Text = "Building \"x\"…";
        Assert.AreEqual("Building \"x\"…", System.Text.Json.JsonDocument.Parse(status.ToJson()).RootElement.GetProperty("text").GetString());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await feedback.RunAsync<int>([], false, (progress, _) =>
            {
                progress!.Report(new PluginBuildProgress { Package = Package("x"), Index = 0, Total = 1, State = PluginBuildProgressState.Running });
                throw new InvalidOperationException();
            }, static (_, _) => "summary", CancellationToken.None));
        Assert.AreEqual(string.Empty, status.Text, "a failed start leaves no status behind");
    }

    [TestMethod]
    public void StartupFailures_NameThePluginsThatDidNotStart()
    {
        static PluginRuntimeDiagnostic Error(string? package, string? key = null)
            => PluginRuntimeDiagnostic.Error(PluginRuntimeDiagnosticSource.Build, "Plugin build failed.", package) with { RuntimeKey = key };

        Assert.IsNull(DesktopPlugins.DescribeStartupFailures([]));
        Assert.IsNull(DesktopPlugins.DescribeStartupFailures([PluginRuntimeDiagnostic.Info(PluginRuntimeDiagnosticSource.Build, "Plugin build finished.", "fine")]));
        Assert.AreEqual("The plugin notes could not be started. Settings > Plugins shows why.",
            DesktopPlugins.DescribeStartupFailures([Error("notes"), Error("Notes"), Error(null)]));
        Assert.AreEqual("The plugins a, source:b could not be started. Settings > Plugins shows why.",
            DesktopPlugins.DescribeStartupFailures([Error("a"), Error(null, "source:b")]));
        Assert.AreEqual("4 plugins could not be started. Settings > Plugins shows why.",
            DesktopPlugins.DescribeStartupFailures([Error("a"), Error("b"), Error("c"), Error("d")]));
    }

    [TestMethod]
    public void RuntimeState_SaysWhatTheHostDidWithAPackage()
    {
        var unsupported = PluginRuntimeManager.CreateUnsupportedFrontendDiagnostic(
            new PluginDescriptor { RuntimeKey = "source:TerminalOnly", TypeName = "TerminalOnly", AssemblyName = "plugin", Frontends = PluginFrontends.Terminal }, PluginFrontends.Desktop, "terminal-only", null)!;
        var built = PluginRuntimeDiagnostic.Info(PluginRuntimeDiagnosticSource.Build, "Plugin build finished.", "notes");
        var broken = PluginRuntimeDiagnostic.Error(PluginRuntimeDiagnosticSource.Build, "Plugin build failed: plugin.cs(6,27): error CS0103: The name\r\n'x' does not exist", "notes");
        const string Reason = "Plugin build failed: plugin.cs(6,27): error CS0103: The name'x' does not exist";
        static PluginPackageStatus Status(PluginPackageState state, params PluginRuntimeDiagnostic[] diagnostics) => new() { Package = Package("notes"), State = state, Diagnostics = diagnostics };

        Assert.AreEqual(("running", (string?)null), PluginsService.RuntimeState(Status(PluginPackageState.Running, built)));
        Assert.AreEqual(("failed", (string?)Reason), PluginsService.RuntimeState(Status(PluginPackageState.Failed, built, broken)));
        // A plugin that still runs says why its new source was not loaded.
        Assert.AreEqual(("running", (string?)Reason), PluginsService.RuntimeState(Status(PluginPackageState.Running, broken)));
        Assert.AreEqual(("unsupported", (string?)null), PluginsService.RuntimeState(Status(PluginPackageState.Unsupported, unsupported)));
        // Created or turned on after the host started: nothing was done with it.
        Assert.AreEqual(("stopped", (string?)null), PluginsService.RuntimeState(Status(PluginPackageState.Stopped)));
    }

    private static SourcePluginPackage Package(string id)
        => new() { PackageId = id, Root = new PluginRoot { RootPath = "root", Scope = PluginScope.Global }, PackageDirectory = "root/" + id, EntryFilePath = "root/" + id + "/plugin.cs" };
}
