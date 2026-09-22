using CodeAlta.Tests;

namespace CodeAlta.Desktop.Tests;

/// <summary>Named current-source reads only; not catalog, native bridge or lifetime qualification.</summary>
[TestClass]
[Ignore("Brittle source-text reconstruction is not a functional desktop acceptance test.")]
public sealed class DesktopWorkspaceSourceTests
{
    [TestMethod]
    public void Composition_UsesOnlyExplicitCatalogReads()
    {
        var rpc = Read("CodeAlta/Desktop/Rpc/WorkspaceRpc.cs");
        StringAssert.Contains(rpc, "new ProjectCatalog(options)");
        StringAssert.Contains(rpc, "new SessionViewJournalStore(options)");
        StringAssert.Contains(rpc, "var store = journals.CreateSessionStore();");
        StringAssert.Contains(rpc, "IAgentSessionCatalog sessions = new AgentSessionCatalog(store)");
        StringAssert.Contains(rpc, "projects.LoadAsync");
        StringAssert.Contains(rpc, "sessions.ListSessionsAsync(filter: null, cancellationToken: token)");
        foreach (var forbidden in new[] { "ListHeadersAsync", "ReadLatestStateAsync", "CodeAltaHost", "InvalidateAsync", "Environment.", "Task.Run(", "new CancellationTokenSource" })
            Assert.IsFalse(rpc.Contains(forbidden, StringComparison.Ordinal), forbidden);
        var app = Read("CodeAlta/Desktop/DesktopApplication.cs");
        StringAssert.Contains(app, "builder.AddWorkspaceService(new WorkspaceService(options.CatalogRoot));");
        var cli = Read("CodeAlta/Desktop/DesktopCommandLine.cs");
        StringAssert.Contains(cli, "TryParse(args, Directory.Exists, File.Exists, out var options, out var message)");
        StringAssert.Contains(cli, "return startNative(options!);");
    }

    [TestMethod]
    public void Rpc_UsesGeneratedContractAndActualReadSeam()
    {
        var rpc = Read("CodeAlta/Desktop/Rpc/WorkspaceRpc.cs");
        StringAssert.Contains(rpc, "[NeoRpcService(\"workspace\", Version = 1)]");
        StringAssert.Contains(rpc, "[NeoRpcMethod(\"snapshot\")]");
        StringAssert.Contains(rpc, "internal sealed record WorkspaceRequest;");
        StringAssert.Contains(rpc, "ReadAsync(projects.LoadAsync,");
        StringAssert.Contains(rpc, "await foreach (var session in loadSessions(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))");
        StringAssert.Contains(Read("CodeAlta/Desktop/Rpc/BootRpc.cs"), "[JsonSerializable(typeof(WorkspaceSnapshot))]");
        StringAssert.Contains(Read("CodeAlta/Desktop/Rpc/BootRpc.cs"), "DesktopCommandLine.Version, false)");
        StringAssert.Contains(Read("CodeAlta/frontend/src/main.tsx"), "loadWorkspace(workspace.snapshot, abort.signal, setWorkspaceState)");
        StringAssert.Contains(Read("CodeAlta/frontend/src/main.tsx"), "boot.status(");
    }

    [TestMethod]
    public void Frontend_UsesRealRpcAndRendersAllStates()
    {
        var main = Read("CodeAlta/frontend/src/main.tsx");
        foreach (var text in new[] { "#neoastra", "role=\"status\"", "role=\"alert\"", "unconfigured", "loading", "error", "ready", "Global / unmatched", "No persisted sessions", "sessionsForProject(", "workspaceNotice(", "abort.abort()" })
            StringAssert.Contains(main, text);
        var helper = Read("CodeAlta/frontend/src/workspace.ts");
        StringAssert.Contains(helper, "if (signal.aborted) return;");
        StringAssert.Contains(helper, "await invoke({}, { signal, timeoutMilliseconds: 30_000 })");
        Assert.IsFalse(main.Contains("dangerouslySetInnerHTML", StringComparison.Ordinal));
        Assert.IsFalse(main.Contains("Refresh", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CurrentReadLimits_RemainExplicitAndDoNotClaimStoppingCatalogLoads()
    {
        StringAssert.Contains(Read("CodeAlta/Desktop/Rpc/WorkspaceRpc.cs"), "five seconds");
        StringAssert.Contains(Read("CodeAlta/Desktop/Rpc/WorkspaceRpc.cs"), "CancellationToken.None");
        StringAssert.Contains(Read("CodeAlta/frontend/src/main.tsx"), "cache/cache.sqlite3");
    }

    private static string Read(string path)
        => SourceTestText.DecodeSource(File.ReadAllBytes(Path.Combine(DesktopArchitectureTests.SourceRoot, path)));
}
