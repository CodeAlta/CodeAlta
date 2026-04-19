using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Bootstrap;
using CodeAlta.Catalog.Skills;
using System.Diagnostics;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class CatalogInfrastructureTests
{
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
        var internalThreadRoot = Path.Combine(catalogRoot, "threads", "internal", "child-1");
        Directory.CreateDirectory(catalogRoot);
        Directory.CreateDirectory(projectA);
        Directory.CreateDirectory(projectB);
        Directory.CreateDirectory(internalThreadRoot);

        var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = catalogRoot });
        var imported = await catalog.ImportWorkingDirectoriesAsync(
            [
                projectA,
                projectB,
                projectA,
                catalogRoot,
                internalThreadRoot,
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
        Directory.CreateDirectory(Path.Combine(projectsRoot, "codealta"));
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
            Path.Combine(projectsRoot, "codealta", "readme.md"),
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
    public async Task ProjectCatalog_LoadAsync_LoadsProjectsFromReadmeCatalog()
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
    public async Task ProjectCatalog_SaveAsync_WritesReadmeCatalogFiles()
    {
        using var root = TempDirectory.Create();
        var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = root.Path });
        var project = CreateProjectDescriptor("repo-main", "Repo.Main", "Main Repo");

        await catalog.SaveAsync(project).ConfigureAwait(false);

        Assert.IsTrue(File.Exists(Path.Combine(root.Path, "projects", "repo-main", "readme.md")));

        var loaded = await catalog.GetBySlugAsync("repo-main").ConfigureAwait(false);
        Assert.IsNotNull(loaded);
        Assert.AreEqual("Repo.Main", loaded.Name);
    }

    [TestMethod]
    public void WorkThreadYamlSerializer_MarkdownRoundTrip_Works()
    {
        var serializer = new WorkThreadYamlSerializer();
        var descriptor = CreateInternalThreadDescriptor();
        descriptor.MessageCount = 7;
        descriptor.MarkdownBody = "# Review sqlitevec integration";

        var markdown = serializer.SerializeThreadMarkdown(descriptor);
        var reloaded = serializer.DeserializeThreadMarkdown(markdown);
        reloaded.Validate();

        Assert.AreEqual(descriptor.ThreadId, reloaded.ThreadId);
        Assert.AreEqual(descriptor.Kind, reloaded.Kind);
        Assert.AreEqual(descriptor.ProjectRef, reloaded.ProjectRef);
        Assert.AreEqual(descriptor.ParentThreadId, reloaded.ParentThreadId);
        Assert.AreEqual(descriptor.BackendId, reloaded.BackendId);
        Assert.AreEqual(descriptor.ResolvedProviderKey, reloaded.ResolvedProviderKey);
        Assert.AreEqual(descriptor.BackendSessionId, reloaded.BackendSessionId);
        Assert.AreEqual(descriptor.StartedAt, reloaded.StartedAt);
        Assert.AreEqual(7, reloaded.MessageCount);
    }

    [TestMethod]
    public async Task WorkThreadCatalog_SaveInternalAsync_PersistsInternalThreads()
    {
        using var root = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        var catalog = new WorkThreadCatalog(options);
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
        var internalThread = new WorkThreadDescriptor
        {
            ThreadId = "codex:019cc85b",
            Kind = WorkThreadKind.InternalThread,
            BackendId = "codex",
            BackendSessionId = "019cc85b",
            ProjectRef = project.Id,
            ParentThreadId = "copilot:019cc700",
            WorkingDirectory = Path.Combine(root.Path, "threads", "internal", "codex-019cc85b"),
            Title = "Review sqlitevec integration",
            Status = WorkThreadStatus.Active,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
            LastActiveAt = timestamp,
            StartedAt = timestamp,
            MarkdownBody = "# Review sqlitevec integration",
        };

        await catalog.SaveInternalAsync(internalThread).ConfigureAwait(false);

        var loaded = await catalog.LoadInternalAsync().ConfigureAwait(false);
        Assert.AreEqual(1, loaded.Count);
        Assert.AreEqual(WorkThreadKind.InternalThread, loaded[0].Kind);
        Assert.AreEqual(project.Id, loaded[0].ProjectRef);
        Assert.IsNotNull(internalThread.SourcePath);
        Assert.IsTrue(File.Exists(internalThread.SourcePath));
    }

    [TestMethod]
    public async Task WorkThreadCatalog_SaveInternalAsync_RejectsNonInternalThread()
    {
        using var root = TempDirectory.Create();
        var catalog = new WorkThreadCatalog(new CatalogOptions { GlobalRoot = root.Path });
        var descriptor = new WorkThreadDescriptor
        {
            ThreadId = "copilot:019cc700",
            Kind = WorkThreadKind.ProjectThread,
            BackendId = "copilot",
            BackendSessionId = "019cc700",
            ProjectRef = Guid.CreateVersion7().ToString(),
            WorkingDirectory = @"C:\code\repo-main",
            Title = "Project thread",
            Status = WorkThreadStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
        };

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.SaveInternalAsync(descriptor));
    }

    [TestMethod]
    public async Task WorkThreadCatalog_ViewStateRoundTrip_Works()
    {
        using var root = TempDirectory.Create();
        var threadCatalog = new WorkThreadCatalog(new CatalogOptions { GlobalRoot = root.Path });

        var viewState = new WorkThreadViewState
        {
            OpenThreadIds = ["global", "platform-search-review"],
            Selection = WorkThreadSelectionState.Thread("platform-search-review", "project-1"),
            SelectedThreadId = "platform-search-review",
            UpdatedAt = new DateTimeOffset(2026, 03, 10, 13, 0, 0, TimeSpan.Zero),
            ThreadPreferences = new Dictionary<string, WorkThreadPreference>(StringComparer.OrdinalIgnoreCase)
            {
                ["platform-search-review"] = new WorkThreadPreference
                {
                    ModelId = "gpt-5.4",
                    ReasoningEffort = AgentReasoningEffort.High,
                    AutoScroll = false,
                },
            },
            Navigator = new NavigatorSettings
            {
                SortMode = NavigatorProjectSortMode.Date,
                RecentThreadsPerProject = 8,
            },
            ThreadStates = new Dictionary<string, WorkThreadLocalState>(StringComparer.OrdinalIgnoreCase)
            {
                ["platform-search-review"] = new WorkThreadLocalState
                {
                    Archived = true,
                    MessageCount = 42,
                },
            },
        };

        await threadCatalog.SaveViewStateAsync(viewState).ConfigureAwait(false);
        var reloaded = await threadCatalog.LoadViewStateAsync().ConfigureAwait(false);

        CollectionAssert.AreEqual(viewState.OpenThreadIds, reloaded.OpenThreadIds);
        Assert.AreEqual(WorkThreadSelectionSurface.Thread, reloaded.Selection.Surface);
        Assert.AreEqual("platform-search-review", reloaded.Selection.ThreadId);
        Assert.AreEqual("project-1", reloaded.Selection.ProjectId);
        Assert.AreEqual(viewState.SelectedThreadId, reloaded.SelectedThreadId);
        Assert.AreEqual("gpt-5.4", reloaded.ThreadPreferences["platform-search-review"].ModelId);
        Assert.AreEqual(AgentReasoningEffort.High, reloaded.ThreadPreferences["platform-search-review"].ReasoningEffort);
        Assert.IsFalse(reloaded.ThreadPreferences["platform-search-review"].AutoScroll);
        Assert.AreEqual(NavigatorProjectSortMode.Date, reloaded.Navigator.SortMode);
        Assert.AreEqual(8, reloaded.Navigator.RecentThreadsPerProject);
        Assert.IsTrue(reloaded.ThreadStates["platform-search-review"].Archived);
        Assert.AreEqual(42, reloaded.ThreadStates["platform-search-review"].MessageCount);
    }

    [TestMethod]
    public void WorkThreadYamlSerializer_DeserializeViewState_MigratesLegacyThreadSelection()
    {
        var serializer = new WorkThreadYamlSerializer();
        var yaml =
            """
            open_thread_ids:
              - global
              - platform-search-review
            selected_thread_id: platform-search-review
            """;

        var reloaded = serializer.DeserializeViewState(yaml);

        Assert.AreEqual(WorkThreadSelectionSurface.Thread, reloaded.Selection.Surface);
        Assert.AreEqual("platform-search-review", reloaded.Selection.ThreadId);
        Assert.AreEqual("platform-search-review", reloaded.SelectedThreadId);
    }

    [TestMethod]
    public void CodeAltaConfigStore_SaveGlobalProviderPreference_WritesTomlAndReloads()
    {
        using var root = TempDirectory.Create();
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root.Path });

        store.SaveGlobalProviderPreference(AgentBackendIds.Codex.Value, "gpt-5.4", AgentReasoningEffort.High);

        var configPath = Path.Combine(root.Path, "config.toml");
        var content = File.ReadAllText(configPath);
        var preference = store.GetEffectiveProviderPreference(AgentBackendIds.Codex.Value);

        StringAssert.Contains(content, "[providers.codex]");
        StringAssert.Contains(content, "type = \"codex\"");
        StringAssert.Contains(content, "gpt-5.4");
        StringAssert.Contains(content, "reasoning_effort = \"high\"");
        Assert.AreEqual("gpt-5.4", preference.Model);
        Assert.AreEqual(AgentReasoningEffort.High, preference.ReasoningEffort);
    }

    [TestMethod]
    public void CodeAltaConfigStore_GetEffectiveProviderPreference_ProjectConfigOverridesGlobalPerField()
    {
        using var root = TempDirectory.Create();
        var projectRoot = Path.Combine(root.Path, "project-a");
        Directory.CreateDirectory(Path.Combine(projectRoot, ".alta"));

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root.Path });
        store.SaveGlobalProviderPreference(AgentBackendIds.Codex.Value, "gpt-5.4", AgentReasoningEffort.High);

        File.WriteAllText(
            Path.Combine(projectRoot, ".alta", "config.toml"),
            """
            [providers.codex]
            reasoning_effort = "medium"
            """);

        var preference = store.GetEffectiveProviderPreference(AgentBackendIds.Codex.Value, projectRoot);

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
            "# Sample Skill").ConfigureAwait(false);

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

    private static WorkThreadDescriptor CreateInternalThreadDescriptor()
    {
        var timestamp = new DateTimeOffset(2026, 03, 10, 12, 0, 0, TimeSpan.Zero);
        return new WorkThreadDescriptor
        {
            ThreadId = "codex:platform-search-review",
            Kind = WorkThreadKind.InternalThread,
            BackendId = "codex",
            BackendSessionId = "platform-search-review",
            ProjectRef = Guid.CreateVersion7().ToString(),
            ParentThreadId = "copilot:global-thread",
            WorkingDirectory = @"C:\Users\alexa\.alta\threads\internal\platform-search-review",
            Title = "Review sqlitevec integration",
            Status = WorkThreadStatus.Active,
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
