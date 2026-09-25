using System.Text;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class SessionViewBoundedHeaderTests
{
    [TestMethod]
    public async Task ExactPersistedHeaderAndMissingFileAreDistinguished()
    {
        using var f = new Fixture();
        var missing = await f.Store.ReadBoundedHeaderAsync("session-1", f.At);
        Assert.AreEqual(BoundedSessionHeaderStatus.Missing, missing.Status);
        var path = f.Write("session-1", f.At, "ProjectSession", "project-1", f.Workspace);
        var result = await f.Store.ReadBoundedHeaderAsync("session-1", f.At);
        Assert.AreEqual(BoundedSessionHeaderStatus.Found, result.Status);
        Assert.AreEqual("session-1", result.Header!.SessionId);
        Assert.AreEqual("project-1", result.Header.ProjectRef);
        Assert.IsTrue(result.BytesRead <= SessionViewJournalStore.MaximumBoundedHeaderBytes + 1);
        await File.AppendAllTextAsync(path, new string('x', 512 * 1024));
        Assert.AreEqual(BoundedSessionHeaderStatus.Found, (await f.Store.ReadBoundedHeaderAsync("session-1", f.At)).Status);
    }

    [TestMethod]
    public async Task OversizedOrCorruptFirstLineAndUnsafeIdNeverCertifyMetadata()
    {
        using var f = new Fixture();
        Assert.AreEqual(BoundedSessionHeaderStatus.Invalid, (await f.Store.ReadBoundedHeaderAsync("../session-1", f.At)).Status);
        var path = f.Write("session-1", f.At, "GlobalSession", null, f.Workspace);
        await File.WriteAllTextAsync(path, new string('x', SessionViewJournalStore.MaximumBoundedHeaderBytes + 128) + "\n");
        var tooLarge = await f.Store.ReadBoundedHeaderAsync("session-1", f.At);
        Assert.AreEqual(BoundedSessionHeaderStatus.Incomplete, tooLarge.Status);
        Assert.IsTrue(tooLarge.BytesRead <= SessionViewJournalStore.MaximumBoundedHeaderBytes + 1);
        await File.WriteAllBytesAsync(path, [0xff, 0xff, 0x0a]);
        Assert.AreEqual(BoundedSessionHeaderStatus.Invalid, (await f.Store.ReadBoundedHeaderAsync("session-1", f.At)).Status);
        f.Write("session-1", f.At, "GlobalSession", null, f.Workspace);
        var original = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, original.Replace("\"session_id\":", "\"session_id\":\"wrong\",\"session_id\":", StringComparison.Ordinal));
        Assert.AreEqual(BoundedSessionHeaderStatus.Invalid, (await f.Store.ReadBoundedHeaderAsync("session-1", f.At)).Status);
        await File.WriteAllTextAsync(path, original.Replace("\"kind\":\"GlobalSession\",", "", StringComparison.Ordinal));
        Assert.AreEqual(BoundedSessionHeaderStatus.Invalid, (await f.Store.ReadBoundedHeaderAsync("session-1", f.At)).Status);
        await File.WriteAllTextAsync(path, original.Replace("\"$type\":\"raw\"", "\"$type\":123", StringComparison.Ordinal));
        Assert.AreEqual(BoundedSessionHeaderStatus.Invalid, (await f.Store.ReadBoundedHeaderAsync("session-1", f.At)).Status);
    }

    [TestMethod]
    public async Task LockedAndObservedLinkedJournalFailClosedAndCancellationThrows()
    {
        using var f = new Fixture();
        var path = f.Write("session-1", f.At, "GlobalSession", null, f.Workspace);
        await Assert.ThrowsAsync<OperationCanceledException>(() => f.Store.ReadBoundedHeaderAsync("session-1", f.At, new CancellationToken(true)));
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.AreEqual(BoundedSessionHeaderStatus.ReadError, (await f.Store.ReadBoundedHeaderAsync("session-1", f.At)).Status);
        File.Delete(path);
        try
        {
            File.CreateSymbolicLink(path, Path.Combine(f.Root, "not-present"));
            Assert.AreEqual(BoundedSessionHeaderStatus.ReadError, (await f.Store.ReadBoundedHeaderAsync("session-1", f.At)).Status);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "alta-header-" + Guid.NewGuid().ToString("N"));
        public DateTimeOffset At { get; } = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);
        public string Workspace => Path.Combine(Root, "workspace");
        public SessionViewJournalStore Store { get; }
        public Fixture() { Directory.CreateDirectory(Root); Store = new(new CatalogOptions { GlobalRoot = Root }); }
        public string Write(string id, DateTimeOffset at, string kind, string? projectId, string path)
        {
            var journal = new AgentRuntimePathLayout(Root).GetSessionFilePath(id, at);
            Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
            var raw = System.Text.Json.JsonSerializer.Serialize(new { session_id = id, kind, project_ref = projectId,
                working_directory = path, backend_id = "provider", provider_key = "provider", created_at = at });
            File.WriteAllText(journal, "{\"$type\":\"raw\",\"backendEventType\":\"codealta.sessionHeader\",\"raw\":" + raw + "}\n", new UTF8Encoding(false));
            return journal;
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
