using System.Diagnostics;
using CodeAlta.Catalog.Skills;

namespace CodeAlta.Catalog.Tests;

/// <summary>How the catalog keeps what it read of a skill: listed again, nothing is read again until a file changes.</summary>
[TestClass]
public sealed class SkillCatalogCacheTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task FiveHundredSkills_AreListedCold_ThenWarm_WithTheTimePrinted()
    {
        using var temp = TempDirectory.Create();
        var userHome = Path.Combine(temp.Path, "home");
        var skills = Path.Combine(userHome, ".alta", "skills");
        for (var i = 0; i < 500; i++)
        {
            await WriteSkillAsync(Path.Combine(skills, $"skill-{i:000}"), $"skill-{i:000}", $"Does task {i}.");
        }

        Settle(temp.Path);
        var catalog = new SkillCatalog([new UserCodeAltaSkillRootProvider()]);
        var query = Query(userHome);

        var watch = Stopwatch.StartNew();
        var cold = await catalog.ListAsync(query);
        var coldTime = watch.Elapsed;

        const int warmRuns = 5;
        watch.Restart();
        IReadOnlyList<SkillDescriptor> warm = [];
        for (var i = 0; i < warmRuns; i++)
        {
            warm = await catalog.ListAsync(query);
        }

        var warmTime = watch.Elapsed / warmRuns;
        TestContext.WriteLine($"500 skills: cold {coldTime.TotalMilliseconds:0.0} ms, warm {warmTime.TotalMilliseconds:0.0} ms per listing");

        Assert.AreEqual(500, cold.Count);
        CollectionAssert.AreEqual(cold.Select(static x => x.Name).ToArray(), warm.Select(static x => x.Name).ToArray());
        Assert.AreEqual(1, catalog.RootWalkCount);
    }

    [TestMethod]
    public async Task ASecondListing_ReadsNoSkillFile()
    {
        using var temp = TempDirectory.Create();
        var userHome = Path.Combine(temp.Path, "home");
        var skills = Path.Combine(userHome, ".alta", "skills");
        await WriteSkillAsync(Path.Combine(skills, "alpha"), "alpha", "First.");
        await WriteSkillAsync(Path.Combine(skills, "beta"), "beta", "Second.");
        var catalog = new SkillCatalog([new UserCodeAltaSkillRootProvider()]);

        var first = await catalog.ListAsync(Query(userHome));
        Assert.AreEqual(2, catalog.SkillFileReadCount);

        var second = await catalog.ListAsync(Query(userHome));
        var document = await catalog.GetAsync(Query(userHome), "beta");
        var activation = await catalog.ActivateAsync(Query(userHome), "alpha");

        Assert.AreEqual(2, catalog.SkillFileReadCount, "nothing changed: no SKILL.md is parsed again");
        CollectionAssert.AreEqual(first.ToArray(), second.ToArray());
        Assert.AreEqual("Second.", document!.Descriptor.Description);
        StringAssert.Contains(document.Body, "# Title");
        Assert.IsNotNull(activation);
    }

    [TestMethod]
    public async Task AnEditedSkill_IsReadAgain()
    {
        using var temp = TempDirectory.Create();
        var userHome = Path.Combine(temp.Path, "home");
        var folder = Path.Combine(userHome, ".alta", "skills", "alpha");
        await WriteSkillAsync(folder, "alpha", "Old text.");
        var catalog = new SkillCatalog([new UserCodeAltaSkillRootProvider()]);
        var skillFile = Path.Combine(folder, "SKILL.md");
        var before = File.GetLastWriteTimeUtc(skillFile);
        Assert.AreEqual("Old text.", (await catalog.ListAsync(Query(userHome))).Single().Description);

        // The same length, a later time.
        await WriteSkillAsync(folder, "alpha", "New text.", "# Changed\n");
        File.SetLastWriteTimeUtc(skillFile, before.AddSeconds(5));
        var edited = (await catalog.ListAsync(Query(userHome))).Single();
        Assert.AreEqual("New text.", edited.Description);
        Assert.AreEqual("Changed", edited.Title);
        Assert.AreEqual(2, catalog.SkillFileReadCount);

        // Another length, the same time.
        await WriteSkillAsync(folder, "alpha", "A longer text than before.");
        File.SetLastWriteTimeUtc(skillFile, before.AddSeconds(5));
        Assert.AreEqual("A longer text than before.", (await catalog.ListAsync(Query(userHome))).Single().Description);

        // Made invalid: the diagnostics are those of the new content.
        await File.WriteAllTextAsync(skillFile, "no frontmatter");
        File.SetLastWriteTimeUtc(skillFile, before.AddSeconds(10));
        var invalid = (await catalog.ListAsync(Query(userHome))).Single();
        Assert.IsFalse(invalid.IsValid);
        Assert.IsTrue(invalid.Diagnostics.Any(static x => x.Code == "frontmatter-missing"));
    }

    [TestMethod]
    public async Task ADeletedSkill_Disappears_AndARenamedFolder_IsReadUnderItsNewName()
    {
        using var temp = TempDirectory.Create();
        var userHome = Path.Combine(temp.Path, "home");
        var skills = Path.Combine(userHome, ".alta", "skills");
        await WriteSkillAsync(Path.Combine(skills, "alpha"), "alpha", "First.");
        await WriteSkillAsync(Path.Combine(skills, "beta"), "beta", "Second.");
        var catalog = new SkillCatalog([new UserCodeAltaSkillRootProvider()]);
        Assert.AreEqual(2, (await catalog.ListAsync(Query(userHome))).Count);

        Directory.Delete(Path.Combine(skills, "beta"), recursive: true);
        Assert.AreEqual("alpha", (await catalog.ListAsync(Query(userHome))).Single().Name);

        // The folder no longer matches the name it declares: the skill is read again at its new place.
        Directory.Move(Path.Combine(skills, "alpha"), Path.Combine(skills, "gamma"));
        var renamed = (await catalog.ListAsync(Query(userHome))).Single();
        Assert.AreEqual(Path.Combine(skills, "gamma"), renamed.SkillRootPath);
        Assert.IsTrue(renamed.Diagnostics.Any(static x => x.Code == "name-directory-mismatch"));
        Assert.AreEqual(0, (await catalog.ListAsync(Query(userHome) with { IncludeInvalid = false })).Count);

        // Back as it was, under its own name again.
        Directory.Move(Path.Combine(skills, "gamma"), Path.Combine(skills, "alpha"));
        Assert.IsTrue((await catalog.ListAsync(Query(userHome))).Single().IsValid);

        // A root that is gone leaves nothing behind.
        Directory.Delete(skills, recursive: true);
        Assert.AreEqual(0, (await catalog.ListAsync(Query(userHome))).Count);
    }

    [TestMethod]
    public async Task WhatDependsOnTheQuery_IsAppliedOnEveryListing()
    {
        using var temp = TempDirectory.Create();
        var userHome = Path.Combine(temp.Path, "home");
        var projectRoot = Path.Combine(temp.Path, "project");
        Directory.CreateDirectory(projectRoot);
        await WriteSkillAsync(Path.Combine(userHome, ".alta", "skills", "alpha"), "alpha", "User one.");
        var catalog = new SkillCatalog([new ProjectCodeAltaSkillRootProvider(), new UserCodeAltaSkillRootProvider()]);
        var query = Query(userHome) with
        {
            Discovery = Query(userHome).Discovery with { ProjectRoots = [projectRoot] },
            IncludeShadowed = true,
            IncludeDisabled = true,
        };
        var user = (await catalog.ListAsync(query)).Single();
        Assert.IsTrue(user.IsModelVisible);

        // Disabled by the query, not by the cache.
        var disabled = (await catalog.ListAsync(query with { GlobalDisabledSkillNames = ["alpha"] })).Single();
        Assert.IsFalse(disabled.IsEnabled);
        Assert.IsTrue((await catalog.ListAsync(query)).Single().IsEnabled);

        // A skill of the same name in the project shadows the cached one of the user, then no longer does.
        var projectSkill = Path.Combine(projectRoot, ".alta", "skills", "alpha");
        await WriteSkillAsync(projectSkill, "alpha", "Project one.");
        var both = await catalog.ListAsync(query);
        Assert.IsTrue(both.Single(static x => x.Description == "User one.").IsShadowed);
        Assert.IsFalse(both.Single(static x => x.Description == "Project one.").IsShadowed);
        Directory.Delete(projectSkill, recursive: true);
        var alone = (await catalog.ListAsync(query)).Single();
        Assert.IsFalse(alone.IsShadowed);
        Assert.IsFalse(alone.Diagnostics.Any(static x => x.Code == "skill-shadowed"));

        // The same folder as a root of another trust is read as that root.
        var untrusted = new SkillRootRegistration
        {
            RootPath = Path.Combine(userHome, ".alta", "skills"),
            SourceKind = SkillSourceKind.Temporary,
            SourceId = "temporary:fixture",
            Scope = SkillScopeKind.Temporary,
            Precedence = 0,
            IsTrusted = false,
        };
        var extra = new SkillCatalogQuery
        {
            Discovery = new SkillDiscoveryContext { UseBuiltInRoots = false, AdditionalRoots = [untrusted] },
            IncludeUntrusted = true,
        };
        var asUntrusted = (await catalog.ListAsync(extra)).Single();
        Assert.IsFalse(asUntrusted.IsTrusted);
        Assert.AreEqual(SkillSourceKind.Temporary, asUntrusted.SourceKind);
        Assert.IsTrue((await catalog.ListAsync(query)).Single().IsTrusted);
    }

    [TestMethod]
    public async Task TheFilesOfASkill_AreListedAsTheyAre_AfterACachedListing()
    {
        using var temp = TempDirectory.Create();
        var userHome = Path.Combine(temp.Path, "home");
        var folder = Path.Combine(userHome, ".alta", "skills", "alpha");
        await WriteSkillAsync(folder, "alpha", "First.");
        var catalog = new SkillCatalog([new UserCodeAltaSkillRootProvider()]);
        Assert.AreEqual(0, (await catalog.ActivateAsync(Query(userHome), "alpha"))!.Files.Count);

        await File.WriteAllTextAsync(Path.Combine(folder, "notes.md"), "Notes.");
        var activation = await catalog.ActivateAsync(Query(userHome), "alpha");
        CollectionAssert.AreEqual(new[] { "notes.md" }, activation!.Files.ToArray());
        CollectionAssert.AreEqual(new[] { "notes.md" }, catalog.ListFiles(activation.Descriptor).ToArray());
        Assert.AreEqual(1, catalog.SkillFileReadCount);
    }

    [TestMethod]
    public async Task ASettledRoot_IsWalkedOnce_UntilAFolderOfItChanges()
    {
        using var temp = TempDirectory.Create();
        var userHome = Path.Combine(temp.Path, "home");
        var skills = Path.Combine(userHome, ".alta", "skills");
        await WriteSkillAsync(Path.Combine(skills, "alpha"), "alpha", "First.");
        await WriteSkillAsync(Path.Combine(skills, "group", "beta"), "beta", "Second.");
        Settle(temp.Path);
        var catalog = new SkillCatalog([new UserCodeAltaSkillRootProvider()]);

        Assert.AreEqual(2, (await catalog.ListAsync(Query(userHome))).Count);
        Assert.AreEqual(2, (await catalog.ListAsync(Query(userHome))).Count);
        Assert.AreEqual(1, catalog.RootWalkCount, "nothing changed: the root is not walked again");

        // A skill added in a folder below the root changes that folder: the root is walked again, and the skill is found.
        await WriteSkillAsync(Path.Combine(skills, "group", "gamma"), "gamma", "Third.");
        CollectionAssert.AreEquivalent(new[] { "alpha", "beta", "gamma" }, (await catalog.ListAsync(Query(userHome))).Select(static x => x.Name).ToArray());
        Assert.AreEqual(2, catalog.RootWalkCount);

        // Removed again.
        Settle(temp.Path);
        Assert.AreEqual(3, (await catalog.ListAsync(Query(userHome))).Count);
        Directory.Delete(Path.Combine(skills, "group", "gamma"), recursive: true);
        CollectionAssert.AreEquivalent(new[] { "alpha", "beta" }, (await catalog.ListAsync(Query(userHome))).Select(static x => x.Name).ToArray());

        // A SKILL.md removed from its folder, the folder staying.
        Settle(temp.Path);
        Assert.AreEqual(2, (await catalog.ListAsync(Query(userHome))).Count);
        File.Delete(Path.Combine(skills, "alpha", "SKILL.md"));
        Assert.AreEqual("beta", (await catalog.ListAsync(Query(userHome))).Single().Name);
    }

    [TestMethod]
    public async Task AWalkOfAFolderThatJustChanged_IsNotTakenAgain()
    {
        using var temp = TempDirectory.Create();
        var userHome = Path.Combine(temp.Path, "home");
        await WriteSkillAsync(Path.Combine(userHome, ".alta", "skills", "alpha"), "alpha", "First.");
        var catalog = new SkillCatalog([new UserCodeAltaSkillRootProvider()]);

        // The folders were written a moment ago: an entry added while the walk ran could be missing from it.
        await catalog.ListAsync(Query(userHome));
        await catalog.ListAsync(Query(userHome));
        Assert.AreEqual(2, catalog.RootWalkCount);

        Settle(temp.Path);
        await catalog.ListAsync(Query(userHome));
        await catalog.ListAsync(Query(userHome));
        Assert.AreEqual(3, catalog.RootWalkCount);
    }

    [TestMethod]
    public async Task AnIgnoreFileChanged_InTheRootOrAboveIt_IsApplied()
    {
        using var temp = TempDirectory.Create();
        var userHome = Path.Combine(temp.Path, "home");
        var skills = Path.Combine(userHome, ".alta", "skills");
        await WriteSkillAsync(Path.Combine(skills, "alpha"), "alpha", "First.");
        await WriteSkillAsync(Path.Combine(skills, "beta"), "beta", "Second.");
        var rootIgnore = Path.Combine(skills, ".gitignore");
        await File.WriteAllTextAsync(rootIgnore, "# nothing\n");
        Settle(temp.Path);
        var catalog = new SkillCatalog([new UserCodeAltaSkillRootProvider()]);
        Assert.AreEqual(2, (await catalog.ListAsync(Query(userHome))).Count);

        // Out of a repository: the .gitignore of the root, edited in place.
        await File.WriteAllTextAsync(rootIgnore, "beta/\n");
        Assert.AreEqual("alpha", (await catalog.ListAsync(Query(userHome))).Single().Name);

        // In a repository: the .gitignore of a folder above the root.
        Directory.CreateDirectory(Path.Combine(userHome, ".git"));
        await File.WriteAllTextAsync(rootIgnore, "# nothing\n");
        var aboveIgnore = Path.Combine(userHome, ".gitignore");
        await File.WriteAllTextAsync(aboveIgnore, "# nothing\n");
        Settle(temp.Path);
        Assert.AreEqual(2, (await catalog.ListAsync(Query(userHome))).Count);
        var walks = catalog.RootWalkCount;
        Assert.AreEqual(2, (await catalog.ListAsync(Query(userHome))).Count);
        Assert.AreEqual(walks, catalog.RootWalkCount);

        await File.WriteAllTextAsync(aboveIgnore, ".alta/skills/alpha/\n");
        Assert.AreEqual("beta", (await catalog.ListAsync(Query(userHome))).Single().Name);
    }

    // Puts the times of a tree a minute back, as a tree that nothing changed for a while.
    private static void Settle(string root)
    {
        var time = DateTime.UtcNow.AddMinutes(-1);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, time);
        }

        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Append(root))
        {
            Directory.SetLastWriteTimeUtc(directory, time);
        }
    }

    private static SkillCatalogQuery Query(string userHome)
        => new()
        {
            Discovery = new SkillDiscoveryContext
            {
                UserCodeAltaRoot = Path.Combine(userHome, ".alta"),
                UserProfileRoot = userHome,
            },
            IncludeInvalid = true,
        };

    private static async Task WriteSkillAsync(string folder, string name, string description, string body = "# Title\n")
    {
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "SKILL.md"), $"---\nname: {name}\ndescription: {description}\n---\n{body}");
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"CodeAlta.Catalog.Tests.{Guid.NewGuid():N}");
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
            catch (IOException)
            {
            }
        }
    }
}
