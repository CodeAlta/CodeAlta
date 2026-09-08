using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Tests;

/// <summary>Named-source wiring and complete pre-feedback reconstruction, not runtime qualification.</summary>
/// <remarks>All I/O reads fixed checkout sources. Inverses operate on canonical LF text after strict
/// UTF-8/no-BOM/final-newline validation; existing historical renderings/hashes remain unchanged.</remarks>
[TestClass]
public sealed class PluginFeedbackExtractionSourceTests
{
    [TestMethod]
    public void Manager_UsesNeutralPortAndPreservesStartupOrder()
    {
        var manager = Read("CodeAlta.Plugins/PluginRuntimeManager.cs");
        RequireOnce(manager, "ArgumentNullException.ThrowIfNull(options.StartupFeedback);");
        Assert.IsTrue(manager.IndexOf("ThrowIfNull(options.StartupFeedback)", StringComparison.Ordinal) < manager.IndexOf("var configStore =", StringComparison.Ordinal));
        RequireOnce(manager, "return await PluginStartupFeedbackRouting.RunAsync(");
        RequireOnce(manager, "IPluginStartupProgress? liveStatus, CancellationToken token)", 2);
        RequireOnce(manager, "scheduler.ProgressChanged += OnProgress;");
        RequireOnce(manager, "scheduler.ProgressChanged -= OnProgress;");
        AssertOld("CodeAlta.Plugins/PluginRuntimeManager.cs", Restore("CodeAlta.Plugins/PluginRuntimeManager.cs", manager));
    }

    [TestMethod]
    public void TuiAndHost_ForwardFeedbackWithoutChangingOwnership()
    {
        foreach (var path in new[] { "CodeAlta.Tui/Program.cs", "CodeAlta.Tui/App/CodeAltaOwnedServices.cs", "CodeAlta.Orchestration/Hosting/CodeAltaHost.cs", "CodeAlta.Orchestration/Hosting/CodeAltaHostOptions.cs" })
            AssertOld(path, Restore(path, Read(path)));
        var host = Read("CodeAlta.Orchestration/Hosting/CodeAltaHost.cs");
        Assert.IsTrue(host.IndexOf("ThrowIfNull(options.PluginStartupFeedback)", StringComparison.Ordinal) < host.IndexOf("var globalRoot =", StringComparison.Ordinal));
        CodeAltaStartupAdmissionSourceTests.RequireProgramAdmission(Read("CodeAlta.Tui/Program.cs"));
    }

    [TestMethod]
    public void Feedback_ContainsNoTerminalDependency()
    {
        foreach (var path in new[] { "CodeAlta.Plugins/PluginStartupFeedback.cs", "CodeAlta.Plugins/PluginChangeNotifications.cs" })
            Assert.IsFalse(Read(path).Contains("XenoAtom.Terminal", StringComparison.Ordinal));
        var adapter = Read("CodeAlta.Tui/Plugins/TerminalPluginStartupFeedback.cs");
        RequireOnce(adapter, "return await RunPresentedOperationAsync(liveStatus, operation, RunLive, cancellationToken).ConfigureAwait(false);");
        RequireOnce(adapter, "var operationTask = operation(status, cancellationToken).AsTask();");
        RequireOnce(adapter, "runLive(operationTask);");
        RequireOnce(adapter, "return await operationTask.ConfigureAwait(false);");
    }

    [TestMethod]
    public void Preservation_RestoresCompletePreExtractionSourcesAndGuardChain()
    {
        foreach (var path in Baselines().Keys)
        {
            var source = Read(path);
            var restored = path switch
            {
                "CodeAlta.Plugins/PluginStartupFeedback.cs" => RestoreFeedback(source, Read("CodeAlta.Tui/Plugins/TerminalPluginStartupFeedback.cs")),
                "CodeAlta.Plugins.Tests/PluginStartupFeedbackReporterTests.cs" => RestoreReporter(source, Read("CodeAlta.Tests/TerminalPluginStartupFeedbackTests.cs")),
                _ => Restore(path, source),
            };
            AssertOld(path, restored);
        }
    }

