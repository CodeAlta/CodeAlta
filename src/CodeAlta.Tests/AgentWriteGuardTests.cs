using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime.Tools;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AgentWriteGuardTests
{
    [TestMethod]
    public void TheGuard_LetsASessionWithoutTheShellWriteItsProject_ButNotWhatRunsCodeOrGivesToolsBack()
    {
        using var temp = new Temp();
        var project = temp.Folder("project");
        var alta = temp.Folder("home", ".alta");
        var worktree = temp.Folder("home", ".alta", "worktrees", "w");
        var guard = new AgentWriteGuard([project, worktree], [alta], [Path.Combine(alta, "scratchpad")], temp.Folder("home"));

        foreach (var allowed in new[]
                 {
                     Path.Combine(project, "src", "a.cs"), Path.Combine(project, ".alta", "plans", "p.md"), Path.Combine(project, ".alta", "tasks", "t.md"),
                     Path.Combine(project, ".github", "workflows", "ci.yml"), Path.Combine(project, ".gitignore"),
                     Path.Combine(alta, "scratchpad", "notes.md"), Path.Combine(worktree, "src", "b.cs"),
                 })
        {
            Assert.IsNull(guard.Check(allowed), allowed);
        }

        foreach (var refused in new[]
                 {
                     // Code that CodeAlta builds and loads, its MCP servers, its prompts and settings.
                     Path.Combine(project, ".alta", "plugins", "x", "plugin.cs"), Path.Combine(project, ".alta", "mcp.json"), Path.Combine(project, ".alta", "prompts", "agents", "a.prompt.md"),
                     Path.Combine(project, ".alta"), Path.Combine(alta, "config.toml"), Path.Combine(alta, "plugins", "x", "plugin.cs"),
                     // The configuration and the hooks of git, of coding agents and of editors.
                     Path.Combine(project, ".git", "config"), Path.Combine(project, "lib", ".git", "hooks", "post-checkout"), Path.Combine(worktree, ".git"),
                     Path.Combine(project, ".claude", "settings.local.json"), Path.Combine(project, ".mcp.json"), Path.Combine(project, ".github", "mcp.json"),
                     Path.Combine(project, ".vscode", "tasks.json"), Path.Combine(project, ".codex", "config.toml"),
                     // Anything outside the folders of the session.
                     Path.Combine(temp.Path, "elsewhere", "a.txt"),
                 })
        {
            Assert.IsNotNull(guard.Check(refused), refused);
        }
    }

    [TestMethod]
    public void TheGuard_OfASessionThatWorksInTheHomeFolder_RefusesTheSettingsOfTheUser()
    {
        using var temp = new Temp();
        var home = temp.Folder("home");
        var guard = new AgentWriteGuard([home], [Path.Combine(home, ".alta")], [], home);

        Assert.IsNull(guard.Check(Path.Combine(home, "code", "a.txt")));
        Assert.IsNotNull(guard.Check(Path.Combine(home, ".zshrc")));
        Assert.IsNotNull(guard.Check(Path.Combine(home, ".config", "gh", "config.yml")));
        Assert.IsNotNull(guard.Check(Path.Combine(home, "Library", "LaunchAgents", "x.plist")));
        Assert.IsNotNull(guard.Check(Path.Combine(home, ".alta", "config.toml")));
    }

    [TestMethod]
    public void TheGuard_FollowsLinks_SoThatALinkInTheProjectDoesNotLeadOut()
    {
        using var temp = new Temp();
        var project = temp.Folder("project");
        var outside = temp.Folder("outside");
        var link = Path.Combine(project, "link");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive("This system does not let the test create a link.");
        }

        var guard = new AgentWriteGuard([project], [], [], null);
        Assert.IsNotNull(guard.Check(Path.Combine(link, "a.txt")));
        Assert.IsNull(guard.Check(Path.Combine(project, "a.txt")));
    }

    [TestMethod]
    public async Task TheFileTools_OfASessionWithoutTheShell_RefuseWhatTheGuardRefuses_BeforeAnyPermission()
    {
        using var temp = new Temp();
        var project = temp.Folder("project");
        Directory.CreateDirectory(Path.Combine(project, "src"));
        File.WriteAllText(Path.Combine(project, "src", "plugin.cs"), "class P {}");
        var asked = 0;
        var tools = AgentBuiltInToolFactory.CreateDefaultTools(new AgentBuiltInToolOptions
        {
            ProviderId = ModelProviderIds.OpenAIResponses,
            SessionId = "session-1",
            WorkingDirectory = project,
            OnPermissionRequest = (_, _) => { asked++; return Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)); },
            DisabledTools = new HashSet<string>(["shell_command"], StringComparer.Ordinal),
            WriteGuard = new AgentWriteGuard([project], [], [], null),
        });

        async Task<AgentToolResult> Run(string tool, string arguments)
        {
            var definition = tools.Single(candidate => candidate.Spec.Name == tool);
            using var document = JsonDocument.Parse(arguments);
            return await definition.Handler(new AgentToolInvocation(ModelProviderIds.OpenAIResponses, "session-1", "call-1", tool, document.RootElement.Clone()), CancellationToken.None);
        }

        var plugin = await Run("write_file", """{"path":".alta/plugins/x/plugin.cs","content":"class P {}"}""");
        Assert.IsFalse(plugin.Success);
        StringAssert.Contains(plugin.Error, ".alta");
        Assert.IsFalse(Directory.Exists(Path.Combine(project, ".alta")), "Nothing is written.");
        Assert.IsFalse((await Run("apply_patch", """{"input":"*** Begin Patch\n*** Add File: .git/config\n+[core]\n+\tfsmonitor = sh x\n*** End Patch"}""")).Success);
        Assert.IsFalse((await Run("rename_file_or_dir", """{"old_path":"src","new_path":".alta/plugins/x"}""")).Success, "A folder of its own is not moved there either.");
        Assert.IsFalse((await Run("write_file", $$"""{"path":{{JsonSerializer.Serialize(Path.Combine(temp.Path, "outside.txt"))}},"content":"x"}""")).Success);
        Assert.AreEqual(0, asked, "The permission handler is not asked for what is refused.");

        Assert.IsTrue((await Run("write_file", """{"path":"src/a.txt","content":"x"}""")).Success);
        Assert.AreEqual(1, asked);
    }

    // A folder for one test, outside any link: the guard compares the paths it resolves.
    private sealed class Temp : IDisposable
    {
        public Temp()
        {
            Path = System.IO.Path.Combine(AppContext.BaseDirectory, "agent-write-guard-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Folder(params string[] parts) => Directory.CreateDirectory(System.IO.Path.Combine([Path, .. parts])).FullName;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
