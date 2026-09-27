using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class SkillsInspectionRpcTests
{
    private const string Epoch = "11111111-1111-4111-8111-111111111111";

    [TestMethod]
    public async Task ParsedMultilineDescriptionIsPreservedWithoutBodyOrOtherFrontmatter()
    {
        using var fixture = new Fixture();
        var directory = Directory.CreateDirectory(Path.Combine(fixture.Root, "test-skill"));
        await File.WriteAllTextAsync(Path.Combine(directory.FullName, "SKILL.md"),
            "---\nname: test-skill\ndescription: |\n  First\n  second\nlicense: PRIVATE_LICENSE\n---\nPRIVATE_BODY");
        var service = new SkillsInspectionService(Epoch, (_, _) => Task.FromResult(("ok", (string?)fixture.Root)));
        var response = await service.ScanAsync(Request(), default);
        Assert.AreEqual("parsed", response.Candidates.Single().Status);
        Assert.AreEqual("First\nsecond", response.Candidates.Single().Description);
        Assert.IsFalse(JsonSerializer.Serialize(response, DesktopJsonContext.Default.SkillsScanResponse).Contains("PRIVATE_", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ActualBoundedHeaderAndCatalogOwnBothRootsAndRefuseChangedOrArchivedScopes()
    {
        using var fixture = new Fixture();
        var options = new CatalogOptions { GlobalRoot = Path.Combine(fixture.Root, "global") };
        var catalog = new ProjectCatalog(options);
        var projectRoot = Directory.CreateDirectory(Path.Combine(fixture.Root, "project")).FullName;
        var project = await catalog.UpsertFromPathAsync(projectRoot);
        var journal = new SessionViewJournalStore(options);
        var session = new SessionViewDescriptor { SessionId = "skill-session", Kind = SessionViewKind.ProjectSession,
            ProjectRef = project.Id, WorkingDirectory = projectRoot, ProviderId = "fake", ProviderKey = "fake", CreatedAt = DateTimeOffset.UtcNow, Title = "test" };
        await journal.EnsureHeaderAsync(session);
        var service = new SkillsInspectionService(catalog, journal, Epoch);
        var request = Request() with { SessionId = session.SessionId, CreatedAt = session.CreatedAt.ToString("O"),
            Scope = "project", ProjectId = project.Id, ProjectPath = projectRoot, RootKind = "project_alta" };
        var root = Directory.CreateDirectory(Path.Combine(projectRoot, ".alta", "skills", "project-skill")).FullName;
        await File.WriteAllTextAsync(Path.Combine(root, "SKILL.md"), "---\nname: project-skill\ndescription: project metadata\n---\nPRIVATE BODY");
        var user = Directory.CreateDirectory(Path.Combine(options.GlobalRoot, "skills", "user-skill")).FullName;
        await File.WriteAllTextAsync(Path.Combine(user, "SKILL.md"), "---\nname: user-skill\ndescription: user metadata\n---");
        var projectResult = await service.ScanAsync(request, default);
        Assert.AreEqual("ok", projectResult.Status);
        Assert.AreEqual("project-skill", projectResult.Candidates.Single().Name);
        Assert.AreEqual("user-skill", (await service.ScanAsync(request with { RootKind = "user_alta" }, default)).Candidates.Single().Name);
        Assert.AreEqual("scope_mismatch", (await service.ScanAsync(request with { Scope = "global", RootKind = "user_alta", ProjectId = null, ProjectPath = null }, default)).Status);
        var global = new SessionViewDescriptor { SessionId = "global-skill-session", Kind = SessionViewKind.GlobalSession,
            WorkingDirectory = options.GlobalRoot, ProviderId = "fake", ProviderKey = "fake", CreatedAt = session.CreatedAt, Title = "global test" };
        await journal.EnsureHeaderAsync(global);
        var globalRequest = Request() with { SessionId = global.SessionId, CreatedAt = global.CreatedAt.ToString("O") };
        Assert.AreEqual("user-skill", (await service.ScanAsync(globalRequest, default)).Candidates.Single().Name);
        Assert.AreEqual("invalid_request", (await service.ScanAsync(globalRequest with { RootKind = "project_alta" }, default)).Status);
        Assert.AreEqual("scope_mismatch", (await service.ScanAsync(request with { ProjectPath = fixture.Root }, default)).Status);
        Assert.AreEqual("scope_mismatch", (await service.ScanAsync(request with { ProjectId = Guid.NewGuid().ToString("D") }, default)).Status);
        Assert.AreEqual("metadata_unavailable", (await service.ScanAsync(request with { SessionId = "not-saved" }, default)).Status);
        Assert.AreEqual("metadata_unavailable", (await service.ScanAsync(request with { CreatedAt = DateTimeOffset.UnixEpoch.ToString("O") }, default)).Status);
        project.Archived = true; await catalog.SaveAsync(project);
        Assert.AreEqual("archived_project", (await service.ScanAsync(request, default)).Status);
        Assert.AreEqual("archived_project", (await service.ScanAsync(request with { RootKind = "user_alta" }, default)).Status);
        project.Archived = false; await catalog.SaveAsync(project);
        File.Delete(Directory.EnumerateFiles(options.ProjectsRoot, "*.md").Single());
        Assert.AreEqual("project_unverified", (await service.ScanAsync(request, default)).Status);
    }

    [TestMethod]
    public async Task LimitsAndIgnoredPathsAreRawObservationsNeverEffectiveInventory()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, ".gitignore"), "*\nSECRET IGNORE CONTENT");
        for (var i = 0; i < 20; i++)
        {
            var path = Directory.CreateDirectory(Path.Combine(fixture.Root, $"skill-{i:d2}"));
            await File.WriteAllTextAsync(Path.Combine(path.FullName, "SKILL.md"), $"---\nname: skill-{i:d2}\ndescription: raw only\n---");
        }
        var service = new SkillsInspectionService(Epoch, (_, _) => Task.FromResult(("ok", (string?)fixture.Root)));
        var result = await service.ScanAsync(Request(), default);
        Assert.AreEqual("incomplete", result.TraversalStatus);
        Assert.AreEqual(16, result.Candidates.Count);
        Assert.AreEqual(4, result.Candidates.Count(row => row.Status == "parsed"));
        Assert.AreEqual(12, result.Candidates.Count(row => row.Status == "omitted"));
        StringAssert.Contains(result.Diagnostics, "CandidateLimit");
        Assert.IsTrue(result.MetadataBytesRead <= 4 * (256 * 1024 + 1));
        for (var i = 0; i < 300; i++) await File.WriteAllTextAsync(Path.Combine(fixture.Root, $"file-{i}"), "not read");
        result = await service.ScanAsync(Request(), default);
        Assert.IsTrue(result.EntriesVisited <= 257);
        Assert.IsTrue(result.DirectoriesOpened <= 64);
        Assert.AreEqual("incomplete", result.TraversalStatus);
        Assert.IsTrue(JsonSerializer.SerializeToUtf8Bytes(result, DesktopJsonContext.Default.SkillsScanResponse).Length <= SkillsInspectionService.MaximumResponseBytes);
    }

    [TestMethod]
    public async Task ReparseRootCannotReadOutsideAndLockedFileReadFailureIsDistinctWhereSupported()
    {
        using var fixture = new Fixture();
        var external = Directory.CreateDirectory(Path.Combine(fixture.Root, "outside"));
        var link = Path.Combine(fixture.Root, "link");
        try
        {
            Directory.CreateSymbolicLink(link, external.FullName);
            var service = new SkillsInspectionService(Epoch, (_, _) => Task.FromResult(("ok", (string?)link)));
            var result = await service.ScanAsync(Request(), default);
            Assert.AreEqual("read_error", result.TraversalStatus);
            StringAssert.Contains(result.Diagnostics, "LinkedRoot");
            Assert.AreEqual(0, result.Candidates.Count);
        }
        catch (UnauthorizedAccessException) { TestContext.WriteLine("Symbolic-link creation unavailable; no reparse qualification claimed on this platform."); }
        finally { if (Directory.Exists(link)) Directory.Delete(link); }
        var candidate = Path.Combine(fixture.Root, "SKILL.md");
        await File.WriteAllTextAsync(candidate, "---\nname: test\ndescription: test\n---");
        if (OperatingSystem.IsWindows())
        {
            using var locked = new FileStream(candidate, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var service = new SkillsInspectionService(Epoch, (_, _) => Task.FromResult(("ok", (string?)fixture.Root)));
            var result = await service.ScanAsync(Request(), default);
            Assert.AreEqual("read_error", result.Candidates.Single().Status);
        }
    }

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task CooperativeDeadlineIsAnUnknownOutcomeNotAnEmptyInventory()
    {
        var service = new SkillsInspectionService(Epoch, async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return ("ok", (string?)null);
        });
        var result = await service.ScanAsync(Request(), default);
        Assert.AreEqual("deadline", result.Status);
        Assert.IsNull(result.TraversalStatus);
        Assert.AreEqual(0, result.Candidates.Count);
    }

    [TestMethod]
    public async Task ExplicitRootOnlyWithParsedInvalidUnsupportedAndOmittedMetadata()
    {
        using var fixture = new Fixture();
        for (var i = 0; i < 7; i++)
        {
            var dir = Directory.CreateDirectory(Path.Combine(fixture.Root, $"skill-{i}"));
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "SKILL.md"), i switch
            {
                1 => "not frontmatter SECRET_BODY",
                2 => "---\nname: skill-2\ndescription: *alias\n---\nSECRET_BODY",
                3 => new string('x', 300_000),
                _ => $"---\nname: skill-{i}\ndescription: inspected metadata\n---\nSECRET_BODY",
            });
        }
        var calls = 0;
        var service = new SkillsInspectionService(Epoch, (_, _) => { calls++; return Task.FromResult(("ok", (string?)fixture.Root)); });
        Assert.AreEqual(0, calls);
        var result = await service.ScanAsync(Request(), default);
        Assert.AreEqual("ok", result.Status);
        Assert.AreEqual(1, calls);
        Assert.AreEqual(7, result.Candidates.Count);
        CollectionAssert.AreEqual(new[] { "parsed", "invalid", "unsupported", "too_large", "omitted", "omitted", "omitted" }, result.Candidates.Select(c => c.Status).ToArray());
        Assert.AreEqual("skill-0", result.Candidates[0].Name);
        Assert.IsTrue(result.Candidates.Skip(1).All(c => c.Name is null && c.Description is null));
        var json = JsonSerializer.Serialize(result, DesktopJsonContext.Default.SkillsScanResponse);
        Assert.IsFalse(json.Contains("SECRET_BODY", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains(fixture.Root, StringComparison.Ordinal));
        Assert.IsTrue(System.Text.Encoding.UTF8.GetByteCount(json) <= SkillsInspectionService.MaximumResponseBytes);
    }

    [TestMethod]
    public async Task InvalidScopeEpochAndCancellationNeverResolveOrScanAndOriginalRetainsBusyGate()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var service = new SkillsInspectionService(Epoch, async (_, _) => { calls++; entered.SetResult(); await release.Task; return ("ok", fixture.Root); });
        Assert.AreEqual("invalid_request", (await service.ScanAsync(Request() with { RootKind = "../../private" }, default)).Status);
        Assert.AreEqual("invalid_request", (await service.ScanAsync(Request() with { SessionId = "../secret" }, default)).Status);
        Assert.AreEqual("stale_epoch", (await service.ScanAsync(Request() with { ExpectedHostEpoch = Guid.NewGuid().ToString("D") }, default)).Status);
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ScanAsync(Request(), new CancellationToken(true)));
        Assert.AreEqual(0, calls);
        using var canceled = new CancellationTokenSource();
        var original = service.ScanAsync(Request(), canceled.Token);
        await entered.Task;
        canceled.Cancel();
        Assert.AreEqual("busy", (await service.ScanAsync(Request(), default)).Status);
        release.SetResult();
        Assert.AreEqual("canceled", (await original).Status);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task MissingAndExceptionsAreFixedDiagnosticsWithoutPrivatePaths()
    {
        using var fixture = new Fixture();
        var service = new SkillsInspectionService(Epoch, (_, _) => Task.FromResult(("ok", (string?)Path.Combine(fixture.Root, "missing"))));
        Assert.AreEqual("missing", (await service.ScanAsync(Request(), default)).TraversalStatus);
        var failing = new SkillsInspectionService(Epoch, (_, _) => throw new IOException("PRIVATE_PATH SECRET"));
        var response = await failing.ScanAsync(Request(), default);
        Assert.AreEqual("read_failed", response.Status);
        Assert.IsFalse(JsonSerializer.Serialize(response, DesktopJsonContext.Default.SkillsScanResponse).Contains("SECRET", StringComparison.Ordinal));
    }

    private static SkillsScanRequest Request() => new(Epoch, "request-1", "session", DateTimeOffset.UnixEpoch.ToString("O"), "global", null, null, "user_alta");
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-skills-rpc-" + Guid.NewGuid().ToString("N"))).FullName;
        public void Dispose() => Directory.Delete(Root, true);
    }
}
