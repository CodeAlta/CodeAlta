using System.Text;
using CodeAlta.Catalog.Skills;

namespace CodeAlta.Catalog.Tests;

/// <summary>Skills in folders that are symbolic links to a library shared with other tools (#249).</summary>
[TestClass]
public sealed class SkillCatalogLinkTests
{
    [TestMethod]
    public async Task ALinkedSkillFolder_IsDiscovered_AndReadable()
    {
        using var temp = TempDirectory.Create();
        var (userHome, skills, library) = Layout(temp.Path);
        await WriteSkillAsync(Path.Combine(library, "my-skill"), "my-skill", "Shared.");
        await File.WriteAllTextAsync(Path.Combine(library, "my-skill", "notes.md"), "Notes.");
        Directory.CreateDirectory(skills);
        LinkDirectory(Path.Combine(skills, "my-skill"), Path.Combine(library, "my-skill"));
        var catalog = new SkillCatalog([new UserCodeAltaSkillRootProvider()]);

        var skill = (await catalog.ListAsync(Query(userHome))).Single();
        Assert.AreEqual("my-skill", skill.Name);
        Assert.IsTrue(skill.IsValid);
        Assert.AreEqual(Path.Combine(skills, "my-skill"), skill.SkillRootPath, "a linked skill keeps the path of its link");

        var document = await catalog.GetAsync(Query(userHome), "my-skill");
        StringAssert.Contains(document!.Body, "# Title");
        var resource = await catalog.ReadResourceAsync(Query(userHome), "my-skill", "notes.md");
        Assert.AreEqual("Notes.", Encoding.UTF8.GetString(resource.Content));
        var activation = await catalog.ActivateAsync(Query(userHome), "my-skill");
        CollectionAssert.AreEqual(new[] { "notes.md" }, activation!.Files.ToArray());
    }

    [TestMethod]
    public async Task ABrokenLink_LeavesItsSiblingsFound()
    {
        using var temp = TempDirectory.Create();
        var (userHome, skills, library) = Layout(temp.Path);
        await WriteSkillAsync(Path.Combine(skills, "alpha"), "alpha", "Healthy.");
        await WriteSkillAsync(Path.Combine(library, "beta"), "beta", "Linked.");
        LinkDirectory(Path.Combine(skills, "beta"), Path.Combine(library, "beta"));
        LinkDirectory(Path.Combine(skills, "broken"), Path.Combine(library, "missing"));
        LinkFile(Path.Combine(skills, "alpha", "missing.md"), Path.Combine(library, "missing.md"));
        var catalog = new SkillCatalog([new UserCodeAltaSkillRootProvider()]);

        CollectionAssert.AreEquivalent(new[] { "alpha", "beta" }, (await catalog.ListAsync(Query(userHome))).Select(static x => x.Name).ToArray());
        Assert.AreEqual(0, (await catalog.ActivateAsync(Query(userHome), "alpha"))!.Files.Count);
    }

    [TestMethod]
    public async Task LinksThatMakeACycle_EndTheWalk()
    {
        using var temp = TempDirectory.Create();
        var (userHome, skills, library) = Layout(temp.Path);
        await WriteSkillAsync(Path.Combine(library, "group", "alpha"), "alpha", "In a cycle.");
        Directory.CreateDirectory(skills);
        LinkDirectory(Path.Combine(skills, "group"), Path.Combine(library, "group"));
        LinkDirectory(Path.Combine(library, "group", "back"), library);
        LinkDirectory(Path.Combine(skills, "self"), skills);
        var catalog = new SkillCatalog([new UserCodeAltaSkillRootProvider()]);

        var listing = Task.Run(() => catalog.ListAsync(Query(userHome) with { IncludeShadowed = true }));
        Assert.IsTrue(listing.Wait(TimeSpan.FromMinutes(1)), "the walk must end");
        var found = listing.Result.Where(static x => x.Name == "alpha").Select(static x => x.SkillRootPath).ToArray();
        CollectionAssert.Contains(found, Path.Combine(skills, "group", "alpha"));
    }

