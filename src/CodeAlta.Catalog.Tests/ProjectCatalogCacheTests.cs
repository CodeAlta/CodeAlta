using System.Collections;
using System.Reflection;

namespace CodeAlta.Catalog.Tests;

/// <summary>
/// A catalog reads and parses a project file again only when it changed: each load must give what a new catalog gives,
/// in the same order, and the descriptors it gives belong to the caller.
/// </summary>
[TestClass]
public sealed class ProjectCatalogCacheTests
{
    [TestMethod]
    public async Task Loads_GiveWhatANewCatalogGives_InTheSameOrder()
    {
        using var fixture = await Fixture.CreateAsync(12);
        // A legacy folder, and a flat file for the same project as a legacy one, as older catalogs have them.
        var legacy = (await fixture.Catalog.LoadAsync())[3];
        Directory.CreateDirectory(Path.Combine(fixture.Options.ProjectsRoot, legacy.Slug));
        File.Copy(legacy.SourcePath!, Path.Combine(fixture.Options.ProjectsRoot, legacy.Slug, "readme.md"));
        Directory.CreateDirectory(Path.Combine(fixture.Options.ProjectsRoot, "only-legacy"));
        await File.WriteAllTextAsync(Path.Combine(fixture.Options.ProjectsRoot, "only-legacy", "readme.md"),
            new CatalogYamlSerializer().SerializeProjectMarkdown(Project("only-legacy", Path.Combine(fixture.Root, "legacy"))));

        for (var round = 0; round < 3; round++)
        {
            var expected = await new ProjectCatalog(fixture.Options).LoadAsync();
            var actual = await fixture.Catalog.LoadAsync();
            CollectionAssert.AreEqual(expected.Select(Describe).ToArray(), actual.Select(Describe).ToArray());
        }
    }

    [TestMethod]
    public async Task TheDescriptorsOfALoad_AreTheCallersOwn()
    {
        using var fixture = await Fixture.CreateAsync(3);
        var first = await fixture.Catalog.LoadAsync();
        var before = first.Select(Describe).ToArray();
        foreach (var project in first)
        {
            project.DisplayName = "Changed";
            project.Archived = true;
            project.Tags.Add("changed");
            project.Spaces.Add("changed");
            project.Checkout.PathTemplate = "changed";
            project.SourcePath = null;
        }

        CollectionAssert.AreEqual(before, (await fixture.Catalog.LoadAsync()).Select(Describe).ToArray());
        var byId = await fixture.Catalog.GetByIdAsync(first[0].Id);
        Assert.AreNotSame(byId, await fixture.Catalog.GetByIdAsync(first[0].Id));
    }

    [TestMethod]
    public async Task FilesAddedChangedAndDeletedByAnotherWriter_AreSeenAtTheNextLoad()
    {
        using var fixture = await Fixture.CreateAsync(3);
        Assert.AreEqual(3, (await fixture.Catalog.LoadAsync()).Count);

        var other = new ProjectCatalog(fixture.Options);
        var added = await other.UpsertFromPathAsync(Path.Combine(fixture.Root, "work", "added"));
        Assert.AreEqual(4, (await fixture.Catalog.LoadAsync()).Count);

        added.Description = "Described elsewhere.";
        await other.SaveAsync(added);
        Assert.AreEqual("Described elsewhere.", (await fixture.Catalog.GetByIdAsync(added.Id))!.Description);

        Assert.IsTrue(await other.DeleteAsync(added));
        Assert.IsNull(await fixture.Catalog.GetByIdAsync(added.Id));
        Assert.AreEqual(3, (await fixture.Catalog.LoadAsync()).Count);
    }

