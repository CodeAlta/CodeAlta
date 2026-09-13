using CodeAlta.Tests;

namespace CodeAlta.Desktop.Tests;

/// <summary>Named raw-current source and literal preservation checks; no runtime/native acquisition.</summary>
[TestClass]
public sealed class DesktopNotesSourceTests
{
    [TestMethod]
    public void CurrentNotesWiring_IsContainedOwnedReadOnlyAndExplicit()
    {
        var store = Read("CodeAlta.Agent/Runtime/FileSystemAgentSessionStore.cs");
        var start = store.IndexOf("public async Task<AgentNotesEvent?> ReadLatestNotesContainedAsync", StringComparison.Ordinal);
        Assert.IsTrue(start >= 0);
        var contained = store[start..store.IndexOf("    private Task<AgentNotesEvent?> ReadLatestNotesAtPathAsync", start, StringComparison.Ordinal)];
        StringAssert.Contains(contained, "AgentJournalHistoryReader.OpenContainedAsync(_layout.SessionsRootPath, path, ReadLatestNotesAtPathAsync, cancellationToken)");
        Assert.IsFalse(contained.Contains("ReadLatestNotesAsync(", StringComparison.Ordinal));
        var runtime = Read("CodeAlta.Orchestration/Runtime/SessionRuntimeService.cs");
        StringAssert.Contains(runtime, "AdmitAsync(() => GetOwnedNotesMarkdownBodyAsync(sessionId, cancellationToken)");
        StringAssert.Contains(runtime, ".ReadLatestNotesContainedAsync(session.SessionId, cancellationToken)");
        var reads = Read("CodeAlta.Orchestration/Runtime/OwnedSessionWorkspace.cs");
        StringAssert.Contains(reads, "_notes = runtime.GetOwnedNotesMarkdownAsync;");
        StringAssert.Contains(reads, "return Admit(() => _notes(sessionId, CancellationToken.None), cancellationToken);");
        StringAssert.Contains(reads, "if (_active.Count == 8)");
        var app = Read("CodeAlta/Desktop/DesktopApplication.cs");
        StringAssert.Contains(app, "builder.AddSessionNotesService(new SessionNotesService(host.WorkspaceReads, epoch));");
        var legacy = app[app.IndexOf("    private async ValueTask RunAsync(", StringComparison.Ordinal)..];
        Assert.IsFalse(legacy.Contains("AddSessionNotesService", StringComparison.Ordinal));
        var rpc = Read("CodeAlta/Desktop/Rpc/SessionNotesRpc.cs");
        StringAssert.Contains(rpc, "[NeoRpcMethod(\"current\")]");
        foreach (var forbidden in new[] { "UpdateNotes", "SetMarkdown", "AltaCommandDispatcher", "StreamEventsAsync", "EnableOwnedAsks" })
            Assert.IsFalse((rpc + reads).Contains(forbidden, StringComparison.Ordinal), forbidden);
        var boot = Read("CodeAlta/Desktop/Rpc/BootRpc.cs");
        StringAssert.Contains(boot, "[JsonSerializable(typeof(SessionNotesRequest))]");
        StringAssert.Contains(boot, "[JsonSerializable(typeof(SessionNotesResponse))]");
        var main = Read("CodeAlta/frontend/src/main.tsx");
        StringAssert.Contains(main, "useState(() => createNotesReader(sessionNotes.current))");
        StringAssert.Contains(main, "reader={notesReader} capability={mutation.capability}");
        var panel = Read("CodeAlta/frontend/src/NotesPanel.tsx");
        StringAssert.Contains(panel, "<pre>{state.markdown}</pre>");
        StringAssert.Contains(panel, "Refresh notes");
        StringAssert.Contains(panel, "onClick={() => { void selection.current?.refresh(); }}");
        var effectStart = panel.IndexOf("useEffect(() => {", StringComparison.Ordinal);
        Assert.IsTrue(effectStart >= 0);
        var effectEnd = panel.IndexOf("}, [epoch, sessionId, reader, capability]);", effectStart, StringComparison.Ordinal);
        Assert.IsTrue(effectEnd > effectStart);
        Assert.IsFalse(panel[effectStart..effectEnd].Contains("refresh", StringComparison.OrdinalIgnoreCase));
        var helper = Read("CodeAlta/frontend/src/sessionNotes.ts");
        var epochIndex = helper.IndexOf("observeEpoch(value, request.expectedHostEpoch", StringComparison.Ordinal);
        var fenceIndex = helper.IndexOf("if (!current()) return;", StringComparison.Ordinal);
        Assert.IsTrue(epochIndex >= 0);
        Assert.IsTrue(fenceIndex > epochIndex);
        foreach (var forbidden in new[] { "dangerouslySetInnerHTML", "localStorage", "setInterval(", "fetch(" })
            Assert.IsFalse((panel + helper).Contains(forbidden, StringComparison.Ordinal), forbidden);
    }

    [TestMethod]
    public void NotesDelta_RestoresFrozenOriginalsBeforeHistoricalReaders()
    {
        foreach (var path in OwnedSessionNotesSourceInverse.Paths)
        {
            var source = Read(path);
            foreach (var text in new[] { source, source.Replace("\n", "\r\n", StringComparison.Ordinal) })
            {
                var restored = OwnedSessionNotesSourceInverse.Restore(path, text);
                Assert.AreEqual(OwnedSessionNotesSourceInverse.Original(path), OwnedSessionAskSourceInverse.GitObjectId(restored), path);
            }
        }
        foreach (var path in OwnedSessionAskSourceInverse.Paths)
            Assert.AreEqual(OwnedSessionAskSourceInverse.Original(path), OwnedSessionAskSourceInverse.GitObjectId(OwnedSessionAskSourceInverse.Restore(path, Read(path))), path);
    }

    private static string Read(string path) => SourceTestText.DecodeSource(File.ReadAllBytes(Path.Combine(DesktopArchitectureTests.SourceRoot, path)));
}
