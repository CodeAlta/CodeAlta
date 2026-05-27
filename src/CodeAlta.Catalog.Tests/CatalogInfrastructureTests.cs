using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Bootstrap;
using CodeAlta.Catalog.Skills;
using System.Diagnostics;
using System.Text.Json;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class CatalogInfrastructureTests
{
    [TestMethod]
    public void CatalogOptions_InternalSessionsRoot_KeepsLegacyPersistedDirectory()
    {
        var options = new CatalogOptions { GlobalRoot = Path.Combine("tmp", "alta-home") };

        // Compatibility: internal session linkage was persisted below threads/internal before the
        // in-memory terminology moved to Session/SessionView, so the wire path must not change.
        Assert.AreEqual(Path.Combine(options.GlobalRoot, "threads", "internal"), options.InternalSessionsRoot);
    }

    [TestMethod]
    public async Task ProjectCatalog_UpsertFromPathAsync_CreatesAndReloadsProject()
    {
        using var root = TempDirectory.Create();
        var projectRoot = Path.Combine(root.Path, "Tomlyn");
        Directory.CreateDirectory(projectRoot);

        var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = root.Path });
        var descriptor = await catalog.UpsertFromPathAsync(projectRoot).ConfigureAwait(false);
        var reloaded = await catalog.LoadAsync().ConfigureAwait(false);

        Assert.AreEqual("Tomlyn", descriptor.Name);
        Assert.AreEqual("Tomlyn", descriptor.DisplayName);
        Assert.AreEqual("tomlyn", descriptor.Slug);
        Assert.AreEqual(projectRoot, descriptor.ProjectPath);
        Assert.AreEqual(1, reloaded.Count);
        Assert.AreEqual(projectRoot, reloaded[0].ProjectPath);
        Assert.AreEqual("Tomlyn", reloaded[0].Name);
    }

    [TestMethod]
    public async Task ProjectCatalog_UpsertFromPathAsync_AllowsFileSystemRootProjectPath()
    {
        using var root = TempDirectory.Create();
        var projectRoot = Path.GetPathRoot(Environment.CurrentDirectory);
        Assert.IsFalse(string.IsNullOrWhiteSpace(projectRoot));

        var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = root.Path });
        var descriptor = await catalog.UpsertFromPathAsync(projectRoot!).ConfigureAwait(false);
        var reloaded = await catalog.LoadAsync().ConfigureAwait(false);

        Assert.AreEqual(projectRoot, descriptor.DisplayName);
        Assert.AreEqual(projectRoot, descriptor.ProjectPath);
        Assert.IsTrue(IsValidSingleDirectoryName(descriptor.Name));
        Assert.AreEqual(1, reloaded.Count);
        Assert.AreEqual(projectRoot, reloaded[0].DisplayName);
        Assert.AreEqual(projectRoot, reloaded[0].ProjectPath);
        Assert.IsTrue(IsValidSingleDirectoryName(reloaded[0].Name));
    }

    [TestMethod]
    public void ProjectDescriptor_Validate_InfersRootPathNameAndDisplayName()
    {
        var projectRoot = Path.GetPathRoot(Environment.CurrentDirectory);
        Assert.IsFalse(string.IsNullOrWhiteSpace(projectRoot));
        var descriptor = new ProjectDescriptor
        {
            Id = ProjectId.NewVersion7().ToString(),
            Slug = "root-project",
            ProjectPath = projectRoot!,
            DefaultBranch = "main",
        };

        descriptor.Validate();

        Assert.AreEqual(projectRoot, descriptor.DisplayName);
        Assert.IsTrue(IsValidSingleDirectoryName(descriptor.Name));
    }

    [TestMethod]
    public async Task ProjectCatalog_UpsertFromPathAsync_ReusesExistingProjectForSamePath()
    {
        using var root = TempDirectory.Create();
        var projectRoot = Path.Combine(root.Path, "Tomlyn");
        Directory.CreateDirectory(projectRoot);

        var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = root.Path });
        var first = await catalog.UpsertFromPathAsync(projectRoot).ConfigureAwait(false);
        var second = await catalog.UpsertFromPathAsync(projectRoot).ConfigureAwait(false);

        Assert.AreEqual(first.Id, second.Id);
        Assert.AreEqual(first.Slug, second.Slug);
    }

    [TestMethod]
    public async Task ProjectCatalog_UpsertFromPathAsync_ReactivatesArchivedProject()
    {
        using var root = TempDirectory.Create();
        var projectRoot = Path.Combine(root.Path, "Tomlyn");
        Directory.CreateDirectory(projectRoot);

        var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = root.Path });
        var first = await catalog.UpsertFromPathAsync(projectRoot).ConfigureAwait(false);
        first.Archived = true;
        await catalog.SaveAsync(first).ConfigureAwait(false);

        var reopened = await catalog.UpsertFromPathAsync(projectRoot).ConfigureAwait(false);

        Assert.AreEqual(first.Id, reopened.Id);
        Assert.IsFalse(reopened.Archived);
        var reloaded = await catalog.LoadAsync().ConfigureAwait(false);
        Assert.IsFalse(reloaded.Single().Archived);
    }

    [TestMethod]
    public async Task ProjectCatalog_ImportWorkingDirectoriesAsync_ImportsDistinctProjectsAndSkipsGlobalRoot()
    {
        using var root = TempDirectory.Create();
        var catalogRoot = Path.Combine(root.Path, ".alta");
        var projectA = Path.Combine(root.Path, "Tomlyn");
        var projectB = Path.Combine(root.Path, "XenoAtom.Terminal");
        var internalSessionRoot = Path.Combine(catalogRoot, "sessions", "internal", "child-1");
        Directory.CreateDirectory(catalogRoot);
        Directory.CreateDirectory(projectA);
        Directory.CreateDirectory(projectB);
        Directory.CreateDirectory(internalSessionRoot);

        var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = catalogRoot });
        var imported = await catalog.ImportWorkingDirectoriesAsync(
            [
                projectA,
                projectB,
                projectA,
                catalogRoot,
                internalSessionRoot,
                Path.Combine(catalogRoot, "missing"),
            ]).ConfigureAwait(false);

        Assert.AreEqual(2, imported.Count);
        CollectionAssert.AreEquivalent(
            new[] { projectA, projectB },
            imported.Select(static project => project.ProjectPath).ToArray());

        var reloaded = await catalog.LoadAsync().ConfigureAwait(false);
        Assert.AreEqual(2, reloaded.Count);
    }

    [TestMethod]
    public async Task ProjectCatalog_LoadAsync_DeduplicatesProjectsByNormalizedPath()
    {
        using var root = TempDirectory.Create();
        var catalogRoot = Path.Combine(root.Path, ".alta");
        var projectsRoot = Path.Combine(catalogRoot, "projects");
        Directory.CreateDirectory(Path.Combine(projectsRoot, "codealta-copy"));

        var projectPath = Path.Combine(root.Path, "CodeAlta");
        Directory.CreateDirectory(projectPath);

        const string projectTemplate = """
            ---
            kind: "project"
            id: "{0}"
            slug: "{1}"
            name: "CodeAlta"
            display_name: "CodeAlta"
            path: "{2}"
            default_branch: "main"
            ---

            # CodeAlta
            """;

        await File.WriteAllTextAsync(
            Path.Combine(projectsRoot, "codealta.md"),
            string.Format(
                projectTemplate,
                ProjectId.NewVersion7(),
                "codealta",
                projectPath.Replace("\\", "\\\\"))).ConfigureAwait(false);

        await File.WriteAllTextAsync(
            Path.Combine(projectsRoot, "codealta-copy", "readme.md"),
            string.Format(
                projectTemplate,
                ProjectId.NewVersion7(),
                "codealta-copy",
                ($@"\\?\{projectPath}").Replace("\\", "\\\\"))).ConfigureAwait(false);

        var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = catalogRoot });
        var loaded = await catalog.LoadAsync().ConfigureAwait(false);

        Assert.AreEqual(1, loaded.Count);
        Assert.AreEqual("CodeAlta", loaded[0].DisplayName);
        Assert.AreEqual("CodeAlta", loaded[0].Name);
        Assert.AreEqual(
            Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(loaded[0].ProjectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    [TestMethod]
    public void CatalogYamlSerializer_ProjectMarkdownRoundTrip_Works()
    {
        var serializer = new CatalogYamlSerializer();
        var project = CreateProjectDescriptor("repo-main", "Repo.Main", "Main Repo");
        project.Archived = true;
        project.MarkdownBody = "# Main Repo\n\nShared code and services.";

        var markdown = serializer.SerializeProjectMarkdown(project);
        var reloaded = serializer.DeserializeProjectMarkdown(markdown);
        reloaded.Validate();

        Assert.AreEqual(project.Slug, reloaded.Slug);
        Assert.AreEqual(project.Name, reloaded.Name);
        Assert.AreEqual(project.DisplayName, reloaded.DisplayName);
        Assert.IsTrue(reloaded.Archived);
        StringAssert.Contains(reloaded.MarkdownBody, "Shared code and services.");
    }

    [TestMethod]
    public async Task ProjectCatalog_LoadAsync_LoadsProjectsFromFlatCatalogFile()
    {
        using var root = TempDirectory.Create();
        var projectsRoot = Path.Combine(root.Path, "projects");
        Directory.CreateDirectory(projectsRoot);

        await File.WriteAllTextAsync(
            Path.Combine(projectsRoot, "repo-main.md"),
            """
            ---
            kind: "project"
            id: "01963b36-0d70-7a11-b3c2-1f2e3d4c5b6a"
            slug: "repo-main"
            name: "Repo.Main"
            display_name: "Main Repo"
            path: "C:\\code\\repo-main"
            default_branch: "main"
            checkout:
              path_template: '{projectName}'
            ---

            # Main Repo
            """
        ).ConfigureAwait(false);

        var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = root.Path });
        var projects = await catalog.LoadAsync().ConfigureAwait(false);

        Assert.AreEqual(1, projects.Count);
        Assert.AreEqual("repo-main", projects[0].Slug);
        Assert.AreEqual("Repo.Main", projects[0].Name);
    }

    [TestMethod]
    public async Task ProjectCatalog_LoadAsync_LoadsLegacyProjectReadmeDuringTransition()
    {
        using var root = TempDirectory.Create();
        var projectRoot = Path.Combine(root.Path, "projects", "repo-main");
        Directory.CreateDirectory(projectRoot);

        await File.WriteAllTextAsync(
            Path.Combine(projectRoot, "readme.md"),
            """
            ---
            kind: "project"
            id: "01963b36-0d70-7a11-b3c2-1f2e3d4c5b6a"
            slug: "repo-main"
            name: "Repo.Main"
            display_name: "Main Repo"
            path: "C:\\code\\repo-main"
            default_branch: "main"
            ---

            # Main Repo
            """
        ).ConfigureAwait(false);

        var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = root.Path });
        var projects = await catalog.LoadAsync().ConfigureAwait(false);

        Assert.AreEqual(1, projects.Count);
        Assert.AreEqual("repo-main", projects[0].Slug);
    }

    [TestMethod]
    public void PathTemplateResolver_Resolve_ExpandsMacros()
    {
        using var root = TempDirectory.Create();
        var context = CreatePathTemplateContext(root.Path);
        var resolved = PathTemplateResolver.Resolve(
            Path.Combine("{projectName}", "{projectSlug}"),
            context);

        Assert.AreEqual(
            Path.GetFullPath(Path.Combine(root.Path, "Repo.Main", "repo-main")),
            resolved);
    }

    [TestMethod]
    public void PathTemplateResolver_Resolve_ThrowsForTraversal()
    {
        using var root = TempDirectory.Create();
        var context = CreatePathTemplateContext(root.Path);

        Assert.ThrowsExactly<ArgumentException>(() =>
            PathTemplateResolver.Resolve(Path.Combine("..", "escape"), context));
    }

    [TestMethod]
    public async Task ProjectResolver_ResolveAsync_UsesMachineOverridesAndPlansCheckouts()
    {
        using var root = TempDirectory.Create();
        var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = root.Path });
        var project = CreateProjectDescriptor("repo-main", "Repo.Main", "Main Repo");
        project.ProjectPath = "C:\\code\\repo-main";
        project.Checkout = new CheckoutRule { PathTemplate = "{projectName}" };

        await catalog.SaveAsync(project).ConfigureAwait(false);

        var resolver = new ProjectResolver(catalog);
        var machineRoot = Path.Combine(root.Path, "checkouts");

        var machine = new MachineProfile
        {
            MachineId = "machine-a",
            CheckoutRoot = machineRoot,
        };

        var resolutions = await resolver.ResolveAsync(ScopeSelector.Project("repo-main"), machine).ConfigureAwait(false);
        Assert.AreEqual(1, resolutions.Count);
        Assert.AreEqual(1, resolutions[0].Projects.Count);
        Assert.AreEqual("repo-main", resolutions[0].SelectedProject?.Slug);
        StringAssert.StartsWith(resolutions[0].Projects[0].CheckoutPath, Path.GetFullPath(machineRoot));

        var planner = new ProjectBootstrapPlanner();
        var plans = planner.Plan(resolutions[0]);

        Assert.AreEqual(1, plans.Count);
        Assert.AreEqual(CheckoutAction.Clone, plans[0].Action);
    }

    [TestMethod]
    public async Task ProjectCatalog_SaveAsync_WritesFlatCatalogFiles()
    {
        using var root = TempDirectory.Create();
        var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = root.Path });
        var project = CreateProjectDescriptor("repo-main", "Repo.Main", "Main Repo");

        await catalog.SaveAsync(project).ConfigureAwait(false);

        Assert.IsTrue(File.Exists(Path.Combine(root.Path, "projects", "repo-main.md")));

        var loaded = await catalog.GetBySlugAsync("repo-main").ConfigureAwait(false);
        Assert.IsNotNull(loaded);
        Assert.AreEqual("Repo.Main", loaded.Name);
    }

    [TestMethod]
    public void SessionViewYamlSerializer_MarkdownRoundTrip_Works()
    {
        var serializer = new SessionViewYamlSerializer();
        var descriptor = CreateInternalSessionDescriptor();
        descriptor.MessageCount = 7;
        descriptor.CreatedBy = new AltaActorProvenance
        {
            Kind = "plugin",
            SourceSessionId = "parent",
            SourceProjectId = descriptor.ProjectRef,
            SourceAgentId = "agent:parent",
            PluginRuntimeKey = "sample-plugin",
            CorrelationId = "correlation-1",
            CreatedAt = new DateTimeOffset(2026, 05, 09, 10, 15, 00, TimeSpan.Zero),
        };
        descriptor.MarkdownBody = "# Review sqlitevec integration";

        var markdown = serializer.SerializeSessionMarkdown(descriptor);
        var reloaded = serializer.DeserializeSessionMarkdown(markdown);
        reloaded.Validate();

        Assert.AreEqual(descriptor.SessionId, reloaded.SessionId);
        Assert.AreEqual(descriptor.Kind, reloaded.Kind);
        Assert.AreEqual(descriptor.ProjectRef, reloaded.ProjectRef);
        Assert.AreEqual(descriptor.ParentSessionId, reloaded.ParentSessionId);
        Assert.AreEqual(descriptor.ProviderId, reloaded.ProviderId);
        Assert.AreEqual(descriptor.ResolvedProviderKey, reloaded.ResolvedProviderKey);
        Assert.AreEqual(descriptor.SessionId, reloaded.SessionId);
        Assert.AreEqual(descriptor.StartedAt, reloaded.StartedAt);
        Assert.IsNotNull(reloaded.CreatedBy);
        Assert.AreEqual("plugin", reloaded.CreatedBy.Kind);
        Assert.AreEqual("parent", reloaded.CreatedBy.SourceSessionId);
        Assert.AreEqual("sample-plugin", reloaded.CreatedBy.PluginRuntimeKey);
        Assert.AreEqual(descriptor.CreatedBy.CreatedAt, reloaded.CreatedBy.CreatedAt);
        Assert.AreEqual(7, reloaded.MessageCount);
    }

    [TestMethod]
    public void SessionViewYamlSerializer_DeserializeSessionMarkdown_MigratesLegacySessionId()
    {
        var serializer = new SessionViewYamlSerializer();
        var legacyKey = "backend_" + "session_id";

        var reloaded = serializer.DeserializeSessionMarkdown($"""
            ---
            session_id: codex:session-one
            kind: internal_session
            backend_id: codex
            provider_key: codex
            {legacyKey}: session-one
            title: Legacy
            ---

            # Legacy
            """);

        Assert.AreEqual("session-one", reloaded.SessionId);
    }

    [TestMethod]
    public void SessionViewYamlSerializer_DeserializeSessionMarkdown_KeepsMatchingLegacySessionId()
    {
        var serializer = new SessionViewYamlSerializer();
        var legacyKey = "backend_" + "session_id";

        var reloaded = serializer.DeserializeSessionMarkdown($"""
            ---
            session_id: session-one
            kind: internal_session
            backend_id: codex
            provider_key: codex
            {legacyKey}: session-one
            title: Legacy
            ---

            # Legacy
            """);

        Assert.AreEqual("session-one", reloaded.SessionId);
    }

    [TestMethod]
    public void SessionViewYamlSerializer_DeserializeSessionMarkdown_RejectsConflictingLegacyIds()
    {
        var serializer = new SessionViewYamlSerializer();
        var legacyKey = "backend_" + "session_id";

        Assert.ThrowsExactly<InvalidDataException>(() => serializer.DeserializeSessionMarkdown($"""
            ---
            session_id: session-one
            kind: internal_session
            backend_id: codex
            provider_key: codex
            {legacyKey}: other-session
            title: Legacy
            ---

            # Legacy
            """));
    }

    [TestMethod]
    public async Task SessionViewCatalog_SaveInternalAsync_PersistsInternalSessions()
    {
        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        var catalog = new SessionViewCatalog(options);
        var project = new ProjectDescriptor
        {
            Id = Guid.CreateVersion7().ToString(),
            Slug = "repo-main",
            Name = "Repo.Main",
            DisplayName = "Main Repo",
            ProjectPath = "C:\\code\\repo-main",
            MarkdownBody = "# Main Repo"
        };
        var timestamp = new DateTimeOffset(2026, 03, 10, 12, 0, 0, TimeSpan.Zero);
        var internalSession = new SessionViewDescriptor
        {
            SessionId = "019cc85b",
            Kind = SessionViewKind.InternalSession,
            ProviderId = "codex",
            ProjectRef = project.Id,
            ParentSessionId = "019cc700",
            WorkingDirectory = Path.Combine(root.Path, "sessions", "internal", "codex-019cc85b"),
            Title = "Review sqlitevec integration",
            Status = SessionViewStatus.Active,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
            LastActiveAt = timestamp,
            StartedAt = timestamp,
            MarkdownBody = "# Review sqlitevec integration",
        };

        await catalog.SaveInternalAsync(internalSession).ConfigureAwait(false);

        var loaded = await catalog.LoadInternalAsync().ConfigureAwait(false);
        Assert.AreEqual(1, loaded.Count);
        Assert.AreEqual(SessionViewKind.InternalSession, loaded[0].Kind);
        Assert.AreEqual(project.Id, loaded[0].ProjectRef);
        Assert.IsNotNull(internalSession.SourcePath);
        Assert.IsTrue(File.Exists(internalSession.SourcePath));
    }

    [TestMethod]
    public async Task SessionViewCatalog_SaveInternalAsync_RejectsNonInternalSession()
    {
        using var root = TempDirectory.Create();
        var catalog = new SessionViewCatalog(new CatalogOptions { GlobalRoot = root.Path });
        var descriptor = new SessionViewDescriptor
        {
            SessionId = "019cc700",
            Kind = SessionViewKind.ProjectSession,
            ProviderId = "copilot",
            ProjectRef = Guid.CreateVersion7().ToString(),
            WorkingDirectory = @"C:\code\repo-main",
            Title = "Project session",
            Status = SessionViewStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
        };

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.SaveInternalAsync(descriptor));
    }

    [TestMethod]
    public async Task SessionViewJournalStore_ImmutableHeaderOmitsDerivedStateFields()
    {
        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        var catalog = new SessionViewCatalog(options);
        var createdAt = new DateTimeOffset(2026, 05, 12, 10, 00, 00, TimeSpan.Zero);
        var session = new SessionViewDescriptor
        {
            SessionId = "session-header-test",
            Kind = SessionViewKind.ProjectSession,
            ProviderId = "codex",
            ProviderKey = "codex",
            ProjectRef = Guid.CreateVersion7().ToString(),
            ParentSessionId = "session-parent",
            WorkingDirectory = @"C:\code\repo-main",
            Title = "Header test",
            Status = SessionViewStatus.Active,
            CreatedAt = createdAt,
            UpdatedAt = createdAt.AddMinutes(5),
            LastActiveAt = createdAt.AddMinutes(10),
            LatestSummary = "Later summary",
            MessageCount = 42,
        };

        await catalog.JournalStore.EnsureHeaderAsync(session).ConfigureAwait(false);
        await catalog.JournalStore.AppendStateAsync(session, new SessionViewLocalState { MessageCount = 42 }).ConfigureAwait(false);

        var path = new AgentRuntimePathLayout(options.GlobalRoot).GetSessionFilePath(session.SessionId, session.CreatedAt);
        var firstLine = File.ReadLines(path).First();
        using var document = JsonDocument.Parse(firstLine);
        Assert.AreEqual(SessionViewJournalStore.SessionHeaderEventType, document.RootElement.GetProperty("backendEventType").GetString());
        var raw = document.RootElement.GetProperty("raw");
        Assert.IsFalse(raw.TryGetProperty("updated_at", out _));
        Assert.IsFalse(raw.TryGetProperty("last_active_at", out _));
        Assert.IsFalse(raw.TryGetProperty("latest_summary", out _));
        Assert.IsFalse(raw.TryGetProperty("message_count", out _));
        Assert.IsFalse(raw.TryGetProperty("model_id", out _));
        Assert.IsFalse(raw.TryGetProperty("reasoning_effort", out _));
        Assert.AreEqual("Header test", raw.GetProperty("title").GetString());
        Assert.AreEqual("session-parent", raw.GetProperty("parent_session_id").GetString());
    }

    [TestMethod]
    public async Task SessionViewJournalStore_ConcurrentProviderAndCatalogWrites_DoNotThrow()
    {
        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        var catalog = new SessionViewCatalog(options);
        var sessionStore = catalog.JournalStore.CreateSessionStore();
        var createdAt = new DateTimeOffset(2026, 05, 12, 10, 00, 00, TimeSpan.Zero);
        var session = new SessionViewDescriptor
        {
            SessionId = "session-concurrent-log-test",
            Kind = SessionViewKind.ProjectSession,
            ProviderId = "codex",
            ProviderKey = "codex",
            WorkingDirectory = @"C:\code\repo-main",
            Title = "Concurrent log test",
            Status = SessionViewStatus.Active,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            LastActiveAt = createdAt,
        };

        var writes = Enumerable.Range(0, 40)
            .Select(index => index % 2 == 0
                ? sessionStore.UpsertSessionAsync(
                    new AgentSessionSummary
                    {
                        SessionId = session.SessionId,
                        ProviderId = new ModelProviderId(session.ProviderId),
                        ProtocolFamily = "local",
                        ProviderKey = "codex",
                        CreatedAt = createdAt,
                        UpdatedAt = createdAt.AddSeconds(index),
                    })
                : catalog.JournalStore.AppendStateAsync(session, new SessionViewLocalState { MessageCount = index }))
            .ToArray();

        await Task.WhenAll(writes).ConfigureAwait(false);

        var path = new AgentRuntimePathLayout(options.GlobalRoot).GetSessionFilePath(session.SessionId, session.CreatedAt);
        var lines = File.ReadLines(path).ToArray();
        Assert.AreEqual(41, lines.Length);
        using var firstLine = JsonDocument.Parse(lines[0]);
        Assert.AreEqual(SessionViewJournalStore.SessionHeaderEventType, firstLine.RootElement.GetProperty("backendEventType").GetString());
        Assert.AreEqual(0, Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.lock").Count());
    }

    [TestMethod]
    public async Task SessionViewJournalStore_AppendStateWaitsForExclusiveFileHandleWithoutDiskLock()
    {
        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        var catalog = new SessionViewCatalog(options);
        var createdAt = new DateTimeOffset(2026, 05, 12, 10, 00, 00, TimeSpan.Zero);
        var session = new SessionViewDescriptor
        {
            SessionId = "session-exclusive-file-test",
            Kind = SessionViewKind.ProjectSession,
            ProviderId = "codex",
            ProviderKey = "codex",
            WorkingDirectory = @"C:\code\repo-main",
            Title = "Exclusive file test",
            Status = SessionViewStatus.Active,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            LastActiveAt = createdAt,
        };

        await catalog.JournalStore.EnsureHeaderAsync(session).ConfigureAwait(false);
        var path = new AgentRuntimePathLayout(options.GlobalRoot).GetSessionFilePath(session.SessionId, session.CreatedAt);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var exclusiveHandle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var appendTask = catalog.JournalStore.AppendStateAsync(
            session,
            new SessionViewLocalState { MessageCount = 7 },
            cancellation.Token);
        await Task.Delay(100, cancellation.Token).ConfigureAwait(false);

        Assert.IsFalse(appendTask.IsCompleted);
        await exclusiveHandle.DisposeAsync().ConfigureAwait(false);
        await appendTask.ConfigureAwait(false);

        var lines = File.ReadLines(path).ToArray();
        Assert.AreEqual(2, lines.Length);
        Assert.AreEqual(0, Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.lock").Count());
    }

    [TestMethod]
    public async Task SessionViewJournalStore_PrependsHeaderWhenProviderCreatedJournalFirst()
    {
        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        var catalog = new SessionViewCatalog(options);
        var sessionStore = catalog.JournalStore.CreateSessionStore();
        var createdAt = new DateTimeOffset(2026, 05, 12, 10, 00, 00, TimeSpan.Zero);
        var session = new SessionViewDescriptor
        {
            SessionId = "session-provider-first-test",
            Kind = SessionViewKind.ProjectSession,
            ProviderId = "codex",
            ProviderKey = "codex",
            WorkingDirectory = @"C:\code\repo-main",
            Title = "Provider first test",
            Status = SessionViewStatus.Active,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            LastActiveAt = createdAt,
        };

        await sessionStore.UpsertSessionAsync(
                new AgentSessionSummary
                {
                    SessionId = session.SessionId,
                    ProviderId = new ModelProviderId(session.ProviderId),
                    ProtocolFamily = "local",
                    ProviderKey = "codex",
                    CreatedAt = createdAt,
                    UpdatedAt = createdAt,
                })
            .ConfigureAwait(false);

        await catalog.JournalStore.AppendStateAsync(session, new SessionViewLocalState { MessageCount = 7 }).ConfigureAwait(false);

        var path = new AgentRuntimePathLayout(options.GlobalRoot).GetSessionFilePath(session.SessionId, session.CreatedAt);
        var lines = File.ReadLines(path).ToArray();
        Assert.AreEqual(3, lines.Length);
        using var firstLine = JsonDocument.Parse(lines[0]);
        Assert.AreEqual(SessionViewJournalStore.SessionHeaderEventType, firstLine.RootElement.GetProperty("backendEventType").GetString());
        var latestState = await catalog.JournalStore.ReadLatestStateAsync(session.SessionId, session.CreatedAt).ConfigureAwait(false);
        Assert.IsNotNull(latestState);
        Assert.AreEqual(7, latestState.MessageCount);
    }

    [TestMethod]
    public async Task SessionViewJournalStore_ReadLatestStateFindsStateBeyondInitialTailProbe()
    {
        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        var catalog = new SessionViewCatalog(options);
        var createdAt = new DateTimeOffset(2026, 05, 12, 10, 00, 00, TimeSpan.Zero);
        var session = new SessionViewDescriptor
        {
            SessionId = "session-tail-probe-test",
            Kind = SessionViewKind.ProjectSession,
            ProviderId = "codex",
            ProviderKey = "codex",
            WorkingDirectory = @"C:\code\repo-main",
            Title = "Tail probe test",
            Status = SessionViewStatus.Active,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            LastActiveAt = createdAt,
        };

        await catalog.JournalStore.AppendStateAsync(session, new SessionViewLocalState { MessageCount = 99 }).ConfigureAwait(false);
        var path = new AgentRuntimePathLayout(options.GlobalRoot).GetSessionFilePath(session.SessionId, session.CreatedAt);
        var padding = new string('x', 70 * 1024);
        await File.AppendAllTextAsync(path, $"{{\"$type\":\"raw\",\"ProviderId\":\"codex\",\"sessionId\":\"{session.SessionId}\",\"timestamp\":\"{createdAt:O}\",\"backendEventType\":\"padding\",\"raw\":{{\"value\":\"{padding}\"}}}}{Environment.NewLine}").ConfigureAwait(false);

        var latestState = await catalog.JournalStore.ReadLatestStateAsync(session.SessionId, session.CreatedAt).ConfigureAwait(false);

        Assert.IsNotNull(latestState);
        Assert.AreEqual(99, latestState.MessageCount);
    }

    [TestMethod]
    public async Task SessionViewCatalog_ViewStateRoundTrip_Works()
    {
        using var root = TempDirectory.Create();
        var sessionCatalog = new SessionViewCatalog(new CatalogOptions { GlobalRoot = root.Path });

        var viewState = new SessionViewViewState
        {
            OpenSessionIds = ["global", "platform-search-review"],
            Selection = SessionViewSelectionState.Session("platform-search-review", "project-1"),
            SelectedSessionId = "platform-search-review",
            UpdatedAt = new DateTimeOffset(2026, 03, 10, 13, 0, 0, TimeSpan.Zero),
            ProjectPreferences = new Dictionary<string, SessionViewPreference>(StringComparer.OrdinalIgnoreCase)
            {
                ["project-1"] = new SessionViewPreference
                {
                    ProviderKey = "zai",
                    ModelId = "gpt-5.4",
                    ReasoningEffort = AgentReasoningEffort.High,
                },
            },
            Navigator = new NavigatorSettings
            {
                SortMode = NavigatorProjectSortMode.Date,
                RecentSessionsPerProject = 8,
                ThemeSchemeName = "Elderberry Dark Soft",
            },
            SessionStates = new Dictionary<string, SessionViewLocalState>(StringComparer.OrdinalIgnoreCase)
            {
                ["platform-search-review"] = new SessionViewLocalState
                {
                    Archived = true,
                    MessageCount = 42,
                    ParentSessionId = "codex:parent",
                    CreatedBy = new AltaActorProvenance
                    {
                        Kind = "agent",
                        SourceSessionId = "codex:parent",
                        SourceProjectId = "project-1",
                        SourceAgentId = "agent:parent",
                        CorrelationId = "correlation-viewstate",
                        CreatedAt = new DateTimeOffset(2026, 05, 09, 10, 15, 00, TimeSpan.Zero),
                    },
                },
            },
        };

        await sessionCatalog.SaveViewStateAsync(viewState).ConfigureAwait(false);
        var reloaded = await sessionCatalog.LoadViewStateAsync().ConfigureAwait(false);

        CollectionAssert.AreEqual(viewState.OpenSessionIds, reloaded.OpenSessionIds);
        Assert.AreEqual(SessionViewSelectionSurface.Session, reloaded.Selection.Surface);
        Assert.AreEqual("platform-search-review", reloaded.Selection.SessionId);
        Assert.AreEqual("project-1", reloaded.Selection.ProjectId);
        Assert.AreEqual(viewState.SelectedSessionId, reloaded.SelectedSessionId);
        Assert.AreEqual("zai", reloaded.ProjectPreferences["project-1"].ProviderKey);
        Assert.AreEqual("gpt-5.4", reloaded.ProjectPreferences["project-1"].ModelId);
        Assert.AreEqual(AgentReasoningEffort.High, reloaded.ProjectPreferences["project-1"].ReasoningEffort);
        Assert.AreEqual(NavigatorProjectSortMode.Date, reloaded.Navigator.SortMode);
        Assert.AreEqual(8, reloaded.Navigator.RecentSessionsPerProject);
        Assert.AreEqual("Elderberry Dark Soft", reloaded.Navigator.ThemeSchemeName);
        Assert.AreEqual(0, reloaded.SessionPreferences.Count);
        Assert.AreEqual(0, reloaded.SessionStates.Count);
    }

    [TestMethod]
    public void SessionViewYamlSerializer_DeserializeViewState_MigratesLegacySessionSelection()
    {
        var serializer = new SessionViewYamlSerializer();
        var yaml =
            """
            open_session_ids:
              - global
              - platform-search-review
            selected_session_id: platform-search-review
            """;

        var reloaded = serializer.DeserializeViewState(yaml);

        Assert.AreEqual(SessionViewSelectionSurface.Session, reloaded.Selection.Surface);
        Assert.AreEqual("platform-search-review", reloaded.Selection.SessionId);
        Assert.AreEqual("platform-search-review", reloaded.SelectedSessionId);
    }

    [TestMethod]
    public void CodeAltaConfigStore_SaveGlobalProviderPreference_WritesTomlAndReloads()
    {
        using var root = TempDirectory.Create();
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root.Path });
        store.EnsureGlobalConfigExists();

        store.SaveGlobalProviderPreference(ModelProviderIds.Codex.Value, "gpt-5.4", AgentReasoningEffort.High);

        var configPath = Path.Combine(root.Path, "config.toml");
        var content = File.ReadAllText(configPath);
        var preference = store.GetEffectiveProviderPreference(ModelProviderIds.Codex.Value);

        StringAssert.Contains(content, "[providers.codex]");
        StringAssert.Contains(content, "gpt-5.4");
        StringAssert.Contains(content, "reasoning_effort = \"high\"");
        Assert.IsFalse(content.Contains("enabled = true", StringComparison.Ordinal));
        Assert.IsFalse(content.Contains("display_name = \"Codex\"", StringComparison.Ordinal));
        Assert.IsFalse(content.Contains("[providers.codex.compaction]", StringComparison.Ordinal));
        Assert.AreEqual("gpt-5.4", preference.Model);
        Assert.AreEqual(AgentReasoningEffort.High, preference.ReasoningEffort);
    }

    [TestMethod]
    public void CodeAltaConfigStore_SaveGlobalProviderPreference_PrunesProviderDefaults()
    {
        using var root = TempDirectory.Create();
        var configPath = Path.Combine(root.Path, "config.toml");
        File.WriteAllText(
            configPath,
            """
            [providers.codex]
            enabled = true
            display_name = "Codex"
            type = "codex"
            model = "gpt-5.4"
            reasoning_effort = "high"

            [providers.codex.compaction]
            enabled = true
            ratio = 0.95
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root.Path });
        store.SaveGlobalProviderPreference(ModelProviderIds.Codex.Value, "gpt-5.4", AgentReasoningEffort.High);

        var content = File.ReadAllText(configPath);
        StringAssert.Contains(content, "[providers.codex]");
        StringAssert.Contains(content, "model = \"gpt-5.4\"");
        StringAssert.Contains(content, "reasoning_effort = \"high\"");
        Assert.IsFalse(content.Contains("enabled = true", StringComparison.Ordinal));
        Assert.IsFalse(content.Contains("display_name = \"Codex\"", StringComparison.Ordinal));
        Assert.IsFalse(content.Contains("[providers.codex.compaction]", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodeAltaConfigStore_LoadGlobalConfigContent_ReturnsDefaultWhenMissing()
    {
        using var root = TempDirectory.Create();
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root.Path });

        var content = store.LoadGlobalConfigContent();

        StringAssert.Contains(content, "[providers.codex]");
        Assert.IsFalse(File.Exists(Path.Combine(root.Path, "config.toml")));
    }

    [TestMethod]
    public void CodeAltaConfigStore_UpgradeGlobalConfigFromDefaults_AppendsMissingProviderEntriesWithBackup()
    {
        using var root = TempDirectory.Create();
        var configPath = Path.Combine(root.Path, "config.toml");
        const string originalContent = """
            [providers.openai]
            enabled = true
            display_name = "Work OpenAI"
            type = "openai-responses"
            model = "work-model"
            api_key_env = "WORK_OPENAI_KEY"
            """;
        File.WriteAllText(configPath, originalContent);
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root.Path });

        var upgraded = store.UpgradeGlobalConfigFromDefaults(out var backupPath);

        Assert.IsTrue(upgraded);
        Assert.IsFalse(string.IsNullOrWhiteSpace(backupPath));
        Assert.IsTrue(File.Exists(backupPath));
        Assert.AreEqual(originalContent, File.ReadAllText(backupPath));
        var content = File.ReadAllText(configPath);
        StringAssert.Contains(content, "display_name = \"Work OpenAI\"");
        StringAssert.Contains(content, "model = \"work-model\"");
        StringAssert.Contains(content, "[providers.xai]");
        StringAssert.Contains(content, "[providers.minimax]");
    }

    [TestMethod]
    public void CodeAltaConfigStore_UpgradeGlobalConfigFromDefaults_AddsMissingKeysWithoutChangingExistingValues()
    {
        using var root = TempDirectory.Create();
        var configPath = Path.Combine(root.Path, "config.toml");
        File.WriteAllText(
            configPath,
            """
            [providers.xai]
            display_name = "My Grok"
            type = "xai"
            model = "grok-custom"
            """);
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root.Path });

        var upgraded = store.UpgradeGlobalConfigFromDefaults(out var backupPath);

        Assert.IsTrue(upgraded);
        Assert.IsFalse(string.IsNullOrWhiteSpace(backupPath));
        var content = File.ReadAllText(configPath);
        var xaiSectionStart = content.IndexOf("[providers.xai]", StringComparison.Ordinal);
        var xaiSectionEnd = content.IndexOf("\n[", xaiSectionStart + "[providers.xai]".Length, StringComparison.Ordinal);
        var xaiSection = xaiSectionEnd < 0 ? content[xaiSectionStart..] : content[xaiSectionStart..xaiSectionEnd];
        StringAssert.Contains(content, "display_name = \"My Grok\"");
        StringAssert.Contains(content, "model = \"grok-custom\"");
        Assert.IsFalse(xaiSection.Contains("enabled = false", StringComparison.Ordinal));
        StringAssert.Contains(content, "reasoning_effort = \"high\"");
        StringAssert.Contains(content, "auth_source = \"xai_browser_oauth\"");
        StringAssert.Contains(content, "model_discovery = \"xai_endpoint_with_static_fallback\"");
    }

    [TestMethod]
    public void CodeAltaConfigStore_SaveGlobalConfigContent_RejectsInvalidWithoutOverwrite()
    {
        using var root = TempDirectory.Create();
        var configPath = Path.Combine(root.Path, "config.toml");
        const string validContent = """
            [providers.openai]
            enabled = false
            type = "openai-responses"
            """;
        File.WriteAllText(configPath, validContent);
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root.Path });

        Assert.ThrowsExactly<InvalidDataException>(() => store.SaveGlobalConfigContent("[providers.openai]\ntype = \"not-supported\""));

        Assert.AreEqual(validContent, File.ReadAllText(configPath));
    }

    [TestMethod]
    public void CodeAltaConfigStore_GetEffectiveProviderPreference_ProjectConfigOverridesGlobalPerField()
    {
        using var root = TempDirectory.Create();
        var projectRoot = Path.Combine(root.Path, "project-a");
        Directory.CreateDirectory(Path.Combine(projectRoot, ".alta"));

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root.Path });
        store.EnsureGlobalConfigExists();
        store.SaveGlobalProviderPreference(ModelProviderIds.Codex.Value, "gpt-5.4", AgentReasoningEffort.High);

        File.WriteAllText(
            Path.Combine(projectRoot, ".alta", "config.toml"),
            """
            [providers.codex]
            reasoning_effort = "medium"
            """);

        var preference = store.GetEffectiveProviderPreference(ModelProviderIds.Codex.Value, projectRoot);

        Assert.AreEqual("gpt-5.4", preference.Model);
        Assert.AreEqual(AgentReasoningEffort.Medium, preference.ReasoningEffort);
    }

    [TestMethod]
    public async Task SkillCatalog_ListAndGet_Works()
    {
        using var root = TempDirectory.Create();
        var skillsRoot = Path.Combine(root.Path, ".alta", "skills");
        var skillPath = Path.Combine(skillsRoot, "sample-skill");
        Directory.CreateDirectory(skillPath);

        await File.WriteAllTextAsync(
            Path.Combine(skillPath, "SKILL.md"),
            """
            ---
            name: sample-skill
            description: Lists and reads skills from disk.
            license: Apache-2.0
            metadata:
              author: CodeAlta
            ---
            # Sample Skill

            Lists and reads skills from disk.
            """
        ).ConfigureAwait(false);

        var catalog = new SkillCatalog();
        var listed = await catalog.ListAsync([skillsRoot]).ConfigureAwait(false);

        Assert.AreEqual(1, listed.Count);
        Assert.AreEqual("sample-skill", listed[0].Name);
        Assert.AreEqual("Sample Skill", listed[0].Title);
        Assert.AreEqual("Lists and reads skills from disk.", listed[0].Description);

        var doc = await catalog.GetAsync([skillsRoot], "sample-skill").ConfigureAwait(false);
        Assert.IsNotNull(doc);
        Assert.AreEqual("sample-skill", doc.Descriptor.Name);
        Assert.AreEqual("Apache-2.0", doc.Frontmatter.License);
        Assert.AreEqual("CodeAlta", doc.Frontmatter.Metadata["author"]);
        StringAssert.Contains(doc.Body, "Lists and reads skills from disk.");
        StringAssert.Contains(doc.Content, "Sample Skill");
    }

    [TestMethod]
    public async Task SkillCatalog_GetResourceAsync_ReadsBytes()
    {
        using var root = TempDirectory.Create();
        var skillsRoot = Path.Combine(root.Path, ".alta", "skills");
        var skillPath = Path.Combine(skillsRoot, "sample-skill");
        Directory.CreateDirectory(skillPath);

        await File.WriteAllTextAsync(
            Path.Combine(skillPath, "SKILL.md"),
            """
            ---
            name: sample-skill
            description: Reads skill resources.
            ---
            # Sample Skill
            """).ConfigureAwait(false);

        var resources = Path.Combine(skillPath, "resources");
        Directory.CreateDirectory(resources);
        var resourcePath = Path.Combine(resources, "data.txt");
        await File.WriteAllTextAsync(resourcePath, "hello").ConfigureAwait(false);

        var catalog = new SkillCatalog();
        var bytes = await catalog.GetResourceAsync([skillsRoot], "sample-skill", Path.Combine("resources", "data.txt"))
            .ConfigureAwait(false);

        Assert.AreEqual("hello", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [TestMethod]
    public async Task GlobalRepoBootstrapper_EnsureAsync_InitializesGitRepo()
    {
        if (!IsGitAvailable())
        {
            Assert.Inconclusive("git CLI was not available on PATH.");
        }

        using var root = TempDirectory.Create();
        var globalRepoRoot = Path.Combine(root.Path, "repo");
        var bootstrapper = new GlobalRepoBootstrapper(new GitService());

        var result = await bootstrapper.EnsureAsync(globalRepoRoot).ConfigureAwait(false);

        Assert.AreEqual(Path.GetFullPath(globalRepoRoot), result.GlobalRepoRoot);
        Assert.IsTrue(Directory.Exists(Path.Combine(globalRepoRoot, ".git")));
        Assert.IsTrue(Directory.Exists(Path.Combine(globalRepoRoot, "projects")));
        Assert.IsTrue(Directory.Exists(Path.Combine(globalRepoRoot, "checkouts")));
        Assert.IsTrue(Directory.Exists(Path.Combine(globalRepoRoot, "machines")));
    }

    [TestMethod]
    public async Task ProjectBootstrapper_EnsureCheckedOutAsync_ClonesMissingRepo()
    {
        if (!IsGitAvailable())
        {
            Assert.Inconclusive("git CLI was not available on PATH.");
        }

        using var root = TempDirectory.Create();
        var remote = Path.Combine(root.Path, "remote.git");
        var checkout = Path.Combine(root.Path, "checkouts", "Repo.Main");

        RunGit(root.Path, $"init --bare \"{remote}\"");

        var resolution = new ProjectScopeResolution
        {
            Kind = ScopeKind.Project,
            SelectedProject = CreateProjectDescriptor("repo-main", "Repo.Main", "Main Repo"),
            Projects =
            [
                new ResolvedProject
                {
                    Project = new ProjectDescriptor
                    {
                        Id = ProjectId.NewVersion7().ToString(),
                        Slug = "repo-main",
                        Name = "Repo.Main",
                        DisplayName = "Main Repo",
                        ProjectPath = remote,
                        DefaultBranch = "main",
                        Checkout = new CheckoutRule { PathTemplate = checkout },
                    },
                    CheckoutPath = checkout,
                    CodeAltaRoot = Path.Combine(checkout, ".alta"),
                },
            ],
            CodeAltaRoots = [Path.Combine(checkout, ".alta")],
        };

        var bootstrapper = new ProjectBootstrapper(new ProjectBootstrapPlanner(), new GitService());
        var results = await bootstrapper.EnsureCheckedOutAsync(resolution).ConfigureAwait(false);

        Assert.AreEqual(1, results.Count);
        Assert.IsTrue(results[0].Success);
        Assert.IsTrue(Directory.Exists(Path.Combine(checkout, ".git")));
        Assert.IsTrue(Directory.Exists(Path.Combine(checkout, ".alta")));
    }

    private static ProjectDescriptor CreateProjectDescriptor(string slug, string name, string displayName)
    {
        return new ProjectDescriptor
        {
            Id = ProjectId.NewVersion7().ToString(),
            Slug = slug,
            Name = name,
            DisplayName = displayName,
            ProjectPath = $@"C:\code\{name}",
            DefaultBranch = "main",
            Checkout = new CheckoutRule { PathTemplate = @"{projectName}" },
        };
    }

    private static bool IsValidSingleDirectoryName(string name)
        => !string.IsNullOrWhiteSpace(name) &&
           name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
           !name.Contains(Path.DirectorySeparatorChar) &&
           !name.Contains(Path.AltDirectorySeparatorChar);

    private static PathTemplateContext CreatePathTemplateContext(string baseRoot)
    {
        return new PathTemplateContext
        {
            ProjectSlug = "repo-main",
            ProjectName = "Repo.Main",
            RepoName = "repo-main",
            MachineId = "machine-a",
            ProjectId = ProjectId.Parse("01963b36-0d70-7a11-b3c2-1f2e3d4c5b6a"),
            BaseRoot = baseRoot,
        };
    }

    private static SessionViewDescriptor CreateInternalSessionDescriptor()
    {
        var timestamp = new DateTimeOffset(2026, 03, 10, 12, 0, 0, TimeSpan.Zero);
        return new SessionViewDescriptor
        {
            SessionId = "platform-search-review",
            Kind = SessionViewKind.InternalSession,
            ProviderId = "codex",
            ProjectRef = Guid.CreateVersion7().ToString(),
            ParentSessionId = "global-session",
            WorkingDirectory = @"C:\Users\alexa\.alta\sessions\internal\platform-search-review",
            Title = "Review sqlitevec integration",
            Status = SessionViewStatus.Active,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
            LastActiveAt = timestamp,
            StartedAt = timestamp,
        };
    }

    private static bool IsGitAvailable()
    {
        try
        {
            var startInfo = new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(milliseconds: 2000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void RunGit(string workingDirectory, string arguments)
    {
        var startInfo = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            Assert.Inconclusive("Failed to start git process.");
        }

        process.WaitForExit(milliseconds: 10_000);
        if (process.ExitCode != 0)
        {
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            Assert.Fail($"git {arguments} failed: {string.Join("\n", new[] { output, error }.Where(x => !string.IsNullOrWhiteSpace(x)))}");
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"CodeAlta.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup for temporary test files.
            }
        }
    }
}
