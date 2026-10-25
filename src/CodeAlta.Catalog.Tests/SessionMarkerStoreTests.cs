namespace CodeAlta.Catalog.Tests;

/// <summary>
/// A process without the application database names the sessions it writes with markers, which the instance that owns
/// the sessions folder lists and deletes.
/// </summary>
[TestClass]
public sealed class SessionMarkerStoreTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 25, 9, 30, 0, TimeSpan.Zero);

    [TestMethod]
    public void AMarker_IsWrittenReadListedAndDeleted()
    {
        using var fixture = new Fixture();
        Assert.AreEqual(fixture.Options.SessionMarkersRoot, fixture.Store.MarkersDirectory);
        Assert.AreEqual(0, fixture.Store.List().Count, "No folder: no marker.");

        var marker = new SessionMarker("s-1", "2026/10/25/s-1.jsonl", Created, SessionLeaseOwner.CliKind, 4242);
        fixture.Store.Write(marker);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Store.MarkersDirectory, "s-1.json")));
        Assert.AreEqual(marker, fixture.Store.TryRead("s-1"));
        CollectionAssert.AreEqual(new[] { marker }, fixture.Store.List().ToArray());
        Assert.AreEqual(Path.Combine(fixture.Options.SessionsRoot, "2026", "10", "25", "s-1.jsonl"), fixture.Store.GetJournalPath(marker));

        var json = File.ReadAllText(Path.Combine(fixture.Store.MarkersDirectory, "s-1.json"));
        foreach (var name in new[] { "\"sessionId\"", "\"journalPath\"", "\"createdUtc\"", "\"writer\"", "\"pid\"" })
        {
            StringAssert.Contains(json, name);
        }

        var replaced = marker with { ProcessId = 7 };
        fixture.Store.Write(replaced);
        Assert.AreEqual(replaced, fixture.Store.TryRead("s-1"), "A marker written again replaces the one there.");

        Assert.IsTrue(fixture.Store.Delete("s-1"));
        Assert.IsFalse(fixture.Store.Delete("s-1"));
        Assert.IsNull(fixture.Store.TryRead("s-1"));
        Assert.AreEqual(0, fixture.Store.List().Count);
    }

    [TestMethod]
    public void AWrite_LeavesNoPartialMarkerForList_AndListSkipsWhatIsNotAWholeMarker()
    {
        using var fixture = new Fixture();
        fixture.Store.Write(new SessionMarker("kept", "2026/10/25/kept.jsonl", Created, SessionLeaseOwner.CliKind, 1));
        var folder = fixture.Store.MarkersDirectory;
        Assert.AreEqual(1, Directory.GetFiles(folder).Length, "Only the marker: the staged file was renamed onto it.");

        // A writer stopped half way leaves a staged file, never a marker; and what is not a marker is skipped.
        File.WriteAllText(Path.Combine(folder, "staged.0123456789abcdef.tmp"), "{\"sessionId\":\"staged\"");
        File.WriteAllText(Path.Combine(folder, "cut.json"), "{\"sessionId\":\"cut\",\"journalPa");
        File.WriteAllText(Path.Combine(folder, "garbage.json"), "not json at all");
        File.WriteAllText(Path.Combine(folder, "empty.json"), string.Empty);
        File.WriteAllText(Path.Combine(folder, "other-name.json"), "{\"sessionId\":\"someone-else\",\"journalPath\":\"a.jsonl\",\"createdUtc\":\"2026-10-25T09:30:00+00:00\",\"writer\":\"altacli\",\"pid\":1}");
        File.WriteAllText(Path.Combine(folder, "escape.json"), "{\"sessionId\":\"escape\",\"journalPath\":\"../../secret.jsonl\",\"createdUtc\":\"2026-10-25T09:30:00+00:00\",\"writer\":\"altacli\",\"pid\":1}");
        File.WriteAllText(Path.Combine(folder, "no-writer.json"), "{\"sessionId\":\"no-writer\",\"journalPath\":\"a.jsonl\",\"createdUtc\":\"2026-10-25T09:30:00+00:00\",\"pid\":1}");
        File.WriteAllText(Path.Combine(folder, "wrong-type.json"), "{\"sessionId\":\"wrong-type\",\"journalPath\":\"a.jsonl\",\"createdUtc\":\"2026-10-25T09:30:00+00:00\",\"writer\":\"altacli\",\"pid\":\"one\"}");
        Directory.CreateDirectory(Path.Combine(folder, "folder.json"));

        CollectionAssert.AreEqual(new[] { "kept" }, fixture.Store.List().Select(static marker => marker.SessionId).ToArray());
        Assert.IsNull(fixture.Store.TryRead("garbage"));
        Assert.IsNull(fixture.Store.TryRead("escape"));
    }

    [TestMethod]
    public void AMarker_NamesAJournalInsideTheSessionsFolder()
    {
        using var fixture = new Fixture();
        var marker = new SessionMarker("s-2", "2026\\10\\25\\s-2.jsonl", Created, SessionLeaseOwner.CliKind, 1);
        fixture.Store.Write(marker);
        Assert.AreEqual("2026/10/25/s-2.jsonl", fixture.Store.TryRead("s-2")?.JournalPath, "Separators are written as '/'.");

        foreach (var journal in new[] { "", "/abs/s.jsonl", "C:/s.jsonl", "../s.jsonl", "a/../../s.jsonl", "a//s.jsonl", "s.json", "markers/s.jsonl", "leases/s.jsonl" })
        {
            Assert.ThrowsExactly<ArgumentException>(() => fixture.Store.Write(marker with { JournalPath = journal }), journal);
        }

        foreach (var id in new[] { "", "a/b", "..", "con", "a:b" })
        {
            Assert.ThrowsExactly<ArgumentException>(() => fixture.Store.Write(marker with { SessionId = id }), id);
            Assert.ThrowsExactly<ArgumentException>(() => fixture.Store.TryRead(id), id);
            Assert.ThrowsExactly<ArgumentException>(() => fixture.Store.Delete(id), id);
        }

        Assert.ThrowsExactly<ArgumentException>(() => fixture.Store.Write(marker with { Writer = " " }));
        Assert.ThrowsExactly<ArgumentNullException>(() => fixture.Store.Write(null!));
    }

    [TestMethod]
    public void TheMarkersAndLeases_AreNeverTakenForJournals()
    {
        using var fixture = new Fixture();
        fixture.Store.Write(new SessionMarker("s-3", "2026/10/25/s-3.jsonl", Created, SessionLeaseOwner.CliKind, 1));
        using var lease = new SessionLeaseStore(fixture.Options).TryAcquire("s-3", new SessionLeaseOwner(SessionLeaseOwner.CliKind, "run"));
        Assert.IsNotNull(lease);

        // The journal scans list *.jsonl under the sessions folder, at any depth.
        Assert.AreEqual(0, Directory.EnumerateFiles(fixture.Options.SessionsRoot, "*.jsonl", SearchOption.AllDirectories).Count());
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "codealta-session-marker-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Options = new CatalogOptions { GlobalRoot = Root };
            Store = new SessionMarkerStore(Options);
        }

        public string Root { get; }

        public CatalogOptions Options { get; }

        public SessionMarkerStore Store { get; }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
