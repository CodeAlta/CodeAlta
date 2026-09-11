using CodeAlta.Tests;

namespace CodeAlta.Desktop.Tests;

/// <summary>Named checkout content only; no host, catalog, provider, native or frontend execution.</summary>
[TestClass]
public sealed class DesktopOwnedSessionSourceTests
{
    private const string Reads = "CodeAlta.Orchestration/Runtime/OwnedSessionWorkspace.cs";
    private const string Operations = "CodeAlta/Desktop/Rpc/SessionOperationsRpc.cs";
    private const string Panel = "CodeAlta/frontend/src/OwnedSessionPanel.tsx";
    private const string Helper = "CodeAlta/frontend/src/sessionOperations.ts";

    /// <summary>Checks sole-host composition, cached-store retention, and owned-only inbound framing.</summary>
    [TestMethod]
    public void Composition_UsesSingleHostAndOnlyCachedDirectReads()
    {
        Assert.IsFalse(Read("CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj").Contains("<Compile Include=\"../CodeAlta.Orchestration/Runtime/SessionDiscoveryScope.cs\"", StringComparison.Ordinal));
        RequireOnce(Read("CodeAlta.Orchestration/Properties/AssemblyInfo.cs"), "[assembly: InternalsVisibleTo(\"CodeAlta.Desktop.Tests\")]");
        var app = Read("CodeAlta/Desktop/DesktopApplication.cs");
        RequireOnce(app, "_hostCreation = CodeAltaHost.CreateAsync(");
        RequireOnce(app, "MaximumFrameBytes = 208 * 1024");
        Before(app, "CodeAltaSingleInstanceGuard.Acquire(", "Directory.CreateDirectory(options.DataRoot);", "private static int RunOwned(");
        StringAssert.Contains(app, "ConfigureModelProviders = registry => ConfiguredModelProviderRegistryBuilder.RegisterConfiguredProviders(");
        StringAssert.Contains(app, "StartPlugins = false, OwnsLogging = false, IsHeadless = true");
        StringAssert.Contains(app, "if (!shutdownUnconfirmed) window.Title");
        StringAssert.Contains(app, "GC.KeepAlive(environmentLifetime)");
        StringAssert.Contains(app, "return ValueTask.CompletedTask; // Never await host cleanup inside the native deadline.");
        var legacy = app[app.IndexOf("    private async ValueTask RunAsync(", StringComparison.Ordinal)..];
        Assert.IsFalse(legacy.Contains("MaximumFrameBytes", StringComparison.Ordinal));
        StringAssert.Contains(legacy, "await using var environment");
        StringAssert.Contains(legacy, "builder.AddWorkspaceService(new WorkspaceService(options.CatalogRoot));");
        Assert.IsFalse(legacy.Contains("AddSessionDisplayService", StringComparison.Ordinal));
        RequireOnce(app, "builder.AddSessionDisplayService(new SessionDisplayService(host.RuntimeService.Display, epoch));");

        var host = Read("CodeAlta.Orchestration/Hosting/CodeAltaHost.cs");
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
        StringAssert.Contains(Read("CodeAlta/Desktop/Rpc/WorkspaceRpc.cs"), "var actual = reads.ReadSnapshotAsync(token);");

        var rpc = Read(Operations);
        foreach (var method in new[] { "send", "abort", "receipts" }) StringAssert.Contains(rpc, $"[NeoRpcMethod(\"{method}\")]");
        StringAssert.Contains(rpc, "value is { Length: 36 }");
        StringAssert.Contains(rpc, "string.Equals(value, parsed.ToString(\"D\"), StringComparison.Ordinal)");
        StringAssert.Contains(rpc, "receipt.Completion.IsCompletedSuccessfully");
        StringAssert.Contains(rpc, "64 * 6400 + 8192 = 417792");
        StringAssert.Contains(rpc, "NeoAstra does not enforce a per-response byte cap");
        StringAssert.Contains(Read("CodeAlta/Desktop/Rpc/BootRpc.cs"), "[JsonSerializable(typeof(SessionReceiptPage))]");
    }

    /// <summary>Checks genuine legacy History and explicit epoch-bound submission presentation.</summary>
    [TestMethod]
    public void Frontend_PreservesLegacyHistoryAndUsesEpochBoundMutations()
    {
        var main = Read("CodeAlta/frontend/src/main.tsx");
        var history = main[main.IndexOf("function History(", StringComparison.Ordinal)..];
        StringAssert.Contains(history, "loadHistory(workspace.history, request, abort.signal, setState)");
        StringAssert.Contains(history, "return () => abort.abort()");
        StringAssert.Contains(main, "<History key={selectedSession.id} sessionId={selectedSession.id} />");
        StringAssert.Contains(main, "createSessionDisplayStore(sessionDisplay.observe)");
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
        StringAssert.Contains(panel, "return () => { controller.abort(); scope.current = null; runtimeScope.current = null; };");
        var helper = Read(Helper);
        StringAssert.Contains(helper, "Object.freeze({ expectedEpoch: epoch, clientRequestId: key, sessionId, text })");
        StringAssert.Contains(helper, "if (signal.aborted || !capability.canSubmit(request)) return;");
        StringAssert.Contains(helper, "if (!signal.aborted) { capability.observe(result); publish(result); }");
        foreach (var forbidden in new[] { "setInterval(", "localStorage", "dangerouslySetInnerHTML", "StreamEvents", "fetch(" })
            Assert.IsFalse((main + panel + helper).Contains(forbidden, StringComparison.Ordinal), forbidden);
    }

    private static string Read(string path)
    {
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

}
