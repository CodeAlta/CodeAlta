using System.Text;

namespace CodeAlta.Tests;

/// <summary>Named source reads only; no constructors, command execution, startup or fixture I/O.</summary>
[TestClass]
public sealed class PluginAgentEventOwnershipSourceTests
{
    [TestMethod]
    public void Preservation_RestoresEighteenCompleteParentInputs()
    {
        Assert.AreEqual(18, PluginAgentEventOwnershipSourceInverse.Paths.Count);
        foreach (var path in PluginAgentEventOwnershipSourceInverse.Paths)
        {
            var current = Read(path);
            // Read() decodes actual checkout bytes first. The following are explicit representation variants only.
            var lines = current.Split('\n');
            var mixed = string.Concat(lines.Select((line, index) => line + (index == lines.Length - 1 ? "" : index % 2 == 0 ? "\r\n" : "\n")));
            foreach (var text in new[] { current, current.Replace("\n", "\r\n", StringComparison.Ordinal), mixed })
            {
                var restored = PluginAgentEventOwnershipSourceInverse.Restore(path, SourceTestText.DecodeSource(Encoding.UTF8.GetBytes(text)));
                Assert.AreEqual(PluginAgentEventOwnershipSourceInverse.Original(path), OwnedSessionAskSourceInverse.GitObjectId(restored), path);
                Assert.ThrowsExactly<AssertFailedException>(() => PluginAgentEventOwnershipSourceInverse.Restore(path, restored));
            }
            Assert.ThrowsExactly<AssertFailedException>(() => PluginAgentEventOwnershipSourceInverse.Restore(path, current + "// unexpected\n"));
            Assert.ThrowsExactly<ArgumentException>(() => SourceTestText.DecodeSource(Encoding.UTF8.GetBytes("\uFEFF" + current)));
            Assert.ThrowsExactly<ArgumentException>(() => SourceTestText.DecodeSource(Encoding.UTF8.GetBytes(current.TrimEnd('\n'))));
            Assert.ThrowsExactly<ArgumentException>(() => SourceTestText.DecodeSource(Encoding.UTF8.GetBytes(current.Replace("\n", "\r", StringComparison.Ordinal))));
        }
        Assert.ThrowsExactly<DecoderFallbackException>(() => SourceTestText.DecodeSource(new byte[] { 0xFF, 0x0A }));
        Assert.AreEqual("untouched", PluginAgentEventOwnershipSourceInverse.RestoreInput("unlisted", "untouched"));
    }