    [TestMethod]
    public async Task ARenameOfTheSameLengthAtTheSameTime_IsSeenWhileTheWriteIsRecent()
    {
        using var fixture = await Fixture.CreateAsync(1);
        var project = (await fixture.Catalog.LoadAsync())[0];
        var path = project.SourcePath!;
        var time = DateTime.UtcNow + TimeSpan.FromHours(1);
        File.SetLastWriteTimeUtc(path, time);
        Assert.AreEqual(project.DisplayName, (await fixture.Catalog.LoadAsync())[0].DisplayName);

        var text = await File.ReadAllTextAsync(path);
        var renamed = new string('Z', project.DisplayName.Length);
        var line = text.Split('\n').Single(static line => line.StartsWith("display_name:", StringComparison.Ordinal));
        await File.WriteAllTextAsync(path, text.Replace(line, line.Replace(project.DisplayName, renamed, StringComparison.Ordinal), StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(path, time);
        Assert.AreEqual(text.Length, (await File.ReadAllTextAsync(path)).Length);
        Assert.AreEqual(renamed, (await fixture.Catalog.LoadAsync())[0].DisplayName);
    }

    [TestMethod]
    public async Task TheCatalogsOwnEdits_AreSeenAtOnce_EvenWhenTheyLeaveTheStampAsItWas()
    {
        using var fixture = await Fixture.CreateAsync(1);
        var project = (await fixture.Catalog.LoadAsync())[0];
        var path = project.SourcePath!;
        var old = DateTime.UtcNow - TimeSpan.FromHours(1);
        File.SetLastWriteTimeUtc(path, old);
        var snapshot = await fixture.Catalog.ReadDisplayNameAsync(project.Id, project.ProjectPath);
        var renamed = new string('Y', project.DisplayName.Length);
        Assert.AreEqual(ProjectDisplayNameRenameStatus.Updated,
            await fixture.Catalog.RenameDisplayNameAsync(project.Id, project.ProjectPath, snapshot!.SourcePath, snapshot.Revision, renamed));
        Assert.AreEqual(renamed, (await fixture.Catalog.LoadAsync())[0].DisplayName);

        File.SetLastWriteTimeUtc(path, old);
        _ = await fixture.Catalog.LoadAsync();
        var length = new FileInfo(path).Length;
        var archive = await fixture.Catalog.ReadArchiveAsync(project.Id, project.ProjectPath);
        Assert.AreEqual(ProjectDisplayNameRenameStatus.Updated,
            await fixture.Catalog.SetArchivedAsync(project.Id, project.ProjectPath, archive!.SourcePath, archive.Revision, false, true));
        File.SetLastWriteTimeUtc(path, old);
        Assert.AreNotEqual(length, new FileInfo(path).Length);
        Assert.IsTrue((await fixture.Catalog.LoadAsync())[0].Archived);

        Assert.AreEqual(ProjectSpacesStatus.Updated, await fixture.Catalog.UpdateSpacesAsync(project.Id, _ => ["personal"]));
        CollectionAssert.AreEqual(new[] { "personal" }, (await fixture.Catalog.LoadAsync())[0].Spaces.ToArray());

        var current = (await fixture.Catalog.LoadAsync())[0];
        current.Description = "Saved here.";
        await fixture.Catalog.SaveAsync(current);
        File.SetLastWriteTimeUtc(path, old);
        Assert.AreEqual("Saved here.", (await fixture.Catalog.LoadAsync())[0].Description);
    }

    [TestMethod]
    public async Task AnInvalidFile_ThrowsAtEachLoad_AndAFixedOneIsReadAgain()
    {
        using var fixture = await Fixture.CreateAsync(2);
        var path = (await fixture.Catalog.LoadAsync())[1].SourcePath!;
        var text = await File.ReadAllTextAsync(path);

        await File.WriteAllTextAsync(path, text.Replace("id: ", "id: not-an-id-", StringComparison.Ordinal));
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => fixture.Catalog.LoadAsync());
        }

        await File.WriteAllTextAsync(path, text);
        Assert.AreEqual(2, (await fixture.Catalog.LoadAsync()).Count);
    }

