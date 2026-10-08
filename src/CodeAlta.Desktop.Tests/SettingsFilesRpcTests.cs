using CodeAlta.Catalog;
using CodeAlta.Catalog.PullRequests;
using CodeAlta.Catalog.Skills;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The files and the folders behind the pages of Settings: where each one is, how the code editor opens it, and
/// what a tab that was opened for one file can reach.
/// </summary>
[TestClass]
public sealed class SettingsFilesRpcTests
{
    private const string Epoch = "epoch-1";

    [TestMethod]
    public async Task WithoutAnOwnedHost_NothingIsListedOrOpened_AndAnotherEpochIsRefused()
    {
        var unavailable = new SettingsFilesService();
        Assert.AreEqual("unavailable", (await unavailable.ListAsync(new(Epoch, null, "config"), default)).Status);
        Assert.AreEqual("unavailable", (await unavailable.OpenAsync(new(Epoch, null, "config", "Global"), default)).Status);
        Assert.AreEqual("unavailable", (await unavailable.RevealAsync(new(Epoch, null, "config", "Global"), default)).Status);

        using var fixture = await Fixture.CreateAsync();
        Assert.AreEqual("stale_epoch", (await fixture.Service.ListAsync(new("another", null, "config"), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Service.OpenAsync(new("another", null, "config", "Global"), default)).Status);
        Assert.AreEqual("unknown_project", (await fixture.Service.ListAsync(new(Epoch, "no-such-project", "config"), default)).Status);
        Assert.IsEmpty(fixture.Shown);
    }

    [TestMethod]
    public async Task TheConfigurationFileOfTheUser_OpensInATabThatHasThatFileAlone()
    {
        using var fixture = await Fixture.CreateAsync();
        var config = fixture.Write(fixture.Global, "config.toml", "[chat]\n");
        var secret = fixture.Write(fixture.Global, "auth/credentials.json", "{ \"token\": \"secret\" }");
        fixture.Write(fixture.Global, "mcp.json", "{ \"mcpServers\": {} }");

        var listed = await fixture.Service.ListAsync(new(Epoch, null, "config"), default);
        Assert.AreEqual("ok", listed.Status);
        Assert.AreEqual(new SettingsFileLocation("config", "Global", null, config, false, true, true), listed.Locations.Single());

        Assert.AreEqual("ok", (await fixture.Service.OpenAsync(new(Epoch, null, "config", "Global"), default)).Status);
        var shown = fixture.Shown.Single();
        Assert.AreEqual(("config.toml", "config.toml", fixture.Global), (shown.Path, shown.Name, shown.Root));
        StringAssert.StartsWith(shown.ProjectId, DiskFolders.FilePrefix);
        var tab = shown.ProjectId;

        // The tab lists, reads and writes that file.
        var files = fixture.Files;
        var folder = (await files.ListAsync(new(Epoch, tab, [new("", null)], true), default)).Folders.Single();
        CollectionAssert.AreEqual(new[] { "config.toml" }, folder.Entries.Select(static entry => entry.Name).ToArray());
        var read = await files.ReadAsync(new(Epoch, tab, "config.toml"), default);
        Assert.AreEqual(("ok", "[chat]\n", false), (read.Status, read.Content, read.ReadOnly));
        Assert.AreEqual("ok", (await files.WriteAsync(new(Epoch, tab, "config.toml", "[chat]\n# edited\n", read.Revision, false), default)).Status);
        Assert.AreEqual("[chat]\n# edited\n", File.ReadAllText(config));
        Assert.AreEqual("ok", (await files.StatAsync(new(Epoch, tab, ["config.toml"]), default)).Files.Single().Status);
        Assert.AreEqual("ok", (await files.RevealAsync(new(Epoch, tab, ""), default)).Status);
        Assert.AreEqual(config, fixture.Revealed.Single(), "The folder of such a tab is shown by its file.");

        // Nothing else of the folder is reached through it: not the credentials beside it, nor another file.
        foreach (var other in new[] { "auth/credentials.json", "mcp.json", "auth" })
        {
            Assert.AreEqual("not_found", (await files.ReadAsync(new(Epoch, tab, other), default)).Status, other);
            Assert.AreEqual("not_found", (await files.WriteAsync(new(Epoch, tab, other, "x", null, true), default)).Status, other);
            Assert.AreEqual("not_found", (await files.StatAsync(new(Epoch, tab, [other]), default)).Files.Single().Status, other);
            Assert.AreEqual("not_found", (await files.ImageAsync(new(Epoch, tab, other), default)).Status, other);
            Assert.AreEqual("not_found", (await files.RevealAsync(new(Epoch, tab, other), default)).Status, other);
        }

        Assert.AreEqual("not_found", (await files.ListAsync(new(Epoch, tab, [new("auth", null)], true), default)).Folders.Single().Status);
        Assert.AreEqual("outside_root", (await files.ReadAsync(new(Epoch, tab, "../.alta/auth/credentials.json"), default)).Status);
        // Nothing is created, renamed or removed in it, the file itself included.
        Assert.AreEqual("read_only", (await files.CreateAsync(new(Epoch, tab, "new.toml", false), default)).Status);
        Assert.AreEqual("read_only", (await files.RenameAsync(new(Epoch, tab, "config.toml", "other.toml"), default)).Status);
        Assert.AreEqual("read_only", (await files.DeleteAsync(new(Epoch, tab, "config.toml", true), default)).Status);
        var found = new List<ProjectFileSearchEvent>();
        await foreach (var result in files.SearchAsync(new(Epoch, tab, "secret", false, false, false, null, null), default)) found.Add(result);
        Assert.AreEqual(("done", "ok", 0), (found.Single().Kind, found.Single().Status, found.Single().MatchCount));
        Assert.AreEqual("{ \"token\": \"secret\" }", File.ReadAllText(secret));
        Assert.IsTrue(File.Exists(config));

        // An id of that kind that this host did not give names nothing.
        var unknown = DiskFolders.FilePrefix + new string('0', 24);
        Assert.AreEqual("not_found", (await files.ReadAsync(new(Epoch, unknown, "config.toml"), default)).Status);
        Assert.AreEqual("unknown_project", (await files.ListAsync(new(Epoch, unknown, [new("", null)], true), default)).Status);
    }

    [TestMethod]
    public async Task AFileOfAProject_OpensInTheCodeEditorOfThatProject_AndAMissingOneIsListedWithoutAWayToOpenIt()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Write(fixture.Global, "config.toml", "");
        var project = fixture.Project.Id;
        var local = Path.Combine(fixture.ProjectPath, ".alta", "config.toml");

        var listed = await fixture.Service.ListAsync(new(Epoch, project, "config"), default);
        Assert.AreEqual(new SettingsFileLocation("config", "Project", null, local, false, false, false), listed.Locations[1]);
        Assert.AreEqual("not_found", (await fixture.Service.OpenAsync(new(Epoch, project, "config", "Project"), default)).Status);
        Assert.AreEqual("not_found", (await fixture.Service.RevealAsync(new(Epoch, project, "config", "Project"), default)).Status);
        Assert.AreEqual("invalid", (await fixture.Service.OpenAsync(new(Epoch, null, "config", "Project"), default)).Status, "The file of a project is not named without that project.");

        fixture.Write(fixture.ProjectPath, ".alta/config.toml", "[plugins.notes]\nenabled = true\n");
        Assert.IsTrue((await fixture.Service.ListAsync(new(Epoch, project, "config"), default)).Locations[1] is { Exists: true, CanOpen: true });
        Assert.AreEqual("ok", (await fixture.Service.OpenAsync(new(Epoch, project, "config", "Project"), default)).Status);
        Assert.AreEqual(new ProjectFileShowEvent(project, ".alta/config.toml", null, null), fixture.Shown.Single());
        Assert.AreEqual("ok", (await fixture.Service.RevealAsync(new(Epoch, project, "config", "Project"), default)).Status);
        Assert.AreEqual(local, fixture.Revealed.Single());
    }

