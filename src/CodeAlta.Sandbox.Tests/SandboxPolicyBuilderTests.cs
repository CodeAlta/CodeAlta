namespace CodeAlta.Sandbox.Tests;

[TestClass]
public sealed class SandboxPolicyBuilderTests
{
    [TestMethod]
    public void AProjectSession_WritesItsFolderAndPrivateFolders_AndProtectsWhatOtherProgramsRun()
    {
        using var temp = new TempTree();
        var project = temp.Folder("project");
        Directory.CreateDirectory(Path.Combine(project, ".git", "hooks"));

        var policy = Build(temp, SandboxProfiles.Resolve("workspace", []), project);

        Assert.AreEqual(project, policy.WorkingDirectory);
        CollectionAssert.Contains(policy.ReadWrite.ToList(), project);
        CollectionAssert.Contains(policy.ReadWrite.ToList(), policy.Temp);
        CollectionAssert.Contains(policy.ReadWrite.ToList(), policy.Home);
        Assert.IsTrue(policy.Temp.StartsWith(temp.Folder("data"), StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(policy.ReadWrite.Any(path => path.Equals(temp.Folder("home"), StringComparison.OrdinalIgnoreCase)), "the user's home is not granted");
        foreach (var entry in new[] { Path.Combine(".git", "hooks"), Path.Combine(".git", "config"), Path.Combine(".alta", "plugins"), Path.Combine(".alta", "config.toml"), ".vscode", ".mcp.json" })
        {
            CollectionAssert.Contains(policy.Protected.ToList(), Path.Combine(project, entry), entry);
        }

        Assert.IsFalse(policy.Protected.Any(path => path.EndsWith(Path.Combine(".alta", "plans"), StringComparison.OrdinalIgnoreCase)), "plans stay writable");
        Assert.AreEqual(SandboxNetwork.Hosts, policy.Network);
    }

    [TestMethod]
    public void AWorktreeSession_WritesTheGitFoldersOfItsRepository_AndProtectsTheirHooksAndConfiguration()
    {
        using var temp = new TempTree();
        var repository = temp.Folder("repo");
        var common = Path.Combine(repository, ".git");
        var gitDir = Path.Combine(common, "worktrees", "wt");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "commondir"), "../..\n");
        var worktree = temp.Folder("worktrees/wt");
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {gitDir}\n");

        var policy = Build(temp, SandboxProfiles.Resolve("strict", []), worktree, repositories: [repository]);

        CollectionAssert.Contains(policy.ReadWrite.ToList(), common, "the common folder holds the one of the worktree");
        CollectionAssert.Contains(policy.Protected.ToList(), Path.Combine(common, "hooks"));
        CollectionAssert.Contains(policy.Protected.ToList(), Path.Combine(common, "config"));
        CollectionAssert.Contains(policy.Protected.ToList(), Path.Combine(gitDir, "config.worktree"));
        Assert.IsFalse(policy.ReadWrite.Contains(repository, StringComparer.OrdinalIgnoreCase), "the working tree of the repository is not granted");
    }

