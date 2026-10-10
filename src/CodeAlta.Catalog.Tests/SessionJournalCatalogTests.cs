using System.Text;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class SessionJournalCatalogTests
{
    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-journal-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string WriteJournal(string root, string id, string day, string text, DateTime? lastWriteUtc = null)
    {
        var parts = day.Split('/');
        var folder = Path.Combine(root, "sessions", parts[0], parts[1], parts[2]);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, id + ".jsonl");
        File.WriteAllText(path, text, new UTF8Encoding(false));
        if (lastWriteUtc is { } time)
        {
            File.SetLastWriteTimeUtc(path, time);
        }

        return path;
    }

    private static async Task<List<SessionJournalFile>> ListAsync(ISessionJournalCatalog catalog)
    {
        var files = new List<SessionJournalFile>();
        await foreach (var file in catalog.ListAsync())
        {
            files.Add(file);
        }

        return files;
    }

    [TestMethod]
    public async Task ListAsync_ListsEveryJournalOfTheStore_TheMostRecentlyChangedFirst()
    {
        var root = NewRoot();
        try
        {
            WriteJournal(root, "session-a", "2026/05/01", "{}\n", new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc));
            WriteJournal(root, "session-b", "2026/10/09", "{}\n{}\n", new DateTime(2026, 10, 9, 10, 0, 0, DateTimeKind.Utc));
            WriteJournal(root, "session-c", "2026/07/15", "{}\n", new DateTime(2026, 7, 15, 10, 0, 0, DateTimeKind.Utc));
            var traces = Path.Combine(root, "sessions", "traces");
            Directory.CreateDirectory(traces);
            File.WriteAllText(Path.Combine(traces, "session-a.jsonl"), "not a journal");
            File.WriteAllText(Path.Combine(root, "sessions", "2026", "05", "01", "session-a.attachments"), "not a journal");
            var catalog = new FileSystemSessionJournalCatalog(new AgentRuntimePathLayout(root));

            var files = await ListAsync(catalog);

            CollectionAssert.AreEqual(new[] { "session-b", "session-c", "session-a" }, files.Select(static file => file.SessionId).ToArray());
            Assert.AreEqual(6, files[0].Length);
            Assert.AreEqual(new DateTimeOffset(2026, 10, 9, 10, 0, 0, TimeSpan.Zero), files[0].LastWriteUtc);
            Assert.IsTrue(Path.IsPathFullyQualified(files[0].Path));
            Assert.IsTrue(files.All(static file => File.Exists(file.Path)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ListAsync_OfAFolderThatDoesNotExistIsEmpty()
    {
        var catalog = new FileSystemSessionJournalCatalog(new AgentRuntimePathLayout(Path.Combine(Path.GetTempPath(), "codealta-missing-" + Guid.NewGuid().ToString("N"))));

        Assert.AreEqual(0, (await ListAsync(catalog)).Count);
        Assert.IsNull(await catalog.GetAsync("anything"));
        Assert.IsNull(await catalog.OpenAsync("anything"));
    }

    [TestMethod]
    public async Task ListAsync_ListsTheSessionsOfARemovedProjectToo()
    {
        // The store knows nothing of projects: a journal is listed whatever its header says.
        var root = NewRoot();
        try
        {
            WriteJournal(root, "orphan", "2026/06/01", "{\"$type\":\"raw\",\"backendEventType\":\"codealta.sessionHeader\",\"raw\":{\"project_ref\":\"a-removed-project\"}}\n");
            var catalog = new FileSystemSessionJournalCatalog(new AgentRuntimePathLayout(root));

            Assert.AreEqual("orphan", (await ListAsync(catalog)).Single().SessionId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task GetAsync_FindsTheJournalOfASession_AndNullWhenThereIsNone()
    {
        var root = NewRoot();
        try
        {
            var path = WriteJournal(root, "session-a", "2026/05/01", "{}\n");
            var catalog = new FileSystemSessionJournalCatalog(new AgentRuntimePathLayout(root));

            var found = await catalog.GetAsync("session-a");

            Assert.AreEqual(path, found!.Path);
            Assert.AreEqual(3, found.Length);
            Assert.IsNull(await catalog.GetAsync("session-b"));
            await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await catalog.GetAsync(" "));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task OpenAsync_ReadsFromAnOffset_WhileTheSessionKeepsWritingAndReplacingTheFile()
    {
        var root = NewRoot();
        try
        {
            var path = WriteJournal(root, "session-a", "2026/05/01", "first line\nsecond line\n");
            var catalog = new FileSystemSessionJournalCatalog(new AgentRuntimePathLayout(root));

            // The session holds the file the way the journal writer does: for writing, letting readers read.
            using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            writer.Seek(0, SeekOrigin.End);
            await using var stream = (await catalog.OpenAsync("session-a", offset: 11))!;
            Assert.AreEqual(11, stream.Position);
            Assert.IsTrue(stream.CanSeek);

            var bytes = Encoding.UTF8.GetBytes("third line\n");
            await writer.WriteAsync(bytes);
            await writer.FlushAsync();

            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 64);
            Assert.AreEqual("second line", await reader.ReadLineAsync());
            Assert.AreEqual("third line", await reader.ReadLineAsync());

            // The file may be deleted while a reader has it open: the sharing flags allow it.
            writer.Dispose();
            File.Delete(path);
            Assert.IsNull(await catalog.OpenAsync("session-a"), "a deleted journal is not there any more");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task OpenAsync_RefusesANegativeOffset()
    {
        var catalog = new FileSystemSessionJournalCatalog(new AgentRuntimePathLayout(NewRoot()));

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () => await catalog.OpenAsync("a", -1));
    }

    [TestMethod]
    public async Task JournalStore_CreatesACatalogOverItsOwnSessionsFolder()
    {
        var root = NewRoot();
        try
        {
            var options = new CatalogOptions { GlobalRoot = root };
            WriteJournal(root, "session-a", "2026/05/01", "{}\n");
            var store = new SessionViewJournalStore(options);

            var catalog = store.CreateJournalCatalog();

            Assert.AreEqual("session-a", (await ListAsync(catalog)).Single().SessionId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
