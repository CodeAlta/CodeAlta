using CodeAlta.Catalog.Skills;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class SpaceSkillFilterTests
{
    [TestMethod]
    public async Task ASkillOfOtherSpaces_IsDisabledThere_WhereverItComesFrom()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("""
            [skills.spaces]
            terraform = ["business"]
            notes = ["personal", "default"]
            """);
        fixture.WriteSkill(Path.Combine(fixture.GlobalRoot, "skills", "terraform"), "terraform");
        fixture.WriteSkill(Path.Combine(fixture.GlobalRoot, "skills", "notes"), "notes");
        fixture.WriteSkill(Path.Combine(fixture.GlobalRoot, "skills", "shared"), "shared");
        var catalog = new Catalog();

        var business = await catalog.ListAsync(fixture.Query("business") with { IncludeDisabled = true, ModelVisibleOnly = true });
        CollectionAssert.AreEquivalent(new[] { "terraform", "shared" }, business.Select(static skill => skill.Name).ToArray());

        var personal = await catalog.ListAsync(fixture.Query("personal") with { IncludeDisabled = true });
        Assert.IsTrue(personal.Single(static skill => skill.Name == "terraform").IsOutsideSpace);
        Assert.IsFalse(personal.Single(static skill => skill.Name == "terraform").IsEnabled);
        Assert.IsTrue(personal.Single(static skill => skill.Name == "shared").IsEnabled, "A skill that lists no space is in every space.");

        var inDefault = await catalog.ListAsync(fixture.Query(null) with { ModelVisibleOnly = true });
        CollectionAssert.AreEquivalent(new[] { "notes", "shared" }, inDefault.Select(static skill => skill.Name).ToArray(),
            "A session of no space is in the default space, which has what lists 'default'.");

        Assert.IsNull(await catalog.ActivateAsync(fixture.Query("personal"), "terraform"), "A skill of another space is not activated.");
        Assert.IsNotNull(await catalog.ActivateAsync(fixture.Query("business"), "terraform"));
    }

    [TestMethod]
    public async Task AProjectSkill_CannotBringIntoASpace_ASkillTheSpaceDoesNotHave()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("""
            [skills.spaces]
            terraform = ["business"]
            """);
        // The project enables the skill and has its own copy, which shadows the one of the user.
        fixture.WriteSkill(Path.Combine(fixture.ProjectRoot, ".alta", "skills", "terraform"), "terraform");
        fixture.WriteSkill(Path.Combine(fixture.ProjectRoot, ".alta", "skills", "local"), "local");
        var catalog = new Catalog();

        var personal = await catalog.ListAsync(fixture.Query("personal") with { ModelVisibleOnly = true });
        CollectionAssert.AreEquivalent(new[] { "local" }, personal.Select(static skill => skill.Name).ToArray(),
            "A skill of the project that no space lists follows its project.");
        Assert.IsNull(await catalog.ActivateAsync(fixture.Query("personal"), "terraform"));
    }

    [TestMethod]
    public async Task WithoutASpace_TheQueryListsTheSkillsOfEverySpace()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("""
            [skills.spaces]
            terraform = ["business"]
            """);
        fixture.WriteSkill(Path.Combine(fixture.GlobalRoot, "skills", "terraform"), "terraform");
        var query = (SkillCatalogQuery.ForSession(new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = fixture.GlobalRoot }),
            fixture.GlobalRoot, fixture.ProfileRoot, fixture.ProjectRoot, otherProjectRoots: null, space: null));

        var skills = await new Catalog().ListAsync(query with { ModelVisibleOnly = true });

        Assert.AreEqual("terraform", skills.Single().Name, "The settings of the user list every skill, whatever its spaces.");
        Assert.IsFalse(skills.Single().IsOutsideSpace);
    }

    [TestMethod]
    public async Task TheSpaceComesBeforeTheEnablement_AndBothAreSaid()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("""
            [skills]
            disabled = ["terraform"]

            [skills.spaces]
            terraform = ["business"]
            """);
        fixture.WriteSkill(Path.Combine(fixture.GlobalRoot, "skills", "terraform"), "terraform");

        var skill = (await new Catalog().ListAsync(fixture.Query("personal") with { IncludeDisabled = true })).Single();

        Assert.IsTrue(skill.IsOutsideSpace);
        Assert.IsTrue(skill.IsDisabledGlobally);
        Assert.IsFalse(skill.IsEnabled);
    }

    // The skills of the fixture's folders, without those CodeAlta brings.
    private sealed class Catalog
    {
        private readonly SkillCatalog _catalog = new();

        public async Task<IReadOnlyList<SkillDescriptor>> ListAsync(SkillCatalogQuery query)
            => [.. (await _catalog.ListAsync(query)).Where(static skill => skill.SourceKind is not (SkillSourceKind.Builtin or SkillSourceKind.Plugin))];

        public Task<SkillActivation?> ActivateAsync(SkillCatalogQuery query, string name) => _catalog.ActivateAsync(query, name);
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Directory.CreateDirectory(GlobalRoot);
            Directory.CreateDirectory(ProjectRoot);
            Directory.CreateDirectory(ProfileRoot);
        }

        public string Root { get; } = Path.Combine(Path.GetTempPath(), "codealta-space-skills-" + Guid.NewGuid().ToString("N"));

        public string GlobalRoot => Path.Combine(Root, "alta");

        public string ProjectRoot => Path.Combine(Root, "project");

        public string ProfileRoot => Path.Combine(Root, "home");

        public void WriteConfig(string content) => File.WriteAllText(Path.Combine(GlobalRoot, "config.toml"), content);

        public void WriteSkill(string folder, string name)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "SKILL.md"), $"---\nname: {name}\ndescription: The {name} skill.\n---\n# {name}\n");
        }

        public SkillCatalogQuery Query(string? spaceId)
        {
            var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = GlobalRoot });
            return (SkillCatalogQuery.ForSession(store, GlobalRoot, ProfileRoot, ProjectRoot, otherProjectRoots: null, SpaceFilter.Load(store, spaceId)));
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