    // Every mapped path requires its entire NEW text before inversion. Unchanged paths pass only
    // through the explicit catalog dispatch below; mapped paths never accept an OLD fallback.
    internal static string Restore(string path, string source)
    {
        source = Canonical(source);
        return RestoreCore(path, source);
    }

    internal static string RestoreCatalogInput(string path, string source)
        => path is "CodeAlta.Tui/App/CodeAltaOwnedServices.cs" or "CodeAlta.Tests/CodeAltaOwnedServicesLifetimeTests.cs" or "CodeAlta.Tests/AltaReminderLifetimeSourceTests.cs"
            ? Restore(path, source) : source;

    private static string RestoreCore(string path, string source)
    {
        switch (path)
        {
            case "CodeAlta.Tui/Program.cs":
                return Replace(source, "                    StartupFeedback = new CodeAlta.Tui.Plugins.TerminalPluginStartupFeedback(),\n", "");
            case "CodeAlta.Tui/App/CodeAltaOwnedServices.cs":
                return Replace(source, "                        PluginStartupFeedback = new CodeAlta.Tui.Plugins.TerminalPluginStartupFeedback(),\n", "");
            case "CodeAlta.Orchestration/Hosting/CodeAltaHostOptions.cs":
                return Replace(source, "\n" + Canonical(HostOption) + "\n", "");
            case "CodeAlta.Orchestration/Hosting/CodeAltaHost.cs":
                source = Replace(source, "        ArgumentNullException.ThrowIfNull(options.PluginStartupFeedback);\n", "");
                source = Replace(source, "                            StartupFeedback = options.PluginStartupFeedback,\n", "");
                return Replace(source, "    /// <exception cref=\"ArgumentNullException\">Thrown when <paramref name=\"options\"/> or its plugin startup feedback is null.</exception>", OriginalOptionsException);
            case "CodeAlta.Plugins/CodeAlta.Plugins.csproj":
                return Replace(source, "    <InternalsVisibleTo Include=\"CodeAlta.Plugins.Tests\" />\n", "");
            case "CodeAlta.Plugins/PluginRuntimeManager.cs":
                source = Replace(source, "\n" + Canonical(RuntimeOption) + "\n", "");
                source = Replace(source, "        ArgumentNullException.ThrowIfNull(options.StartupFeedback);\n", "");
                source = Replace(source, "    /// <exception cref=\"ArgumentNullException\">Thrown when <paramref name=\"options\"/> or its startup feedback is null.</exception>", OriginalOptionsException);
                source = Replace(source, NewRoute, OldRoute);
                return Replace(source, "IPluginStartupProgress? liveStatus, CancellationToken token)", "PluginStartupFeedbackReporter.PluginBuildLiveStatus? liveStatus, CancellationToken token)", 2);
            case "CodeAlta.Plugins/PluginChangeNotifications.cs":
                source = Replace(source, NewChangeDocs, OldChangeDocs);
                source = Replace(source, "        if (_options.Interactive) ArgumentNullException.ThrowIfNull(toastSink);\n", "");
                source = Replace(source, "                _toastSink!(message); // Validated for interactive options at construction.", OldChangeDispatch);
                RequireOnce(source, "namespace CodeAlta.Plugins;\n");
                return "using XenoAtom.Terminal.UI.Controls;\n\n" + source;
            case "CodeAlta.Plugins.Tests/PluginChangeNotificationServiceTests.cs":
                return RemoveCheckedBlock(source, "    [TestMethod]\n    public void Constructor_InteractiveRequiresExplicitSink()", "    [TestMethod]\n    public void NotifyCoalescesToastsFooterStatusAndActionDispatch()", ChangeTestsHash);
            case "CodeAlta.Tests/CodeAltaStartupAdmissionSourceTests.cs":
                source = Replace(source, "    internal static string RestoreProgram(string program) => Invert(PluginFeedbackExtractionSourceTests.Restore(\"CodeAlta.Tui/Program.cs\", program), ProgramEdits());", "    internal static string RestoreProgram(string program) => Invert(program, ProgramEdits());");
                return Replace(source, "        source = PluginFeedbackExtractionSourceTests.RestoreCatalogInput(path, source);\n", "");
            case "CodeAlta.Tests/AltaReminderLifetimeSourceTests.cs":
                return Replace(source, ReminderRestoration, "");
            case "CodeAlta.Tests/CodeAltaHostLifetimeTests.cs":
                source = Replace(source, "        source = PluginFeedbackExtractionSourceTests.Restore(\"CodeAlta.Orchestration/Hosting/CodeAltaHost.cs\", source);\n", "");
                source = Replace(source, "Assert.IsTrue(create.StartsWith(SourceTestText.Canonicalize(\"\"\"", "Assert.IsTrue(create.StartsWith(\"\"\"");
                source = Replace(source, "\"\"\") + \"\\n\", StringComparison.Ordinal));", "\"\"\" + \"\\n\", StringComparison.Ordinal));");
                source = Replace(source, "Assert.AreEqual(SourceTestText.Canonicalize(\"\"\"", "Assert.AreEqual(\"\"\"", 3);
                source = Replace(source, "\"\"\") + \"\\n\\n\", setup);", "\"\"\" + \"\\n\\n\", setup);");
                source = Replace(source, "\"\"\") + \"\\n\", acquisition);", "\"\"\" + \"\\n\", acquisition);");
                source = Replace(source, "\"\"\"), rollback);", "\"\"\", rollback);");
                return Replace(source, "        expected = SourceTestText.Canonicalize(expected);\n", "");
            case "CodeAlta.Tests/CodeAltaOwnedServicesLifetimeTests.cs":
                return Replace(source, "        source = PluginFeedbackExtractionSourceTests.Restore(\"CodeAlta.Tui/App/CodeAltaOwnedServices.cs\", source);\n", "");
            default:
                throw new AssertFailedException("No feedback inverse for " + path);
        }
    }

