using System.Diagnostics;
using CodeAlta.Catalog.Skills;

namespace CodeAlta.Catalog.Tests;

/// <summary>A listing can read the roots of some scopes only: a project alone, without walking the skills of the user again.</summary>
[TestClass]
public sealed class SkillCatalogScopesTests
{
    private static readonly IReadOnlySet<SkillScopeKind> ProjectOnly = new HashSet<SkillScopeKind> { SkillScopeKind.Project };
    private static readonly IReadOnlySet<SkillScopeKind> NotProject = new HashSet<SkillScopeKind> { SkillScopeKind.User, SkillScopeKind.Builtin, SkillScopeKind.Plugin };

    public TestContext TestContext { get; set; } = null!;

    private static SkillCatalog Catalog() => new([new ProjectCodeAltaSkillRootProvider(), new ProjectCommonSkillRootProvider(),
        new UserCodeAltaSkillRootProvider(), new UserCommonSkillRootProvider()]);

    private static SkillCatalogQuery Query(string home, string? project, IReadOnlySet<SkillScopeKind>? scopes = null) => new()
    {
        Discovery = new SkillDiscoveryContext { UserCodeAltaRoot = Path.Combine(home, ".alta"), UserProfileRoot = home, ProjectRoots = project is null ? [] : [project] },
        IncludeInvalid = false,
        IncludeShadowed = false,
        Scopes = scopes,
    };

    private static void Skill(string root, string name)
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        File.WriteAllText(Path.Combine(folder, "SKILL.md"), $"---\nname: {name}\ndescription: Does {name}.\n---\n# {name}\n");
    }

    [TestMethod]
    public async Task AProjectListedAlone_HasTheProjectSkillsOfAFullListing_AndReadsNoSkillOfTheUser()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-skill-scopes-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var home = Path.Combine(root, "home");
            var project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
            Skill(Path.Combine(home, ".alta", "skills"), "alpha");
            Skill(Path.Combine(home, ".alta", "skills"), "release");
            Skill(Path.Combine(project, ".alta", "skills"), "release");
            // A skill of the project in two of its folders: the one of .alta shadows the common one, with or without the user.
            Skill(Path.Combine(project, ".alta", "skills"), "beta");
            Skill(Path.Combine(project, ".agents", "skills"), "beta");

            var full = await Catalog().ListAsync(Query(home, project));
            var alone = Catalog();
            var projectOnly = await alone.ListAsync(Query(home, project, ProjectOnly));

            string Describe(SkillDescriptor skill) => $"{skill.Name}|{skill.SourceKind}|{skill.SkillFilePath}|{skill.IsShadowed}";
            CollectionAssert.AreEqual(full.Where(static skill => skill.Scope == SkillScopeKind.Project).Select(Describe).ToArray(), projectOnly.Select(Describe).ToArray());
            CollectionAssert.AreEquivalent(new[] { "beta", "release" }, projectOnly.Select(static skill => skill.Name).ToArray());
            Assert.AreEqual(3, alone.SkillFileReadCount, "the SKILL.md of the project only, none of the user");

            // Without the project, the skill of the user that the project shadows is listed: the index keeps both.
            var user = await Catalog().ListAsync(Query(home, project, NotProject));
            CollectionAssert.AreEquivalent(new[] { "alpha", "release" }, user.Select(static skill => skill.Name).ToArray());
            Assert.IsTrue(user.All(static skill => skill.Scope == SkillScopeKind.User));
            Assert.IsFalse(full.Any(static skill => skill is { Name: "release", Scope: SkillScopeKind.User }), "a full listing leaves the shadowed skill of the user out");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    [TestMethod]
    public async Task FiveHundredSkillsOfTheUser_AndFiftyProjects_AreListedOnceRatherThanFiftyOneTimes_WithTheTimePrinted()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-skill-scopes-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var home = Path.Combine(root, "home");
            for (var i = 0; i < 500; i++) Skill(Path.Combine(home, ".alta", "skills"), $"user-{i:000}");
            var projects = Enumerable.Range(0, 50).Select(index => Directory.CreateDirectory(Path.Combine(root, $"project-{index:00}")).FullName).ToArray();
            foreach (var project in projects)
            {
                Skill(Path.Combine(project, ".alta", "skills"), "local-one");
                Skill(Path.Combine(project, ".alta", "skills"), "local-two");
            }

            var catalog = Catalog();
            // As the index read them before: once without a project, then every root again for each project.
            async Task<int> Before()
            {
                var count = (await catalog.ListAsync(Query(home, null))).Count;
                foreach (var project in projects) count += (await catalog.ListAsync(Query(home, project))).Count(static skill => skill.Scope == SkillScopeKind.Project);
                return count;
            }

            // As it reads them now: the roots of the user once, the roots of each project alone.
            async Task<int> After()
            {
                var count = (await catalog.ListAsync(Query(home, null, NotProject))).Count;
                foreach (var project in projects) count += (await catalog.ListAsync(Query(home, project, ProjectOnly))).Count;
                return count;
            }

            Assert.AreEqual(await Before(), await After(), "the same skills");
            var watch = Stopwatch.StartNew();
            var before = await Before();
            var beforeTime = watch.Elapsed;
            watch.Restart();
            var after = await After();
            var afterTime = watch.Elapsed;
            TestContext.WriteLine($"500 skills of the user, 50 projects of 2 skills, warm: before {beforeTime.TotalMilliseconds:0} ms, after {afterTime.TotalMilliseconds:0} ms per check");
            Assert.AreEqual(600, after);
            Assert.AreEqual(before, after);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }
}
