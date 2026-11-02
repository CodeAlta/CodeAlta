using CodeAlta.Catalog.Skills;

namespace CodeAlta.Catalog.Tests;

/// <summary>
/// The folders of skills a user adds for every space, for a space and for a project: what can be added, where they come in
/// the precedence of the roots, how they are saved, and that one that cannot be read never hides the others.
/// </summary>
[TestClass]
public sealed class AddedSkillFoldersTests
{
    private string _root = null!;

    private string Home => Path.Combine(_root, "home");
    private string AltaRoot => Path.Combine(Home, ".alta");

    [TestInitialize]
    public void Initialize() => _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-added-skills-" + Guid.NewGuid().ToString("N"))).FullName;

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
    }

    private static void Skill(string root, string name, string description = "Does it.")
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        File.WriteAllText(Path.Combine(folder, "SKILL.md"), $"---\nname: {name}\ndescription: {description}\n---\n# {name}\n");
    }

    [TestMethod]
    public void Check_RefusesWhatWouldReadFarMoreThanSkills()
    {
        var library = Path.Combine(_root, "library");
        Assert.AreEqual(AddedSkillFolderProblem.None, AddedSkillFolders.Check(library, Home, AltaRoot));
        Assert.AreEqual(AddedSkillFolderProblem.None, AddedSkillFolders.Check(Path.Combine(Home, ".claude", "skills"), Home, AltaRoot),
            "a folder inside the home folder is fine");
        Assert.AreEqual(AddedSkillFolderProblem.Required, AddedSkillFolders.Check("  ", Home, AltaRoot));
        Assert.AreEqual(AddedSkillFolderProblem.NotFullPath, AddedSkillFolders.Check(Path.Combine("relative", "skills"), Home, AltaRoot));
        Assert.AreEqual(AddedSkillFolderProblem.TooLong, AddedSkillFolders.Check(Path.Combine(_root, new string('a', AddedSkillFolders.MaximumPathLength)), Home, AltaRoot));
        Assert.AreEqual(AddedSkillFolderProblem.Disk, AddedSkillFolders.Check(Path.GetPathRoot(_root), Home, AltaRoot));
        Assert.AreEqual(AddedSkillFolderProblem.Home, AddedSkillFolders.Check(Home, Home, AltaRoot));
        Assert.AreEqual(AddedSkillFolderProblem.Home, AddedSkillFolders.Check(_root, Home, AltaRoot), "a folder that holds the home folder");
        Assert.AreEqual(AddedSkillFolderProblem.Home, AddedSkillFolders.Check("~", Home, AltaRoot));
        Assert.AreEqual(AddedSkillFolderProblem.CodeAlta, AddedSkillFolders.Check(AltaRoot, Path.Combine(_root, "other"), AltaRoot));
        Assert.IsNotNull(AddedSkillFolders.Problem(Home, Home, AltaRoot));
    }

    [TestMethod]
    public void Resolve_ReadsTheHomeFolderInATildeAndRefusesARelativePath()
    {
        Assert.AreEqual(Path.Combine(Home, "skills"), AddedSkillFolders.Resolve("~/skills", Home));
        Assert.AreEqual(Path.Combine(Home, "skills"), AddedSkillFolders.Resolve(Path.Combine(Home, "skills") + Path.DirectorySeparatorChar, Home));
        Assert.IsNull(AddedSkillFolders.Resolve("~/skills", null), "without a home folder, a tilde means nothing");
        Assert.IsNull(AddedSkillFolders.Resolve("skills", Home));
        Assert.IsNull(AddedSkillFolders.Resolve("bad\u0001path", Home));
    }

    [TestMethod]
    public void Change_AddsOnceAndRemovesWhatTheListNames()
    {
        var library = Path.Combine(_root, "library");
        var once = AddedSkillFolders.Change([], library, add: true, Home);
        CollectionAssert.AreEqual(new[] { library }, once.ToArray());
        Assert.AreSame(once, AddedSkillFolders.Change(once, library + Path.DirectorySeparatorChar, add: true, Home), "the same folder is not added twice");
        CollectionAssert.AreEqual(new[] { library, Path.Combine(Home, "shared") }, AddedSkillFolders.Change(once, "~/shared", add: true, Home).ToArray(),
            "a tilde is written as the full path");
        Assert.AreEqual(0, AddedSkillFolders.Change(once, library, add: false, Home).Count);
        // A path written by hand that is not a full path is removed as it is written.
        Assert.AreEqual(0, AddedSkillFolders.Change(["relative"], "relative", add: false, Home).Count);
    }

    [TestMethod]
    public void Roots_KeepTheOrderOfEachListBetweenTheKindsOfRoots()
    {
        var first = Path.Combine(_root, "first");
        var second = Path.Combine(_root, "second");
        var roots = AddedSkillFolders.Roots([first, second, first, Home], "work", [second], [first], Home, AltaRoot);
        CollectionAssert.AreEqual(new[] { SkillSourceKind.ProjectFolder, SkillSourceKind.SpaceFolder, SkillSourceKind.UserFolder, SkillSourceKind.UserFolder },
            roots.Select(static root => root.SourceKind).ToArray(), "the home folder is left out, and a folder listed twice is read once");
        CollectionAssert.AreEqual(new[] { SkillRootPrecedence.ProjectFolder, SkillRootPrecedence.SpaceFolder, SkillRootPrecedence.UserFolder, SkillRootPrecedence.UserFolder + 1 },
            roots.Select(static root => root.Precedence).ToArray());
        Assert.AreEqual("space-folder:work:" + second, roots[1].SourceId);
        Assert.AreEqual(SkillScopeKind.Project, roots[0].Scope);
        Assert.AreEqual(SkillScopeKind.User, roots[1].Scope);
    }

    [TestMethod]
    public async Task ASessionReadsTheAddedFolders_TheClosestToItsWorkWinning()
    {
        var project = Directory.CreateDirectory(Path.Combine(_root, "project")).FullName;
        var global = Path.Combine(_root, "global-library");
        var space = Path.Combine(_root, "space-library");
        var ofProject = Path.Combine(_root, "project-library");
        Skill(global, "everywhere", "From the global folder.");
        Skill(global, "shared", "From the global folder.");
        Skill(space, "shared", "From the folder of the space.");
        Skill(ofProject, "shared", "From the folder of the project.");
        Skill(Path.Combine(AltaRoot, "skills"), "mine", "From the user.");
        Skill(global, "mine", "From the global folder, shadowed by the folder of the user.");
        Skill(space, "spaced", "From the folder of the space.");

        var config = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = AltaRoot });
        config.SaveGlobalSkillFolders([global]);
        var filter = new SpaceFilter(SpaceAssignments.Empty, "work") { SkillFolders = [space] };
        var catalog = new SkillCatalog();
        var query = SkillCatalogQuery.ForSession(config, AltaRoot, Home, project, null, filter, [ofProject]) with { IncludeShadowed = false };
        var skills = await catalog.ListAsync(query);
        string From(string name) => skills.Single(skill => skill.Name == name).Description;
        Assert.AreEqual("From the folder of the project.", From("shared"));
        Assert.AreEqual("From the user.", From("mine"));
        Assert.AreEqual("From the global folder.", From("everywhere"));
        Assert.AreEqual(SkillSourceKind.SpaceFolder, skills.Single(skill => skill.Name == "spaced").SourceKind);

        // Another space does not read the folders of this one; without the project, the folder of the space wins.
        var other = await catalog.ListAsync(SkillCatalogQuery.ForSession(config, AltaRoot, Home, null, null, new SpaceFilter(SpaceAssignments.Empty, "other"), null)
            with { IncludeShadowed = false });
        Assert.IsFalse(other.Any(static skill => skill.Name == "spaced"));
        Assert.AreEqual("From the global folder.", other.Single(static skill => skill.Name == "shared").Description);
        var inSpace = await catalog.ListAsync(SkillCatalogQuery.ForSession(config, AltaRoot, Home, null, null, filter, null) with { IncludeShadowed = false });
        Assert.AreEqual("From the folder of the space.", inSpace.Single(static skill => skill.Name == "shared").Description);
    }

    [TestMethod]
    public async Task AFolderThatIsGone_HidesNoOtherSkill_AndASkillAddedLaterIsRead()
    {
        var library = Path.Combine(_root, "library");
        Skill(library, "present");
        var config = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = AltaRoot });
        config.SaveGlobalSkillFolders([Path.Combine(_root, "gone"), library, Path.Combine(_root, "a-file.txt")]);
        File.WriteAllText(Path.Combine(_root, "a-file.txt"), "not a folder");
        var catalog = new SkillCatalog();
        var query = SkillCatalogQuery.ForSession(config, AltaRoot, Home, null, null, null);
        Assert.IsTrue((await catalog.ListAsync(query)).Any(static skill => skill.Name == "present"));
        Assert.AreEqual(AddedSkillFolderState.Missing, AddedSkillFolders.Inspect(Path.Combine(_root, "gone"), Home, AltaRoot).State);
        Assert.AreEqual(AddedSkillFolderState.Missing, AddedSkillFolders.Inspect(Path.Combine(_root, "a-file.txt"), Home, AltaRoot).State);
        Assert.AreEqual(AddedSkillFolderState.Ready, AddedSkillFolders.Inspect(library, Home, AltaRoot).State);
        Assert.AreEqual(AddedSkillFolderState.Invalid, AddedSkillFolders.Inspect(Home, Home, AltaRoot).State);

        Skill(library, "later");
        Assert.IsTrue((await catalog.ListAsync(query)).Any(static skill => skill.Name == "later"), "a skill added to the folder is read at the next listing");
        Directory.Delete(Path.Combine(library, "later"), recursive: true);
        Assert.IsFalse((await catalog.ListAsync(query)).Any(static skill => skill.Name == "later"));
    }

    [TestMethod]
    public async Task AnUnreadableFolder_HidesNoOtherSkill()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("A folder is made unreadable with the permissions of Unix.");
            return;
        }

        var locked = Directory.CreateDirectory(Path.Combine(_root, "locked")).FullName;
        Skill(locked, "hidden");
        var library = Path.Combine(_root, "library");
        Skill(library, "present");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var config = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = AltaRoot });
            config.SaveGlobalSkillFolders([locked, library]);
            var skills = await new SkillCatalog().ListAsync(SkillCatalogQuery.ForSession(config, AltaRoot, Home, null, null, null));
            Assert.IsTrue(skills.Any(static skill => skill.Name == "present"));
            Assert.IsFalse(skills.Any(static skill => skill.Name == "hidden"));
            if (Environment.UserName != "root") Assert.AreEqual(AddedSkillFolderState.Unreadable, AddedSkillFolders.Inspect(locked, Home, AltaRoot).State);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [TestMethod]
    public void TheGlobalList_IsSavedBesideTheDisabledSkills_AndRemovedWhenEmpty()
    {
        var options = new CatalogOptions { GlobalRoot = Directory.CreateDirectory(AltaRoot).FullName };
        File.WriteAllText(options.ConfigPath, "# my settings\n[skills]\ndisabled = [\"noisy\"]\n");
        var config = new CodeAltaConfigStore(options);
        var library = Path.Combine(_root, @"lib's \skills");
        config.SaveGlobalSkillFolders([library]);
        CollectionAssert.AreEqual(new[] { library }, new CodeAltaConfigStore(options).LoadGlobalSkillFolders().ToArray());
        CollectionAssert.AreEqual(new[] { "noisy" }, config.LoadGlobalDisabledSkillNames().ToArray());
        StringAssert.Contains(File.ReadAllText(options.ConfigPath), "# my settings");

        config.SaveGlobalSkillFolders([]);
        Assert.AreEqual(0, config.LoadGlobalSkillFolders().Count);
        Assert.IsFalse(File.ReadAllText(options.ConfigPath).Contains("folders", StringComparison.Ordinal));
        CollectionAssert.AreEqual(new[] { "noisy" }, config.LoadGlobalDisabledSkillNames().ToArray());
        Assert.ThrowsExactly<ArgumentException>(() => config.SaveGlobalSkillFolders(Enumerable.Range(0, AddedSkillFolders.MaximumFolders + 1).Select(index => Path.Combine(_root, $"f{index}"))));
    }

    [TestMethod]
    public void TwoWindowsThatAddAFolderAtOnce_BothKeepTheirs()
    {
        var options = new CatalogOptions { GlobalRoot = Directory.CreateDirectory(AltaRoot).FullName };
        var first = new CodeAltaConfigStore(options);
        var second = new CodeAltaConfigStore(options);
        _ = first.LoadGlobalSkillFolders();
        _ = second.LoadGlobalSkillFolders();
        Assert.IsTrue(first.UpdateGlobalSkillFolders(current => [.. current, "/one"]));
        Assert.IsTrue(second.UpdateGlobalSkillFolders(current => [.. current, "/two"]), "the change is made on the file as it is now");
        CollectionAssert.AreEqual(new[] { "/one", "/two" }, new CodeAltaConfigStore(options).LoadGlobalSkillFolders().ToArray());
        Assert.IsFalse(first.UpdateGlobalSkillFolders(static _ => null));
        Assert.IsFalse(first.UpdateGlobalSkillFolders(static current => current), "nothing is written when nothing changes");
    }

    [TestMethod]
    public async Task TwoFoldersWhoseNamesDifferByTheirCase_AreTwoFolders_WhereTheSystemSaysSo()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            Assert.Inconclusive("The file systems of Windows and macOS do not tell the case of a name apart.");
            return;
        }

        var lower = Path.Combine(_root, "library");
        var upper = Path.Combine(_root, "Library");
        Skill(lower, "lower");
        Skill(upper, "upper");
        var config = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = AltaRoot });
        config.SaveGlobalSkillFolders([lower, upper]);
        var skills = await new SkillCatalog().ListAsync(SkillCatalogQuery.ForSession(config, AltaRoot, Home, null, null, null));
        Assert.IsTrue(skills.Any(static skill => skill.Name == "lower") && skills.Any(static skill => skill.Name == "upper"));
    }

    [TestMethod]
    public void AStrictReading_KnowsTheGlobalList()
    {
        var options = new CatalogOptions { GlobalRoot = Directory.CreateDirectory(AltaRoot).FullName, Config = new CodeAltaConfigOptions { Strict = true } };
        File.WriteAllText(options.ConfigPath, "[skills]\nfolders = [\"/skills\"]\n");
        CollectionAssert.AreEqual(new[] { "/skills" }, new CodeAltaConfigStore(options).LoadGlobalSkillFolders().ToArray());
    }

    [TestMethod]
    public async Task TheListOfAProject_IsInTheUsersFileOfTheProject_AndEditsNothingElse()
    {
        var options = new CatalogOptions { GlobalRoot = Directory.CreateDirectory(AltaRoot).FullName };
        var projects = new ProjectCatalog(options);
        var project = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(_root, "project")).FullName);
        var file = project.SourcePath!;
        var before = File.ReadAllText(file);
        // A path of Windows holds backslashes, one of a user may hold a quote: both are read back as they were written.
        var library = Path.Combine(_root, @"it's a \library");
        Assert.AreEqual(ProjectSpacesStatus.Updated, await projects.UpdateSkillFoldersAsync(project.Id, current => [.. current, library]));
        var after = File.ReadAllText(file);
        StringAssert.Contains(after, "skill_folders:");
        Assert.AreEqual(before.Length, after.Replace(after.Split('\n').First(line => line.StartsWith("skill_folders:", StringComparison.Ordinal)) + "\n", string.Empty).Length,
            "only the line of the list is added");
        var reread = await new ProjectCatalog(options).GetByIdAsync(project.Id);
        CollectionAssert.AreEqual(new[] { library }, reread!.SkillFolders);
        CollectionAssert.AreEqual(new[] { library }, reread.Copy().SkillFolders);
        Assert.AreEqual(ProjectSpacesStatus.Unchanged, await projects.UpdateSkillFoldersAsync(project.Id, current => current));
        Assert.AreEqual(ProjectSpacesStatus.Updated, await projects.UpdateSkillFoldersAsync(project.Id, static _ => []));
        Assert.AreEqual(before, File.ReadAllText(file), "removing the last folder leaves the file as it was");
        Assert.AreEqual(ProjectSpacesStatus.NotFound, await projects.UpdateSkillFoldersAsync(Guid.NewGuid().ToString(), static current => current));
    }

    [TestMethod]
    public async Task TheListOfASpace_IsInTheFileOfTheSpace()
    {
        var options = new CatalogOptions { GlobalRoot = Directory.CreateDirectory(AltaRoot).FullName };
        var spaces = new SpaceCatalog(new ProjectCatalog(options));
        var created = await spaces.CreateAsync("Work");
        var library = Path.Combine(_root, @"work \skills");
        var change = await spaces.UpdateSettingsAsync(created.Space!.Id, settings => settings with { Skills = new SpaceSkills([library]) });
        Assert.AreEqual(SpaceChangeStatus.Ok, change.Status);
        var reread = (await new SpaceCatalog(new ProjectCatalog(options)).LoadAsync()).Single(space => space.Id == created.Space.Id);
        CollectionAssert.AreEqual(new[] { library }, reread.Settings.Skills!.Folders.ToArray());
        var filter = SpaceFilter.Load(new CodeAltaConfigStore(options), created.Space.Id, [reread]);
        CollectionAssert.AreEqual(new[] { library }, filter.SkillFolders.ToArray());
        Assert.AreEqual(0, SpaceFilter.Load(new CodeAltaConfigStore(options), "default", [reread]).SkillFolders.Count);
        Assert.IsNotNull(new SpaceSkills([.. Enumerable.Range(0, AddedSkillFolders.MaximumFolders + 1).Select(index => $"/f{index}")]).Problem());
        Assert.IsNull(new SpaceSkills(["", " "]).Sanitized());
    }
}
