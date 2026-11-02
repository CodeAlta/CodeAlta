using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The folders of skills added on the Skills page: for every space, for the space the window shows and for the selected
/// project, listed with their skills, and taken out of the list without a file of theirs being touched.
/// </summary>
[TestClass]
public sealed class SkillsRpcFolderTests
{
    private const string Epoch = "epoch-1";

    private string _root = null!;
    private ProjectCatalog _projects = null!;
    private ProjectDescriptor _project = null!;
    private SpaceCatalog _spaces = null!;
    private SkillsService _service = null!;

    private string Home => Path.Combine(_root, "home");

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CodeAlta-skill-folders-" + Guid.NewGuid().ToString("N"))).FullName;
        _projects = new ProjectCatalog(new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(Home, ".alta")).FullName });
        _project = await _projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(_root, "project")).FullName);
        _spaces = new SpaceCatalog(_projects);
        // The roots of the user and of the project, without built-in or plugin skills: the listing is what the test writes.
        _service = new SkillsService(_projects, new SkillCatalog([new ProjectCodeAltaSkillRootProvider(), new UserCodeAltaSkillRootProvider()]), Home, Epoch)
        {
            Spaces = _spaces,
        };
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
    }

    private string Library(string name, params string[] skills)
    {
        var library = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        foreach (var skill in skills)
        {
            var folder = Directory.CreateDirectory(Path.Combine(library, skill)).FullName;
            File.WriteAllText(Path.Combine(folder, "SKILL.md"), $"---\nname: {skill}\ndescription: {skill} from {name}\n---\n\n# {skill}\n");
        }

        return library;
    }

    private Task<SkillsMutationResponse> AddAsync(string scope, string path, string? projectId = null, string? spaceId = null)
        => _service.AddFolderAsync(new SkillsFolderRequest(Epoch, projectId, spaceId, scope, path), default);

    private Task<SkillsMutationResponse> RemoveAsync(string scope, string path, string? projectId = null, string? spaceId = null)
        => _service.RemoveFolderAsync(new SkillsFolderRequest(Epoch, projectId, spaceId, scope, path), default);

    [TestMethod]
    public async Task AFolderAddedForEverySpace_IsListedWithItsSkills_AndRemovingItLeavesItsFiles()
    {
        var library = Library("library", "alpha", "beta");
        Assert.AreEqual("ok", (await AddAsync("Global", library)).Status);

        var listing = await _service.ListAsync(new(Epoch, null), default);
        var folder = listing.Folders.Single();
        Assert.AreEqual(("Global", library, library, "ready", 2), (folder.Scope, folder.Path, folder.FullPath, folder.State, folder.Skills));
        var alpha = listing.Skills.Single(static skill => skill.Name == "alpha");
        Assert.AreEqual(("UserFolder", "User", true), (alpha.Source, alpha.Scope, alpha.Enabled));
        Assert.IsNotNull(alpha.Folder, "its folder opens in the code editor");

        var detail = await _service.DetailAsync(new(Epoch, null, null, alpha.Source, alpha.Id), default);
        Assert.AreEqual("ok", detail.Status);
        StringAssert.Contains(detail.Content, "# alpha");
        Assert.AreEqual("ok", (await _service.SetEnabledAsync(new(Epoch, null, "Global", "alpha", false), default)).Status);
        Assert.IsFalse((await _service.ListAsync(new(Epoch, null), default)).Skills.Single(static skill => skill.Name == "alpha").Enabled);
        Assert.AreEqual("read_only", (await _service.DeleteAsync(new(Epoch, null, null, alpha.Source, alpha.Id), default)).Status,
            "a skill of a library is not moved to the trash from here");

        Assert.AreEqual("folder_listed", (await AddAsync("Global", library + Path.DirectorySeparatorChar)).Status);
        Assert.AreEqual("ok", (await RemoveAsync("Global", library)).Status);
        Assert.AreEqual("not_found", (await RemoveAsync("Global", library)).Status);
        var after = await _service.ListAsync(new(Epoch, null), default);
        Assert.AreEqual(0, after.Folders.Count);
        Assert.IsFalse(after.Skills.Any(static skill => skill.Name == "alpha"));
        Assert.IsTrue(File.Exists(Path.Combine(library, "alpha", "SKILL.md")), "removing the folder from the list leaves its files");
    }

    [TestMethod]
    public async Task AFolderThatCannotHoldSkills_IsRefused_AndOneThatIsGoneIsSaid()
    {
        Assert.AreEqual("folder_missing", (await AddAsync("Global", Path.Combine(_root, "nothing"))).Status);
        var home = await AddAsync("Global", Home);
        Assert.AreEqual(("folder_refused", "Home"), (home.Status, home.Message));
        Assert.AreEqual("folder_refused", (await AddAsync("Global", "relative")).Status);
        Assert.AreEqual("folder_refused", (await AddAsync("Global", Path.GetPathRoot(_root)!)).Status);
        Assert.AreEqual("invalid", (await AddAsync("Elsewhere", _root)).Status);
        Assert.AreEqual("invalid", (await AddAsync("Project", Library("library"))).Status, "a folder of a project needs the project");
        Assert.AreEqual("stale_epoch", (await _service.AddFolderAsync(new SkillsFolderRequest("another", null, null, "Global", _root), default)).Status);
        Assert.AreEqual("unavailable", (await new SkillsService().AddFolderAsync(new SkillsFolderRequest(Epoch, null, null, "Global", _root), default)).Status);

        var library = Library("going", "alpha");
        Assert.AreEqual("ok", (await AddAsync("Global", library)).Status);
        Directory.Delete(library, recursive: true);
        var listing = await _service.ListAsync(new(Epoch, null), default);
        Assert.AreEqual("missing", listing.Folders.Single().State);
        Assert.AreEqual("ok", (await RemoveAsync("Global", library)).Status, "a folder that is gone is removed from the list");
    }

    [TestMethod]
    public async Task AFolderAddedForAProject_IsInTheUsersFileOfTheProject_AndListedWithTheProjectAlone()
    {
        var library = Library("project-library", "alpha");
        Assert.AreEqual("ok", (await AddAsync("Project", library, _project.Id)).Status);
        CollectionAssert.AreEqual(new[] { library }, (await new ProjectCatalog(_projects.Options).GetByIdAsync(_project.Id))!.SkillFolders);
        Assert.IsFalse(File.Exists(Path.Combine(_project.ProjectPath, ".alta", "config.toml")), "nothing is written in the repository");

        var withProject = await _service.ListAsync(new(Epoch, _project.Id), default);
        Assert.AreEqual(("Project", 1), (withProject.Folders.Single().Scope, withProject.Folders.Single().Skills));
        var alpha = withProject.Skills.Single(static skill => skill.Name == "alpha");
        Assert.AreEqual(("ProjectFolder", "Project"), (alpha.Source, alpha.Scope));
        Assert.AreEqual("ok", (await _service.DetailAsync(new(Epoch, _project.Id, null, alpha.Source, alpha.Id), default)).Status);
        var editor = _service.Folders!;
        Assert.IsTrue(Desktop.SkillFolder.TryParse(alpha.Folder, out var folder));
        Assert.AreEqual(("ok", Path.Combine(library, "alpha")), await editor.ResolveAsync(folder, default));

        var without = await _service.ListAsync(new(Epoch, null), default);
        Assert.AreEqual(0, without.Folders.Count);
        Assert.IsFalse(without.Skills.Any(static skill => skill.Name == "alpha"));
        Assert.AreEqual("ok", (await RemoveAsync("Project", library, _project.Id)).Status);
        Assert.AreEqual(0, (await new ProjectCatalog(_projects.Options).GetByIdAsync(_project.Id))!.SkillFolders.Count);
    }

    [TestMethod]
    public async Task AFolderAddedForASpace_IsListedInThatSpaceAlone_AndWinsOverTheFoldersOfEverySpace()
    {
        var work = (await _spaces.CreateAsync("Work")).Space!;
        var global = Library("global-library", "shared");
        var spaced = Library("work-library", "shared", "only-work");
        Assert.AreEqual("ok", (await AddAsync("Global", global)).Status);
        Assert.AreEqual("ok", (await AddAsync("Space", spaced, spaceId: work.Id)).Status);
        Assert.AreEqual("folder_listed", (await AddAsync("Space", spaced, spaceId: work.Id)).Status);

        var inWork = await _service.ListAsync(new(Epoch, null, work.Id), default);
        CollectionAssert.AreEqual(new[] { "Space", "Global" }, inWork.Folders.Select(static folder => folder.Scope).ToArray());
        var winner = inWork.Skills.Single(static skill => skill.Name == "shared" && !skill.Shadowed);
        Assert.AreEqual("SpaceFolder", winner.Source);
        Assert.IsTrue(inWork.Skills.Any(static skill => skill.Name == "shared" && skill.Shadowed && skill.Source == "UserFolder"));
        var onlyWork = inWork.Skills.Single(static skill => skill.Name == "only-work");
        Assert.AreEqual("ok", (await _service.DetailAsync(new(Epoch, null, null, onlyWork.Source, onlyWork.Id, work.Id), default)).Status);
        Assert.AreEqual("not_found", (await _service.DetailAsync(new(Epoch, null, null, onlyWork.Source, onlyWork.Id), default)).Status,
            "the default space does not have it");

        var inDefault = await _service.ListAsync(new(Epoch, null), default);
        Assert.AreEqual("Global", inDefault.Folders.Single().Scope);
        Assert.IsFalse(inDefault.Skills.Any(static skill => skill.Name == "only-work"));

        // The code editor finds the folder of a skill of a space with the space its id names: another space may give the
        // same name to another skill, and each opens its own.
        Assert.IsTrue(Desktop.SkillFolder.TryParse(onlyWork.Folder, out var folder));
        Assert.AreEqual(work.Id, folder.SpaceId);
        var home = (await _spaces.CreateAsync("Home")).Space!;
        var homeLibrary = Library("home-library", "only-work");
        Assert.AreEqual("ok", (await AddAsync("Space", homeLibrary, spaceId: home.Id)).Status);
        var inHome = (await _service.ListAsync(new(Epoch, null, home.Id), default)).Skills.Single(static skill => skill.Name == "only-work");
        Assert.AreNotEqual(onlyWork.Folder, inHome.Folder);
        Assert.IsTrue(Desktop.SkillFolder.TryParse(inHome.Folder, out var homeFolder));
        Assert.AreEqual(("ok", Path.Combine(homeLibrary, "only-work")), await _service.Folders!.ResolveAsync(homeFolder, default));
        Assert.AreEqual(("ok", Path.Combine(spaced, "only-work")), await _service.Folders!.ResolveAsync(folder, default));
        Assert.IsFalse(Desktop.SkillFolder.TryParse("skill:global:SpaceFolder:only-work", out _), "a skill of a space is named with its space");

        Assert.AreEqual("ok", (await RemoveAsync("Space", spaced, spaceId: work.Id)).Status);
        Assert.IsNull((await _spaces.LoadAsync()).Single(space => space.Id == work.Id).Settings.Skills);
        Assert.AreEqual("unavailable", (await new SkillsService(_projects, new SkillCatalog(), Home, Epoch).AddFolderAsync(
            new SkillsFolderRequest(Epoch, null, work.Id, "Space", spaced), default)).Status, "without spaces, no folder is added for a space");
    }
}
