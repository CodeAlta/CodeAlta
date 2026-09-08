using System.Text;
using CodeAlta.Tests;
using Inverse = CodeAlta.Tests.DesktopOwnedSessionSourceInverse;
using Owner = CodeAlta.Tests.OwnedSessionCommandSourceInverse;

namespace CodeAlta.Desktop.Tests;

/// <summary>Named checkout content only; no host, catalog, provider, native or frontend execution.</summary>
[TestClass]
public sealed class DesktopOwnedSessionSourceTests
{
    private const string Reads = "CodeAlta.Orchestration/Runtime/OwnedSessionWorkspace.cs";
    private const string Operations = "CodeAlta/Desktop/Rpc/SessionOperationsRpc.cs";
    private const string Panel = "CodeAlta/frontend/src/OwnedSessionPanel.tsx";
    private const string Helper = "CodeAlta/frontend/src/sessionOperations.ts";
    private const string InverseSource = "CodeAlta.Tests/DesktopOwnedSessionSourceInverse.cs";

    // Twenty-one newest inputs plus the nine additional originals used by the full owner/discovery chains.
    internal static IReadOnlyList<string> DirectContentPaths =>
    [
        Inverse.Project, Inverse.Cli, Inverse.App, Inverse.Boot, Inverse.Workspace, Inverse.Main, Inverse.Styles,
        Inverse.Architecture, Inverse.HistoryTests, Inverse.WorkspaceTests, Inverse.Host, Inverse.OwnerInverse,
        Inverse.OwnerTests, Inverse.Lifetime, Inverse.DesktopProject, Reads, Operations, Panel, Helper, InverseSource,
        Inverse.OrchestrationAssemblyInfo,
        Owner.Options, Owner.Builtin, Owner.Runtime, Owner.Discovery, Owner.Profile,
        SessionDiscoveryScopeSourceInverse.Template, SessionDiscoveryScopeSourceInverse.Builder,
        SessionDiscoveryScopeSourceInverse.Mcp, SessionDiscoveryScopeSourceInverse.Statistics,
    ];

    // Actual inherited History.Boundaries -> Workspace.Boundaries reads. Pure inverse calls add none.
    // SourceRoot additionally probes ancestor CodeAlta.slnx existence; it does not read its content.
    internal static IReadOnlyList<string> TransitiveContentPaths =>
    [
        "CodeAlta.Agent/Runtime/FileSystemAgentSessionStore.cs", Inverse.Workspace, Inverse.Boot,
        Inverse.Main, Inverse.Styles, Inverse.WorkspaceTests, Inverse.Project, Inverse.Cli, Inverse.App,
        "CodeAlta.Desktop.Tests/DesktopStartupTests.cs", Inverse.Architecture, Inverse.DesktopProject,
        "CodeAlta.Tests/SourceTestText.cs", "CodeAlta.Tests/PluginKeyBindingExtractionSourceTests.cs",
        "CodeAlta.Tests/PluginUiContentExtractionSourceTests.cs", "CodeAlta.Tests/PluginFeedbackExtractionSourceTests.cs",
        "CodeAlta.Tui/App/CodeAltaApp.cs", "../doc/development-guide.md", "../.gitattributes",
    ];

