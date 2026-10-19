using System.Text;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class ObservedProjectOwnershipTests
{
    private const string First = "01963b36-0d70-7a11-b3c2-1f2e3d4c5b6a";
    private const string Second = "01963b36-0d70-7a11-b3c2-1f2e3d4c5b6b";
    private const string Third = "01963b36-0d70-7a11-b3c2-1f2e3d4c5b6c";

    [TestMethod]
    public async Task AnswersAreThoseOfTheScan_WhateverChangesInTheCatalog()
    {
        using var f = new Fixture();
        f.Write("alpha", First, f.WorkspaceA);
        f.Write("bravo", Second, f.WorkspaceB);
        var requests = new[] { (First, f.WorkspaceA), (Second, f.WorkspaceB), (Third, f.WorkspaceC), (First, f.WorkspaceB) };
        // Each change of the catalog, then the same requests: the cached answers are always those of a new scan.
        var changes = new (string What, Action Change)[]
        {
            ("nothing", () => { }),
            ("archived", () => f.Write("alpha", First, f.WorkspaceA, archived: true)),
            ("added", () => f.Write("charlie", Third, f.WorkspaceC)),
            ("removed", () => File.Delete(Path.Combine(f.Projects, "bravo.md"))),
            ("legacy readme", () => f.Write("bravo", Second, f.WorkspaceB, legacy: true)),
            ("legacy readme changed", () => f.Write("bravo", Second, f.WorkspaceB, legacy: true, archived: true)),
            ("folder without readme", () => Directory.CreateDirectory(Path.Combine(f.Projects, "empty"))),
            ("duplicate identity", () => f.Write("delta", First, f.WorkspaceC)),
            ("duplicate removed", () => File.Delete(Path.Combine(f.Projects, "delta.md"))),
            ("catalog folder removed", () => Directory.Delete(f.Projects, recursive: true)),
            ("catalog folder back", () => f.Write("alpha", First, f.WorkspaceA)),
            ("unarchived same name", () => f.Write("alpha", First, f.WorkspaceA, archived: false, body: "notes")),
        };
        var cached = new ObservedProjectOwnership();
        foreach (var (what, change) in changes)
        {
            change();
            foreach (var (id, path) in requests)
            {
                for (var repeat = 0; repeat < 2; repeat++)
                {
                    var expected = await f.Catalog.ReadBoundedOwnershipAsync(id, path);
                    var actual = await cached.ReadAsync(f.Projects, token => f.Catalog.ReadBoundedOwnershipAsync(id, path, token), id, path, CancellationToken.None);
                    Assert.AreEqual(expected, actual, $"{what}: {id} at {path}");
                }
            }
        }
    }

    [TestMethod]
    public async Task UnchangedCatalog_IsNotScannedAgain_UntilTheAnswerIsTooOld()
    {
        using var f = new Fixture();
        f.Write("alpha", First, f.WorkspaceA);
        var now = 0L;
        var cached = new ObservedProjectOwnership(() => now);
        var scans = 0;
        Task<ProjectOwnershipResult> Scan(CancellationToken token) { scans++; return f.Catalog.ReadBoundedOwnershipAsync(First, f.WorkspaceA, token); }

        var first = await cached.ReadAsync(f.Projects, Scan, First, f.WorkspaceA, CancellationToken.None);
        Assert.AreEqual(ProjectOwnershipStatus.Match, first.Status);
        for (var i = 0; i < 5; i++) Assert.AreEqual(first, await cached.ReadAsync(f.Projects, Scan, First, f.WorkspaceA, CancellationToken.None));
        Assert.AreEqual(1, scans);
        // Another project is another answer.
        Assert.AreEqual(ProjectOwnershipStatus.Missing, (await cached.ReadAsync(f.Projects, _ => f.Catalog.ReadBoundedOwnershipAsync(Second, f.WorkspaceB), Second, f.WorkspaceB, CancellationToken.None)).Status);
        now += (long)ObservedProjectOwnership.MaximumAge.TotalMilliseconds - 1;
        await cached.ReadAsync(f.Projects, Scan, First, f.WorkspaceA, CancellationToken.None);
        Assert.AreEqual(1, scans);
        now += 1;
        Assert.AreEqual(first, await cached.ReadAsync(f.Projects, Scan, First, f.WorkspaceA, CancellationToken.None));
        Assert.AreEqual(2, scans);
    }

    [TestMethod]
    public async Task CatalogChangedDuringTheScan_IsNotKept()
    {
        using var f = new Fixture();
        f.Write("alpha", First, f.WorkspaceA);
        var cached = new ObservedProjectOwnership();
        var scans = 0;
        async Task<ProjectOwnershipResult> Changing(CancellationToken token)
        {
            scans++;
            var result = await f.Catalog.ReadBoundedOwnershipAsync(First, f.WorkspaceA, token);
            f.Write("alpha", First, f.WorkspaceA, archived: scans % 2 == 1);
            return result;
        }

        Assert.AreEqual(false, (await cached.ReadAsync(f.Projects, Changing, First, f.WorkspaceA, CancellationToken.None)).Archived);
        // The answer read before the change is not given again: the next request scans, and sees the change.
        Assert.AreEqual(true, (await cached.ReadAsync(f.Projects, Changing, First, f.WorkspaceA, CancellationToken.None)).Archived);
        Assert.AreEqual(2, scans);
    }

    [TestMethod]
    public async Task CanceledScan_IsNotKept()
    {
        using var f = new Fixture();
        f.Write("alpha", First, f.WorkspaceA);
        var cached = new ObservedProjectOwnership();
        await Assert.ThrowsAsync<OperationCanceledException>(() => cached.ReadAsync(f.Projects,
            token => f.Catalog.ReadBoundedOwnershipAsync(First, f.WorkspaceA, token), First, f.WorkspaceA, new CancellationToken(true)));
        var scans = 0;
        var answer = await cached.ReadAsync(f.Projects, token => { scans++; return f.Catalog.ReadBoundedOwnershipAsync(First, f.WorkspaceA, token); },
            First, f.WorkspaceA, CancellationToken.None);
        Assert.AreEqual(ProjectOwnershipStatus.Match, answer.Status);
        Assert.AreEqual(1, scans);
    }

    [TestMethod]
    public void Description_FollowsTheSourcesTheScanReads()
    {
        using var f = new Fixture();
        f.Write("alpha", First, f.WorkspaceA);
        var original = ObservedProjectOwnership.Describe(f.Projects);
        Assert.IsNotNull(original);
        Assert.AreEqual(original, ObservedProjectOwnership.Describe(f.Projects));
        f.Write("bravo", Second, f.WorkspaceB, legacy: true);
        var legacy = ObservedProjectOwnership.Describe(f.Projects);
        Assert.AreNotEqual(original, legacy);
        StringAssert.Contains(legacy, "bravo/readme.md|");
        File.AppendAllText(Path.Combine(f.Projects, "bravo", "readme.md"), "more");
        Assert.AreNotEqual(legacy, ObservedProjectOwnership.Describe(f.Projects));
        // A missing catalog folder is described too, with its ancestors.
        var missing = ObservedProjectOwnership.Describe(Path.Combine(f.Root, "absent", "projects"));
        StringAssert.Contains(missing, "projects|missing");
        StringAssert.Contains(missing, "absent|missing");
        Assert.IsNull(ObservedProjectOwnership.Describe("relative/projects"));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "codealta-observed-ownership-" + Guid.NewGuid().ToString("N"));
        public string Projects => Path.Combine(Root, "projects");
        public string WorkspaceA => Path.Combine(Root, "a");
        public string WorkspaceB => Path.Combine(Root, "b");
        public string WorkspaceC => Path.Combine(Root, "c");
        public ProjectCatalog Catalog { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Projects);
            foreach (var path in new[] { WorkspaceA, WorkspaceB, WorkspaceC }) Directory.CreateDirectory(path);
            Catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = Root });
        }

        public void Write(string slug, string id, string path, bool legacy = false, bool archived = false, string body = "")
        {
            var location = legacy ? Path.Combine(Projects, slug, "readme.md") : Path.Combine(Projects, slug + ".md");
            Directory.CreateDirectory(Path.GetDirectoryName(location)!);
            File.WriteAllText(location, $"---\nid: '{id}'\nslug: '{slug}'\nname: '{slug}'\npath: '{path}'\narchived: {archived.ToString().ToLowerInvariant()}\n---\n{body}",
                new UTF8Encoding(false));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
