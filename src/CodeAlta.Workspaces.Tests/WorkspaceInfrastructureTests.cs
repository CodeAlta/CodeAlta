using CodeAlta.Catalog;
using CodeAlta.Catalog.Bootstrap;
using CodeAlta.Catalog.Skills;
using System.Diagnostics;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class WorkspaceInfrastructureTests
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

        Assert.AreEqual("Tomlyn", descriptor.DisplayName);
        Assert.AreEqual("tomlyn", descriptor.Slug);
        Assert.AreEqual(projectRoot, descriptor.ProjectPath);
        Assert.AreEqual(1, reloaded.Count);
        Assert.AreEqual(projectRoot, reloaded[0].ProjectPath);
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
    public async Task ProjectCatalog_ImportWorkingDirectoriesAsync_ImportsDistinctProjectsAndSkipsGlobalRoot()
    {
        using var root = TempDirectory.Create();
        var catalogRoot = Path.Combine(root.Path, ".codealta");
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
        var catalogRoot = Path.Combine(root.Path, ".codealta");
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
        Assert.AreEqual(
            Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(loaded[0].ProjectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    [TestMethod]
    public void WorkspaceYamlSerializer_RoundTrip_Works()
    {
        var serializer = new WorkspaceYamlSerializer();
        var workspace = CreateWorkspaceDescriptor();
        workspace.ProjectRefs = workspace.Projects.Select(static x => x.Id).ToList();

        var yaml = serializer.SerializeWorkspace(workspace);
        var reloaded = serializer.DeserializeWorkspace(yaml);
        reloaded.Validate();

        Assert.AreEqual(workspace.Slug, reloaded.Slug);
        Assert.AreEqual(workspace.DisplayName, reloaded.DisplayName);
        Assert.AreEqual(2, reloaded.Projects.Count);
        Assert.AreEqual("repo-main", reloaded.Projects[0].Slug);
    }

    [TestMethod]
    public void WorkspaceYamlSerializer_MarkdownRoundTrip_Works()
    {
        var serializer = new WorkspaceYamlSerializer();
        var workspace = CreateWorkspaceDescriptor();
        workspace.ProjectRefs = workspace.Projects.Select(static x => x.Id).ToList();
        workspace.MarkdownBody = "# Core Workspace\n\nShared code and services.";

        var markdown = serializer.SerializeWorkspaceMarkdown(workspace);
        var reloaded = serializer.DeserializeWorkspaceMarkdown(markdown);
        reloaded.Validate();

        Assert.AreEqual(workspace.Slug, reloaded.Slug);
        Assert.AreEqual(workspace.DisplayName, reloaded.DisplayName);
        CollectionAssert.AreEqual(workspace.ProjectRefs, reloaded.ProjectRefs);
        StringAssert.Contains(reloaded.MarkdownBody, "Shared code and services.");
    }

    [TestMethod]
    public async Task WorkspaceCatalog_LoadAsync_LoadsProjectsFromReadmeCatalog()
    {
        using var root = TempDirectory.Create();
        var workspaceRoot = Path.Combine(root.Path, "workspaces", "wk-core");
        var projectsRoot = Path.Combine(root.Path, "projects", "repo-main");
        Directory.CreateDirectory(workspaceRoot);
        Directory.CreateDirectory(projectsRoot);

        await File.WriteAllTextAsync(
            Path.Combine(workspaceRoot, "readme.md"),
            """
            ---
            id: "01963b36-0d6f-7e4b-a7e0-6b2e6d1f4c8a"
            kind: "workspace"
            slug: "wk-core"
            display_name: "Core Workspace"
            checkout:
              path_template: 'C:\code'
            tags:
              - "core"
            project_refs:
              - "01963b36-0d70-7a11-b3c2-1f2e3d4c5b6a"
            ---

            # Core Workspace

            Shared code and services.
            """
        ).ConfigureAwait(false);

        await File.WriteAllTextAsync(
            Path.Combine(projectsRoot, "readme.md"),
            """
            ---
            kind: "project"
            id: "01963b36-0d70-7a11-b3c2-1f2e3d4c5b6a"
            slug: "repo-main"
            display_name: "Main Repo"
            path: "C:\\code\\repo-main"
            default_branch: "main"
            checkout:
              path_template: '{workspaceKey}\{projectKey}'
            ---

            # Main Repo
            """
        ).ConfigureAwait(false);

        var catalog = new WorkspaceCatalog(new WorkspaceCatalogOptions { GlobalRepoRoot = root.Path });
        var workspaces = await catalog.LoadAsync().ConfigureAwait(false);

        Assert.AreEqual(1, workspaces.Count);
        Assert.AreEqual("wk-core", workspaces[0].Slug);
        Assert.AreEqual(1, workspaces[0].Projects.Count);
        Assert.AreEqual("repo-main", workspaces[0].Projects[0].Slug);
    }

    [TestMethod]
    public void PathTemplateResolver_Resolve_ExpandsMacros()
    {
        var context = new PathTemplateContext
        {
            WorkspaceSlug = "wk-core",
            ProjectSlug = "repo-main",
            RepoName = "repo-main",
            MachineId = "machine-a",
            WorkspaceId = WorkspaceId.Parse("01963b36-0d6f-7e4b-a7e0-6b2e6d1f4c8a"),
            ProjectId = ProjectId.Parse("01963b36-0d70-7a11-b3c2-1f2e3d4c5b6a"),
            BaseRoot = @"C:\code",
        };

        var resolved = PathTemplateResolver.Resolve(@"{workspaceKey}\{projectKey}", context);

        StringAssert.EndsWith(resolved, @"wk-core\repo-main");
    }

    [TestMethod]
    public void PathTemplateResolver_Resolve_ThrowsForTraversal()
    {
        var context = new PathTemplateContext
        {
            WorkspaceSlug = "wk-core",
            ProjectSlug = "repo-main",
            RepoName = "repo-main",
            MachineId = "machine-a",
            WorkspaceId = WorkspaceId.Parse("01963b36-0d6f-7e4b-a7e0-6b2e6d1f4c8a"),
            ProjectId = ProjectId.Parse("01963b36-0d70-7a11-b3c2-1f2e3d4c5b6a"),
            BaseRoot = @"C:\code",
        };

        Assert.ThrowsExactly<ArgumentException>(() =>
            PathTemplateResolver.Resolve(@"..\escape", context));
    }

    [TestMethod]
    public async Task WorkspaceResolver_ResolveAsync_UsesMachineOverridesAndPlansCheckouts()
    {
        using var root = TempDirectory.Create();
        var workspaceRoot = Path.Combine(root.Path, "workspaces", "wk-core");
        var projectRoot = Path.Combine(root.Path, "projects", "repo-main");
        Directory.CreateDirectory(workspaceRoot);
        Directory.CreateDirectory(projectRoot);

        await File.WriteAllTextAsync(
            Path.Combine(workspaceRoot, "readme.md"),
            """
            ---
            id: "01963b36-0d6f-7e4b-a7e0-6b2e6d1f4c8a"
            kind: "workspace"
            slug: "wk-core"
            display_name: "Core Workspace"
            checkout:
              path_template: 'C:\default'
            project_refs:
              - "01963b36-0d70-7a11-b3c2-1f2e3d4c5b6a"
            ---

            # Core Workspace
            """
        ).ConfigureAwait(false);

        await File.WriteAllTextAsync(
            Path.Combine(projectRoot, "readme.md"),
            """
            ---
            id: "01963b36-0d70-7a11-b3c2-1f2e3d4c5b6a"
            kind: "project"
            slug: "repo-main"
            display_name: "Main Repo"
            path: "C:\\code\\repo-main"
            default_branch: "main"
            checkout:
              path_template: '{workspaceKey}\{projectKey}'
            ---

            # Main Repo
            """
        ).ConfigureAwait(false);

        var catalog = new WorkspaceCatalog(new WorkspaceCatalogOptions { GlobalRepoRoot = root.Path });
        var resolver = new WorkspaceResolver(catalog);
        var machineRoot = Path.Combine(root.Path, "checkouts");

        var machine = new MachineProfile
        {
            MachineId = "machine-a",
            WorkspaceCheckoutRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["wk-core"] = machineRoot,
            },
        };

        var resolutions = await resolver.ResolveAsync(ScopeSelector.Workspace("wk-core"), machine).ConfigureAwait(false);
        Assert.AreEqual(1, resolutions.Count);
        Assert.AreEqual(1, resolutions[0].Projects.Count);
        StringAssert.StartsWith(resolutions[0].Projects[0].CheckoutPath, Path.GetFullPath(machineRoot));

        var planner = new WorkspaceBootstrapPlanner();
        var plans = planner.Plan(resolutions[0]);

        Assert.AreEqual(1, plans.Count);
        Assert.AreEqual(CheckoutAction.Clone, plans[0].Action);
    }

    [TestMethod]
    public async Task WorkspaceCatalog_SaveAsync_WritesReadmeCatalogFiles()
    {
        using var root = TempDirectory.Create();
        var catalog = new WorkspaceCatalog(new WorkspaceCatalogOptions { GlobalRepoRoot = root.Path });
        var project = new ProjectDescriptor
        {
            Id = ProjectId.NewVersion7().ToString(),
            Slug = "repo-main",
            DisplayName = "Main Repo",
            ProjectPath = "C:\\code\\repo-main",
            DefaultBranch = "main",
            Checkout = new CheckoutRule { PathTemplate = @"{workspaceKey}\{projectKey}" },
            MarkdownBody = "# Main Repo",
        };

        await catalog.SaveProjectAsync(project).ConfigureAwait(false);

        var workspace = new WorkspaceDescriptor
        {
            Id = WorkspaceId.NewVersion7().ToString(),
            Slug = "wk-core",
            DisplayName = "Core Workspace",
            DefaultCheckoutRoot = @"C:\code",
            ProjectRefs = [project.Id],
            MarkdownBody = "# Core Workspace",
        };

        await catalog.SaveWorkspaceAsync(workspace).ConfigureAwait(false);

        Assert.IsTrue(File.Exists(Path.Combine(root.Path, "projects", "repo-main", "readme.md")));
        Assert.IsTrue(File.Exists(Path.Combine(root.Path, "workspaces", "wk-core", "readme.md")));

        var loaded = await catalog.GetBySlugAsync("wk-core").ConfigureAwait(false);
        Assert.IsNotNull(loaded);
        Assert.AreEqual(1, loaded.Projects.Count);
        Assert.AreEqual("repo-main", loaded.Projects[0].Slug);
    }

    [TestMethod]
    public void WorkThreadYamlSerializer_MarkdownRoundTrip_Works()
    {
        var serializer = new WorkThreadYamlSerializer();
        var descriptor = CreateInternalThreadDescriptor();
        descriptor.MarkdownBody = "# Review sqlitevec integration";

        var markdown = serializer.SerializeThreadMarkdown(descriptor);
        var reloaded = serializer.DeserializeThreadMarkdown(markdown);
        reloaded.Validate();

        Assert.AreEqual(descriptor.ThreadId, reloaded.ThreadId);
        Assert.AreEqual(descriptor.Kind, reloaded.Kind);
        Assert.AreEqual(descriptor.ProjectRef, reloaded.ProjectRef);
        Assert.AreEqual(descriptor.ParentThreadId, reloaded.ParentThreadId);
        Assert.AreEqual(descriptor.BackendId, reloaded.BackendId);
        Assert.AreEqual(descriptor.BackendSessionId, reloaded.BackendSessionId);
        Assert.AreEqual(descriptor.StartedAt, reloaded.StartedAt);
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
            SelectedThreadId = "platform-search-review",
            UpdatedAt = new DateTimeOffset(2026, 03, 10, 13, 0, 0, TimeSpan.Zero),
        };

        await threadCatalog.SaveViewStateAsync(viewState).ConfigureAwait(false);
        var reloaded = await threadCatalog.LoadViewStateAsync().ConfigureAwait(false);

        CollectionAssert.AreEqual(viewState.OpenThreadIds, reloaded.OpenThreadIds);
        Assert.AreEqual(viewState.SelectedThreadId, reloaded.SelectedThreadId);
    }

    [TestMethod]
    public async Task SkillCatalog_ListAndGet_Works()
    {
        using var root = TempDirectory.Create();
        var skillsRoot = Path.Combine(root.Path, ".codealta", "skills");
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
        var skillsRoot = Path.Combine(root.Path, ".codealta", "skills");
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
        Assert.IsTrue(Directory.Exists(Path.Combine(globalRepoRoot, "workspaces")));
        Assert.IsTrue(Directory.Exists(Path.Combine(globalRepoRoot, "machines")));
    }

    [TestMethod]
    public async Task WorkspaceBootstrapper_EnsureCheckedOutAsync_ClonesMissingRepo()
    {
        if (!IsGitAvailable())
        {
            Assert.Inconclusive("git CLI was not available on PATH.");
        }

        using var root = TempDirectory.Create();
        var remote = Path.Combine(root.Path, "remote.git");
        var checkout = Path.Combine(root.Path, "checkouts", "repo-main");

        RunGit(root.Path, $"init --bare \"{remote}\"");

        var resolution = new WorkspaceResolution
        {
            Workspace = new WorkspaceDescriptor
            {
                Id = WorkspaceId.NewVersion7().ToString(),
                Slug = "wk-core",
                DisplayName = "Core Workspace",
                DefaultCheckoutRoot = root.Path,
                Projects = [],
            },
            Projects =
            [
                new ResolvedProject
                {
                    Project = new ProjectDescriptor
                    {
                        Id = ProjectId.NewVersion7().ToString(),
                        Slug = "repo-main",
                        DisplayName = "Main Repo",
                        ProjectPath = remote,
                        DefaultBranch = "main",
                        Checkout = new CheckoutRule { PathTemplate = checkout },
                    },
                    CheckoutPath = checkout,
                    CodeAltaRoot = Path.Combine(checkout, ".codealta"),
                },
            ],
            CodeAltaRoots = [Path.Combine(checkout, ".codealta")],
        };

        var bootstrapper = new WorkspaceBootstrapper(new WorkspaceBootstrapPlanner(), new GitService());
        var results = await bootstrapper.EnsureCheckedOutAsync(resolution).ConfigureAwait(false);

        Assert.AreEqual(1, results.Count);
        Assert.IsTrue(results[0].Success);
        Assert.IsTrue(Directory.Exists(Path.Combine(checkout, ".git")));
        Assert.IsTrue(Directory.Exists(Path.Combine(checkout, ".codealta")));
    }

    private static WorkspaceDescriptor CreateWorkspaceDescriptor()
    {
        return new WorkspaceDescriptor
        {
            Id = WorkspaceId.NewVersion7().ToString(),
            Slug = "wk-core",
            DisplayName = "Core Workspace",
            DefaultCheckoutRoot = @"C:\code",
            Projects =
            [
                new ProjectDescriptor
                {
                    Id = ProjectId.NewVersion7().ToString(),
                    Slug = "repo-main",
                    DisplayName = "Main Repo",
                    ProjectPath = "C:\\code\\repo-main",
                    DefaultBranch = "main",
                    Checkout = new CheckoutRule { PathTemplate = @"{workspaceKey}\{projectKey}" },
                },
                new ProjectDescriptor
                {
                    Id = ProjectId.NewVersion7().ToString(),
                    Slug = "repo-tools",
                    DisplayName = "Tools Repo",
                    ProjectPath = "C:\\code\\repo-tools",
                    DefaultBranch = "main",
                    Checkout = new CheckoutRule { PathTemplate = @"{workspaceKey}\{projectKey}" },
                },
            ],
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
            WorkingDirectory = @"C:\Users\alexa\.codealta\threads\internal\platform-search-review",
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