    [TestMethod]
    public async Task TwoLinksToOneFolder_AreEachDiscovered()
    {
        using var temp = TempDirectory.Create();
        var (userHome, skills, library) = Layout(temp.Path);
        await WriteSkillAsync(Path.Combine(library, "group", "alpha"), "alpha", "Twice.");
        Directory.CreateDirectory(skills);
        LinkDirectory(Path.Combine(skills, "one"), Path.Combine(library, "group"));
        LinkDirectory(Path.Combine(skills, "two"), Path.Combine(library, "group"));
        var catalog = new SkillCatalog([new UserCodeAltaSkillRootProvider()]);

        var found = (await catalog.ListAsync(Query(userHome) with { IncludeShadowed = true })).Select(static x => x.SkillRootPath).ToArray();
        CollectionAssert.AreEquivalent(new[] { Path.Combine(skills, "one", "alpha"), Path.Combine(skills, "two", "alpha") }, found);
    }

    [TestMethod]
    public async Task IgnoreRules_ApplyBelowALink()
    {
        using var temp = TempDirectory.Create();
        var (userHome, skills, library) = Layout(temp.Path);
        await WriteSkillAsync(Path.Combine(library, "shared", "alpha"), "alpha", "Kept.");
        await WriteSkillAsync(Path.Combine(library, "shared", "beta"), "beta", "Ignored by the root.");
        await WriteSkillAsync(Path.Combine(library, "shared", "gamma"), "gamma", "Ignored by the library.");
        await File.WriteAllTextAsync(Path.Combine(library, "shared", ".gitignore"), "gamma/\n");
        await File.WriteAllTextAsync(Path.Combine(library, "shared", "alpha", "secret.txt"), "Ignored resource.");
        await File.WriteAllTextAsync(Path.Combine(library, "shared", "alpha", "notes.md"), "Notes.");
        Directory.CreateDirectory(skills);
        await File.WriteAllTextAsync(Path.Combine(skills, ".gitignore"), "shared/beta/\n*.txt\n");
        LinkDirectory(Path.Combine(skills, "shared"), Path.Combine(library, "shared"));
        var catalog = new SkillCatalog([new UserCodeAltaSkillRootProvider()]);

        Assert.AreEqual("alpha", (await catalog.ListAsync(Query(userHome))).Single().Name);

        // In a repository too, where the files of the skill are listed with the rules of the folders above it.
        Directory.CreateDirectory(Path.Combine(userHome, ".git"));
        Assert.AreEqual("alpha", (await catalog.ListAsync(Query(userHome))).Single().Name);
        CollectionAssert.AreEqual(new[] { "notes.md" }, (await catalog.ActivateAsync(Query(userHome), "alpha"))!.Files.ToArray());
    }

