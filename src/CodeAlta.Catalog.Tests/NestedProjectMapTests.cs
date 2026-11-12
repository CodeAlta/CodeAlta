using System.Diagnostics;

namespace CodeAlta.Catalog.Tests;

// A project map inside a sub-project (#433): the sub-project is a repository too, at any depth up to a bound.
[TestClass]
public sealed class NestedProjectMapTests
{
    private const string OuterMap = """
        [labels.repos]
        name = "Repos"

        [projects.billing]
        path = "repos/billing"
        labels = ["repos"]

        [projects.checkout]
        path = "repos/checkout"
        labels = ["repos"]
        """;

    private const string BillingMap = """
        [labels.apps]
        name = "Apps"
        [labels.libs]
        name = "Libs"

        [projects.invoices-api]
        path = "services/invoices-api"
        labels = ["apps"]

        [projects.ledger]
        path = "libs/ledger"
        labels = ["libs"]
        """;

    [TestMethod]
    [DataRow(true, DisplayName = "A separate git clone inside the repository")]
    [DataRow(false, DisplayName = "A plain folder of the same repository")]
    public async Task ASubProjectWithAMapOfItsOwn_IsARepositoryToo(bool clone)
    {
        using var fixture = new Fixture();
        var (platform, billingFolder) = await fixture.PlatformAsync(clone, spaces: ["work"]);

        var projects = await fixture.Projects.LoadAsync();
        var billing = projects.Single(static project => project.DeclaredKey == "billing");
        Assert.AreEqual((ProjectCatalog.DeriveSubProjectId(platform.Id, "billing"), platform.Id), (billing.Id, billing.ParentId));
        var invoices = projects.Single(static project => project.DeclaredKey == "invoices-api");
        Assert.AreEqual(ProjectCatalog.DeriveSubProjectId(billing.Id, "invoices-api"), invoices.Id, "Its identifier comes from its repository and its key, as one level does.");
        Assert.AreEqual((billing.Id, "services/invoices-api", "billing", billingFolder), (invoices.ParentId, invoices.SubPath, invoices.RepositoryName, invoices.RepositoryPath));
        Assert.AreEqual("platform-map-billing-invoices-api", invoices.Slug);
        CollectionAssert.AreEqual(new[] { billingFolder, platform.ProjectPath }, invoices.InheritedRoots, "Its repository, then what its repository inherits.");
        CollectionAssert.AreEqual(invoices.InheritedRoots, fixture.Projects.GetInheritedRoots(invoices.Id).ToList());
        Assert.AreEqual(new ProjectRepositoryRef(platform.Id, platform.Slug, "platform-map", platform.ProjectPath), invoices.OuterRepositories.Single());
        CollectionAssert.AreEqual(new[] { "work" }, invoices.Spaces, "The spaces of the outer repository, through its repository.");
        CollectionAssert.AreEqual(new[] { ProjectCatalog.DeclaredLabelId(billing.Slug, "apps") }, invoices.DeclaredLabels);
        CollectionAssert.AreEqual(new[] { billingFolder }, ProjectResourceRoots.WriteRoots(invoices).ToArray(), "Its sessions write its own repository.");

        var inner = (await fixture.Projects.ReadRepositoryMapAsync(billing.Id))!;
        Assert.IsNull(inner.ClaimedBy, "The map of a sub-project counts.");
        Assert.AreEqual(0, inner.Problems.Count);
        Assert.IsTrue(inner.Entries.All(static entry => entry.Project is not null), "Every entry is a project.");
        var outer = (await fixture.Projects.ReadRepositoryMapAsync(platform.Id))!;
        Assert.AreEqual(2, outer.Entries.Single(static entry => entry.Entry.Key == "billing").SubProjects);
        Assert.AreEqual(0, outer.Problems.Count);

        var labels = await new SpaceCatalog(fixture.Projects).Labels.LoadAsync();
        var apps = labels.Single(static label => label.DeclaredKey == "apps");
        Assert.AreEqual(("platform-map-billing.apps", billing.Id), (apps.Id, apps.Repository));
        Assert.IsTrue(labels.Any(static label => label.Id == "platform-map.repos"));
    }