    private static string RestoreFeedback(string source, string adapter)
    {
        Assert.AreEqual(AdapterHash, Hash(adapter), "Complete accepted adapter, including the mandatory seam.");
        const string portStart = "\n/// <summary>Receives startup phases and build progress without prescribing a frontend toolkit.</summary>";
        RequireOnce(source, portStart);
        var ports = source[source.IndexOf(portStart, StringComparison.Ordinal)..];
        Assert.AreEqual(PortHash, Hash(ports));
        source = Replace(source, ports, "");

        var imports = Before(adapter, "namespace CodeAlta.Tui.Plugins;");
        imports = Replace(imports, "using CodeAlta.Plugins;\n", "");
        var build = Between(adapter, "    /// <summary>\n    /// Builds stale plugin requests", "    internal static async ValueTask<T> RunWithInteractiveLiveAsync<T>(");
        build = Replace(build, "                    // Both branches of this adapter supply a non-null status (unlike silent feedback).\n", "");
        build = Replace(build, "status!.Report(progress);", "status.Report(progress);");
        build = Replace(build, "status!.MarkBuildsCompleted();", "status.MarkBuildsCompleted();");
        var live = Between(adapter, "    internal static async ValueTask<T> RunWithInteractiveLiveAsync<T>(", "    private static string Pluralize(");
        live = Replace(live, "Func<IPluginStartupProgress?, CancellationToken, ValueTask<T>> operation,", "Func<PluginBuildLiveStatus, CancellationToken, ValueTask<T>> operation,");
        live = Replace(live, "internal sealed class PluginBuildLiveStatus : IPluginStartupProgress", "internal sealed class PluginBuildLiveStatus");
        // Explicit forward rendering of this one moved literal, not stripping checkout whitespace.
        var nestedLoop = string.Join('\n', Canonical(OldLiveLoop).Split('\n').Select(static line => line.Length == 0 ? line : "    " + line));
        live = Replace(live, nestedLoop, OldLiveLoop);
        live = Replace(live, "        return await RunPresentedOperationAsync(liveStatus, operation, RunLive, cancellationToken).ConfigureAwait(false);\n\n        void RunLive(Task<T> operationTask)\n        {\n", "        var operationTask = operation(liveStatus, cancellationToken).AsTask();\n");
        live = Replace(live, "            });\n        }\n    }\n\n    internal sealed class PluginBuildLiveStatus", "            });\n        return await operationTask.ConfigureAwait(false);\n    }\n\n    internal sealed class PluginBuildLiveStatus");
        return imports + Before(source, "    internal static string BuildStartupSummary(") + build
            + Between(source, "    internal static string BuildStartupSummary(", "    private void Write(") + live
            + Between(source, "    private void Write(", "    private static string FormatElapsed(")
            + Between(adapter, "    private static string BuildBuildOnlySummary(", "    private static string FormatElapsed(")
            + source[source.IndexOf("    private static string FormatElapsed(", StringComparison.Ordinal)..];
    }

