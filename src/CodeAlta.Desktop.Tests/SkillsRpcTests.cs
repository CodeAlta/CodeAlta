using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class SkillsRpcTests
{
    private const string Epoch = "epoch-1";

    [TestMethod]
    public async Task WithoutAnOwnedHost_EveryOperationIsUnavailable_AndAnotherEpochIsRefused()
    {
        var unavailable = new SkillsService();
        Assert.AreEqual("unavailable", (await unavailable.ListAsync(new(Epoch, null), default)).Status);
        Assert.AreEqual("unavailable", (await unavailable.SetEnabledAsync(new(Epoch, null, "Global", "alpha", false), default)).Status);
        Assert.AreEqual("unavailable", (await unavailable.SetAllEnabledAsync(new(Epoch, null, "Global", ["alpha"], false), default)).Status);
        Assert.AreEqual("unavailable", (await unavailable.CreateAsync(new(Epoch, null, "Global", "new-skill", "A skill"), default)).Status);

        using var fixture = await Fixture.CreateAsync();
        Assert.AreEqual("stale_epoch", (await fixture.Service.ListAsync(new("another", null), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Service.SetEnabledAsync(new(null, null, "Global", "alpha", false), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Service.SetAllEnabledAsync(new("another", null, "Global", ["alpha"], false), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Service.CreateAsync(new("another", null, "Global", "new-skill", "A skill"), default)).Status);
        Assert.IsFalse(File.Exists(fixture.GlobalConfig), "Refused requests must not write.");
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.GlobalRoot, "skills", "new-skill")));
    }

    [TestMethod]
    public async Task List_ReturnsUserSkillsAndTheProjectSkillsOfTheNamedProject()
    {
        using var fixture = await Fixture.CreateAsync();
        var global = await fixture.Service.ListAsync(new(Epoch, null), default);
        Assert.AreEqual("ok", global.Status);
        Assert.AreEqual(0, global.Omitted);
        var alpha = global.Skills.Single();
        Assert.AreEqual("alpha", alpha.Name);
        Assert.AreEqual("Alpha workflow", alpha.Description);
        Assert.AreEqual("UserAlta", alpha.Source);
        Assert.AreEqual("User", alpha.Scope);
        Assert.IsTrue(alpha is { Enabled: true, EnabledGlobal: true, EnabledProject: true, Valid: true, Shadowed: false });

        var scoped = await fixture.Service.ListAsync(new(Epoch, fixture.Project.Id), default);
        Assert.AreEqual(fixture.Project.Id, scoped.ProjectId);
        CollectionAssert.AreEquivalent(new[] { "alpha", "beta" }, scoped.Skills.Select(skill => skill.Name).ToArray());
        var beta = scoped.Skills.Single(skill => skill.Name == "beta");
        Assert.AreEqual("ProjectAlta", beta.Source);
        Assert.AreEqual("Project", beta.Scope);
    }

    [TestMethod]
    public async Task SetEnabled_WritesTheNamedConfigurationAndIsReflectedByTheListing()
    {
        using var fixture = await Fixture.CreateAsync();
        var project = fixture.Project.Id;
        var disabled = await fixture.Service.SetEnabledAsync(new(Epoch, project, "Global", "alpha", false), default);
        Assert.AreEqual("ok", disabled.Status, disabled.Message);
        Assert.AreEqual(1, disabled.Changed);
        StringAssert.Contains(File.ReadAllText(fixture.GlobalConfig), "alpha");
        Assert.IsFalse(File.Exists(fixture.ProjectConfig));
        var alpha = (await fixture.Service.ListAsync(new(Epoch, project), default)).Skills.Single(skill => skill.Name == "alpha");
        Assert.IsTrue(alpha is { Enabled: false, EnabledGlobal: false, EnabledProject: true });
        Assert.AreEqual(0, (await fixture.Service.SetEnabledAsync(new(Epoch, project, "Global", "alpha", false), default)).Changed);

        Assert.AreEqual("ok", (await fixture.Service.SetEnabledAsync(new(Epoch, project, "Project", "beta", false), default)).Status);
        StringAssert.Contains(File.ReadAllText(fixture.ProjectConfig), "beta");
        Assert.IsFalse(File.ReadAllText(fixture.GlobalConfig).Contains("beta", StringComparison.Ordinal));
        var beta = (await fixture.Service.ListAsync(new(Epoch, project), default)).Skills.Single(skill => skill.Name == "beta");
        Assert.IsTrue(beta is { Enabled: false, EnabledGlobal: true, EnabledProject: false });

        Assert.AreEqual(1, (await fixture.Service.SetEnabledAsync(new(Epoch, project, "Global", "alpha", true), default)).Changed);
        Assert.IsTrue((await fixture.Service.ListAsync(new(Epoch, null), default)).Skills.Single().Enabled);
    }

    [TestMethod]
    public async Task SetAllEnabled_ChangesEveryListedNameInOneScopeOrNoneWhenOneIsInvalid()
    {
        using var fixture = await Fixture.CreateAsync();
        var project = fixture.Project.Id;
        var refused = await fixture.Service.SetAllEnabledAsync(new(Epoch, project, "Global", ["alpha", "Not A Skill"], false), default);
        Assert.AreEqual("invalid", refused.Status);
        Assert.IsFalse(string.IsNullOrWhiteSpace(refused.Message));
        Assert.IsFalse(refused.Message!.Contains("Parameter", StringComparison.Ordinal), refused.Message);
        Assert.IsFalse(File.Exists(fixture.GlobalConfig), "No name is changed when one is invalid.");

        var disabled = await fixture.Service.SetAllEnabledAsync(new(Epoch, project, "Global", ["alpha", "beta"], false), default);
        Assert.AreEqual("ok", disabled.Status, disabled.Message);
        Assert.AreEqual(2, disabled.Changed);
        Assert.IsTrue((await fixture.Service.ListAsync(new(Epoch, project), default)).Skills.All(skill => skill is { Enabled: false, EnabledGlobal: false }));
        var enabled = await fixture.Service.SetAllEnabledAsync(new(Epoch, project, "Global", ["alpha", "beta"], true), default);
        Assert.AreEqual(2, enabled.Changed);
        Assert.IsTrue((await fixture.Service.ListAsync(new(Epoch, project), default)).Skills.All(skill => skill.Enabled));

        Assert.AreEqual("invalid", (await fixture.Service.SetAllEnabledAsync(new(Epoch, project, "Global", [], false), default)).Status);
        Assert.AreEqual("invalid", (await fixture.Service.SetAllEnabledAsync(new(Epoch, project, "Global", null, false), default)).Status);
        Assert.AreEqual("invalid", (await fixture.Service.SetAllEnabledAsync(new(Epoch, project, "Both", ["alpha"], false), default)).Status);
        Assert.AreEqual("invalid", (await fixture.Service.SetEnabledAsync(new(Epoch, null, "Project", "alpha", false), default)).Status, "The project scope requires a project.");
        Assert.AreEqual("invalid", (await fixture.Service.SetEnabledAsync(new(Epoch, null, "Global", null, false), default)).Status);
    }

    [TestMethod]
    public async Task Create_ScaffoldsASkillInTheNamedScopeAndRefusesAnExistingOrInvalidOne()
    {
        using var fixture = await Fixture.CreateAsync();
        var project = fixture.Project.Id;
        var created = await fixture.Service.CreateAsync(new(Epoch, null, "Global", "release-notes", "Writes release notes"), default);
        Assert.AreEqual("ok", created.Status, created.Message);
        Assert.AreEqual("release-notes", created.Name);
        StringAssert.Contains(File.ReadAllText(Path.Combine(fixture.GlobalRoot, "skills", "release-notes", "SKILL.md")), "Writes release notes");
        var listed = (await fixture.Service.ListAsync(new(Epoch, null), default)).Skills.Single(skill => skill.Name == "release-notes");
        Assert.AreEqual("UserAlta", listed.Source);
        Assert.AreEqual("Writes release notes", listed.Description);

        var conflict = await fixture.Service.CreateAsync(new(Epoch, null, "Global", "release-notes", "Again"), default);
        Assert.AreEqual("conflict", conflict.Status);
        Assert.IsNull(conflict.Message, "A conflict must not name the folder.");

        Assert.AreEqual("ok", (await fixture.Service.CreateAsync(new(Epoch, project, "Project", "local-skill", "Project only"), default)).Status);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.ProjectPath, ".alta", "skills", "local-skill", "SKILL.md")));
        Assert.AreEqual("ProjectAlta", (await fixture.Service.ListAsync(new(Epoch, project), default)).Skills.Single(skill => skill.Name == "local-skill").Source);

        foreach (var request in new SkillsCreateRequest[]
        {
            new(Epoch, null, "Global", "Bad Name", "Description"), new(Epoch, null, "Global", "../escape", "Description"),
            new(Epoch, null, "Global", "valid-name", " "), new(Epoch, null, "Global", null, "Description"),
            new(Epoch, null, "Global", "valid-name", null), new(Epoch, null, "Project", "valid-name", "Description"),
            new(Epoch, null, "Builtin", "valid-name", "Description"),
        })
        {
            var refused = await fixture.Service.CreateAsync(request, default);
            Assert.AreEqual("invalid", refused.Status, request.ToString());
            Assert.IsFalse(string.IsNullOrWhiteSpace(refused.Message));
            Assert.IsTrue(refused.Message!.Length <= 512);
        }

        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.GlobalRoot, "skills", "valid-name")));
    }

    [TestMethod]
    public async Task ProjectScope_IsResolvedThroughTheCatalog_AndUnreadableConfigurationIsLeftUntouched()
    {
        using var fixture = await Fixture.CreateAsync();
        Assert.AreEqual("unknown_project", (await fixture.Service.ListAsync(new(Epoch, Guid.NewGuid().ToString("D")), default)).Status);
        Assert.AreEqual("unknown_project", (await fixture.Service.SetEnabledAsync(new(Epoch, fixture.ProjectPath, "Project", "beta", false), default)).Status, "A path is not a project id.");
        Assert.AreEqual("unknown_project", (await fixture.Service.CreateAsync(new(Epoch, "missing", "Project", "valid-name", "Description"), default)).Status);

        File.WriteAllText(fixture.GlobalConfig, "[skills\nbroken");
        Assert.AreEqual("config_invalid", (await fixture.Service.ListAsync(new(Epoch, null), default)).Status);
        var refused = await fixture.Service.SetEnabledAsync(new(Epoch, null, "Global", "alpha", false), default);
        Assert.AreEqual("config_invalid", refused.Status);
        Assert.IsNull(refused.Message);
        Assert.AreEqual("[skills\nbroken", File.ReadAllText(fixture.GlobalConfig));
        File.Delete(fixture.GlobalConfig);

        fixture.Project.Archived = true;
        await fixture.Projects.SaveAsync(fixture.Project);
        Assert.AreEqual("archived_project", (await fixture.Service.ListAsync(new(Epoch, fixture.Project.Id), default)).Status);
        Assert.AreEqual("archived_project", (await fixture.Service.SetEnabledAsync(new(Epoch, fixture.Project.Id, "Project", "beta", false), default)).Status);
        Assert.AreEqual("archived_project", (await fixture.Service.CreateAsync(new(Epoch, fixture.Project.Id, "Project", "valid-name", "Description"), default)).Status);
        Assert.IsFalse(File.Exists(fixture.ProjectConfig));
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.ProjectPath, ".alta", "skills", "valid-name")));
    }

    [TestMethod]
    public async Task Detail_DescribesTheNamedSkillWithItsFileTextAndRelatedFiles()
    {
        using var fixture = await Fixture.CreateAsync();
        var root = Path.Combine(fixture.GlobalRoot, "skills", "alpha");
        Directory.CreateDirectory(Path.Combine(root, "scripts"));
        File.WriteAllText(Path.Combine(root, "scripts", "run.ps1"), "Write-Output 1");

        var detail = await fixture.Service.DetailAsync(new(Epoch, null, "alpha", "UserAlta"), default);
        Assert.AreEqual("ok", detail.Status);
        Assert.AreEqual("alpha", detail.Name);
        Assert.AreEqual(Path.Combine(root, "SKILL.md"), detail.SkillFilePath);
        Assert.AreEqual(root, detail.SkillRootPath);
        Assert.IsNull(detail.ShadowedBy);
        StringAssert.Contains(detail.Content, "# alpha");
        Assert.IsFalse(detail.ContentTruncated);
        CollectionAssert.AreEqual(new[] { "scripts/run.ps1" }, detail.RelatedFiles.Select(static file => file.Path).ToArray());
        Assert.AreEqual(0, detail.Diagnostics.Count);

        // A long file is cut, never refused, and says so.
        File.WriteAllText(Path.Combine(root, "SKILL.md"), string.Join('\n', "---", "name: alpha", "description: Alpha workflow", "---", "",
            new string('x', SkillsService.MaximumContentLength + 10)));
        var cut = await fixture.Service.DetailAsync(new(Epoch, null, "alpha", "UserAlta"), default);
        Assert.AreEqual(SkillsService.MaximumContentLength, cut.Content!.Length);
        Assert.IsTrue(cut.ContentTruncated);

        // The project's skill is only known with its project; a name with another source is not found.
        Assert.AreEqual("not_found", (await fixture.Service.DetailAsync(new(Epoch, null, "beta", "ProjectAlta"), default)).Status);
        Assert.AreEqual("ok", (await fixture.Service.DetailAsync(new(Epoch, fixture.Project.Id, "beta", "ProjectAlta"), default)).Status);
        Assert.AreEqual("not_found", (await fixture.Service.DetailAsync(new(Epoch, null, "alpha", "Builtin"), default)).Status);
        Assert.AreEqual("invalid", (await fixture.Service.DetailAsync(new(Epoch, null, "", "UserAlta"), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Service.DetailAsync(new("another", null, "alpha", "UserAlta"), default)).Status);
        Assert.AreEqual("unavailable", (await new SkillsService().DetailAsync(new(Epoch, null, "alpha", "UserAlta"), default)).Status);
    }

    [TestMethod]
    public void TheFolderOfASkill_IsNamedByAnId_ThatSaysWhereTheSkillComesFrom()
    {
        Assert.AreEqual("skill:global:UserAlta:alpha", new SkillFolder(null, SkillSourceKind.UserAlta, "alpha").Id);
        Assert.AreEqual("skill:project:p1:ProjectCommon:beta", new SkillFolder("p1", SkillSourceKind.ProjectCommon, "beta").Id);
        Assert.IsTrue(SkillFolder.TryParse("skill:global:UserAlta:alpha", out var user));
        Assert.AreEqual(new SkillFolder(null, SkillSourceKind.UserAlta, "alpha"), user);
        Assert.IsTrue(SkillFolder.TryParse("skill:project:p1:ProjectCommon:beta", out var local));
        Assert.AreEqual(new SkillFolder("p1", SkillSourceKind.ProjectCommon, "beta"), local);
        // The name is the last part of an id: it keeps its colons.
        Assert.IsTrue(SkillFolder.TryParse("skill:global:Plugin:pack:tool", out var named));
        Assert.AreEqual(new SkillFolder(null, SkillSourceKind.Plugin, "pack:tool"), named);

        // The skills of the user and of a project are written to; every other one is only read.
        foreach (var source in Enum.GetValues<SkillSourceKind>())
        {
            var written = source is SkillSourceKind.ProjectAlta or SkillSourceKind.ProjectCommon or SkillSourceKind.UserAlta or SkillSourceKind.UserCommon;
            Assert.AreEqual(!written, new SkillFolder(null, source, "a").ReadOnly, source.ToString());
        }

        // The id of a project, the id of a plugin folder, a source that is none, and a name that is none are not such ids.
        foreach (var other in new[] { null, "", "p1", "skill:", "skill:global:", "skill:global:UserAlta", "skill:global:UserAlta:", "skill:global:useralta:a",
            "skill:global:3:a", "skill:global:Nowhere:a", "skill:global::a", "skill:project:UserAlta:a", "skill:project::UserAlta:a", "skill:other:UserAlta:a",
            "plugin:global:notes", "skill:global:UserAlta:a\nb", "skill:global:UserAlta:" + new string('x', 129) })
        {
            Assert.IsFalse(SkillFolder.TryParse(other, out _), other);
        }
    }

    [TestMethod]
    public async Task Detail_NamesTheFolderOfTheSkill_AndCreate_TheFolderItMade()
    {
        using var fixture = await Fixture.CreateAsync(builtin: true);
        var project = fixture.Project.Id;
        // A skill of the user has one id, asked with a project or without; a skill of a project is found with its project.
        Assert.AreEqual("skill:global:UserAlta:alpha", (await fixture.Service.DetailAsync(new(Epoch, null, "alpha", "UserAlta"), default)).Folder);
        Assert.AreEqual("skill:global:UserAlta:alpha", (await fixture.Service.DetailAsync(new(Epoch, project, "alpha", "UserAlta"), default)).Folder);
        Assert.AreEqual($"skill:project:{project}:ProjectAlta:beta", (await fixture.Service.DetailAsync(new(Epoch, project, "beta", "ProjectAlta"), default)).Folder);
        var builtin = await fixture.Service.DetailAsync(new(Epoch, project, "gamma", "Builtin"), default);
        Assert.AreEqual(("skill:global:Builtin:gamma", Path.Combine(fixture.BuiltinRoot, "gamma")), (builtin.Folder, builtin.SkillRootPath));
        Assert.IsNull((await fixture.Service.DetailAsync(new(Epoch, null, "missing", "UserAlta"), default)).Folder);

        var created = await fixture.Service.CreateAsync(new(Epoch, null, "Global", "release-notes", "Writes release notes"), default);
        Assert.AreEqual(("skill:global:UserAlta:release-notes", Path.Combine(fixture.GlobalRoot, "skills", "release-notes")), (created.Folder, created.Path));
        var local = await fixture.Service.CreateAsync(new(Epoch, project, "Project", "local-skill", "Project only"), default);
        Assert.AreEqual(($"skill:project:{project}:ProjectAlta:local-skill", Path.Combine(fixture.ProjectPath, ".alta", "skills", "local-skill")), (local.Folder, local.Path));
        var again = await fixture.Service.CreateAsync(new(Epoch, null, "Global", "release-notes", "Again"), default);
        Assert.AreEqual(("conflict", (string?)null, (string?)null), (again.Status, again.Folder, again.Path));
        // The folder that was named is the one the detail of the new skill names.
        Assert.AreEqual(created.Folder, (await fixture.Service.DetailAsync(new(Epoch, null, "release-notes", "UserAlta"), default)).Folder);
    }

    [TestMethod]
    public async Task TheCodeEditor_WorksInTheFolderOfASkill_AndOnlyReadsOneThatIsNotOfTheUserOrOfAProject()
    {
        using var fixture = await Fixture.CreateAsync(builtin: true);
        var files = new ProjectFilesService(fixture.Projects, Epoch, skills: fixture.Service.Folders);
        const string User = "skill:global:UserAlta:alpha";
        var root = Path.Combine(fixture.GlobalRoot, "skills", "alpha");
        Directory.CreateDirectory(Path.Combine(root, "scripts"));
        File.WriteAllText(Path.Combine(root, "scripts", "run.ps1"), "Write-Output 1\n");

        var listed = await files.ListAsync(new(Epoch, User, [new("", null)], false), default);
        Assert.AreEqual("ok", listed.Status);
        CollectionAssert.AreEquivalent(new[] { "scripts", "SKILL.md" }, listed.Folders.Single().Entries.Select(static entry => entry.Name).ToArray());
        var read = await files.ReadAsync(new(Epoch, User, "SKILL.md"), default);
        Assert.AreEqual("ok", read.Status);
        Assert.IsFalse(read.ReadOnly);
        StringAssert.Contains(read.Content, "# alpha");
        Assert.AreEqual("ok", (await files.WriteAsync(new(Epoch, User, "SKILL.md", read.Content + "\nMore.\n", read.Revision, false), default)).Status);
        StringAssert.Contains(File.ReadAllText(Path.Combine(root, "SKILL.md")), "More.");
        Assert.AreEqual("ok", (await files.CreateAsync(new(Epoch, User, "references/notes.md", false), default)).Status);
        Assert.AreEqual("ok", (await files.RenameAsync(new(Epoch, User, "references/notes.md", "references/guide.md"), default)).Status);
        Assert.IsTrue(File.Exists(Path.Combine(root, "references", "guide.md")));
        Assert.AreEqual("ok", (await files.DeleteAsync(new(Epoch, User, "references/guide.md", true), default)).Status);
        Assert.IsFalse((await files.StatAsync(new(Epoch, User, ["SKILL.md"]), default)).Files.Single().ReadOnly);

        // The skill of a project is found with its project, and is written to as well.
        var local = $"skill:project:{fixture.Project.Id}:ProjectAlta:beta";
        var beta = await files.ReadAsync(new(Epoch, local, "SKILL.md"), default);
        Assert.AreEqual(("ok", false), (beta.Status, beta.ReadOnly));
        Assert.AreEqual("ok", (await files.CreateAsync(new(Epoch, local, "notes.md", false), default)).Status);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.ProjectPath, ".alta", "skills", "beta", "notes.md")));

        // The folder is the limit, and an id names a skill that is found: no other folder is reached through one.
        Assert.AreEqual("outside_root", (await files.ReadAsync(new(Epoch, User, "../../config.toml"), default)).Status);
        Assert.AreEqual("project_unavailable", (await files.ReadAsync(new(Epoch, "skill:global:UserAlta:gone", "SKILL.md"), default)).Status);
        Assert.AreEqual("project_unavailable", (await files.ReadAsync(new(Epoch, "skill:global:UserCommon:alpha", "SKILL.md"), default)).Status);
        Assert.AreEqual("project_unavailable", (await files.ReadAsync(new(Epoch, "skill:global:ProjectAlta:beta", "SKILL.md"), default)).Status);
        Assert.AreEqual("unknown_project", (await files.ReadAsync(new(Epoch, "skill:project:no-such-project:ProjectAlta:beta", "SKILL.md"), default)).Status);
        // A name is compared with the names of the skills, never made into a path.
        Assert.AreEqual("project_unavailable", (await files.ReadAsync(new(Epoch, "skill:global:UserAlta:../alpha", "SKILL.md"), default)).Status);
        Assert.AreEqual("unknown_project", (await new ProjectFilesService(fixture.Projects, Epoch).ReadAsync(new(Epoch, User, "SKILL.md"), default)).Status,
            "A host that names no skill opens none.");

        // A built-in skill is read, and nothing of it is changed.
        const string Builtin = "skill:global:Builtin:gamma";
        var folder = Path.Combine(fixture.BuiltinRoot, "gamma");
        var before = File.ReadAllText(Path.Combine(folder, "SKILL.md"));
        var shown = await files.ReadAsync(new(Epoch, Builtin, "SKILL.md"), default);
        Assert.AreEqual(("ok", true, before), (shown.Status, shown.ReadOnly, shown.Content));
        Assert.IsTrue((await files.StatAsync(new(Epoch, Builtin, ["SKILL.md"]), default)).Files.Single().ReadOnly);
        var refused = await files.WriteAsync(new(Epoch, Builtin, "SKILL.md", "changed", shown.Revision, false), default);
        Assert.AreEqual(("read_only", shown.Revision), (refused.Status, refused.Revision));
        Assert.AreEqual("read_only", (await files.WriteAsync(new(Epoch, Builtin, "SKILL.md", "changed", null, true), default)).Status);
        Assert.AreEqual("read_only", (await files.CreateAsync(new(Epoch, Builtin, "new.md", false), default)).Status);
        Assert.AreEqual("read_only", (await files.CreateAsync(new(Epoch, Builtin, "scripts", true), default)).Status);
        Assert.AreEqual("read_only", (await files.RenameAsync(new(Epoch, Builtin, "SKILL.md", "OTHER.md"), default)).Status);
        Assert.AreEqual("read_only", (await files.DeleteAsync(new(Epoch, Builtin, "SKILL.md", true), default)).Status);
        Assert.AreEqual("read_only", (await files.DeleteAsync(new(Epoch, Builtin, "SKILL.md", false), default)).Status);
        Assert.AreEqual(before, File.ReadAllText(Path.Combine(folder, "SKILL.md")));
        CollectionAssert.AreEqual(new[] { "SKILL.md" }, Directory.GetFileSystemEntries(folder).Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    public async Task TheFolderOfASkill_IsFoundAgain_OnceItsFileIsGone()
    {
        using var fixture = await Fixture.CreateAsync();
        var folders = fixture.Service.Folders!;
        var alpha = new SkillFolder(null, SkillSourceKind.UserAlta, "alpha");
        var root = Path.Combine(fixture.GlobalRoot, "skills", "alpha");
        Assert.AreEqual(("ok", root), await folders.ResolveAsync(alpha, default));
        // The folder that was found is the answer while its skill file is there, and no longer once it is gone.
        Assert.AreEqual(("ok", root), await folders.ResolveAsync(alpha, default));
        Directory.Delete(root, recursive: true);
        Assert.AreEqual(("project_unavailable", (string?)null), await folders.ResolveAsync(alpha, default));

        // A skill of a project is not answered for a project that is not known, even after it was found.
        var beta = new SkillFolder(fixture.Project.Id, SkillSourceKind.ProjectAlta, "beta");
        Assert.AreEqual(("ok", Path.Combine(fixture.ProjectPath, ".alta", "skills", "beta")), await folders.ResolveAsync(beta, default));
        Assert.AreEqual(("unknown_project", (string?)null), await folders.ResolveAsync(beta with { ProjectId = "no-such-project" }, default));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;

        private Fixture(string root, ProjectCatalog projects, ProjectDescriptor project, bool builtin)
        {
            _root = root;
            Projects = projects;
            Project = project;
            WriteSkill(Path.Combine(GlobalRoot, "skills"), "alpha", "Alpha workflow");
            WriteSkill(Path.Combine(ProjectPath, ".alta", "skills"), "beta", "Beta workflow");
            // No plugin roots, and a built-in root only when a test asks for it: the listing is exactly what the fixture wrote.
            List<ISkillRootProvider> providers = [new ProjectCodeAltaSkillRootProvider(), new ProjectCommonSkillRootProvider(),
                new UserCodeAltaSkillRootProvider(), new UserCommonSkillRootProvider()];
            if (builtin)
            {
                WriteSkill(BuiltinRoot, "gamma", "Gamma workflow");
                providers.Add(new BuiltInCodeAltaSkillRootProvider(BuiltinRoot));
            }

            Service = new SkillsService(projects, new SkillCatalog(providers), Path.Combine(root, "home"), Epoch);
        }

        public static async Task<Fixture> CreateAsync(bool builtin = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "CodeAlta-skills-" + Guid.NewGuid().ToString("N"));
            var projects = new ProjectCatalog(new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(root, "home", ".alta")).FullName });
            var project = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "project")).FullName);
            return new Fixture(root, projects, project, builtin);
        }

        public string BuiltinRoot => Path.Combine(_root, "builtin");
        public ProjectCatalog Projects { get; }
        public ProjectDescriptor Project { get; }
        public SkillsService Service { get; }
        public string GlobalRoot => Projects.Options.GlobalRoot;
        public string ProjectPath => Project.ProjectPath;
        public string GlobalConfig => Projects.Options.ConfigPath;
        public string ProjectConfig => Path.Combine(ProjectPath, ".alta", "config.toml");

        private static void WriteSkill(string root, string name, string description)
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
            File.WriteAllText(Path.Combine(directory, "SKILL.md"), $"---\nname: {name}\ndescription: {description}\n---\n\n# {name}\n\nUse this skill.\n");
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }
}