    /// <summary>Checks sole-host composition, cached-store retention, and owned-only inbound framing.</summary>
    [TestMethod]
    public void Composition_UsesSingleHostAndOnlyCachedDirectReads()
    {
        Assert.IsFalse(Read(Inverse.DesktopProject).Contains("<Compile Include=\"../CodeAlta.Orchestration/Runtime/SessionDiscoveryScope.cs\"", StringComparison.Ordinal));
        RequireOnce(Read(Inverse.OrchestrationAssemblyInfo), "[assembly: InternalsVisibleTo(\"CodeAlta.Desktop.Tests\")]");
        var app = Read(Inverse.App);
        RequireOnce(app, "_hostCreation = CodeAltaHost.CreateAsync(");
        RequireOnce(app, "MaximumFrameBytes = 208 * 1024");
        Before(app, "CodeAltaSingleInstanceGuard.Acquire(", "Directory.CreateDirectory(options.DataRoot);", "private static int RunOwned(");
        StringAssert.Contains(app, "ConfigureModelProviders = registry => ConfiguredModelProviderRegistryBuilder.RegisterConfiguredProviders(");
        StringAssert.Contains(app, "StartPlugins = false, OwnsLogging = false, IsHeadless = true");
        StringAssert.Contains(app, "if (!shutdownUnconfirmed) window.Title");
        StringAssert.Contains(app, "GC.KeepAlive(environmentLifetime)");
        StringAssert.Contains(app, "return ValueTask.CompletedTask; // Never await host cleanup inside the native deadline.");
        var legacy = Inverse.Restore(Inverse.App, app);
        Assert.IsFalse(legacy.Contains("MaximumFrameBytes", StringComparison.Ordinal));
        StringAssert.Contains(legacy, "await using var environment");
        StringAssert.Contains(legacy, "builder.AddWorkspaceService(new WorkspaceService(options.CatalogRoot));");

        var host = Read(Inverse.Host);
        RequireOnce(host, "WorkspaceReads = new OwnedSessionWorkspace(projectCatalog, sessionViewCatalog.JournalStore);");
        Before(host, "var reads = Start(disposeReads);", "var commands = Start(disposeCommands);");
        Before(host, "var commands = Start(disposeCommands);", "await commands.ConfigureAwait(false)");
        Before(host, "await reads.ConfigureAwait(false)", "var runtime = disposeRuntime();");
        var reads = Read(Reads);
        RequireOnce(reads, "var store = journals.CreateSessionStore();");
        StringAssert.Contains(reads, "internal OwnedSessionWorkspace(ProjectCatalog projects, SessionViewJournalStore journals)");
        StringAssert.Contains(reads, "if (_active.Count == 8)");
        Before(reads, "operation.Work = work;", "operation.Launch.TrySetResult();");
        StringAssert.Contains(reads, "await foreach (var session in _sessions(CancellationToken.None).ConfigureAwait(false)) sessions.Add(session);");
        foreach (var forbidden in new[] { "new AgentSessionCatalog", "new FileSystemAgentSessionStore", "Task.Run(", "ContinueWith(", "public OwnedSessionWorkspace(" })
            Assert.IsFalse(reads.Contains(forbidden, StringComparison.Ordinal), forbidden);
        StringAssert.Contains(Read(Inverse.Workspace), "var actual = reads.ReadSnapshotAsync(token);");

        var rpc = Read(Operations);
        foreach (var method in new[] { "send", "abort", "receipts" }) StringAssert.Contains(rpc, $"[NeoRpcMethod(\"{method}\")]");
        StringAssert.Contains(rpc, "value is { Length: 36 }");
        StringAssert.Contains(rpc, "string.Equals(value, parsed.ToString(\"D\"), StringComparison.Ordinal)");
        StringAssert.Contains(rpc, "receipt.Completion.IsCompletedSuccessfully");
        StringAssert.Contains(rpc, "64 * 6400 + 8192 = 417792");
        StringAssert.Contains(rpc, "NeoAstra does not enforce a per-response byte cap");
        StringAssert.Contains(Read(Inverse.Boot), "[JsonSerializable(typeof(SessionReceiptPage))]");
    }

    /// <summary>Checks genuine legacy History and explicit epoch-bound submission presentation.</summary>
    [TestMethod]
    public void Frontend_PreservesLegacyHistoryAndUsesEpochBoundMutations()
    {
        var main = Read(Inverse.Main);
        var legacy = Inverse.Restore(Inverse.Main, main);
        Assert.AreEqual(legacy[legacy.IndexOf("function History(", StringComparison.Ordinal)..], main[main.IndexOf("function History(", StringComparison.Ordinal)..]);
        StringAssert.Contains(main, "key={JSON.stringify([selectedSession.id, status.hostEpoch])}");
        StringAssert.Contains(main, "capability={mutation.capability}");
        StringAssert.Contains(main, "current?.epoch === value.hostEpoch ? current");
        var panel = Read(Panel);
        StringAssert.Contains(panel, "sessionOperations as sessions");
        StringAssert.Contains(panel, "submission submitted");
        Assert.IsFalse(panel.Contains("{row.outcome ??", StringComparison.Ordinal));
        StringAssert.Contains(panel, "Reload required; mutations are disabled");
        StringAssert.Contains(panel, "observedInvalidEpoch || !capability.canMutate()");
        StringAssert.Contains(panel, "Refresh submissions");
        StringAssert.Contains(panel, "return () => { controller.abort(); scope.current = null; };");
        var helper = Read(Helper);
        StringAssert.Contains(helper, "Object.freeze({ expectedEpoch: epoch, clientRequestId: key, sessionId, text })");
        StringAssert.Contains(helper, "if (signal.aborted || !capability.canSubmit(request)) return;");
        StringAssert.Contains(helper, "if (!signal.aborted) { capability.observe(result); publish(result); }");
        foreach (var forbidden in new[] { "setInterval(", "localStorage", "dangerouslySetInnerHTML", "StreamEvents", "fetch(" })
            Assert.IsFalse((main + panel + helper).Contains(forbidden, StringComparison.Ordinal), forbidden);
    }