    [TestMethod]
    public void TheGitFileOfAWorktree_GrantsOnlyTheGitFolderOfARepositoryCodeAltaGives()
    {
        using var temp = new TempTree();
        var repository = temp.Folder("repo");
        Directory.CreateDirectory(Path.Combine(repository, ".git", "worktrees", "wt"));
        var worktree = temp.Folder("worktrees/wt");
        var home = temp.Folder("home");
        var dotGit = Path.Combine(worktree, ".git");
        var strict = SandboxProfiles.Resolve("strict", []);

        // The sandbox writes the `.git` file of its worktree: it names the home folder, through a commondir of its own.
        var forged = Directory.CreateDirectory(Path.Combine(worktree, "forged")).FullName;
        File.WriteAllText(Path.Combine(forged, "commondir"), home);
        File.WriteAllText(dotGit, $"gitdir: {forged}\n");
        var policy = Build(temp, strict, worktree, repositories: [repository]);
        Assert.IsFalse(policy.ReadWrite.Any(path => path.StartsWith(home, StringComparison.OrdinalIgnoreCase)), "a forged common folder is not granted");

        // Nor the git folder of a repository CodeAlta does not give, nor a worktree folder outside `worktrees`.
        var other = temp.Folder("other");
        Directory.CreateDirectory(Path.Combine(other, ".git", "worktrees", "x"));
        File.WriteAllText(Path.Combine(other, ".git", "worktrees", "x", "commondir"), "../..");
        File.WriteAllText(dotGit, $"gitdir: {Path.Combine(other, ".git", "worktrees", "x")}\n");
        Assert.IsFalse(Build(temp, strict, worktree, repositories: [repository]).ReadWrite.Any(path => path.StartsWith(other, StringComparison.OrdinalIgnoreCase)));
        File.WriteAllText(Path.Combine(repository, ".git", "commondir"), ".");
        File.WriteAllText(dotGit, $"gitdir: {Path.Combine(repository, ".git")}\n");
        Assert.IsFalse(Build(temp, strict, worktree, repositories: [repository]).ReadWrite.Contains(Path.Combine(repository, ".git"), StringComparer.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void AWorkingDirectoryInTheFolderOfCodeAlta_IsReplacedByAFolderOfTheSession_ButItsWorktreesStay()
    {
        using var temp = new TempTree();
        var alta = temp.Folder("home/.alta");
        var strict = SandboxProfiles.Resolve("strict", []);

        // A global session works in the folder of CodeAlta, where its credentials and settings are.
        var global = Build(temp, strict, alta);
        Assert.IsFalse(global.ReadWrite.Any(path => path.StartsWith(alta, StringComparison.OrdinalIgnoreCase)), "the folder of CodeAlta is not granted");
        Assert.IsTrue(global.WorkingDirectory.StartsWith(temp.Folder("data"), StringComparison.OrdinalIgnoreCase));
        CollectionAssert.Contains(global.ReadWrite.ToList(), global.WorkingDirectory);
        Assert.IsFalse(Build(temp, strict, Path.Combine(alta, "auth")).ReadWrite.Any(path => path.StartsWith(alta, StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(Build(temp, strict, Path.Combine(alta, "worktrees")).ReadWrite.Any(path => path.StartsWith(alta, StringComparison.OrdinalIgnoreCase)),
            "the folder of the worktrees is not one of them");

        var worktree = temp.Folder("home/.alta/worktrees/project/wt");
        Assert.AreEqual(worktree, Build(temp, strict, worktree).WorkingDirectory, "a worktree of CodeAlta is the folder of its session");
    }

    [TestMethod]
    public void TheGitFolderOfTheWorkingTree_IsProtectedAsItIsFound()
    {
        using var temp = new TempTree();
        var project = temp.Folder("project");
        Directory.CreateDirectory(Path.Combine(project, ".git"));

        var policy = Build(temp, SandboxProfiles.Resolve("strict", []), project);

        CollectionAssert.Contains(policy.Protected.ToList(), Path.Combine(project, ".git", "config"));
        CollectionAssert.Contains(policy.Protected.ToList(), Path.Combine(project, ".git", "hooks"));
    }

    [TestMethod]
    public void Secrets_AreDeniedWhereAProfileGrantsAFolderThatHoldsThem()
    {
        using var temp = new TempTree();
        var project = temp.Folder("project");

        var policy = Build(temp, SandboxProfiles.Resolve("wide", [new SandboxProfile { Name = "wide", Read = ["~"] }]), project);

        CollectionAssert.Contains(policy.ReadOnly.ToList(), temp.Folder("home"));
        CollectionAssert.Contains(policy.Denied.ToList(), Path.Combine(temp.Folder("home"), ".alta"));
        CollectionAssert.Contains(policy.Denied.ToList(), Path.Combine(temp.Folder("home"), ".ssh"));
        // The sign-in of the coding agents, which keep it in files.
        foreach (var entry in new[] { ".copilot", ".grok", Path.Combine("AppData", "Roaming", "Cursor"), Path.Combine(".config", "cursor") })
        {
            CollectionAssert.Contains(policy.Denied.ToList(), Path.Combine(temp.Folder("home"), entry), entry);
        }

        Assert.IsEmpty(Build(temp, SandboxProfiles.Resolve("workspace", []), project).Denied, "nothing to deny when nothing holds them");
    }

    [TestMethod]
    public void AProcessWithAHomeOfItsOwn_KeepsItBetweenProfiles_AndNotInTheHomeOfAProfile()
    {
        using var temp = new TempTree();
        var project = temp.Folder("project");
        var shared = Build(temp, SandboxProfiles.Resolve("strict", []), project);

        SandboxPolicy Agent(string profile) => SandboxPolicyBuilder.Build(SandboxProfiles.Resolve(profile, []),
            new SandboxSessionFolders { WorkingDirectory = project, OwnHome = "grok-build" },
            new SandboxHost { DataRoot = temp.Folder("data"), HomeDirectory = temp.Folder("home"), Environment = new Dictionary<string, string>() }, [], "session-1-agent");
        var agent = Agent("strict");

        Assert.AreEqual(Path.Combine(temp.Folder("data"), "agents", "grok-build", "home"), agent.Home);
        Assert.AreEqual(agent.Home, Agent("workspace").Home, "it signs in once");
        Assert.AreEqual(agent.Home, agent.Environment["HOME"]);
        CollectionAssert.Contains(agent.ReadWrite.ToList(), agent.Home);
        CollectionAssert.DoesNotContain(agent.ReadWrite.ToList(), shared.Home, "the home of the profile is the one of the other processes");
        Assert.IsFalse(shared.ReadWrite.Concat(shared.ReadOnly).Any(path => SandboxPolicyBuilder.IsInside(path, agent.Home)), "and they do not reach its own");
    }

    [TestMethod]
    public void TheEnvironment_IsBuiltFromScratch_WithoutSecrets_AndPointsToolsAtPrivateFolders()
    {
        using var temp = new TempTree();
        var project = temp.Folder("project");
        var profile = SandboxProfiles.Resolve("env", [new SandboxProfile { Name = "env", Extends = "workspace", Env = ["MY_FEED_TOKEN"] }]);

        var policy = Build(temp, profile, project, new Dictionary<string, string>
        {
            ["CODEALTA_SESSION_ID"] = "s1",
            ["GIT_AUTHOR_NAME"] = "Ada",
            ["AWS_SECRET_ACCESS_KEY"] = "from-the-space",
            // An identity of the repository that copies a secret of the application under another name.
            ["LEAK"] = "sk-host",
            ["WRAPPED"] = "Bearer sk-host",
            // What loads code into the worker as it starts.
            ["DOTNET_STARTUP_HOOKS"] = "C:/project/hook.dll",
            ["LD_PRELOAD"] = "/project/hook.so",
        });

        Assert.AreEqual("s1", policy.Environment["CODEALTA_SESSION_ID"]);
        Assert.AreEqual("Ada", policy.Environment["GIT_AUTHOR_NAME"]);
        Assert.IsFalse(policy.Environment.ContainsKey("AWS_SECRET_ACCESS_KEY"), "a secret of the session is passed only when the profile names it");
        Assert.IsFalse(policy.Environment.ContainsKey("OPENAI_API_KEY"), "the application's own variables are not inherited");
        Assert.IsFalse(policy.Environment.ContainsKey("LEAK"), "nor their values under another name");
        Assert.IsFalse(policy.Environment.ContainsKey("WRAPPED"));
        Assert.IsFalse(policy.Environment.ContainsKey("DOTNET_STARTUP_HOOKS"), "a variable of the session puts no code into the worker");
        Assert.IsFalse(policy.Environment.ContainsKey("LD_PRELOAD"));
        Assert.AreEqual("feed", policy.Environment["MY_FEED_TOKEN"], "the profile names it");
        Assert.AreEqual(policy.Home, policy.Environment["HOME"]);
        Assert.AreEqual(policy.Temp, policy.Environment["TEMP"]);
        Assert.IsTrue(policy.Environment["NUGET_PACKAGES"].StartsWith(temp.Folder("data"), StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual("env", policy.Environment["CODEALTA_SANDBOX"]);
        Assert.AreEqual("/bin", policy.Environment["PATH"]);
    }

    [TestMethod]
    public void SecretNames_AreRecognized()
    {
        foreach (var name in new[] { "GITHUB_TOKEN", "OPENAI_API_KEY", "AWS_SECRET_ACCESS_KEY", "DB_PASSWORD", "SIGNING_KEY", "azure_credentials" })
            Assert.IsTrue(SandboxPolicyBuilder.IsSecretName(name), name);
        foreach (var name in new[] { "PATH", "GIT_AUTHOR_NAME", "CODEALTA_SESSION_ID", "KUBECONFIG", "GIT_SSH_COMMAND" })
            Assert.IsFalse(SandboxPolicyBuilder.IsSecretName(name), name);
    }

    private static SandboxPolicy Build(TempTree temp, ResolvedSandboxProfile profile, string workingDirectory, Dictionary<string, string>? session = null,
        IReadOnlyList<string>? repositories = null)
        => SandboxPolicyBuilder.Build(profile, new SandboxSessionFolders { WorkingDirectory = workingDirectory, Repositories = repositories ?? [] },
            new SandboxHost
            {
                DataRoot = temp.Folder("data"),
                HomeDirectory = temp.Folder("home"),
                Environment = new Dictionary<string, string> { ["PATH"] = "/bin", ["OPENAI_API_KEY"] = "sk-host", ["MY_FEED_TOKEN"] = "feed" },
            },
            session ?? [], "session-1");
}

/// <summary>A temporary folder tree, deleted at the end.</summary>
internal sealed class TempTree : IDisposable
{
    public TempTree() => Root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-sandbox-tests-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    public string Root { get; }

    public string Folder(string relative) => Directory.CreateDirectory(Path.Combine(Root, relative)).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
