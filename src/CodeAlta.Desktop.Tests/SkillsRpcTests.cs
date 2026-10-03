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

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;

        private Fixture(string root, ProjectCatalog projects, ProjectDescriptor project)
        {
            _root = root;
            Projects = projects;
            Project = project;
            WriteSkill(Path.Combine(GlobalRoot, "skills"), "alpha", "Alpha workflow");
            WriteSkill(Path.Combine(ProjectPath, ".alta", "skills"), "beta", "Beta workflow");
            // No built-in or plugin roots: the listing is exactly what the fixture wrote.
            var catalog = new SkillCatalog([new ProjectCodeAltaSkillRootProvider(), new ProjectCommonSkillRootProvider(),
                new UserCodeAltaSkillRootProvider(), new UserCommonSkillRootProvider()]);
            Service = new SkillsService(projects, catalog, Path.Combine(root, "home"), Epoch);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "CodeAlta-skills-" + Guid.NewGuid().ToString("N"));
            var projects = new ProjectCatalog(new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(root, "home", ".alta")).FullName });
            var project = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "project")).FullName);
            return new Fixture(root, projects, project);
        }

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