    private static string RestoreReporter(string source, string moved)
    {
        var prefix = Before(source, "    private static SourcePluginPackage CreatePackage(");
        prefix = Replace(prefix, "        var package = CreatePackage(\"unused\", \"hello\");", "        using var temp = new TestTempDirectory();\n        var package = CreatePackage(temp.Path, \"hello\");", 2);
        var live = Between(moved, "    [TestMethod]\n    [TestCategory(\"PluginFeedbackUiState\")]", "    private static SourcePluginPackage CreatePackage(");
        live = Replace(live, "    [TestCategory(\"PluginFeedbackUiState\")]\n", "");
        var helpers = Between(moved, "    private static SourcePluginPackage CreatePackage(", "\n    private sealed class TestTempDirectory");
        helpers = Replace(helpers, "typeof(TerminalPluginStartupFeedback)", "typeof(PluginStartupFeedbackReporter)");
        // Validate the new pure helper too, rather than silently discarding arbitrary new fixture code.
        var pureHelper = source[source.IndexOf("    private static SourcePluginPackage CreatePackage(", StringComparison.Ordinal)..];
        Assert.AreEqual(PureReporterHelperHash, Hash(pureHelper));
        return "using System.Reflection;\n" + prefix + live + helpers + "}\n";
    }

    private static string Before(string source, string marker)
    {
        RequireOnce(source, marker);
        return source[..source.IndexOf(marker, StringComparison.Ordinal)];
    }
    private static string Between(string source, string start, string end)
    {
        RequireOnce(source, start);
        RequireOnce(source, end);
        var from = source.IndexOf(start, StringComparison.Ordinal);
        var to = source.IndexOf(end, StringComparison.Ordinal);
        Assert.IsTrue(to > from);
        return source[from..to];
    }
    private static string RemoveCheckedBlock(string source, string start, string end, string expectedHash)
    {
        var block = Between(source, start, end);
        Assert.AreEqual(expectedHash, Hash(block));
        return Replace(source, block, "");
    }