    [TestMethod]
    public void Preservation_ReaderMapsAreDisjointAndRejectRepeatedRestoration()
    {
        const string shell = "CodeAlta.Tui/App/ShellFrontendHost.cs";
        const string host = "CodeAlta.Orchestration/Hosting/CodeAltaHost.cs";
        const string owned = "CodeAlta.Tui/App/CodeAltaOwnedServices.cs";
        const string lifecycle = "CodeAlta.Plugins/PluginRuntimeLifecycle.cs";
        const string frontend = "CodeAlta.Tests/CodeAltaFrontendCleanupSourceTests.cs";
        string[] guards = [frontend, "CodeAlta.Tests/FileEditorWorkspaceCleanupSourceTests.cs",
            "CodeAlta.Tests/DeferredCodeAltaAppSourceTests.cs", "CodeAlta.Tests/PromptDraftPrerequisiteSourceTests.cs"];
        (Func<string, string, string> Restore, string[] Paths)[] maps =
        [
            (PluginAgentEventOwnershipSourceInverse.RestoreCacheInput,
                [host, "CodeAlta.Tests/OwnedSessionCommandSourceInverse.cs", "CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj"]),
            (PluginAgentEventOwnershipSourceInverse.RestoreArchitectureInput, [shell, .. guards]),
            (PluginAgentEventOwnershipSourceInverse.RestoreLifetimeInput,
                ["CodeAlta.Plugins/CodeAlta.Plugins.csproj", lifecycle, "CodeAlta.Plugins/PluginContributionAdapters.cs", "CodeAlta.Plugins/PluginTaskTracking.cs", .. guards]),
            (PluginAgentEventOwnershipSourceInverse.RestoreOwnerInput, ["CodeAlta.Tests/PluginAuthoringProfileSourceInverse.cs"]),
            (PluginAgentEventOwnershipSourceInverse.RestoreDiscoveryInput, ["CodeAlta.Tests/PluginMcpBackendSeparationSourceInverse.cs"]),
            (PluginAgentEventOwnershipSourceInverse.RestoreProfileInput, [owned]),
            (PluginAgentEventOwnershipSourceInverse.RestoreGitHubInput, [lifecycle, "CodeAlta.Plugins/PluginRuntimeManager.cs"]),
            (PluginAgentEventOwnershipSourceInverse.RestoreMcpInput, ["CodeAlta.Tests/PluginGitHubBackendSeparationSourceInverse.cs"]),
            (PluginAgentEventOwnershipSourceInverse.RestoreShellInput, [shell]),
            (PluginAgentEventOwnershipSourceInverse.RestoreDeferredInput, [shell, owned, host]),
            (PluginAgentEventOwnershipSourceInverse.RestorePromptInput, [shell, frontend]),
        ];
        foreach (var (restore, paths) in maps)
        {
            foreach (var path in PluginAgentEventOwnershipSourceInverse.Paths)
            {
                var current = Read(path);
                var restored = restore(path, current);
                if (!paths.Contains(path, StringComparer.Ordinal))
                {
                    Assert.AreEqual(current, restored, path + ": unmapped input must not be pre-restored");
                    continue;
                }
                Assert.AreEqual(PluginAgentEventOwnershipSourceInverse.Original(path), OwnedSessionAskSourceInverse.GitObjectId(restored), path);
                Assert.ThrowsExactly<AssertFailedException>(() => restore(path, restored));
                Assert.ThrowsExactly<AssertFailedException>(() => restore(path, current + "// unexpected\n"));
            }
        }
        foreach (var (path, map) in new[]
        {
            (frontend, "RestoreShellInput"),
            ("CodeAlta.Tests/FileEditorWorkspaceCleanupSourceTests.cs", "RestoreShellInput"),
            ("CodeAlta.Tests/DeferredCodeAltaAppSourceTests.cs", "RestoreDeferredInput"),
            ("CodeAlta.Tests/PromptDraftPrerequisiteSourceTests.cs", "RestorePromptInput"),
        })
            StringAssert.Contains(Read(path), $"        => PluginAgentEventOwnershipSourceInverse.{map}(relativePath,\n            SourceTestText.DecodeSource(File.ReadAllBytes(Path.Combine(SourceRoot(), relativePath))));");
        StringAssert.Contains(Read("CodeAlta.Tests/OwnedSessionCommandSourceInverse.cs"),
            "internal static string RestoreLifetimeInput(string path, string source)\n        => path is Lifetime ? RestoreCurrentInput(path, source) : RuntimeFileSearchInvalidationSourceInverse.RestoreLifetimeInput(path, source);");
        StringAssert.Contains(Read("CodeAlta.Tests/AltaReminderLifetimeSourceTests.cs"),
            "return RuntimeFileSearchInvalidationSourceInverse.RestoreArchitectureInput(baseline.Path, SourceTestText.DecodeSource(bytes));");
        StringAssert.Contains(Read("CodeAlta.Tests/ModelsDevCatalogLifetimeSourceTests.cs"),
            "var source = ReadSource(baseline);\n            // Actual checkout bytes were checked by ReadSource; compare reconstructed originals below.\n            var historicalSource = RuntimeFileSearchInvalidationSourceInverse.RestoreArchitectureInput(baseline.Path, source.Text);\n            var restored = CodeAltaStartupAdmissionSourceTests.RestoreCatalogRoute(baseline.Path, historicalSource);");
    }

