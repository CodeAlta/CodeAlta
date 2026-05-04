using System.Reflection;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.Tests;

[TestClass]
public sealed class PluginStartupFeedbackReporterTests
{
    [TestMethod]
    public void ReporterKeepsFastPathQuietAndWritesInteractiveProgress()
    {
        using var temp = new TestTempDirectory();
        var package = CreatePackage(temp.Path, "hello");
        var interactive = new List<string>();
        var headless = new List<string>();
        var reporter = new PluginStartupFeedbackReporter(PluginStartupFeedbackMode.Interactive, interactive.Add, headless.Add);

        reporter.ReportStaleBuilds(1);
        reporter.ReportProgress(new PluginBuildProgress { Package = package, Index = 0, Total = 1, State = PluginBuildProgressState.Running });
        reporter.ReportResult(new PluginBuildResult { Package = package, Succeeded = true, IsUpToDate = true });

        Assert.AreEqual(2, interactive.Count);
        Assert.AreEqual(0, headless.Count);
        Assert.IsFalse(interactive.Any(static message => message.Contains("up", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void ReporterUsesHeadlessFallbackWithoutMarkupControlSequences()
    {
        using var temp = new TestTempDirectory();
        var package = CreatePackage(temp.Path, "hello");
        var interactive = new List<string>();
        var headless = new List<string>();
        var reporter = new PluginStartupFeedbackReporter(PluginStartupFeedbackMode.Headless, interactive.Add, headless.Add);

        reporter.ReportProgress(new PluginBuildProgress { Package = package, Index = 0, Total = 1, State = PluginBuildProgressState.Failed });
        reporter.ReportResult(new PluginBuildResult { Package = package, Succeeded = false });

        Assert.AreEqual(0, interactive.Count);
        Assert.AreEqual(2, headless.Count);
        Assert.IsTrue(headless.All(static message => !message.Contains('\u001b') && !message.Contains("[/]", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void LiveStatusAppliesQueuedProgressToBindableState()
    {
        using var temp = new TestTempDirectory();
        var firstPackage = CreatePackage(temp.Path, "hello");
        var secondPackage = CreatePackage(temp.Path, "world");
        var requests = new List<PluginBuildRequest>
        {
            new() { Package = firstPackage },
            new() { Package = secondPackage },
        };
        var status = CreateLiveStatus(requests);

        InvokeLiveStatus(status, "Report", new PluginBuildProgress { Package = firstPackage, Index = 0, Total = 2, State = PluginBuildProgressState.Running });

        Assert.IsTrue(BuildHeaderMarkup(status).Contains("0/2 complete", StringComparison.Ordinal));

        InvokeLiveStatus(status, "ApplyPendingUpdates");

        Assert.IsTrue(BuildHeaderMarkup(status).Contains("0/2 complete, 1 running", StringComparison.Ordinal));
        Assert.AreEqual("[warning]◌[/]  1. [warning]Building[/] hello", BuildItemMarkup(status, 0));

        InvokeLiveStatus(status, "Report", new PluginBuildProgress { Package = firstPackage, Index = 0, Total = 2, State = PluginBuildProgressState.Succeeded });
        InvokeLiveStatus(status, "Report", new PluginBuildProgress { Package = secondPackage, Index = 1, Total = 2, State = PluginBuildProgressState.UpToDate });
        InvokeLiveStatus(status, "ApplyPendingUpdates");
        InvokeLiveStatus(status, "MarkCompleted", "CodeAlta plugins: 2 source plugin packages checked (1 built, 1 up-to-date); 2 source plugins activated in 42ms.");

        Assert.AreEqual("[success]✓[/] Plugin startup complete (2/2 complete)", BuildHeaderMarkup(status));
        Assert.AreEqual("[dim]Press Enter to continue.[/]", BuildFooterMarkup(status, waitForEnterAfterCompletion: true));
        Assert.AreEqual("CodeAlta plugins: 2 source plugin packages checked (1 built, 1 up-to-date); 2 source plugins activated in 42ms.", BuildSummaryMarkup(status));
    }

    private static SourcePluginPackage CreatePackage(string rootPath, string id)
    {
        var directory = Path.Combine(rootPath, id);
        Directory.CreateDirectory(directory);
        var entry = Path.Combine(directory, "plugin.cs");
        File.WriteAllText(entry, "// plugin");
        return new SourcePluginPackage
        {
            PackageId = id,
            PackageDirectory = directory,
            EntryFilePath = entry,
            Root = new PluginRoot { RootPath = rootPath, Scope = PluginScope.Global },
        };
    }

    private static object CreateLiveStatus(IReadOnlyList<PluginBuildRequest> requests)
    {
        var statusType = GetLiveStatusType();
        return Activator.CreateInstance(statusType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, binder: null, args: [requests], culture: null)
            ?? throw new InvalidOperationException("Could not create plugin build live status.");
    }

    private static string BuildHeaderMarkup(object status)
        => (string)InvokeLiveStatus(status, "BuildHeaderMarkup")!;

    private static string BuildItemMarkup(object status, int index)
        => (string)InvokeLiveStatus(status, "BuildItemMarkup", index)!;

    private static string BuildFooterMarkup(object status, bool waitForEnterAfterCompletion)
        => (string)InvokeLiveStatus(status, "BuildFooterMarkup", waitForEnterAfterCompletion)!;

    private static string BuildSummaryMarkup(object status)
        => ReadStateValue<string?>(status, "_summaryMarkup")!;

    private static T ReadStateValue<T>(object status, string fieldName)
    {
        var field = status.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Could not find live status field {fieldName}.");
        var state = field.GetValue(status) ?? throw new InvalidOperationException($"Live status field {fieldName} is null.");
        var valueProperty = state.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Could not find Value property on {fieldName}.");
        return (T)valueProperty.GetValue(state)!;
    }

    private static object? InvokeLiveStatus(object status, string methodName, params object[] args)
    {
        var method = status.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Could not find live status method {methodName}.");
        return method.Invoke(status, args);
    }

    private static Type GetLiveStatusType()
        => typeof(PluginStartupFeedbackReporter).GetNestedType("PluginBuildLiveStatus", BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find plugin build live status type.");
}
