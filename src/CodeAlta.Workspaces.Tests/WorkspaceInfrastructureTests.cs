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

        var yaml = serializer.SerializeWorkspace(workspace);
        var reloaded = serializer.DeserializeWorkspace(yaml);
        reloaded.Validate();

        Assert.AreEqual(workspace.Key, reloaded.Key);
        Assert.AreEqual(workspace.DisplayName, reloaded.DisplayName);
        Assert.AreEqual(2, reloaded.Projects.Count);
        Assert.AreEqual("repo-main", reloaded.Projects[0].Key);
    }

    [TestMethod]
    public async Task WorkspaceCatalog_LoadAsync_LoadsProjectFiles()
    {
        using var root = TempDirectory.Create();
        var workspaceRoot = Path.Combine(root.Path, "workspaces", "wk-core");
        var projectsRoot = Path.Combine(workspaceRoot, "projects");
        Directory.CreateDirectory(projectsRoot);

        await File.WriteAllTextAsync(
            Path.Combine(workspaceRoot, "workspace.yaml"),
            """
            id: "01963b36-0d6f-7e4b-a7e0-6b2e6d1f4c8a"
            key: "wk-core"
            display_name: "Core Workspace"
            default_checkout_root: 'C:\code'
            tags:
              - "core"
            """
        ).ConfigureAwait(false);

        await File.WriteAllTextAsync(
            Path.Combine(projectsRoot, "repo-main.yaml"),
            """
            id: "01963b36-0d70-7a11-b3c2-1f2e3d4c5b6a"
            key: "repo-main"
            display_name: "Main Repo"
            repo_url: "https://example.com/repo-main.git"
            default_branch: "main"
            checkout:
              path_template: '{workspaceKey}\{projectKey}'
            """
        ).ConfigureAwait(false);

        var catalog = new WorkspaceCatalog(new WorkspaceCatalogOptions { GlobalRepoRoot = root.Path });
        var workspaces = await catalog.LoadAsync().ConfigureAwait(false);

        Assert.AreEqual(1, workspaces.Count);
        Assert.AreEqual("wk-core", workspaces[0].Key);
        Assert.AreEqual(1, workspaces[0].Projects.Count);
        Assert.AreEqual("repo-main", workspaces[0].Projects[0].Key);
    }

    [TestMethod]
    public void PathTemplateResolver_Resolve_ExpandsMacros()
    {
        var context = new PathTemplateContext
        {
            WorkspaceKey = "wk-core",
            ProjectKey = "repo-main",
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
            WorkspaceKey = "wk-core",
            ProjectKey = "repo-main",
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
        var projectsRoot = Path.Combine(workspaceRoot, "projects");
        Directory.CreateDirectory(projectsRoot);

        await File.WriteAllTextAsync(
            Path.Combine(workspaceRoot, "workspace.yaml"),
            """
            id: "01963b36-0d6f-7e4b-a7e0-6b2e6d1f4c8a"
            key: "wk-core"
            display_name: "Core Workspace"
            default_checkout_root: 'C:\default'
            """
        ).ConfigureAwait(false);

        await File.WriteAllTextAsync(
            Path.Combine(projectsRoot, "repo-main.yaml"),
            """
            id: "01963b36-0d70-7a11-b3c2-1f2e3d4c5b6a"
            key: "repo-main"
            display_name: "Main Repo"
            repo_url: "https://example.com/repo-main.git"
            default_branch: "main"
            checkout:
              path_template: '{workspaceKey}\{projectKey}'
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

    private static WorkspaceDescriptor CreateWorkspaceDescriptor()
    {
        return new WorkspaceDescriptor
        {
            Id = WorkspaceId.NewVersion7().ToString(),
            Key = "wk-core",
            DisplayName = "Core Workspace",
            DefaultCheckoutRoot = @"C:\code",
            Projects =
            [
                new ProjectDescriptor
                {
                    Id = ProjectId.NewVersion7().ToString(),
                    Key = "repo-main",
                    DisplayName = "Main Repo",
                    RepoUrl = "https://example.com/repo-main.git",
                    DefaultBranch = "main",
                    Checkout = new CheckoutRule { PathTemplate = @"{workspaceKey}\{projectKey}" },
                },
                new ProjectDescriptor
                {
                    Id = ProjectId.NewVersion7().ToString(),
                    Key = "repo-tools",
                    DisplayName = "Tools Repo",
                    RepoUrl = "https://example.com/repo-tools.git",
                    DefaultBranch = "main",
                    Checkout = new CheckoutRule { PathTemplate = @"{workspaceKey}\{projectKey}" },
                },
            ],
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
