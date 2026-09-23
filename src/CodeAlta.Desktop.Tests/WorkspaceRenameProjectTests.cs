using System.Text;
using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class WorkspaceRenameProjectTests
{
    private const string Epoch = "11111111-1111-4111-8111-111111111111";

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExactFlatAndLegacySources_UpdateOnlyDisplayName_AndRefuseStaleOrWrongTargets(bool legacy)
    {
        using var fixture = new Fixture(legacy);
        await using var reads = fixture.Reads();
        var service = new WorkspaceService(reads, fixture.Catalog, Epoch);
        var preflight = await service.ReadProjectNameAsync(new(Epoch, fixture.Id, fixture.Path), CancellationToken.None);
        Assert.AreEqual("ok", preflight.Status);
        Assert.AreEqual(fixture.Source, preflight.SourcePath);
        Assert.AreEqual("First", preflight.DisplayName);
        Assert.AreEqual(64, preflight.Revision!.Length);
        Assert.AreEqual(preflight, JsonSerializer.Deserialize(JsonSerializer.Serialize(preflight,
            DesktopJsonContext.Default.WorkspaceReadProjectNameResponse), DesktopJsonContext.Default.WorkspaceReadProjectNameResponse));
        var request = new WorkspaceRenameProjectRequest(Epoch, fixture.Id, fixture.Path, preflight.SourcePath!, preflight.Revision!, "Renamed");
        Assert.AreEqual(request, JsonSerializer.Deserialize(JsonSerializer.Serialize(request,
            DesktopJsonContext.Default.WorkspaceRenameProjectRequest), DesktopJsonContext.Default.WorkspaceRenameProjectRequest));
        var before = await File.ReadAllBytesAsync(fixture.Source);
        var otherId = ProjectId.NewVersion7().ToString();
        Assert.AreEqual("scope_missing", (await service.RenameProjectAsync(request with { ProjectId = otherId }, CancellationToken.None)).Status);
        Assert.AreEqual("scope_missing", (await service.RenameProjectAsync(request with { ProjectPath = fixture.Root }, CancellationToken.None)).Status);
        Assert.AreEqual("conflict", (await service.RenameProjectAsync(request with { SourcePath = Path.Combine(fixture.Root, "other.md") }, CancellationToken.None)).Status);
        Assert.AreEqual("conflict", (await service.RenameProjectAsync(request with { Revision = new string('0', 64) }, CancellationToken.None)).Status);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.Source));
        Assert.AreEqual("stale_epoch", (await service.RenameProjectAsync(request with { ExpectedHostEpoch = Guid.NewGuid().ToString() }, CancellationToken.None)).Status);
        Assert.AreEqual("invalid_scope", (await service.RenameProjectAsync(request with { DisplayName = " bad " }, CancellationToken.None)).Status);
        Assert.AreEqual("invalid_scope", (await service.RenameProjectAsync(request with { Revision = "invalid" }, CancellationToken.None)).Status);
        Assert.IsNull((await service.RenameProjectAsync(request with { DisplayName = new string('x', 100_000) }, CancellationToken.None)).ProjectId);
        await File.AppendAllTextAsync(fixture.Source, "\r\n# changed outside the host", new UTF8Encoding(false));
        Assert.AreEqual("conflict", (await service.RenameProjectAsync(request, CancellationToken.None)).Status);
        CollectionAssert.AreEqual(before.Concat(new UTF8Encoding(false).GetBytes("\r\n# changed outside the host")).ToArray(),
            await File.ReadAllBytesAsync(fixture.Source));
        // Explicit fresh preflight is required after a stale raw-byte revision.
        preflight = await service.ReadProjectNameAsync(new(Epoch, fixture.Id, fixture.Path), CancellationToken.None);
        request = request with { Revision = preflight.Revision! };
        var first = await service.RenameProjectAsync(request, CancellationToken.None);
        Assert.AreEqual("ok", first.Status);
        Assert.AreEqual(first, JsonSerializer.Deserialize(JsonSerializer.Serialize(first,
            DesktopJsonContext.Default.WorkspaceRenameProjectResponse), DesktopJsonContext.Default.WorkspaceRenameProjectResponse));
        Assert.AreEqual("conflict", (await service.RenameProjectAsync(request, CancellationToken.None)).Status);
        var after = await File.ReadAllBytesAsync(fixture.Source);
        CollectionAssert.AreEqual(new UTF8Encoding(true).GetPreamble().Concat(new UTF8Encoding(true).GetBytes(
            new UTF8Encoding(true).GetString(before.AsSpan(3)).Replace("'First'", "\"Renamed\"", StringComparison.Ordinal)
                + "\r\n# changed outside the host")).ToArray(), after);
        Assert.AreEqual("Renamed", (await fixture.Catalog.GetByIdAsync(fixture.Id))!.DisplayName);
        if (legacy) Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "projects", "fixture.md")));
        Assert.AreEqual("original", await File.ReadAllTextAsync(fixture.Marker));
        await service.CloseImportsAsync();
    }

    [TestMethod]
    public async Task MissingArchivedUnsupportedAndReadFailure_DoNotWrite()
    {
        using var fixture = new Fixture(false);
        await using var reads = fixture.Reads();
        var service = new WorkspaceService(reads, fixture.Catalog, Epoch);
        var read = new WorkspaceReadProjectNameRequest(Epoch, fixture.Id, fixture.Path);
        Assert.AreEqual("scope_missing", (await service.ReadProjectNameAsync(read with { ProjectPath = fixture.Root }, CancellationToken.None)).Status);
        Assert.AreEqual("scope_missing", (await service.ReadProjectNameAsync(read with { ProjectId = ProjectId.NewVersion7().ToString() }, CancellationToken.None)).Status);
        Assert.AreEqual("unconfigured", (await new WorkspaceService((string?)null).ReadProjectNameAsync(read, CancellationToken.None)).Status);
        var observed = await service.ReadProjectNameAsync(read, CancellationToken.None);
        var request = new WorkspaceRenameProjectRequest(Epoch, fixture.Id, fixture.Path, observed.SourcePath!, observed.Revision!, "New");
        var contents = await File.ReadAllTextAsync(fixture.Source);
        await File.WriteAllTextAsync(fixture.Source, contents.Replace("display_name: 'First'", "display_name: |\r\n  First", StringComparison.Ordinal));
        Assert.AreEqual("unsupported", (await service.ReadProjectNameAsync(read, CancellationToken.None)).Status);
        Assert.AreEqual("unsupported", (await service.RenameProjectAsync(request, CancellationToken.None)).Status);
        await File.WriteAllTextAsync(fixture.Source, contents.Replace("display_name: 'First'", "display_name: 'First'\r\ndisplay_name: Duplicate", StringComparison.Ordinal));
        Assert.AreEqual("read_failure", (await service.ReadProjectNameAsync(read, CancellationToken.None)).Status);
        Assert.AreEqual("read_failure", (await service.RenameProjectAsync(request, CancellationToken.None)).Status);
        await File.WriteAllTextAsync(fixture.Source, contents);
        await File.WriteAllTextAsync(fixture.Source, contents.Replace("display_name:", "archived: true\r\ndisplay_name:", StringComparison.Ordinal), new UTF8Encoding(true));
        Assert.AreEqual("scope_missing", (await service.ReadProjectNameAsync(read, CancellationToken.None)).Status);
        Assert.AreEqual("scope_missing", (await service.RenameProjectAsync(request, CancellationToken.None)).Status);
        await service.CloseImportsAsync();
    }

    [TestMethod]
    public async Task AdmittedRename_BlocksImportAndDuplicate_CanceledWaitAndCloseDrain()
    {
        using var fixture = new Fixture(false);
        await using var reads = fixture.Reads();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var service = new WorkspaceService(reads, fixture.Catalog, Epoch, async (id, path, source, revision, name) =>
        {
            calls++;
            entered.TrySetResult();
            await release.Task;
            return await fixture.Catalog.RenameDisplayNameAsync(id, path, source, revision, name);
        });
        var observed = await service.ReadProjectNameAsync(new(Epoch, fixture.Id, fixture.Path), CancellationToken.None);
        var request = new WorkspaceRenameProjectRequest(Epoch, fixture.Id, fixture.Path, observed.SourcePath!, observed.Revision!, "New");
        using var cancel = new CancellationTokenSource();
        var pending = service.RenameProjectAsync(request, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
        Assert.AreEqual("busy", (await service.RenameProjectAsync(request, CancellationToken.None)).Status);
        Assert.AreEqual("busy", (await service.ReadProjectNameAsync(new(Epoch, fixture.Id, fixture.Path), CancellationToken.None)).Status);
        Assert.AreEqual("busy", (await service.OpenProjectAsync(new(Epoch, fixture.Path, true), CancellationToken.None)).Status);
        var closing = service.CloseImportsAsync();
        Assert.IsFalse(closing.IsCompleted);
        Assert.AreEqual("closed", (await service.RenameProjectAsync(request, CancellationToken.None)).Status);
        release.SetResult();
        await closing;
        Assert.AreEqual(1, calls);
        Assert.AreEqual("New", (await fixture.Catalog.GetByIdAsync(fixture.Id))!.DisplayName);
    }

    [TestMethod]
    public async Task AdmittedImport_BlocksReadAndRename_UntilShutdownDrains()
    {
        using var fixture = new Fixture(false);
        await using var reads = fixture.Reads();
        var hold = new TaskCompletionSource<ProjectDescriptor>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new WorkspaceService(reads, fixture.Catalog, Epoch, _ => hold.Task);
        var snapshot = await service.ReadProjectNameAsync(new(Epoch, fixture.Id, fixture.Path), CancellationToken.None);
        var request = new WorkspaceRenameProjectRequest(Epoch, fixture.Id, fixture.Path, snapshot.SourcePath!, snapshot.Revision!, "New");
        var import = service.OpenProjectAsync(new(Epoch, fixture.Path, true), CancellationToken.None);
        Assert.AreEqual("busy", (await service.ReadProjectNameAsync(new(Epoch, fixture.Id, fixture.Path), CancellationToken.None)).Status);
        Assert.AreEqual("busy", (await service.RenameProjectAsync(request, CancellationToken.None)).Status);
        var closing = service.CloseImportsAsync();
        Assert.IsFalse(closing.IsCompleted);
        hold.SetResult((await fixture.Catalog.GetByIdAsync(fixture.Id))!);
        await closing;
        Assert.AreEqual("ok", (await import).Status);
        Assert.AreEqual("First", (await fixture.Catalog.GetByIdAsync(fixture.Id))!.DisplayName);
        Assert.AreEqual("closed", (await service.ReadProjectNameAsync(new(Epoch, fixture.Id, fixture.Path), CancellationToken.None)).Status);
    }

    [TestMethod]
    public async Task WriteFailureAfterAdmission_IsUnconfirmed_AndNeverRetries()
    {
        using var fixture = new Fixture(false);
        await using var reads = fixture.Reads();
        var attempts = 0;
        var service = new WorkspaceService(reads, fixture.Catalog, Epoch, (_, _, _, _, _) =>
        {
            attempts++;
            return Task.FromException<ProjectDisplayNameRenameStatus>(new IOException("Lost write acknowledgement"));
        });
        var preflight = await service.ReadProjectNameAsync(new(Epoch, fixture.Id, fixture.Path), CancellationToken.None);
        var request = new WorkspaceRenameProjectRequest(Epoch, fixture.Id, fixture.Path, preflight.SourcePath!, preflight.Revision!, "New");
        Assert.AreEqual("rename_unconfirmed", (await service.RenameProjectAsync(request, CancellationToken.None)).Status);
        Assert.AreEqual(1, attempts);
        Assert.AreEqual("First", (await fixture.Catalog.GetByIdAsync(fixture.Id))!.DisplayName);
        await service.CloseImportsAsync();
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codealta-desktop-rename-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public string Source { get; }
        public string Marker { get; }
        public string Id { get; } = ProjectId.NewVersion7().ToString();
        public ProjectCatalog Catalog { get; }
        public Fixture(bool legacy)
        {
            Path = System.IO.Path.Combine(Root, "project");
            Source = legacy ? System.IO.Path.Combine(Root, "catalog", "projects", "fixture", "readme.md")
                : System.IO.Path.Combine(Root, "catalog", "projects", "fixture.md");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Source)!);
            Directory.CreateDirectory(Path);
            Marker = System.IO.Path.Combine(Path, "marker");
            File.WriteAllText(Marker, "original");
            Catalog = new(new CatalogOptions { GlobalRoot = System.IO.Path.Combine(Root, "catalog") });
            File.WriteAllText(Source, $"---\r\nkind: project\r\nid: '{Id}'\r\nslug: fixture\r\nname: Fixture\r\ndisplay_name: 'First' # comment\r\npath: '{Path.Replace("'", "''", StringComparison.Ordinal)}'\r\nunknown: value\r\n---\r\n\r\n# Body\r\n", new UTF8Encoding(true));
        }
        public OwnedSessionWorkspace Reads() => new(Catalog, new SessionViewJournalStore(Catalog.Options));
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
