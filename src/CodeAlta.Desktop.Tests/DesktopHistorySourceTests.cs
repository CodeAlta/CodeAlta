using CodeAlta.Tests;

namespace CodeAlta.Desktop.Tests;

/// <summary>Current history source guards; named checkout reads only, not store or frontend execution.</summary>
[TestClass]
public sealed class DesktopHistorySourceTests
{
    [TestMethod]
    public void Storage_UsesBoundedReaderAndExistingResolution()
    {
        var store = Read("CodeAlta.Agent/Runtime/FileSystemAgentSessionStore.cs");
        var start = store.IndexOf("public async Task<AgentSessionHistoryPage> ReadHistoryPageAsync(", StringComparison.Ordinal);
        Assert.IsTrue(start >= 0);
        var end = store.IndexOf("    /// <inheritdoc />", start, StringComparison.Ordinal);
        Assert.IsTrue(end > start);
        var history = store[start..end];
        Before(history, "AgentJournalHistoryReader.ValidateCursor(sessionId, cursor);", "await TryGetSessionFilePathAsync(sessionId, cancellationToken)");
        StringAssert.Contains(history, "throw new AgentSessionHistoryException(\"missing_session\")");
        StringAssert.Contains(history, "AgentJournalHistoryReader.OpenContainedAsync(_layout.SessionsRootPath, path,");
        StringAssert.Contains(history, "_journalFile.WithPathLockAsync(containedPath, async () =>");
        StringAssert.Contains(history, "await using var stream = await OpenHistoryReadStreamAsync(containedPath, token)");
        StringAssert.Contains(history, "AgentJournalHistoryReader.ReadAsync(stream, sessionId, cursor,");
        StringAssert.Contains(history, "throw new AgentSessionHistoryException(\"history_changed\")");
        StringAssert.Contains(history, "}, token), cancellationToken).ConfigureAwait(false)");
        StringAssert.Contains(history, "FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, useAsync: true)");
        StringAssert.Contains(history, "ReadRetryTime, cancellationToken)");
        var reader = Read("CodeAlta.Agent/Runtime/AgentJournalHistoryReader.cs");
        StringAssert.Contains(reader, "AgentJsonSerializerContext.Default.AgentEvent");
        StringAssert.Contains(reader, "const int PageBytes = 256 * 1024");
        StringAssert.Contains(reader, "const int RecordBytes = 128 * 1024");
        StringAssert.Contains(reader, "physicalRecords < 100");
        foreach (var forbidden in new[] { "ReadLineAsync", "ReadToEnd", "ReadAll", "Directory.", "File.", "Task.Run" })
            Assert.IsFalse(reader.Contains(forbidden, StringComparison.Ordinal), forbidden);
        Before(reader, "throw Failure(\"outside_root\")", "await open(fullPath, cancellationToken)");
    }

    [TestMethod]
    public void Rpc_UsesGeneratedContractAndMandatoryReadSeam()
    {
        var workspace = Read("CodeAlta/Desktop/Rpc/WorkspaceRpc.cs");
        StringAssert.Contains(workspace, "var store = journals.CreateSessionStore();");
        StringAssert.Contains(workspace, "IAgentSessionCatalog sessions = new AgentSessionCatalog(store);");
        StringAssert.Contains(workspace, "_readHistory = store.ReadHistoryPageAsync;");
        var rpc = Read("CodeAlta/Desktop/Rpc/WorkspaceHistoryRpc.cs");
        StringAssert.Contains(rpc, "[NeoRpcMethod(\"history\")]");
        StringAssert.Contains(rpc, "ReadHistoryAsync(request, _readHistory, cancellationToken)");
        StringAssert.Contains(rpc, "await read(request.SessionId, cursor, cancellationToken)");
        StringAssert.Contains(rpc, "return ProjectHistory(page)");
        var json = Read("CodeAlta/Desktop/Rpc/BootRpc.cs");
        StringAssert.Contains(json, "[JsonSerializable(typeof(HistoryRequest))]");
        StringAssert.Contains(json, "[JsonSerializable(typeof(HistoryResponse))]");
        foreach (var forbidden in new[] { "CodeAltaHost", "ListHeadersAsync", "ReadEventsAsync", "Task.Run(", "CancellationToken.None" })
            Assert.IsFalse(rpc.Contains(forbidden, StringComparison.Ordinal), forbidden);
        StringAssert.Contains(Read("CodeAlta/Desktop/DesktopApplication.cs"), "builder.AddWorkspaceService(new WorkspaceService(options.CatalogRoot));");
    }

    [TestMethod]
    public void Frontend_UsesSelectionPagingAndStaleResultGuards()
    {
        var main = Read("CodeAlta/frontend/src/main.tsx");
        var start = main.IndexOf("function History(", StringComparison.Ordinal);
        Assert.IsTrue(start >= 0);
        var history = main[start..];
        foreach (var fragment in new[]
        {
            "loadHistory(workspace.history, request, abort.signal, setState)", "return () => abort.abort()",
            "state?.request === request ? state : undefined", "page.entries.map(entry => <li key={entry.offset}>",
            "setRequest({ sessionId, cursor: null })", "setRequest({ sessionId, cursor: page.next })",
            "page.tailOmitted", "entry.textTruncated", "entry.bodyOmitted", "UTF-8 LF/CRLF only",
            "not same-stamp rewrites", "does not guarantee stopping catalog loads",
        }) StringAssert.Contains(history, fragment);
        StringAssert.Contains(main, "<History key={selectedSession.id} sessionId={selectedSession.id} />");
        var helper = Read("CodeAlta/frontend/src/history.ts");
        StringAssert.Contains(helper, "if (signal.aborted) return;");
        StringAssert.Contains(helper, "await invoke(request, { signal, timeoutMilliseconds: 30_000 })");
        foreach (var forbidden in new[] { "dangerouslySetInnerHTML", "localStorage", "fetch(", "setInterval(" })
            Assert.IsFalse((main + helper).Contains(forbidden, StringComparison.Ordinal), forbidden);
    }

    private static string Read(string path)
        => SourceTestText.DecodeSource(File.ReadAllBytes(Path.Combine(DesktopArchitectureTests.SourceRoot, path)));

    private static void Before(string source, string first, string second)
    {
        var start = source.IndexOf(first, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, first);
        Assert.IsTrue(source.IndexOf(second, StringComparison.Ordinal) > start, second);
    }
}
