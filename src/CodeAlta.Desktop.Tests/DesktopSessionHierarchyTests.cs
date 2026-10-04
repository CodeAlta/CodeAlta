using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopSessionHierarchyTests
{
    [TestMethod]
    public async Task ActualJournalHeadersAndCachedSummaries_ExposeOnlyVerifiedScopeAndBoundedLineage()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-hierarchy-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(root)) throw new InvalidOperationException("Fixture root already exists.");
        Directory.CreateDirectory(root);
        try
        {
            var global = Path.Combine(root, "global"); var firstPath = Path.Combine(root, "first"); var otherPath = Path.Combine(root, "other");
            foreach (var path in new[] { global, firstPath, otherPath }) Directory.CreateDirectory(path);
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = global });
            var first = await catalog.UpsertFromPathAsync(firstPath);
            var other = await catalog.UpsertFromPathAsync(otherPath);
            var journals = new SessionViewJournalStore(catalog.Options);
            var store = journals.CreateSessionStore();
            var created = new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);
            async Task Add(string id, string? parent, string title, string path, string? projectRef, SessionViewKind kind,
                string? headerPath = null)
            {
                var descriptor = new SessionViewDescriptor
                {
                    SessionId = id, ParentSessionId = parent, Kind = kind, ProjectRef = projectRef,
                    ProviderId = "fixture", ProviderKey = "fixture", WorkingDirectory = headerPath ?? path,
                    Title = title, CreatedAt = created, UpdatedAt = created,
                };
                await journals.EnsureHeaderAsync(descriptor);
                await store.UpsertSessionAsync(new AgentSessionSummary
                {
                    SessionId = id, ParentSessionId = parent, ProviderId = new("fixture"), ProviderKey = "fixture",
                    WorkingDirectory = path, Title = title, CreatedAt = created, UpdatedAt = created,
                });
            }
            var longTitle = new string('L', 300);
            await Add("child", "parent", longTitle, firstPath, first.Id, SessionViewKind.ProjectSession);
            await Add("parent", null, "Parent", firstPath, first.Id, SessionViewKind.ProjectSession);
            await Add("other", null, "Other", otherPath, other.Id, SessionViewKind.ProjectSession);
            await Add("wrong", "other", "Wrong parent", firstPath, first.Id, SessionViewKind.ProjectSession);
            await Add("orphan", "absent", "Orphan", firstPath, first.Id, SessionViewKind.ProjectSession);
            await Add("self", "self", "Self", firstPath, first.Id, SessionViewKind.ProjectSession);
            await Add("global", "parent", "Global", global, null, SessionViewKind.GlobalSession);
            await Add("global-in-project", "parent", "Global with project workspace", firstPath, null, SessionViewKind.GlobalSession);
            await Add("no-scope", "parent", "Unverified", firstPath, first.Id, SessionViewKind.ProjectSession, headerPath: otherPath);
            await Add("wrong-scope", "parent", "Wrong scope", firstPath, other.Id, SessionViewKind.ProjectSession);
            await using var reads = new OwnedSessionWorkspace(catalog, journals);
            var rpc = new WorkspaceService(reads, catalog, "11111111-1111-4111-8111-111111111111");
            var snapshot = await rpc.SnapshotAsync(new(), CancellationToken.None);
            Assert.HasCount(10, snapshot.Sessions);
            var child = snapshot.Sessions.Single(row => row.Id == "child");
            Assert.AreEqual("parent", child.ParentSessionId);
            Assert.AreEqual(first.Id, child.ProjectId);
            Assert.AreEqual("project", child.ScopeKind);
            Assert.AreEqual(256, child.Title.Length);
            Assert.AreEqual(longTitle, child.FullTitle);
            Assert.IsFalse(child.FullTitleTruncated);
            Assert.IsTrue(snapshot.DisplayTextTruncated);
            Assert.AreEqual(other.Id, snapshot.Sessions.Single(row => row.Id == "other").ProjectId);
            Assert.AreEqual("other", snapshot.Sessions.Single(row => row.Id == "wrong").ParentSessionId);
            Assert.AreEqual("absent", snapshot.Sessions.Single(row => row.Id == "orphan").ParentSessionId);
            Assert.AreEqual("self", snapshot.Sessions.Single(row => row.Id == "self").ParentSessionId);
            Assert.AreEqual("global", snapshot.Sessions.Single(row => row.Id == "global").ScopeKind);
            Assert.AreEqual("global", snapshot.Sessions.Single(row => row.Id == "global-in-project").ScopeKind);
            Assert.IsNull(snapshot.Sessions.Single(row => row.Id == "global").ProjectId);
            Assert.IsNull(snapshot.Sessions.Single(row => row.Id == "no-scope").ScopeKind);
            Assert.AreEqual("parent", snapshot.Sessions.Single(row => row.Id == "no-scope").ParentSessionId);
            Assert.IsNull(snapshot.Sessions.Single(row => row.Id == "wrong-scope").ScopeKind);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, DesktopJsonContext.Default.WorkspaceSnapshot);
            Assert.IsTrue(bytes.Length < 768 * 1024);
            var fromWire = JsonSerializer.Deserialize(bytes, DesktopJsonContext.Default.WorkspaceSnapshot);
            Assert.IsNotNull(fromWire);
            Assert.AreEqual(longTitle, fromWire.Sessions.Single(row => row.Id == "child").FullTitle);
            var catalogOnly = await new WorkspaceService(global).SnapshotAsync(new(), CancellationToken.None);
            Assert.AreEqual("project", catalogOnly.Sessions.Single(row => row.Id == "child").ScopeKind);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task SessionWhoseRecordIsStampedAfterItsHeader_KeepsItsProjectAndTheHeaderTime()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-header-time-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var global = Path.Combine(root, "global"); var projectPath = Path.Combine(root, "project");
            foreach (var path in new[] { global, projectPath }) Directory.CreateDirectory(path);
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = global });
            var project = await catalog.UpsertFromPathAsync(projectPath);
            var journals = new SessionViewJournalStore(catalog.Options);
            var store = journals.CreateSessionStore();
            // Older sessions: the header names the instant the session was created, the provider's own record
            // an instant a few milliseconds later. Until such a session is resumed the list has the later one.
            var header = new DateTimeOffset(2026, 9, 21, 17, 28, 57, 648, TimeSpan.Zero);
            var recorded = header.AddMilliseconds(11);
            await journals.EnsureHeaderAsync(new SessionViewDescriptor
            {
                SessionId = "older", Kind = SessionViewKind.ProjectSession, ProjectRef = project.Id, ProviderId = "fixture",
                ProviderKey = "fixture", WorkingDirectory = projectPath, Title = "Older", CreatedAt = header, UpdatedAt = header,
            });
            await store.UpsertSessionAsync(new AgentSessionSummary
            {
                SessionId = "older", ProviderId = new("fixture"), ProviderKey = "fixture", WorkingDirectory = projectPath,
                Title = "Older", CreatedAt = recorded, UpdatedAt = recorded,
            });

            await using var reads = new OwnedSessionWorkspace(catalog, journals);
            var owned = await reads.ReadSnapshotAsync(CancellationToken.None);
            Assert.AreEqual(header, owned.Sessions.Single().CreatedAt, "the header's instant is the session's");
            Assert.AreEqual(header, owned.SessionHeaders["older"].CreatedAt);

            foreach (var service in new[] { new WorkspaceService(reads, catalog, "11111111-1111-4111-8111-111111111111"), new WorkspaceService(global) })
            {
                var row = (await service.SnapshotAsync(new(), CancellationToken.None)).Sessions.Single();
                Assert.AreEqual("project", row.ScopeKind);
                Assert.AreEqual(project.Id, row.ProjectId);
                Assert.AreEqual(header, row.CreatedAt);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task ProjectWhoseFolderIsGone_IsLeftOutWithItsSessions()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-missing-folder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var global = Path.Combine(root, "global"); var keptPath = Path.Combine(root, "kept"); var gonePath = Path.Combine(root, "gone");
            foreach (var path in new[] { global, keptPath, gonePath }) Directory.CreateDirectory(path);
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = global });
            var kept = await catalog.UpsertFromPathAsync(keptPath);
            var gone = await catalog.UpsertFromPathAsync(gonePath);
            var journals = new SessionViewJournalStore(catalog.Options);
            var store = journals.CreateSessionStore();
            var created = new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);
            foreach (var (id, project, path) in new[] { ("in-kept", kept.Id, keptPath), ("in-gone", gone.Id, gonePath) })
            {
                await journals.EnsureHeaderAsync(new SessionViewDescriptor
                {
                    SessionId = id, Kind = SessionViewKind.ProjectSession, ProjectRef = project, ProviderId = "fixture",
                    ProviderKey = "fixture", WorkingDirectory = path, Title = id, CreatedAt = created, UpdatedAt = created,
                });
                await store.UpsertSessionAsync(new AgentSessionSummary
                {
                    SessionId = id, ProviderId = new("fixture"), ProviderKey = "fixture", WorkingDirectory = path,
                    Title = id, CreatedAt = created, UpdatedAt = created,
                });
            }
            await using var reads = new OwnedSessionWorkspace(catalog, journals);
            var services = new[] { new WorkspaceService(reads, catalog, "11111111-1111-4111-8111-111111111111"), new WorkspaceService(global) };
            foreach (var service in services)
                Assert.HasCount(2, (await service.SnapshotAsync(new(), CancellationToken.None)).Sessions);

            Directory.Delete(gonePath, recursive: true);
            foreach (var service in services)
            {
                var snapshot = await service.SnapshotAsync(new(), CancellationToken.None);
                Assert.AreEqual(kept.Id, snapshot.Projects.Single().Id);
                Assert.AreEqual("in-kept", snapshot.Sessions.Single().Id);
            }

            // Nothing was removed from the catalog: the project and its session are back with the folder.
            Directory.CreateDirectory(gonePath);
            foreach (var service in services)
                Assert.HasCount(2, (await service.SnapshotAsync(new(), CancellationToken.None)).Sessions);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void OversizeParentAndFullTitle_DoNotHideRowOrExceedWireBudget()
    {
        var session = new AgentSessionMetadata("id", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            ParentSessionId: new string('p', 257), Details: new RawApiSessionMetadataDetails(Title: new string('x', 5000)));
        var snapshot = WorkspaceService.ProjectSnapshot([], [session]);
        Assert.HasCount(1, snapshot.Sessions);
        Assert.AreEqual("invalid_parent", snapshot.Sessions[0].LineageIssue);
        Assert.IsNull(snapshot.Sessions[0].ParentSessionId);
        Assert.IsTrue(snapshot.Sessions[0].FullTitleTruncated);
        Assert.AreEqual(4096, snapshot.Sessions[0].FullTitle.Length);
        Assert.IsTrue(snapshot.DisplayTextTruncated);
        Assert.IsTrue(JsonSerializer.SerializeToUtf8Bytes(snapshot, DesktopJsonContext.Default.WorkspaceSnapshot).Length < 768 * 1024);
    }

    [TestMethod]
    public void SnapshotAtSessionLimit_PreservesVisibleRowsAndReportsTruncation()
    {
        var sessions = Enumerable.Range(0, 501).Select(index => new AgentSessionMetadata($"session-{index:D4}",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(index),
            Summary: $"Session {index}")).ToArray();
        var snapshot = WorkspaceService.ProjectSnapshot([], sessions);
        Assert.HasCount(500, snapshot.Sessions);
        Assert.IsTrue(snapshot.SessionsTruncated);
        Assert.AreEqual("session-0500", snapshot.Sessions[0].Id);
        Assert.AreEqual("session-0001", snapshot.Sessions[^1].Id);
        Assert.IsTrue(JsonSerializer.SerializeToUtf8Bytes(snapshot, DesktopJsonContext.Default.WorkspaceSnapshot).Length < 768 * 1024);
    }
}