    private const string AdapterHash = "AE2611C4B7F10131967C810FF2ADA41217F38A3665DACF97B6ECDDF5913B82B9";
    private const string PortHash = "DF198F2995F11E12BEFA2C638448843D27F37295F8F757248581336D6DD755FE";
    private const string ChangeTestsHash = "5A5F4EBC5DF0B3BB0B87162F48ED9AF3F04A25D4F17AE120EECF4E14579D65FF";
    private const string PureReporterHelperHash = "786E472DDB6837204F81D2D723BF0E62735C416290EDB873CDF8947BE21CD648";
    private const string OriginalOptionsException = "    /// <exception cref=\"ArgumentNullException\">Thrown when <paramref name=\"options\"/> is <see langword=\"null\"/>.</exception>";
    private const string RuntimeOption = "    /// <summary>Gets borrowed startup presentation; the default is silent even for nonheadless callers.</summary>\n    /// <remarks>The runtime never disposes this port. Hosts must explicitly inject frontend feedback.</remarks>\n    public IPluginStartupFeedback StartupFeedback { get; init; } = new SilentPluginStartupFeedback();";
    private const string HostOption = "    /// <summary>Gets borrowed plugin startup presentation; defaults to silent feedback for every host.</summary>\n    /// <remarks>Validated even when plugins are prestarted or disabled; never disposed by the host.</remarks>\n    public IPluginStartupFeedback PluginStartupFeedback { get; init; } = new SilentPluginStartupFeedback();";
    private const string ReminderRestoration = "            if (baseline.Path is \"CodeAlta.Tui/App/CodeAltaOwnedServices.cs\" or \"CodeAlta.Orchestration/Hosting/CodeAltaHost.cs\")\n                source = PluginFeedbackExtractionSourceTests.Restore(baseline.Path, source);\n";
    private const string NewChangeDocs = "    /// <param name=\"toastSink\">Borrowed host sink, required for interactive notifications; no implicit terminal fallback.</param>\n    /// <remarks>The sink still runs under the existing gate after status publication. Its exception\n    /// escapes without rolling back status; this service neither dispatches UI work nor owns the sink.</remarks>\n    /// <exception cref=\"ArgumentNullException\">Interactive options require a non-null <paramref name=\"toastSink\"/>.</exception>";
    private const string OldChangeDocs = "    /// <param name=\"toastSink\">Optional toast sink used by tests or host-specific UI plumbing; defaults to <see cref=\"ToastService.Show(string, ToastSeverity)\"/>.</param>";
    private const string OldChangeDispatch = "                if (_toastSink is not null)\n                {\n                    _toastSink(message);\n                }\n                else\n                {\n                    ToastService.Show(message, ToastSeverity.Info);\n                }";
    private const string NewRoute = """
        return await PluginStartupFeedbackRouting.RunAsync(
                plan.BuildRequests,
                options.StartupFeedback,
                CompleteStartupAsync,
                static (result, elapsed) => PluginStartupFeedbackReporter.BuildStartupSummary(
                    result.BuildResults,
                    result.ActivePlugins.Count(static plugin => plugin.SourcePackage is not null),
                    elapsed),
                options.IsHeadless,
                options.WaitForEnterAfterBuildLiveOutput,
                cancellationToken)
            .ConfigureAwait(false);
""";
    private const string OldRoute = """
        if (!options.IsHeadless && plan.BuildRequests.Count > 0)
        {
            return await PluginStartupFeedbackReporter.RunWithInteractiveLiveAsync(
                    plan.BuildRequests,
                    options.WaitForEnterAfterBuildLiveOutput,
                    CompleteStartupAsync,
                    static (result, elapsed) => PluginStartupFeedbackReporter.BuildStartupSummary(
                        result.BuildResults,
                        result.ActivePlugins.Count(static plugin => plugin.SourcePackage is not null),
                        elapsed),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await CompleteStartupAsync(null, cancellationToken).ConfigureAwait(false);
""";
    private const string OldLiveLoop = """
        var completionApplied = false;
        Terminal.Live(
            liveRegion,
            _ =>
            {
                liveStatus.ApplyPendingUpdates();

                if (cancellationToken.IsCancellationRequested || operationTask.IsCanceled || operationTask.IsFaulted)
                {
                    return TerminalLoopResult.Stop;
                }

                if (!operationTask.IsCompleted)
                {
                    return TerminalLoopResult.Continue;
                }

                if (!completionApplied)
                {
                    stopwatch.Stop();
                    liveStatus.ApplyPendingUpdates();
                    var result = operationTask.GetAwaiter().GetResult();
                    liveStatus.MarkCompleted(summaryFactory(result, stopwatch.Elapsed));
                    completionApplied = true;
                }

                return waitForEnterAfterCompletion && !liveStatus.ContinueRequested
                    ? TerminalLoopResult.Continue
                    : TerminalLoopResult.Stop;
            });
""";

