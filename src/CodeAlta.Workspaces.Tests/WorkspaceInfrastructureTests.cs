using CodeAlta.Workspaces;
using CodeAlta.Workspaces.Bootstrap;
using CodeAlta.Workspaces.Skills;
using System.Diagnostics;

namespace CodeAlta.Workspaces.Tests;

[TestClass]
public sealed class WorkspaceInfrastructureTests
{
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
            repo_url: "https://example.com/repo-main.git"
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
            repo_url: "https://example.com/repo-main.git"
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
            RepoUrl = "https://example.com/repo-main.git",
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
        var descriptor = CreateWorkspaceThreadDescriptor();
        descriptor.MarkdownBody = "# Review sqlitevec integration";

        var markdown = serializer.SerializeThreadMarkdown(descriptor);
        var reloaded = serializer.DeserializeThreadMarkdown(markdown);
        reloaded.Validate();

        Assert.AreEqual(descriptor.ThreadId, reloaded.ThreadId);
        Assert.AreEqual(descriptor.Kind, reloaded.Kind);
        Assert.AreEqual(descriptor.ScopeMode, reloaded.ScopeMode);
        CollectionAssert.AreEqual(descriptor.ProjectRefs, reloaded.ProjectRefs);
        Assert.AreEqual(descriptor.StartedAt, reloaded.StartedAt);
    }

    [TestMethod]
    public async Task WorkThreadCatalog_SaveAndLoadAsync_PersistsWorkspaceAndGlobalThreads()
    {
        using var root = TempDirectory.Create();
        var options = new WorkspaceCatalogOptions { GlobalRepoRoot = root.Path };
        var catalog = new WorkspaceCatalog(options);
        var project = new ProjectDescriptor
        {
            Id = ProjectId.NewVersion7().ToString(),
            Slug = "repo-main",
            DisplayName = "Main Repo",
            RepoUrl = "https://example.com/repo-main.git",
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

        var threadCatalog = new WorkThreadCatalog(catalog, options);
        var timestamp = new DateTimeOffset(2026, 03, 10, 12, 0, 0, TimeSpan.Zero);
        var workspaceThread = new WorkThreadDescriptor
        {
            ThreadId = "platform-search-review",
            Kind = WorkThreadKind.WorkspaceThread,
            WorkspaceRef = workspace.Id,
            ProjectRefs = [project.Id],
            ScopeMode = WorkThreadScopeMode.SingleProject,
            Title = "Review sqlitevec integration",
            Status = WorkThreadStatus.Active,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
            LastActiveAt = timestamp,
            StartedAt = timestamp,
            MarkdownBody = "# Review sqlitevec integration",
        };

        await threadCatalog.SaveAsync(workspaceThread).ConfigureAwait(false);

        var globalThread = new WorkThreadDescriptor
        {
            ThreadId = "global",
            Kind = WorkThreadKind.Global,
            ScopeMode = WorkThreadScopeMode.AllProjects,
            Title = "Global Thread",
            Status = WorkThreadStatus.Active,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
            LastActiveAt = timestamp,
            MarkdownBody = "# Global Thread",
        };

        await threadCatalog.SaveAsync(globalThread).ConfigureAwait(false);

        var loaded = await threadCatalog.LoadAsync().ConfigureAwait(false);
        Assert.AreEqual(2, loaded.Count);
        Assert.IsTrue(File.Exists(Path.Combine(root.Path, "workspaces", "wk-core", "threads", workspaceThread.ThreadId, "readme.md")));
        Assert.IsTrue(File.Exists(Path.Combine(root.Path, "threads", "global", "readme.md")));
        Assert.AreEqual(1, loaded.Count(x => x.Kind == WorkThreadKind.Global));
        Assert.AreEqual(1, loaded.Count(x => x.Kind == WorkThreadKind.WorkspaceThread));

        var workspaces = await catalog.LoadAsync().ConfigureAwait(false);
        Assert.AreEqual(1, workspaces.Count);
        Assert.AreEqual("wk-core", workspaces[0].Slug);
    }

    [TestMethod]
    public async Task WorkThreadCatalog_SaveAsync_RejectsProjectOutsideWorkspace()
    {
        using var root = TempDirectory.Create();
        var options = new WorkspaceCatalogOptions { GlobalRepoRoot = root.Path };
        var catalog = new WorkspaceCatalog(options);
        var projectA = new ProjectDescriptor
        {
            Id = ProjectId.NewVersion7().ToString(),
            Slug = "repo-main",
            DisplayName = "Main Repo",
            RepoUrl = "https://example.com/repo-main.git",
            DefaultBranch = "main",
        };
        var projectB = new ProjectDescriptor
        {
            Id = ProjectId.NewVersion7().ToString(),
            Slug = "repo-tools",
            DisplayName = "Tools Repo",
            RepoUrl = "https://example.com/repo-tools.git",
            DefaultBranch = "main",
        };

        await catalog.SaveProjectAsync(projectA).ConfigureAwait(false);
        await catalog.SaveProjectAsync(projectB).ConfigureAwait(false);

        var workspace = new WorkspaceDescriptor
        {
            Id = WorkspaceId.NewVersion7().ToString(),
            Slug = "wk-core",
            DisplayName = "Core Workspace",
            DefaultCheckoutRoot = @"C:\code",
            ProjectRefs = [projectA.Id],
        };

        await catalog.SaveWorkspaceAsync(workspace).ConfigureAwait(false);

        var threadCatalog = new WorkThreadCatalog(catalog, options);
        var descriptor = CreateWorkspaceThreadDescriptor();
        descriptor.WorkspaceRef = workspace.Id;
        descriptor.ProjectRefs = [projectB.Id];

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => threadCatalog.SaveAsync(descriptor));
    }

    [TestMethod]
    public async Task WorkThreadCatalog_SaveAsync_RejectsWorkspaceChangeAfterStart()
    {
        using var root = TempDirectory.Create();
        var options = new WorkspaceCatalogOptions { GlobalRepoRoot = root.Path };
        var catalog = new WorkspaceCatalog(options);
        var projectA = new ProjectDescriptor
        {
            Id = ProjectId.NewVersion7().ToString(),
            Slug = "repo-main",
            DisplayName = "Main Repo",
            RepoUrl = "https://example.com/repo-main.git",
            DefaultBranch = "main",
        };

        await catalog.SaveProjectAsync(projectA).ConfigureAwait(false);

        var workspaceA = new WorkspaceDescriptor
        {
            Id = WorkspaceId.NewVersion7().ToString(),
            Slug = "wk-core",
            DisplayName = "Core Workspace",
            DefaultCheckoutRoot = @"C:\code",
            ProjectRefs = [projectA.Id],
        };
        var workspaceB = new WorkspaceDescriptor
        {
            Id = WorkspaceId.NewVersion7().ToString(),
            Slug = "wk-alt",
            DisplayName = "Alt Workspace",
            DefaultCheckoutRoot = @"C:\code",
            ProjectRefs = [projectA.Id],
        };

        await catalog.SaveWorkspaceAsync(workspaceA).ConfigureAwait(false);
        await catalog.SaveWorkspaceAsync(workspaceB).ConfigureAwait(false);

        var threadCatalog = new WorkThreadCatalog(catalog, options);
        var descriptor = CreateWorkspaceThreadDescriptor();
        descriptor.WorkspaceRef = workspaceA.Id;
        descriptor.ProjectRefs = [projectA.Id];
        await threadCatalog.SaveAsync(descriptor).ConfigureAwait(false);

        descriptor.WorkspaceRef = workspaceB.Id;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => threadCatalog.SaveAsync(descriptor));
    }

    [TestMethod]
    public async Task WorkThreadCatalog_ViewStateRoundTrip_Works()
    {
        using var root = TempDirectory.Create();
        var options = new WorkspaceCatalogOptions { GlobalRepoRoot = root.Path };
        var catalog = new WorkspaceCatalog(options);
        var threadCatalog = new WorkThreadCatalog(catalog, options);

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
                        RepoUrl = remote,
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
                    RepoUrl = "https://example.com/repo-main.git",
                    DefaultBranch = "main",
                    Checkout = new CheckoutRule { PathTemplate = @"{workspaceKey}\{projectKey}" },
                },
                new ProjectDescriptor
                {
                    Id = ProjectId.NewVersion7().ToString(),
                    Slug = "repo-tools",
                    DisplayName = "Tools Repo",
                    RepoUrl = "https://example.com/repo-tools.git",
                    DefaultBranch = "main",
                    Checkout = new CheckoutRule { PathTemplate = @"{workspaceKey}\{projectKey}" },
                },
            ],
        };
    }

    private static WorkThreadDescriptor CreateWorkspaceThreadDescriptor()
    {
        var timestamp = new DateTimeOffset(2026, 03, 10, 12, 0, 0, TimeSpan.Zero);
        return new WorkThreadDescriptor
        {
            ThreadId = "platform-search-review",
            Kind = WorkThreadKind.WorkspaceThread,
            WorkspaceRef = WorkspaceId.NewVersion7().ToString(),
            ProjectRefs = [ProjectId.NewVersion7().ToString()],
            ScopeMode = WorkThreadScopeMode.SingleProject,
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
