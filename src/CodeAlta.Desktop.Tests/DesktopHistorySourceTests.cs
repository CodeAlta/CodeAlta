using System.Security.Cryptography;
using System.Text;
using CodeAlta.Tests;

namespace CodeAlta.Desktop.Tests;

/// <summary>Named source reads only. Mandatory inverses precede the frozen workspace inverses.</summary>
[TestClass]
public sealed class DesktopHistorySourceTests
{
    [TestMethod]
    public void Storage_UsesBoundedReaderAndExistingResolution()
    {
        var store = Read("CodeAlta.Agent/Runtime/FileSystemAgentSessionStore.cs");
        StringAssert.Contains(store, SourceTestText.Canonicalize(StoreAddition));
        var reader = Read("CodeAlta.Agent/Runtime/AgentJournalHistoryReader.cs");
        StringAssert.Contains(reader, "AgentJsonSerializerContext.Default.AgentEvent");
        StringAssert.Contains(reader, "const int PageBytes = 256 * 1024");
        StringAssert.Contains(reader, "const int RecordBytes = 128 * 1024");
        StringAssert.Contains(reader, "physicalRecords < 100");
        foreach (var forbidden in new[] { "ReadLineAsync", "ReadToEnd", "ReadAll", "Directory.", "File.", "Task.Run" })
            Assert.IsFalse(reader.Contains(forbidden, StringComparison.Ordinal), forbidden);
        StringAssert.Contains(reader, "await open(fullPath, cancellationToken)");
        Assert.IsTrue(reader.IndexOf("throw Failure(\"outside_root\")", StringComparison.Ordinal) <
            reader.IndexOf("await open(fullPath, cancellationToken)", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Rpc_UsesGeneratedContractAndMandatoryReadSeam()
    {
        var workspace = Read("CodeAlta/Desktop/Rpc/WorkspaceRpc.cs");
        StringAssert.Contains(workspace, NewComposition);
        var rpc = Read("CodeAlta/Desktop/Rpc/WorkspaceHistoryRpc.cs");
        StringAssert.Contains(rpc, "[NeoRpcMethod(\"history\")]");
        StringAssert.Contains(rpc, "ReadHistoryAsync(request, _readHistory, cancellationToken)");
        StringAssert.Contains(rpc, "await read(request.SessionId, cursor, cancellationToken)");
        StringAssert.Contains(rpc, "return ProjectHistory(page)");
        StringAssert.Contains(Read("CodeAlta/Desktop/Rpc/BootRpc.cs"), JsonAddition.TrimEnd());
        foreach (var forbidden in new[] { "CodeAltaHost", "ListHeadersAsync", "ReadEventsAsync", "Task.Run(", "CancellationToken.None" })
            Assert.IsFalse(rpc.Contains(forbidden, StringComparison.Ordinal), forbidden);
        StringAssert.Contains(Read("CodeAlta/Desktop/DesktopApplication.cs"), "builder.AddWorkspaceService(new WorkspaceService(options.CatalogRoot));");
    }

    [TestMethod]
    public void Frontend_UsesSelectionPagingAndStaleResultGuards()
    {
        var main = Read("CodeAlta/frontend/src/main.tsx");
        StringAssert.Contains(main, SourceTestText.Canonicalize(HistoryComponent));
        StringAssert.Contains(main, "<History key={selectedSession.id} sessionId={selectedSession.id} />");
        var helper = Read("CodeAlta/frontend/src/history.ts");
        StringAssert.Contains(helper, "if (signal.aborted) return;");
        StringAssert.Contains(helper, "await invoke(request, { signal, timeoutMilliseconds: 30_000 })");
        foreach (var forbidden in new[] { "dangerouslySetInnerHTML", "localStorage", "fetch(", "setInterval(" })
            Assert.IsFalse((main + helper).Contains(forbidden, StringComparison.Ordinal), forbidden);
    }

    [TestMethod]
    public void Boundaries_ReconstructWholeSourcesAndPreserveHistoricalChains()
    {
        foreach (var (path, hash) in Originals)
        {
            var original = Read(path);
            foreach (var representation in Representations(original))
            {
                var restored = RestoreWorkspaceSource(path, SourceTestText.DecodeSource(Encoding.UTF8.GetBytes(representation)));
                Assert.AreEqual(hash, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(restored))), path);
            }
        }
        // This executes the existing mandatory e2a097fa inverses and frozen checks AFTER our inverses.
        new DesktopWorkspaceSourceTests().Boundaries_PreserveTrustAndDocumentReadLimits();
    }

    internal static string RestoreWorkspaceSource(string path, string source)
    {
        source = SourceTestText.Canonicalize(source);
        return path switch
        {
            "CodeAlta.Agent/Runtime/FileSystemAgentSessionStore.cs" => Undo(source, StoreAddition, ""),
            "CodeAlta/Desktop/Rpc/WorkspaceRpc.cs" => Undo(Undo(source, NewComposition, OldComposition),
                "internal sealed partial class WorkspaceService", "internal sealed class WorkspaceService"),
            "CodeAlta/Desktop/Rpc/BootRpc.cs" => Undo(source, JsonAddition, ""),
            "CodeAlta/frontend/src/main.tsx" => Undo(Undo(Undo(Undo(source, HistoryComponent, ""),
                NewImport, OldImport), NewDescription, OldDescription), HistoryMount, ""),
            "CodeAlta/frontend/src/style.css" => Undo(source, HistoryStyles, ""),
            "CodeAlta.Desktop.Tests/DesktopWorkspaceSourceTests.cs" => Undo(source, NewSourceRead, OldSourceRead),
            _ => source
        };
    }

    private static string Undo(string source, string after, string before)
    {
        after = SourceTestText.Canonicalize(after);
        before = SourceTestText.Canonicalize(before);
        Assert.IsTrue(after.Length > 0);
        Assert.AreEqual(1, source.Split(after, StringSplitOptions.None).Length - 1, after);
        return source.Replace(after, before, StringComparison.Ordinal);
    }

    private static string Read(string path) => SourceTestText.DecodeSource(File.ReadAllBytes(Path.Combine(DesktopArchitectureTests.SourceRoot, path)));
    private static IEnumerable<string> Representations(string text)
    {
        yield return text;
        yield return text.Replace("\n", "\r\n", StringComparison.Ordinal);
        var lines = text.Split('\n');
        yield return string.Concat(lines.Select((line, i) => i == lines.Length - 1 ? line : line + (i % 2 == 0 ? "\r\n" : "\n")));
    }

    // Parent supplied strict canonical raw-Git 4b63ee2f anchors before production edits.
    private static IReadOnlyList<(string Path, string Hash)> Originals =>
    [
        ("CodeAlta.Agent/Runtime/FileSystemAgentSessionStore.cs", "0707B94A0414F07A9A39490D8AF9586E06F84216237057F3D558F7D99B785D8C"),
        ("CodeAlta/Desktop/Rpc/WorkspaceRpc.cs", "359AB4CE19D39ADFBC855FF825AE5174A9DB71CF8C9C11B7E988BCA6FB59116B"),
        ("CodeAlta/Desktop/Rpc/BootRpc.cs", "FA1833DFBE101075A1610B5052199102176A29CABB298E838FCF92D4A6447B30"),
        ("CodeAlta/frontend/src/main.tsx", "C50533520DF343BB5AEDDD81DA732123A301BB7B6451377AD8DBA89393F08B80"),
        ("CodeAlta/frontend/src/style.css", "5670BD754F513548BEFB4F144B8A2F8A39F37D856E9EB8827EEB1A38E84C2745"),
        ("CodeAlta.Desktop.Tests/DesktopWorkspaceSourceTests.cs", "5EDB20005DF99F52D75350634CE06D23E86168C4DA18AF0B7E4114680F293979"),
    ];

    private const string OldSourceRead = "private static string Read(string path) => SourceTestText.DecodeSource(File.ReadAllBytes(Path.Combine(DesktopArchitectureTests.SourceRoot, path)));";
    private const string NewSourceRead = "private static string Read(string path) => DesktopHistorySourceTests.RestoreWorkspaceSource(path, SourceTestText.DecodeSource(File.ReadAllBytes(Path.Combine(DesktopArchitectureTests.SourceRoot, path))));";
    private const string OldComposition = "        IAgentSessionCatalog sessions = new AgentSessionCatalog(journals.CreateSessionStore());";
    private const string NewComposition = "        var store = journals.CreateSessionStore();\n        IAgentSessionCatalog sessions = new AgentSessionCatalog(store);\n        _readHistory = store.ReadHistoryPageAsync;";
    private const string JsonAddition = "[JsonSerializable(typeof(HistoryRequest))]\n[JsonSerializable(typeof(HistoryResponse))]\n";
    private const string OldImport = "import { loadWorkspace, sessionsForProject, workspaceNotice, type WorkspaceState } from \"./workspace\";";
    private const string NewImport = OldImport + "\nimport { loadHistory, historyMessage, type HistoryState } from \"./history\";\nimport type { HistoryRequest } from \"#neoastra\";";
    private const string OldDescription = "This is not live session state: sending, resuming, events and history are not connected.";
    private const string NewDescription = "Read-only persisted history is available on selection. This is not live session state: sending, resuming and live events are not connected.";
    private const string HistoryMount = "          <History key={selectedSession.id} sessionId={selectedSession.id} />\n";
    private const string HistoryStyles = ".history-records { padding-inline-start: 1.5rem; }\n.history-records li { border-top: 1px solid currentColor; padding-block: .75rem; overflow-wrap: anywhere; }\n.history-records pre { white-space: pre-wrap; overflow-wrap: anywhere; font-family: ui-monospace, monospace; }\n.history-controls { display: flex; gap: 1rem; }\n";

    private const string StoreAddition = """
        /// <summary>Reads one bounded page of persisted events without materializing complete history.</summary>
        /// <param name="sessionId">Durable catalog identity, not necessarily an event's provider/runtime identity.</param>
        /// <param name="cursor">A previous page's continuation, or null to start at the beginning.</param>
        /// <param name="cancellationToken">Cancels lookup, gate admission and asynchronous reads.</param>
        /// <returns>At most 100 physical records' projected events and an optional continuation.</returns>
        /// <remarks>
        /// Journal input is bounded to 256 KiB plus five probe bytes per page; records to 128 KiB.
        /// Only UTF-8 LF/CRLF journals are supported here. Existing complete-history readers are unchanged.
        /// Lookup retains normal cache errors and directory discovery; those costs are not bounded by paging.
        /// Containment guards the history open, not earlier cache existence probes or copied-cache metadata.
        /// File length/time detects changes, not same-stamp rewrites or all external-writer races.
        /// The shared gate is in-process only; lexical containment does not isolate reparse points.
        /// </remarks>
        /// <exception cref="ArgumentException">The session identifier is empty.</exception>
        /// <exception cref="AgentSessionHistoryException">The cursor, journal format, revision or resolved path cannot be used.</exception>
        /// <exception cref="IOException">Journal access fails.</exception>
        /// <exception cref="UnauthorizedAccessException">Journal access is denied.</exception>
        /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
        public async Task<AgentSessionHistoryPage> ReadHistoryPageAsync(
            string sessionId, AgentSessionHistoryCursor? cursor, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
            AgentJournalHistoryReader.ValidateCursor(sessionId, cursor);
            var path = await TryGetSessionFilePathAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (path is null) throw new AgentSessionHistoryException("missing_session");
            return await AgentJournalHistoryReader.OpenContainedAsync(_layout.SessionsRootPath, path, (containedPath, token) =>
                _journalFile.WithPathLockAsync(containedPath, async () =>
                {
                    await using var stream = await OpenHistoryReadStreamAsync(containedPath, token).ConfigureAwait(false);
                    return await AgentJournalHistoryReader.ReadAsync(stream, sessionId, cursor, () =>
                    {
                        var stamp = GetFileStamp(containedPath);
                        return stamp is null ? throw new AgentSessionHistoryException("history_changed")
                            : new AgentJournalHistoryReader.Stamp(stamp.Value.Length, stamp.Value.LastWriteTimeUtc.Ticks);
                    }, token).ConfigureAwait(false);
                }, token), cancellationToken).ConfigureAwait(false);
        }

        // Disable managed read-ahead so the stream requests respect the page-plus-probe byte budget.
        private static Task<FileStream> OpenHistoryReadStreamAsync(string path, CancellationToken cancellationToken)
            => AgentSessionJournalFile.RetryFileOperationAsync(
                () => Task.FromResult(new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, useAsync: true)),
                ReadRetryTime, cancellationToken);


    """;

    private const string HistoryComponent = """
    function History({ sessionId }: { sessionId: string }) {
      const [request, setRequest] = useState<HistoryRequest>({ sessionId, cursor: null });
      const [state, setState] = useState<HistoryState>();
      useEffect(() => {
        const abort = new AbortController();
        void loadHistory(workspace.history, request, abort.signal, setState);
        return () => abort.abort();
      }, [request]);
      const current = state?.request === request ? state : undefined;
      const page = current?.kind === "ready" ? current.page : undefined;
      return <section aria-labelledby="history-heading">
        <h3 id="history-heading">Persisted event history</h3>
        <p className="detail">One bounded page in journal order, not a reconstructed conversation. Deltas and completed content remain separate records. UTF-8 LF/CRLF only; payloads may be omitted or previews shortened. This does not refresh the catalog.</p>
        <p className="detail">Length/time checks detect changes, not same-stamp rewrites or all external-writer races. Canceling history forwards cancellation but does not guarantee stopping catalog loads or joining work on window close.</p>
        {(!current || current.kind === "loading") && <p role="status">Loading persisted history…</p>}
        {current?.kind === "error" && <p role="alert">{historyMessage(current.code)}</p>}
        {page && <>
          {page.entries.length === 0 && <p role="status">No visible events in this page. Metadata and blank records still count toward its read limit.</p>}
          {page.tailOmitted && <p role="status">The malformed final journal record was omitted; this is not complete history.</p>}
          <ol className="history-records">
            {page.entries.map(entry => <li key={entry.offset}>
              <strong>{entry.eventType}{entry.kind ? ` · ${entry.kind}` : ""}{entry.phase ? ` · ${entry.phase}` : ""}</strong>
              <div className="detail">{entry.timestamp} · byte {entry.offset} · provider {entry.providerId} · recorded session {entry.sessionId}{entry.runId ? ` · run ${entry.runId}` : ""}</div>
              {entry.contentId && <div className="detail">Content: {entry.contentId}</div>}
              {entry.activityId && <div className="detail">Activity: {entry.activityId}</div>}
              {entry.parentActivityId && <div className="detail">Parent activity: {entry.parentActivityId}</div>}
              {entry.name && <p>{entry.name}</p>}
              {entry.text !== null && <pre>{entry.text}</pre>}
              {entry.textTruncated && <p className="detail">Display preview shortened.</p>}
              {entry.bodyOmitted && <p className="detail">Additional stored payload omitted. No action is available for this record.</p>}
            </li>)}
          </ol>
        </>}
        <div className="history-controls">
          <button type="button" onClick={() => setRequest({ sessionId, cursor: null })}>Restart history</button>
          {page?.next && <button type="button" onClick={() => setRequest({ sessionId, cursor: page.next })}>Next page</button>}
        </div>
      </section>;
    }


    """;
}