    [TestMethod]
    public async Task AChangeBehindALink_IsSeen_OnTheNextListing()
    {
        using var temp = TempDirectory.Create();
        var (userHome, skills, library) = Layout(temp.Path);
        await WriteSkillAsync(Path.Combine(library, "group", "alpha"), "alpha", "First.");
        await WriteSkillAsync(Path.Combine(library, "other", "beta"), "beta", "Second.");
        await WriteSkillAsync(Path.Combine(library, "files", "delta"), "delta", "Linked file.");
        await WriteSkillAsync(Path.Combine(skills, "plain"), "plain", "Not linked.");
        var group = Path.Combine(skills, "group");
        LinkDirectory(group, Path.Combine(library, "group"));
        Directory.CreateDirectory(Path.Combine(skills, "delta"));
        LinkFile(Path.Combine(skills, "delta", "SKILL.md"), Path.Combine(library, "files", "delta", "SKILL.md"));
        LinkDirectory(Path.Combine(skills, "later"), Path.Combine(library, "later"));
        var catalog = new SkillCatalog([new UserCodeAltaSkillRootProvider()]);
        var query = Query(userHome);

        Settle(temp.Path);
        CollectionAssert.AreEquivalent(new[] { "alpha", "delta", "plain" }, await NamesAsync(catalog, query));
        CollectionAssert.AreEquivalent(new[] { "alpha", "delta", "plain" }, await NamesAsync(catalog, query));
        Assert.AreEqual(1, catalog.RootWalkCount, "nothing changed: the root is not walked again");

        // A skill added in the folder a link leads to.
        await WriteSkillAsync(Path.Combine(library, "group", "gamma"), "gamma", "Third.");
        CollectionAssert.AreEquivalent(new[] { "alpha", "gamma", "delta", "plain" }, await NamesAsync(catalog, query));

        // The SKILL.md of a linked folder edited, then the target of a linked SKILL.md edited.
        Settle(temp.Path);
        await WriteSkillAsync(Path.Combine(library, "group", "alpha"), "alpha", "Edited and longer.");
        await WriteSkillAsync(Path.Combine(library, "files", "delta"), "delta", "Linked file, edited.");
        var edited = await catalog.ListAsync(query);
        Assert.AreEqual("Edited and longer.", edited.Single(static x => x.Name == "alpha").Description);
        Assert.AreEqual("Linked file, edited.", edited.Single(static x => x.Name == "delta").Description);

        // A link led somewhere else.
        Settle(temp.Path);
        Assert.AreEqual(4, (await catalog.ListAsync(query)).Count);
        Directory.Delete(group);
        LinkDirectory(group, Path.Combine(library, "other"));
        CollectionAssert.AreEquivalent(new[] { "beta", "delta", "plain" }, await NamesAsync(catalog, query));

        // A broken link whose folder appears.
        Settle(temp.Path);
        Assert.AreEqual(3, (await catalog.ListAsync(query)).Count);
        await WriteSkillAsync(Path.Combine(library, "later", "epsilon"), "epsilon", "Fixed link.");
        CollectionAssert.AreEquivalent(new[] { "beta", "delta", "epsilon", "plain" }, await NamesAsync(catalog, query));

        // The folder a link leads to removed.
        Settle(temp.Path);
        Assert.AreEqual(4, (await catalog.ListAsync(query)).Count);
        Directory.Delete(Path.Combine(library, "other"), recursive: true);
        CollectionAssert.AreEquivalent(new[] { "delta", "epsilon", "plain" }, await NamesAsync(catalog, query));

        // A link removed.
        Settle(temp.Path);
        Assert.AreEqual(3, (await catalog.ListAsync(query)).Count);
        Directory.Delete(Path.Combine(skills, "later"));
        CollectionAssert.AreEquivalent(new[] { "delta", "plain" }, await NamesAsync(catalog, query));
    }

    private static async Task<string[]> NamesAsync(SkillCatalog catalog, SkillCatalogQuery query)
        => [.. (await catalog.ListAsync(query)).Select(static x => x.Name)];

    private static (string UserHome, string Skills, string Library) Layout(string root)
    {
        var userHome = Path.Combine(root, "home");
        return (userHome, Path.Combine(userHome, ".alta", "skills"), Path.Combine(root, "library"));
    }

    private static void LinkDirectory(string link, string target)
        => CreateLink(() => Directory.CreateSymbolicLink(link, target));

    private static void LinkFile(string link, string target)
        => CreateLink(() => File.CreateSymbolicLink(link, target));

    private static void CreateLink(Action create)
    {
        // ERROR_PRIVILEGE_NOT_HELD: Windows without Developer Mode or elevation cannot create symbolic links.
        try { create(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex.HResult == unchecked((int)0x80070522))
        { Assert.Inconclusive("Creating a symbolic link requires platform permission."); }
    }

    // Puts the times of a tree a minute back, as a tree that nothing changed for a while; links are not followed.
    private static void Settle(string root)
    {
        var time = DateTime.UtcNow.AddMinutes(-1);
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(root, "*", options))
        {
            File.SetLastWriteTimeUtc(file, time);
        }

        foreach (var directory in Directory.EnumerateDirectories(root, "*", options).Append(root))
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