    [TestMethod]
    public void Preservation_ComposedHistoricalReadersRetainOriginalAnchors()
    {
        // Actual checkout bytes enter each independent route. Never feed a restored source to a second route.
        foreach (var path in RuntimeFileSearchInvalidationSourceInverse.Paths)
            Check(path, RuntimeFileSearchInvalidationSourceInverse.Restore,
                RuntimeFileSearchInvalidationSourceInverse.Original(path), OwnedSessionAskSourceInverse.GitObjectId);
        foreach (var path in OwnedSessionUserInputSourceInverse.Paths)
            Check(path, OwnedSessionUserInputSourceInverse.Restore,
                OwnedSessionUserInputSourceInverse.Original(path), OwnedSessionAskSourceInverse.GitObjectId);

        const string profile = "CodeAlta.Tests/PluginAuthoringProfileSourceInverse.cs";
        Check(profile, OwnedSessionCommandSourceInverse.RestoreCurrentInput,
            OwnedSessionCommandSourceInverse.Originals.Single(item => item.Path == profile).Hash, OwnedSessionCommandSourceInverse.Hash);
        const string mcp = "CodeAlta.Tests/PluginMcpBackendSeparationSourceInverse.cs";
        Check(mcp, SessionDiscoveryScopeSourceInverse.Restore,
            SessionDiscoveryScopeSourceInverse.Originals.Single(item => item.Path == mcp).Hash, SessionDiscoveryScopeSourceInverse.Hash);
        Check(mcp, PluginStatisticsBackendSeparationSourceInverse.Restore,
            PluginStatisticsBackendSeparationSourceInverse.Originals.Single(item => item.Path == mcp).Hash, PluginStatisticsBackendSeparationSourceInverse.Hash);
        const string github = "CodeAlta.Tests/PluginGitHubBackendSeparationSourceInverse.cs";
        Check(github, PluginMcpBackendSeparationSourceInverse.Restore,
            PluginMcpBackendSeparationSourceInverse.Originals.Single(item => item.Path == github).Hash, PluginMcpBackendSeparationSourceInverse.Hash);
        foreach (var path in new[] { "CodeAlta.Plugins/PluginRuntimeLifecycle.cs", "CodeAlta.Plugins/PluginRuntimeManager.cs" })
            Check(path, PluginGitHubBackendSeparationSourceInverse.Restore,
                PluginGitHubBackendSeparationSourceInverse.Originals.Single(item => item.Path == path).Hash, PluginGitHubBackendSeparationSourceInverse.Hash);
        const string owned = "CodeAlta.Tui/App/CodeAltaOwnedServices.cs";
        Check(owned, PluginAuthoringProfileSourceInverse.Restore,
            PluginAuthoringProfileSourceInverse.Originals.Single(item => item.Path == owned).Hash,
            source => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(source))));

        foreach (var path in new[] { "CodeAlta.Tui/App/ShellFrontendHost.cs", "CodeAlta.Tests/CodeAltaFrontendCleanupSourceTests.cs",
            "CodeAlta.Tests/FileEditorWorkspaceCleanupSourceTests.cs", "CodeAlta.Tests/DeferredCodeAltaAppSourceTests.cs",
            "CodeAlta.Tests/PromptDraftPrerequisiteSourceTests.cs" })
            Check(path, RuntimeFileSearchInvalidationSourceInverse.RestoreArchitectureInput,
                PluginAgentEventOwnershipSourceInverse.Original(path), OwnedSessionAskSourceInverse.GitObjectId);
        foreach (var path in new[] { "CodeAlta.Plugins/CodeAlta.Plugins.csproj", "CodeAlta.Plugins/PluginRuntimeLifecycle.cs",
            "CodeAlta.Plugins/PluginContributionAdapters.cs", "CodeAlta.Plugins/PluginTaskTracking.cs",
            "CodeAlta.Tests/CodeAltaFrontendCleanupSourceTests.cs", "CodeAlta.Tests/FileEditorWorkspaceCleanupSourceTests.cs",
            "CodeAlta.Tests/DeferredCodeAltaAppSourceTests.cs", "CodeAlta.Tests/PromptDraftPrerequisiteSourceTests.cs" })
        {
            Check(path, OwnedSessionCommandSourceInverse.RestoreLifetimeInput,
                PluginAgentEventOwnershipSourceInverse.Original(path), OwnedSessionAskSourceInverse.GitObjectId);
            Check(path, PluginAuthoringProfileSourceInverse.RestoreUiContentInput,
                PluginAgentEventOwnershipSourceInverse.Original(path), OwnedSessionAskSourceInverse.GitObjectId);
            Check(path, PluginAuthoringProfileSourceInverse.RestoreFeedbackInput,
                PluginAgentEventOwnershipSourceInverse.Original(path), OwnedSessionAskSourceInverse.GitObjectId);
        }
        foreach (var (path, expected) in new[]
        {
            ("CodeAlta.Tests/ArchitectureGuardrailTests.cs", "321B5BD7F0C30F83E80D8E8EC3C06185DA779F49220B22BB37891821C0ECBBFD"),
            ("CodeAlta.Tests/AltaReminderLifetimeSourceTests.cs", "EAEBDD92546584DE2B50A7C2B6B22A33B432ECEDE82C54C3D4963C497A1C397D"),
        })
            Check(path, (input, source) => PluginAuthoringProfileSourceInverse.RestoreUiContentInput(input,
                    PluginMcpBackendSeparationSourceInverse.RestoreUiContentInput(input, source)), expected,
                source => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(source))));
        // HostOptions and Deferred's older direct-disposal assertion are deliberately not rebased or qualified here.

        static void Check(string path, Func<string, string, string> restore, string expected, Func<string, string> hash)
        {
            var current = Read(path);
            foreach (var source in new[] { current, current.Replace("\n", "\r\n", StringComparison.Ordinal) })
            {
                var restored = restore(path, SourceTestText.DecodeSource(Encoding.UTF8.GetBytes(source)));
                Assert.AreEqual(expected, hash(restored), path);
                Assert.ThrowsExactly<AssertFailedException>(() => restore(path, restored));
                Assert.ThrowsExactly<AssertFailedException>(() => restore(path, current + "// unexpected\n"));
            }
        }
    }

    [TestMethod]
    public void Wiring_BarriersCoverDirectDisposalRollbackAndEarliestFrontendRelease()
    {
        foreach (var path in new[] { "CodeAlta.Orchestration/Hosting/CodeAltaHost.cs", "CodeAlta.Tui/App/CodeAltaOwnedServices.cs" })
        {
            var source = Read(path);
            StringAssert.Contains(source, "PluginEventDependencyBarrier.EnterDispose(PluginRuntime, _disposeTask)");
            StringAssert.Contains(source, "PluginEventDependencyBarrier.BeforeRollbackAsync(");
        }
        var shell = Read("CodeAlta.Tui/App/ShellFrontendHost.cs");
        StringAssert.Contains(shell, "PluginEventDependencyBarrier.Wrap(");
        StringAssert.Contains(shell, "new Lazy<Task>(() => DisposeRemindersThenFrontendAsync(");
        StringAssert.Contains(shell, "PluginEventDependencyBarrier.EnterDispose(_pluginRuntime, _disposeTask)");
        var adapter = Read("CodeAlta.Plugins/PluginContributionAdapters.cs");
        StringAssert.Contains(adapter, "await active.ObserveOwnedAgentEventAsync(");
        var ownership = Read("CodeAlta.Plugins/PluginAgentEventOwnership.cs");
        StringAssert.Contains(ownership, "new AsyncLocal<");
        StringAssert.Contains(ownership, "64");
        StringAssert.Contains(ownership, "earlyControls.Concat(lateControls).Concat(controls)");
        var tasks = Read("CodeAlta.Plugins/PluginTaskTracking.cs");
        StringAssert.Contains(tasks, "ObjectDisposedException.ThrowIf(_closed, this);");
        var registration = tasks.IndexOf("_runningTasks.Add(tracked);", StringComparison.Ordinal);
        var launch = tasks.IndexOf("tracked.Launch();", StringComparison.Ordinal);
        Assert.IsTrue(registration >= 0 && launch > registration, "Register originals before allowing task work to launch.");
        StringAssert.Contains(tasks, "outcome.IsCompletedSuccessfully && outcome.Result is null");
        Assert.IsFalse(ownership.Contains("StreamEventsAsync", StringComparison.Ordinal));
        Assert.IsFalse(ownership.Contains("Display", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Wiring_ManagerPublishesUnloadDiagnosticsOnceAfterSuccessfulOriginal()
    {
        var source = Read("CodeAlta.Plugins/PluginAgentEventOwnership.cs");
        const string publication = "_diagnostics.AddRange(plugin.DeactivationDiagnostics);";
        Assert.AreEqual(1, source.Split(publication, StringSplitOptions.None).Length - 1);
        Assert.AreEqual(1, source.Split("await plugin.DeactivateOriginalAsync().ConfigureAwait(false);", StringSplitOptions.None).Length - 1);
        // The publication belongs to the one retained manager operation, after each successful original.
        // There is no finally/catch path that publishes or clears ownership after failed deactivation.
        StringAssert.Contains(source, """
            operation = _ownedDeactivation ??= new PluginOwnedOperation(async () =>
            {
                await QuiesceAgentEventsAsync().ConfigureAwait(false);
                ActivePluginInstance[] active;
                lock (_lock) active = [.. _activePlugins];
                foreach (var plugin in active.Reverse())
                {
                    await plugin.DeactivateOriginalAsync().ConfigureAwait(false);
                    _diagnostics.AddRange(plugin.DeactivationDiagnostics);
                }
                lock (_lock) _activePlugins.Clear();
            });
""");
    }

    private static string Read(string path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CodeAlta.slnx"))) directory = directory.Parent;
        Assert.IsNotNull(directory);
        return SourceTestText.DecodeSource(File.ReadAllBytes(Path.Combine(directory.FullName, path)));
    }
}
