namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class SubProjectCatalogTests
{
    private const string Map = """
        [labels.backend]
        name = "Backend"
        [labels.payments]
        name = "Payments"
        parent = "backend"

        [projects.billing]
        path = "services/billing"
        name = "Billing"
        description = "Invoices."
        labels = ["payments"]

        [projects.web]
        path = "apps/web"

        [projects.core]
        path = "services/billing/core"

        [projects.reports]
        path = "tools/reports"
        optional = true
        """;

    [TestMethod]
    public async Task TheSubProjectsOfAMap_AreListedWithoutAFile_AndKeepTheirIdentifierOnceUsed()
    {
        using var fixture = new Fixture();
        var repository = await fixture.RepositoryAsync(Map, spaces: ["work"]);

        var projects = await fixture.Projects.LoadAsync();
        CollectionAssert.AreEquivalent(new[] { "contoso", "Billing", "web", "core" }, projects.Select(static project => project.DisplayName).ToArray(),
            "An optional entry is listed only for those who add it.");
        Assert.AreEqual(1, Directory.GetFiles(fixture.Options.ProjectsRoot).Length, "Nothing is written for a sub-project nobody used.");
        var billing = projects.Single(static project => project.DisplayName == "Billing");
        Assert.AreEqual(ProjectCatalog.DeriveSubProjectId(repository.Id, "billing"), billing.Id);
        Assert.AreEqual((repository.Id, "services/billing", "billing"), (billing.ParentId, billing.SubPath, billing.DeclaredKey));
        Assert.AreEqual("contoso-billing", billing.Slug);
        Assert.AreEqual(("contoso", repository.Slug, repository.ProjectPath), (billing.RepositoryName, billing.RepositorySlug, billing.RepositoryPath));
        Assert.AreEqual("Invoices.", billing.Description);
        CollectionAssert.AreEqual(new[] { "work" }, billing.Spaces, "A sub-project is in the spaces of its repository.");
        CollectionAssert.AreEqual(new[] { "contoso.payments" }, billing.DeclaredLabels);
        CollectionAssert.AreEqual(new[] { repository.ProjectPath }, billing.InheritedRoots);
        var core = projects.Single(static project => project.DeclaredKey == "core");
        CollectionAssert.AreEqual(new[] { billing.ProjectPath, repository.ProjectPath }, core.InheritedRoots, "The nearest declared folder first.");
        Assert.AreEqual(billing.Id, (await fixture.Projects.LoadAsync()).Single(static project => project.DeclaredKey == "billing").Id, "Every load gives the same identifier.");

        var used = await fixture.Projects.EnsurePersistedAsync(billing.Copy());
        Assert.AreEqual(billing.Id, used.Id);
        Assert.AreEqual(2, Directory.GetFiles(fixture.Options.ProjectsRoot).Length);
        var file = await File.ReadAllTextAsync(used.SourcePath!);
        Assert.IsFalse(file.Contains("spaces:", StringComparison.Ordinal), "The spaces of the repository are not copied into the file.");
        Assert.IsFalse(file.Contains("Invoices", StringComparison.Ordinal), "What the map says stays in the map.");
        var reloaded = (await fixture.Projects.LoadAsync()).Single(project => project.Id == used.Id);
        Assert.AreEqual(("Billing", "Invoices.", repository.Id), (reloaded.DisplayName, reloaded.Description, reloaded.ParentId));
        CollectionAssert.AreEqual(new[] { "work" }, reloaded.Spaces);
    }

    [TestMethod]
    public async Task AFolderAddedByHand_IsRecognized_AndKeepsItsFileAndIdentifier()
    {
        using var fixture = new Fixture();
        var repository = await fixture.RepositoryAsync(Map);
        var web = await fixture.Projects.UpsertFromPathAsync(Path.Combine(repository.ProjectPath, "apps", "web"));
        Assert.AreEqual(ProjectSpacesStatus.Unsupported, await fixture.Projects.UpdateSpacesAsync(web.Id, _ => ["other"]), "A sub-project follows the spaces of its repository.");

        var listed = (await fixture.Projects.LoadAsync()).Single(project => project.ProjectPath == web.ProjectPath);
        Assert.AreEqual(web.Id, listed.Id, "Nothing is migrated.");
        Assert.AreEqual((repository.Id, "web"), (listed.ParentId, listed.DeclaredKey));
        Assert.AreEqual(1, (await fixture.Projects.LoadAsync()).Count(project => project.ProjectPath == web.ProjectPath));
    }

    [TestMethod]
    public async Task AnEntryThatLeavesTheMap_Disappears_UnlessItWasUsed()
    {
        using var fixture = new Fixture();
        var repository = await fixture.RepositoryAsync(Map);
        var billing = (await fixture.Projects.LoadAsync()).Single(static project => project.DeclaredKey == "billing");
        await fixture.Projects.EnsurePersistedAsync(billing);

        await fixture.WriteMapAsync(repository, "[projects.other]\npath = \"apps/other\"\n");
        var projects = await fixture.Projects.LoadAsync();
        Assert.IsFalse(projects.Any(static project => project.DisplayName == "web"), "A sub-project nobody used goes with its entry.");
        var plain = projects.Single(project => project.Id == billing.Id);
        Assert.IsNull(plain.ParentId, "A used one stays, a plain project in a subfolder.");
        Assert.AreEqual("billing", plain.DisplayName);
    }

    [TestMethod]
    public async Task AFolderThatMoves_KeepsItsProject_ByItsKey()
    {
        using var fixture = new Fixture();
        var repository = await fixture.RepositoryAsync(Map);
        var billing = await fixture.Projects.EnsurePersistedAsync((await fixture.Projects.LoadAsync()).Single(static project => project.DeclaredKey == "billing"));

        var moved = Directory.CreateDirectory(Path.Combine(repository.ProjectPath, "domains", "billing")).FullName;
        Directory.Delete(billing.ProjectPath, recursive: true);
        await fixture.WriteMapAsync(repository, Map.Replace("path = \"services/billing\"", "path = \"domains/billing\"", StringComparison.Ordinal));
        var projects = await fixture.Projects.LoadAsync();
        var found = projects.Single(project => project.Id == billing.Id);
        Assert.AreEqual((moved, "domains/billing"), (found.ProjectPath, found.SubPath));
        Assert.AreEqual(1, projects.Count(project => project.Id == billing.Id));
    }

    [TestMethod]
    public async Task OptionalAndHiddenSubProjects_AreChosenByTheUser()
    {
        using var fixture = new Fixture();
        var repository = await fixture.RepositoryAsync(Map);
        var map = (await fixture.Projects.ReadRepositoryMapAsync(repository.Id))!;
        Assert.IsNull(map.Entries.Single(static entry => entry.Entry.Key == "reports").Project);
        Assert.AreEqual(0, map.Problems.Count);

        var (added, reports) = await fixture.Projects.SetSubProjectShownAsync(repository.Id, "reports", shown: true);
        Assert.AreEqual(SubProjectShowStatus.Updated, added);
        Assert.IsNotNull(reports!.SourcePath);
        Assert.IsTrue((await fixture.Projects.LoadAsync()).Any(static project => project.DeclaredKey == "reports" && !project.Archived));

        Assert.AreEqual(SubProjectShowStatus.Updated, (await fixture.Projects.SetSubProjectShownAsync(repository.Id, "web", shown: false)).Status);
        var web = (await fixture.Projects.LoadAsync()).Single(static project => project.DeclaredKey == "web");
        Assert.IsTrue(web.Archived, "Hidden is archived, in a file of the user.");
        Assert.AreEqual(SubProjectShowStatus.Updated, (await fixture.Projects.SetSubProjectShownAsync(repository.Id, "web", shown: true)).Status);
        Assert.IsFalse((await fixture.Projects.LoadAsync()).Single(static project => project.DeclaredKey == "web").Archived);
        Assert.AreEqual(SubProjectShowStatus.NotFound, (await fixture.Projects.SetSubProjectShownAsync(repository.Id, "missing", shown: true)).Status);
    }

    [TestMethod]
    public async Task TheSetupOfTheUser_FallsBackToTheRepository()
    {
        using var fixture = new Fixture();
        var repository = await fixture.RepositoryAsync(Map);
        Assert.AreEqual(ProjectSpacesStatus.Updated, await fixture.Projects.UpdateSandboxAsync(repository.Id, "strict"));
        Assert.AreEqual(ProjectSpacesStatus.Updated, await fixture.Projects.UpdateSkillFoldersAsync(repository.Id, _ => ["~/skills"]));
        var web = (await fixture.Projects.LoadAsync()).Single(static project => project.DeclaredKey == "web");
        Assert.AreEqual(("strict", null), (web.EffectiveSandbox, web.Sandbox));
        CollectionAssert.AreEqual(new[] { "~/skills" }, web.EffectiveSkillFolders.ToArray());

        Assert.AreEqual(ProjectSpacesStatus.Updated, await fixture.Projects.UpdateSandboxAsync(web.Id, "off"), "An edit of a sub-project nobody used writes its file first.");
        web = (await fixture.Projects.LoadAsync()).Single(static project => project.DeclaredKey == "web");
        Assert.AreEqual(("off", web.Id), (web.EffectiveSandbox, ProjectCatalog.DeriveSubProjectId(repository.Id, "web")));
    }

    [TestMethod]
    public async Task AMapIsReadForAProjectNoOtherMapDeclares_AndTheNearerRepositoryWins()
    {
        using var fixture = new Fixture();
        var repository = await fixture.RepositoryAsync(Map);
        // A declared folder with a map of its own: that map is not read.
        await fixture.WriteMapAsync(Path.Combine(repository.ProjectPath, "services", "billing"), "[projects.inner]\npath = \"inner\"\n");
        Directory.CreateDirectory(Path.Combine(repository.ProjectPath, "services", "billing", "inner"));
        // A project of the repository that no entry declares, with a map that declares a folder the root declares too.
        var tools = await fixture.Projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(repository.ProjectPath, "tools")).FullName);
        await fixture.WriteMapAsync(tools.ProjectPath, "[projects.reports]\npath = \"reports\"\n");

        var projects = await fixture.Projects.LoadAsync();
        Assert.IsFalse(projects.Any(static project => project.DeclaredKey == "inner"));
        var reports = projects.Single(static project => project.DeclaredKey == "reports");
        Assert.AreEqual(tools.Id, reports.ParentId, "The folder belongs to the nearer repository.");
        Assert.IsNull(projects.Single(project => project.Id == tools.Id).ParentId);
    }

    [TestMethod]
    public async Task AnArchivedRepository_HidesItsSubProjects_AndAReadOnlyCatalogWritesNothing()
    {
        using var fixture = new Fixture();
        var repository = await fixture.RepositoryAsync(Map);
        repository.Archived = true;
        await fixture.Projects.SaveAsync(repository);
        Assert.IsTrue((await fixture.Projects.LoadAsync()).Where(static project => project.IsSubProject).All(static project => project.Archived));

        var readOnly = new ProjectCatalog(fixture.Options) { IsReadOnly = true };
        var web = (await readOnly.LoadAsync()).Single(static project => project.DeclaredKey == "web");
        var kept = await readOnly.EnsurePersistedAsync(web);
        Assert.AreEqual(web.Id, kept.Id);
        Assert.IsNull(kept.SourcePath);
        Assert.AreEqual(1, Directory.GetFiles(fixture.Options.ProjectsRoot).Length);
    }

    [TestMethod]
    public async Task AMapThatCannotBeRead_ListsNoSubProject_AndTheOthersStay()
    {
        using var fixture = new Fixture();
        var repository = await fixture.RepositoryAsync("[projects.a\n");
        var projects = await fixture.Projects.LoadAsync();
        Assert.AreEqual(1, projects.Count);
        var map = (await fixture.Projects.ReadRepositoryMapAsync(repository.Id))!;
        Assert.AreEqual("file.invalid", map.Problems.Single().Code);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "codealta-subprojects-" + Guid.NewGuid().ToString("N"));
        public CatalogOptions Options { get; }
        public ProjectCatalog Projects { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Options = new CatalogOptions { GlobalRoot = Path.Combine(Root, "global") };
            Projects = new ProjectCatalog(Options);
        }

        // The repository contoso, with the folders of the map and the map itself.
        public async Task<ProjectDescriptor> RepositoryAsync(string map, IReadOnlyList<string>? spaces = null)
        {
            var root = Directory.CreateDirectory(Path.Combine(Root, "code", "contoso")).FullName;
            foreach (var folder in new[] { "services/billing/core", "apps/web", "tools/reports" })
                Directory.CreateDirectory(Path.Combine(root, folder));
            var project = await Projects.UpsertFromPathAsync(root);
            if (spaces is not null)
            {
                project.Spaces = [.. spaces];
                await Projects.SaveAsync(project);
            }

            await WriteMapAsync(root, map);
            return project;
        }

        public Task WriteMapAsync(ProjectDescriptor repository, string map) => WriteMapAsync(repository.ProjectPath, map);

        public async Task WriteMapAsync(string root, string map)
        {
            var path = ProjectManifest.PathOf(root);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, map);
            // A stamp the cache cannot take for the one of the text before.
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-10 - Random.Shared.Next(1000)));
        }

        public void Dispose() => Directory.Delete(Root, true);
    }
}