    private static Dictionary<string, string> Baselines() => new(StringComparer.Ordinal)
    {
        ["CodeAlta.Plugins/PluginStartupFeedback.cs"] = "4DE0BCAE6FBC9AFD04B52EBFB97EAB7E252BBB897F9A6DAB9BEF9BCA2CDEFCFC",
        ["CodeAlta.Plugins/PluginChangeNotifications.cs"] = "2ACD18D10F0364361110D7AE530B9FBF9AC8B32ECE34636EACA8BA88BE1D8D6F",
        ["CodeAlta.Plugins/PluginRuntimeManager.cs"] = "40D32AC8E61D9B09B10A0F6C7E5746C8DED1DE0E3AF09AEF6771A601F1C2AFEA",
        ["CodeAlta.Plugins/CodeAlta.Plugins.csproj"] = "D30C277B9724412AE921A18318EE63B9E9F4DF216C79D7A8508230C2A600555F",
        ["CodeAlta.Tui/Program.cs"] = "E979E1B05104EF67DB4AA5825D1E4614A53BB7EACFDCC281FA3AFD3B5C5AA155",
        ["CodeAlta.Tui/App/CodeAltaOwnedServices.cs"] = "C9E467A1DF4056BAE4928F1EF7328C6B86A3E774C8C905241ADD14F5F87C96F5",
        ["CodeAlta.Orchestration/Hosting/CodeAltaHostOptions.cs"] = "68D1B569428F8E8F990FCA7FD1454A2BCDD57CF506014F32E04587883CA026F3",
        ["CodeAlta.Orchestration/Hosting/CodeAltaHost.cs"] = "2EEF527E2A819F08529FA7AB1E5A4C29A7B9ABB599789659F1A7B95C2CABDF6F",
        ["CodeAlta.Plugins.Tests/PluginStartupFeedbackReporterTests.cs"] = "CBF72DE24447ABBDED78BF92C278C34013DD35BCD61DC69C12150D82F963E52A",
        ["CodeAlta.Plugins.Tests/PluginChangeNotificationServiceTests.cs"] = "663A967226E7413697EAE52FBE9B671829AC8BD7AE2F83E1EBFDE6C00BAA95A0",
        ["CodeAlta.Tests/CodeAltaStartupAdmissionSourceTests.cs"] = "3D4311236E6B7B0B71C3B709277DFF61CC5D9DCA4A0AE29060CBB709C7862E41",
        ["CodeAlta.Tests/AltaReminderLifetimeSourceTests.cs"] = "4D48834B09FD2D779E55A3C07BD2161F031A9BC225C4FF0C267C1359EEB418D3",
        ["CodeAlta.Tests/CodeAltaHostLifetimeTests.cs"] = "FF6CE8548B7719B07C884CC36A7625C6EBE69A6D81F839B4E5ED8F3964CEA9CC",
        ["CodeAlta.Tests/CodeAltaOwnedServicesLifetimeTests.cs"] = "78A7B045DD137F3ABC1EA5AB2A5222F33AD668300B2915C12F99DCD29065620C",
    };

    private static void AssertOld(string path, string source)
        => Assert.AreEqual(Baselines()[path], Hash(source), "Complete pre-extraction source: " + path);
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(text))));
    private static string Canonical(string text) => SourceTestText.Canonicalize(text);
    private static string Read(string path, [CallerFilePath] string caller = "")
    {
        var bytes = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(caller)!, "..", path));
        return SourceTestText.DecodeSource(bytes);
    }
    private static void RequireOnce(string source, string text, int count = 1)
    {
        text = Canonical(text);
        var found = 0;
        for (var index = 0; (index = source.IndexOf(text, index, StringComparison.Ordinal)) >= 0; index += text.Length) found++;
        Assert.AreEqual(count, found, "Mandatory occurrence count: " + text);
    }
    private static string Replace(string source, string after, string before, int count = 1)
    {
        after = Canonical(after);
        RequireOnce(source, after, count);
        return source.Replace(after, Canonical(before), StringComparison.Ordinal);
    }
}