    [TestMethod]
    public async Task AProjectAddedOnItsOwn_ThenReachedThroughAnOuterMap_KeepsItsIdentity_AndItsSubProjects()
    {
        using var fixture = new Fixture();
        var billingFolder = fixture.Billing(Path.Combine(fixture.Root, "code", "platform-map", "repos", "billing"), clone: true);
        var alone = await fixture.Projects.UpsertFromPathAsync(billingFolder);
        var invoices = (await fixture.Projects.LoadAsync()).Single(static project => project.DeclaredKey == "invoices-api");
        Assert.AreEqual(alone.Id, invoices.ParentId);
        invoices = await fixture.Projects.EnsurePersistedAsync(invoices);

        var platform = await fixture.Projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(fixture.Root, "code", "platform-map", "repos", "checkout")).Parent!.Parent!.FullName);
        await fixture.WriteMapAsync(platform.ProjectPath, OuterMap);
        var projects = await fixture.Projects.LoadAsync();
        var billing = projects.Single(project => project.ProjectPath == billingFolder);
        Assert.AreEqual((alone.Id, platform.Id), (billing.Id, billing.ParentId), "No second project for the folder: the one added keeps its identifier.");
        var found = projects.Single(project => project.ProjectPath == invoices.ProjectPath);
        Assert.AreEqual((invoices.Id, alone.Id), (found.Id, found.ParentId), "Its sub-projects keep theirs, and so their sessions.");
        Assert.AreEqual(projects.Count, projects.Select(static project => project.ProjectPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.AreEqual(projects.Count, projects.Select(static project => project.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [TestMethod]
    public async Task MapsNestToABound_AndADeeperMapSaysWhyItIsNotRead()
    {
        using var fixture = new Fixture();
        // r › a › b › c › d: the map of d would be the fifth level.
        var root = Directory.CreateDirectory(Path.Combine(fixture.Root, "code", "r")).FullName;
        var folder = root;
        foreach (var key in new[] { "a", "b", "c", "d", "e" })
        {
            Directory.CreateDirectory(Path.Combine(folder, key));
            await fixture.WriteMapAsync(folder, $"[projects.{key}]\npath = \"{key}\"\n");
            folder = Path.Combine(folder, key);
        }

        var repository = await fixture.Projects.UpsertFromPathAsync(root);
        var projects = await fixture.Projects.LoadAsync();
        var d = projects.Single(static project => project.DeclaredKey == "d");
        var c = projects.Single(static project => project.DeclaredKey == "c");
        Assert.AreEqual(c.Id, d.ParentId);
        Assert.AreEqual(ProjectManifest.MaximumDepth, d.OuterRepositories.Count + 1, "Four levels of maps.");
        Assert.IsFalse(projects.Any(static project => project.DeclaredKey == "e"), "The fifth map is not read.");

        var deep = (await fixture.Projects.ReadRepositoryMapAsync(d.Id))!;
        Assert.AreEqual(c.Id, deep.ClaimedBy);
        Assert.AreEqual("map.tooDeep", deep.Problems[0].Code);
        Assert.IsNull(deep.Entries.Single().Project);
        var above = (await fixture.Projects.ReadRepositoryMapAsync(c.Id))!;
        Assert.AreEqual(("d", "map.tooDeep"), (above.Problems.Single().Key, above.Problems.Single().Code), "The map above says it on the entry.");
        Assert.IsFalse((await fixture.Projects.LoadRepositoryMapsAsync()).Any(map => map.Repository.Id == d.Id));
        Assert.AreEqual(repository.Id, projects.Single(static project => project.DeclaredKey == "a").ParentId);
    }

    [TestMethod]
    public async Task ALinkThatLeadsBackToItsRepository_IsReadOnce_AndSaysSo()
    {
        using var fixture = new Fixture();
        var (platform, billingFolder) = await fixture.PlatformAsync(clone: true);
        var loop = Path.Combine(billingFolder, "self");
        if (!Fixture.TryLink(loop, billingFolder)) Assert.Inconclusive("Creating a link requires a platform permission.");
        try
        {
            await fixture.WriteMapAsync(billingFolder, BillingMap + "\n[projects.self]\npath = \"self\"\n");
            var projects = await fixture.Projects.LoadAsync();
            var self = projects.Single(static project => project.DeclaredKey == "self");
            Assert.AreEqual(1, projects.Count(static project => project.DeclaredKey == "invoices-api"), "The map is read once, not again through the link.");
            Assert.IsFalse(projects.Any(project => project.ParentId == self.Id));

            var map = (await fixture.Projects.ReadRepositoryMapAsync(self.Id))!;
            Assert.AreEqual(("map.loop", self.ParentId), (map.Problems[0].Code, map.ClaimedBy));
            var billing = (await fixture.Projects.ReadRepositoryMapAsync(self.ParentId!))!;
            Assert.AreEqual(("self", "map.loop"), (billing.Problems.Single().Key, billing.Problems.Single().Code));
            Assert.AreEqual(platform.Id, projects.Single(project => project.Id == self.ParentId).ParentId);
        }
        finally
        {
            Directory.Delete(loop); // The link alone, never what it leads to.
        }
    }

    [TestMethod]
    public async Task AFolderTheOuterAndTheInnerMapsBothDeclare_BelongsToTheInnerOne_AndTheOuterMapSaysSo()
    {
        using var fixture = new Fixture();
        var (platform, billingFolder) = await fixture.PlatformAsync(clone: true);
        await fixture.WriteMapAsync(platform.ProjectPath, OuterMap + "\n[projects.all-ledger]\npath = \"repos/billing/libs/ledger\"\n");

        var projects = await fixture.Projects.LoadAsync();
        var billing = projects.Single(static project => project.DeclaredKey == "billing");
        var ledgers = projects.Where(static project => project.ProjectPath.EndsWith(Path.Combine("libs", "ledger"), StringComparison.Ordinal)).ToArray();
        Assert.AreEqual(1, ledgers.Length, "One project for the folder.");
        Assert.AreEqual((billing.Id, "ledger"), (ledgers[0].ParentId, ledgers[0].DeclaredKey), "The nearer map wins.");

        var outer = (await fixture.Projects.ReadRepositoryMapAsync(platform.Id))!;
        var entry = outer.Entries.Single(static entry => entry.Entry.Key == "all-ledger");
        Assert.AreEqual((null, billing.Id), (entry.Project, entry.ClaimedBy));
        var problem = outer.Problems.Single();
        Assert.AreEqual(("all-ledger", "folder.claimed"), (problem.Key, problem.Code));
        StringAssert.Contains(problem.Message, "billing");
        Assert.AreEqual(0, (await fixture.Projects.ReadRepositoryMapAsync(billing.Id))!.Problems.Count);
    }

    [TestMethod]
    public async Task AHiddenSubProject_HidesTheSubProjectsOfItsMap_AndAnOptionalOneNobodyAddedReadsNoMap()
    {
        using var fixture = new Fixture();
        var (platform, _) = await fixture.PlatformAsync(clone: true);
        Assert.AreEqual(SubProjectShowStatus.Updated, (await fixture.Projects.SetSubProjectShownAsync(platform.Id, "billing", shown: false)).Status);
        var projects = await fixture.Projects.LoadAsync();
        Assert.IsTrue(projects.Single(static project => project.DeclaredKey == "billing").Archived);
        Assert.IsTrue(projects.Single(static project => project.DeclaredKey == "invoices-api").Archived, "Hidden with its repository.");

        using var other = new Fixture();
        var (fresh, _) = await other.PlatformAsync(clone: true, outerMap: "[projects.billing]\npath = \"repos/billing\"\noptional = true\n");
        var listed = await other.Projects.LoadAsync();
        Assert.IsFalse(listed.Any(static project => project.DeclaredKey is "billing" or "invoices-api"), "The map of an optional sub-project nobody added is not read.");
        Assert.AreEqual(SubProjectShowStatus.Updated, (await other.Projects.SetSubProjectShownAsync(fresh.Id, "billing", shown: true)).Status);
        Assert.AreEqual(2, (await other.Projects.LoadAsync()).Count(project => project.ParentId is not null && project.ParentId != fresh.Id));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "codealta-nested-maps-" + Guid.NewGuid().ToString("N"));
        public CatalogOptions Options { get; }
        public ProjectCatalog Projects { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Options = new CatalogOptions { GlobalRoot = Path.Combine(Root, "global") };
            Projects = new ProjectCatalog(Options);
        }

        // platform-map, whose map declares repos/billing and repos/checkout; billing has a map of its own.
        public async Task<(ProjectDescriptor Platform, string Billing)> PlatformAsync(bool clone, IReadOnlyList<string>? spaces = null, string outerMap = OuterMap)
        {
            var root = Directory.CreateDirectory(Path.Combine(Root, "code", "platform-map")).FullName;
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            Directory.CreateDirectory(Path.Combine(root, "repos", "checkout"));
            var billing = Billing(Path.Combine(root, "repos", "billing"), clone);
            await WriteMapAsync(billing, BillingMap);
            var project = await Projects.UpsertFromPathAsync(root);
            if (spaces is not null)
            {
                project.Spaces = [.. spaces];
                await Projects.SaveAsync(project);
            }

            await WriteMapAsync(root, outerMap);
            return (project, billing);
        }

        // The folders of billing, a git clone of its own or a plain folder, with its map.
        public string Billing(string folder, bool clone)
        {
            Directory.CreateDirectory(Path.Combine(folder, "services", "invoices-api"));
            Directory.CreateDirectory(Path.Combine(folder, "libs", "ledger"));
            if (clone) Directory.CreateDirectory(Path.Combine(folder, ".git"));
            var path = ProjectManifest.PathOf(folder);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, BillingMap);
            return Path.GetFullPath(folder);
        }

        public async Task WriteMapAsync(string root, string map)
        {
            var path = ProjectManifest.PathOf(root);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, map);
            // A stamp the cache cannot take for the one of the text before.
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-10 - Random.Shared.Next(1000)));
        }

        // A link to a folder: a junction on Windows, which needs no permission; a symbolic link elsewhere.
        public static bool TryLink(string link, string target)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
                    mklink.StandardOutput.ReadToEnd();
                    mklink.WaitForExit();
                }
                else
                {
                    Directory.CreateSymbolicLink(link, target);
                }

                return Directory.Exists(link);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        public void Dispose() => Directory.Delete(Root, true);
    }
}