    /// <summary>Checks all sixteen parent anchors, forty-two edits, 48 newline reconstructions and inherited chains.</summary>
    [TestMethod]
    public void Boundaries_RestoreWholeOriginalsAndHistoricalReaders()
    {
        Assert.AreEqual(16, Inverse.Originals.Count);
        Assert.AreEqual(42, Inverse.Originals.Sum(item => Inverse.Edits(item.Path).Count));
        Assert.AreEqual(42, Inverse.Originals.Sum(item => Inverse.Edits(item.Path).Sum(edit => edit.Count)));
        Assert.AreEqual(30, DirectContentPaths.Distinct(StringComparer.Ordinal).Count());
        Assert.AreEqual(19, TransitiveContentPaths.Distinct(StringComparer.Ordinal).Count());
        Assert.AreEqual(0, Inverse.DirectContentPaths.Count);
        Assert.AreEqual(0, Inverse.TransitiveContentPaths.Count);
        var reconstructions = 0;
        foreach (var (path, hash) in Inverse.Originals)
        {
            var current = Read(path);
            foreach (var representation in Representations(current))
            {
                Assert.AreEqual(hash, Owner.Hash(Inverse.Restore(path, representation)), path);
                reconstructions++;
            }
            foreach (var (_, after, _) in Inverse.Edits(path))
            {
                var edit = SourceTestText.Canonicalize(after);
                Assert.ThrowsExactly<AssertFailedException>(() => Inverse.Restore(path, current.Replace(edit, "", StringComparison.Ordinal)), path);
                Assert.ThrowsExactly<AssertFailedException>(() => Inverse.Restore(path, current + edit + "\n"), path);
            }
            Assert.ThrowsExactly<AssertFailedException>(() => Inverse.Restore(path, "// unrelated drift\n" + current), path);
            var original = Inverse.Restore(path, current);
            Assert.ThrowsExactly<AssertFailedException>(() => Inverse.Restore(path, original), path);
        }
        Assert.AreEqual(48, reconstructions);
        Assert.ThrowsExactly<AssertFailedException>(() => Inverse.Restore("unknown.cs", "// unknown\n"));
        foreach (var (path, hash) in Owner.Originals)
            Assert.AreEqual(hash, Owner.Hash(Owner.Restore(path, Inverse.RestoreInput(path, Read(path)))), path);
        foreach (var (path, hash) in SessionDiscoveryScopeSourceInverse.Originals)
            Assert.AreEqual(hash, Owner.Hash(SessionDiscoveryScopeSourceInverse.Restore(path, Read(path))), path);

        var lifetime = Read(Inverse.Lifetime);
        var restoredLifetime = Owner.RestoreCurrentInput(Inverse.Lifetime, lifetime);
        Assert.AreEqual(restoredLifetime, PluginAuthoringProfileSourceInverse.RestoreUiContentInput(Inverse.Lifetime, lifetime));
        Assert.AreEqual(restoredLifetime, PluginAuthoringProfileSourceInverse.RestoreFeedbackInput(Inverse.Lifetime, lifetime));
        // Actual full project gateway starts with raw current text, not already-restored b069dbdb input.
        PluginNeutralContractSourceInverse.Restore(Inverse.DesktopProject, Read(Inverse.DesktopProject));
        var inverse = Read(InverseSource);
        var implementation = inverse[..inverse.IndexOf("internal static IReadOnlyList<(string Path, string Hash)> Originals", StringComparison.Ordinal)];
        Assert.IsFalse(implementation.Contains("File.Read", StringComparison.Ordinal));
        Assert.IsFalse(implementation.Contains("Directory.", StringComparison.Ordinal));
        // Execute real inherited readers and their complete 4b63ee2f -> e2a097fa inverses/anchors.
        new DesktopHistorySourceTests().Boundaries_ReconstructWholeSourcesAndPreserveHistoricalChains();
    }

    private static string Read(string path)
    {
        CollectionAssert.Contains(DirectContentPaths.ToArray(), path);
        return SourceTestText.DecodeSource(File.ReadAllBytes(Path.Combine(DesktopArchitectureTests.SourceRoot, path)));
    }

    private static void RequireOnce(string source, string fragment)
        => Assert.AreEqual(1, source.Split(fragment, StringSplitOptions.None).Length - 1, fragment);

    private static void Before(string source, string first, string second, string? start = null)
    {
        if (start is not null) source = source[source.IndexOf(start, StringComparison.Ordinal)..];
        Assert.IsTrue(source.IndexOf(first, StringComparison.Ordinal) >= 0, first);
        Assert.IsTrue(source.IndexOf(second, StringComparison.Ordinal) > source.IndexOf(first, StringComparison.Ordinal), second);
    }

    private static IEnumerable<string> Representations(string text)
    {
        yield return text;
        yield return text.Replace("\n", "\r\n", StringComparison.Ordinal);
        var lines = text.Split('\n');
        yield return string.Concat(lines.Select((line, i) => i == lines.Length - 1 ? line : line + (i % 2 == 0 ? "\r\n" : "\n")));
    }
}