    [TestMethod]
    [DoNotParallelize] // It changes the current folder of the process.
    public async Task ARelativeProjectPath_IsResolvedAgainstTheCurrentFolderAtEachLoad()
    {
        using var fixture = await Fixture.CreateAsync(0);
        await File.WriteAllTextAsync(Path.Combine(fixture.Options.ProjectsRoot, "relative.md"),
            new CatalogYamlSerializer().SerializeProjectMarkdown(Project("relative", "some-folder")));
        var current = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = fixture.Root;
            Assert.AreEqual(Path.GetFullPath(Path.Combine(fixture.Root, "some-folder")), (await fixture.Catalog.LoadAsync())[0].ProjectPath);
            var other = Path.Combine(fixture.Root, "work");
            Directory.CreateDirectory(other);
            Environment.CurrentDirectory = other;
            Assert.AreEqual(Path.GetFullPath(Path.Combine(other, "some-folder")), (await fixture.Catalog.LoadAsync())[0].ProjectPath);
        }
        finally
        {
            Environment.CurrentDirectory = current;
        }
    }

    [TestMethod]
    public void Copy_CarriesEveryPropertyOfADescriptor_AndSharesNoList()
    {
        var number = 0;
        var original = new ProjectDescriptor();
        foreach (var property in Writable(typeof(ProjectDescriptor)))
        {
            property.SetValue(original, Value(property.PropertyType, ref number));
        }

        foreach (var property in Writable(typeof(CheckoutRule)))
        {
            property.SetValue(original.Checkout, Value(property.PropertyType, ref number));
        }

        var copy = ProjectCatalog.Copy(original);

        foreach (var property in Writable(typeof(ProjectDescriptor)))
        {
            var (expected, actual) = (property.GetValue(original), property.GetValue(copy));
            if (expected is IList list)
            {
                Assert.AreNotSame(expected, actual, property.Name);
                CollectionAssert.AreEqual(list, (IList)actual!, property.Name);
            }
            else if (expected is CheckoutRule)
            {
                Assert.AreNotSame(expected, actual, property.Name);
            }
            else
            {
                Assert.AreEqual(expected, actual, property.Name);
            }
        }

        foreach (var property in Writable(typeof(CheckoutRule)))
        {
            Assert.AreEqual(property.GetValue(original.Checkout), property.GetValue(copy.Checkout), $"Checkout.{property.Name}");
        }

        static IEnumerable<PropertyInfo> Writable(Type type)
            => type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(static property => property.CanWrite);

        static object Value(Type type, ref int number)
        {
            number++;
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            if (underlying == typeof(string)) return $"value-{number}";
            if (underlying == typeof(bool)) return true;
            if (underlying == typeof(int)) return number;
            if (type == typeof(List<string>)) return new List<string> { $"a-{number}", $"b-{number}" };
            if (type == typeof(CheckoutRule)) return new CheckoutRule();
            Assert.Fail($"The test does not know how to fill a {type}; teach it, and ProjectCatalog.Copy, that type.");
            return null!;
        }
    }

    private static ProjectDescriptor Project(string slug, string path) => new()
    {
        Id = ProjectId.NewVersion7().ToString(),
        Slug = slug,
        Name = slug,
        DisplayName = slug,
        ProjectPath = path,
    };

    private static string Describe(ProjectDescriptor project)
        => string.Join("|", project.Id, project.Slug, project.Name, project.DisplayName, project.ProjectPath, project.DefaultBranch,
            project.Description, string.Join(",", project.Tags), string.Join(",", project.Spaces), project.Archived,
            project.Checkout.PathTemplate, project.SourcePath, project.MarkdownBody);

    private sealed class Fixture : IDisposable
    {
        private Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "codealta-project-cache-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Options = new CatalogOptions { GlobalRoot = Root };
            Directory.CreateDirectory(Options.ProjectsRoot);
            Catalog = new ProjectCatalog(Options);
        }

        public string Root { get; }

        public CatalogOptions Options { get; }

        public ProjectCatalog Catalog { get; }

        public static async Task<Fixture> CreateAsync(int projects)
        {
            var fixture = new Fixture();
            var writer = new ProjectCatalog(fixture.Options);
            for (var index = 0; index < projects; index++)
            {
                var project = await writer.UpsertFromPathAsync(Path.Combine(fixture.Root, "work", $"project-{index:00}"));
                project.Tags = ["dotnet", $"tag-{index}"];
                project.Spaces = index % 2 == 0 ? ["work"] : [];
                await writer.SaveAsync(project);
            }

            return fixture;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