    [TestMethod]
    public async Task TheFoldersOfSkillsAndPlugins_AreListedBeforeTheyExist_AndOpeningOneOfCodeAltaCreatesIt()
    {
        using var fixture = await Fixture.CreateAsync();
        var project = fixture.Project.Id;
        var userSkills = Path.Combine(fixture.Global, "skills");
        var common = Directory.CreateDirectory(Path.Combine(fixture.Home, ".agents", "skills")).FullName;

        var skills = (await fixture.Service.ListAsync(new(Epoch, project, "skills"), default)).Locations;
        CollectionAssert.AreEqual(new[]
        {
            new SettingsFileLocation("skills", "Global", "UserAlta", userSkills, true, false, true),
            new SettingsFileLocation("skills", "Global", "UserCommon", common, true, true, true),
            new SettingsFileLocation("skills", "Project", "ProjectAlta", Path.Combine(fixture.ProjectPath, ".alta", "skills"), true, false, true),
        }, skills.ToArray(), "A folder of another tool is listed once it is there.");

        Assert.AreEqual("ok", (await fixture.Service.OpenAsync(new(Epoch, project, "skills", "Global", "UserAlta"), default)).Status);
        Assert.IsTrue(Directory.Exists(userSkills));
        var shown = fixture.Shown.Single();
        Assert.AreEqual((null, "skills", userSkills), (shown.Path, shown.Name, shown.Root));
        Assert.IsTrue(DiskFolders.IsId(shown.ProjectId) && !DiskFolders.IsFixed(shown.ProjectId), "The folder of the skills of the user is one to work in.");
        // A skill is created in it through the tab.
        Assert.AreEqual("ok", (await fixture.Files.CreateAsync(new(Epoch, shown.ProjectId, "notes/SKILL.md", false), default)).Status);
        Assert.IsTrue(File.Exists(Path.Combine(userSkills, "notes", "SKILL.md")));

        // A folder that is not one of CodeAlta is never created; one that no page lists is not opened.
        Assert.AreEqual("invalid", (await fixture.Service.OpenAsync(new(Epoch, project, "skills", "Global", "UserCopilot"), default)).Status);
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Home, ".copilot")));
        Assert.AreEqual("invalid", (await fixture.Service.OpenAsync(new(Epoch, project, "skills", "Global", "Builtin"), default)).Status);
        Assert.AreEqual("invalid", (await fixture.Service.OpenAsync(new(Epoch, project, "elsewhere", "Global"), default)).Status);

        fixture.Shown.Clear();
        var plugins = (await fixture.Service.ListAsync(new(Epoch, project, "plugins"), default)).Locations;
        CollectionAssert.AreEqual(new[] { Path.Combine(fixture.Global, "plugins"), Path.Combine(fixture.ProjectPath, ".alta", "plugins") }, plugins.Select(static location => location.Path).ToArray());
        Assert.AreEqual("ok", (await fixture.Service.OpenAsync(new(Epoch, project, "plugins", "Project"), default)).Status);
        Assert.AreEqual((Path.Combine(fixture.ProjectPath, ".alta", "plugins"), "plugins"), (fixture.Shown.Single().Root, fixture.Shown.Single().Name));
        Assert.AreEqual(Path.Combine(fixture.Global, "color-schemes"), (await fixture.Service.ListAsync(new(Epoch, null, "colorSchemes"), default)).Locations.Single().Path);
    }

    [TestMethod]
    public async Task APrompt_OpensInTheFolderOfPromptsItIsReadFrom_AndAShippedOneIsOnlyRead()
    {
        using var fixture = await Fixture.CreateAsync();
        var project = fixture.Project.Id;
        var mine = fixture.Write(fixture.Global, "prompts/agents/reviewer.prompt.md", "---\nname: \"Reviewer\"\n---\nReview the change.\n");
        var local = fixture.Write(fixture.ProjectPath, ".alta/prompts/system/house.system-prompt.md", "House rules\n");

        var folders = (await fixture.Service.ListAsync(new(Epoch, project, "prompts"), default)).Locations;
        CollectionAssert.AreEqual(new[]
        {
            new SettingsFileLocation("prompts", "Global", "CodeAlta", Path.Combine(fixture.Global, "prompts"), true, true, true),
            new SettingsFileLocation("prompts", "Project", "CodeAlta", Path.Combine(fixture.ProjectPath, ".alta", "prompts"), true, true, true),
            new SettingsFileLocation("prompts", "BuiltIn", "CodeAlta", fixture.ShippedPrompts, true, true, true, true),
        }, folders.ToArray());

        // The page reads the path of a prompt with its text.
        var document = (await fixture.Prompts.ReadAsync(new(Epoch, project, "Agent", "Global", "reviewer"), default)).Prompt!;
        Assert.AreEqual(mine, document.File);
        Assert.AreEqual(Path.Combine(fixture.ShippedPrompts, "agents", "default.prompt.md"),
            (await fixture.Prompts.ReadAsync(new(Epoch, null, "Agent", "BuiltIn", "default"), default)).Prompt!.File);

        Assert.AreEqual("ok", (await fixture.Service.OpenAsync(new(Epoch, project, "prompt", "Global", "reviewer", "Agent"), default)).Status);
        var shown = fixture.Shown.Single();
        Assert.AreEqual(("agents/reviewer.prompt.md", "prompts", Path.Combine(fixture.Global, "prompts")), (shown.Path, shown.Name, shown.Root));
        Assert.IsFalse(DiskFolders.IsFixed(shown.ProjectId));

        // A prompt of a project is a file of that project.
        fixture.Shown.Clear();
        Assert.AreEqual("ok", (await fixture.Service.OpenAsync(new(Epoch, project, "prompt", "Project", "house", "System"), default)).Status);
        Assert.AreEqual(new ProjectFileShowEvent(project, ".alta/prompts/system/house.system-prompt.md", null, null), fixture.Shown.Single());
        Assert.IsTrue(File.Exists(local));

        // What ships with the application is read, and not written through the tab.
        fixture.Shown.Clear();
        Assert.AreEqual("ok", (await fixture.Service.OpenAsync(new(Epoch, null, "prompt", "BuiltIn", "default", "Agent"), default)).Status);
        shown = fixture.Shown.Single();
        Assert.AreEqual(("agents/default.prompt.md", fixture.ShippedPrompts), (shown.Path, shown.Root));
        StringAssert.StartsWith(shown.ProjectId, DiskFolders.ViewPrefix);
        var read = await fixture.Files.ReadAsync(new(Epoch, shown.ProjectId, "agents/default.prompt.md"), default);
        Assert.AreEqual(("ok", true), (read.Status, read.ReadOnly));
        Assert.AreEqual("read_only", (await fixture.Files.WriteAsync(new(Epoch, shown.ProjectId, "agents/default.prompt.md", "changed", read.Revision, false), default)).Status);
        Assert.AreEqual("read_only", (await fixture.Files.CreateAsync(new(Epoch, shown.ProjectId, "agents/more.prompt.md", false), default)).Status);
        Assert.AreEqual("read_only", (await fixture.Files.DeleteAsync(new(Epoch, shown.ProjectId, "agents/default.prompt.md", true), default)).Status);

        // A prompt that is not listed, or an id that is no prompt id, opens nothing.
        fixture.Shown.Clear();
        foreach (var request in new SettingsFileRequest[]
        {
            new(Epoch, project, "prompt", "Global", "missing", "Agent"), new(Epoch, project, "prompt", "Global", "../reviewer", "Agent"),
            new(Epoch, project, "prompt", "Global", "reviewer", "System"), new(Epoch, project, "prompt", "Elsewhere", "reviewer", "Agent"),
            new(Epoch, project, "prompt", "Global", "reviewer", "Other"), new(Epoch, project, "prompt", "Global", null, "Agent"),
        })
        {
            Assert.AreEqual("invalid", (await fixture.Service.OpenAsync(request, default)).Status, request.ToString());
        }

        Assert.IsEmpty(fixture.Shown);
    }

    [TestMethod]
    public async Task TheFilesOfMcpServersAndOfPullRequests_AreListedAndOpened()
    {
        using var fixture = await Fixture.CreateAsync();
        var project = fixture.Project.Id;
        var global = McpConfigDiscoveryPath(fixture.Global);
        fixture.Write(fixture.Global, "mcp.json", "{ \"mcpServers\": { \"docs\": { \"command\": \"docs\" } } }");
        var shared = fixture.Write(fixture.ProjectPath, ".mcp.json", "{ \"mcpServers\": { \"shared\": { \"command\": \"shared\" } } }");

        var files = (await fixture.Service.ListAsync(new(Epoch, project, "mcp"), default)).Locations;
        Assert.AreEqual(new SettingsFileLocation("mcp", "Global", "CodeAlta", global, false, true, true), files[0]);
        Assert.AreEqual(new SettingsFileLocation("mcp", "Project", "CodeAlta", Path.Combine(fixture.ProjectPath, ".alta", "mcp.json"), false, false, false), files[1]);
        Assert.IsTrue(files.Any(file => file is { Scope: "Project", Id: "Common", Exists: true } && file.Path == shared), "The file of another tool is listed when it is there.");
        Assert.IsFalse((await fixture.Service.ListAsync(new(Epoch, null, "mcp"), default)).Locations.Any(static file => file.Scope == "Project"));

        // The file of the user is beside the credentials of ~/.alta: a tab that has it alone.
        Assert.AreEqual("ok", (await fixture.Service.OpenAsync(new(Epoch, project, "mcp", "Global", "CodeAlta"), default)).Status);
        StringAssert.StartsWith(fixture.Shown.Single().ProjectId, DiskFolders.FilePrefix);
        Assert.AreEqual("mcp.json", fixture.Shown.Single().Path);
        fixture.Shown.Clear();
        Assert.AreEqual("ok", (await fixture.Service.OpenAsync(new(Epoch, project, "mcp", "Project", "Common"), default)).Status);
        Assert.AreEqual(new ProjectFileShowEvent(project, ".mcp.json", null, null), fixture.Shown.Single());
        Assert.AreEqual("not_found", (await fixture.Service.OpenAsync(new(Epoch, project, "mcp", "Project", "CodeAlta"), default)).Status);
        Assert.AreEqual("invalid", (await fixture.Service.OpenAsync(new(Epoch, project, "mcp", "Project", "Vscode"), default)).Status, "A file that is not there is not one of the page.");

        fixture.Shown.Clear();
        var kind = fixture.PullRequests.Save(null, "release", "Release", null, "Write the release notes.");
        var folders = (await fixture.Service.ListAsync(new(Epoch, project, "pullRequests"), default)).Locations;
        CollectionAssert.AreEqual(new[] { fixture.PullRequests.GlobalFolder, PullRequestPromptCatalog.ProjectFolder(fixture.ProjectPath) }, folders.Select(static folder => folder.Path).ToArray());
        Assert.AreEqual("ok", (await fixture.Service.OpenAsync(new(Epoch, null, "pullRequest", "global", "release"), default)).Status);
        Assert.AreEqual((Path.GetFileName(kind.Path), fixture.PullRequests.GlobalFolder), (fixture.Shown.Single().Path, fixture.Shown.Single().Root));
        Assert.AreEqual("invalid", (await fixture.Service.OpenAsync(new(Epoch, null, "pullRequest", "global", "default"), default)).Status, "What ships with CodeAlta is no file.");
        Assert.AreEqual("invalid", (await fixture.Service.OpenAsync(new(Epoch, null, "pullRequest", "project", "release"), default)).Status);

        // No window is there to show the file.
        fixture.Watching.Dispose();
        Assert.AreEqual("failed", (await fixture.Service.OpenAsync(new(Epoch, null, "pullRequest", "global", "release"), default)).Status);
    }

    private static string McpConfigDiscoveryPath(string global) => Path.Combine(global, "mcp.json");

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;

        private Fixture(string root, ProjectCatalog projects, ProjectDescriptor project)
        {
            _root = root;
            Projects = projects;
            Project = project;
            Write(Path.Combine(root, "app"), "content/prompts/agents/default.prompt.md", "---\nname: \"Default\"\n---\nBuilt-in body\n");
            Write(Path.Combine(root, "app"), "content/prompts/system/default.system-prompt.md", "Built-in system\n");
            var folders = new DiskFolders();
            var view = new DesktopEditorView();
            Watching = view.Watch(Shown.Add);
            Prompts = new AgentPromptsService(projects, Epoch, Path.Combine(root, "app"), Home);
            PullRequests = new PullRequestPromptCatalog(projects.Options);
            Files = new ProjectFilesService(projects, Epoch, view: view, reveal: path => { Revealed.Add(path); return true; }, folders: folders);
            Service = new SettingsFilesService(projects, Epoch, folders, view, Prompts, new SkillsService(projects, new SkillCatalog(), Home, Epoch),
                new McpServersService(projects, Epoch, Home), PullRequests, path => { Revealed.Add(path); return true; });
        }

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "CodeAlta-settings-files-" + Guid.NewGuid().ToString("N"));
            // The home of the user holds the folder of CodeAlta, as it does for a real profile.
            var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
            var projects = new ProjectCatalog(new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(home, ".alta")).FullName });
            var project = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "project")).FullName);
            return new Fixture(root, projects, project);
        }

        public ProjectCatalog Projects { get; }
        public ProjectDescriptor Project { get; }
        public SettingsFilesService Service { get; }
        public ProjectFilesService Files { get; }
        public AgentPromptsService Prompts { get; }
        public PullRequestPromptCatalog PullRequests { get; }
        public IDisposable Watching { get; }
        public List<ProjectFileShowEvent> Shown { get; } = [];
        public List<string> Revealed { get; } = [];
        public string ProjectPath => Project.ProjectPath;
        public string Global => Projects.Options.GlobalRoot;
        public string Home => Path.Combine(_root, "home");
        public string ShippedPrompts => Path.Combine(_root, "app", "content", "prompts");

        public string Write(string folder, string relative, string text)
        {
            var path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }
}
