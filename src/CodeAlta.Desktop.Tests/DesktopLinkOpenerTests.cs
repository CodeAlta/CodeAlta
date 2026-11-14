using CodeAlta.Browser.Links;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopLinkOpenerTests
{
    private const string Epoch = "3b0a7c1e-52c9-4f0b-8a55-6d0c2f9e1b77";
    private const string Address = "https://example.invalid/";

    [TestMethod]
    public async Task ALink_OpensWhereItsSpaceSays_WithTheSettingOfItsProjectOverIt()
    {
        using var fixture = new Fixture();
        var plans = new List<(string? Browser, string? Profile)>();
        var started = new List<LinkLaunch>();
        var opened = new List<string>();
        SpaceLinks? project = null;
        var opener = new DesktopLinkOpener(fixture.Spaces, (_, _) => Task.FromResult(project),
            (sessionId, _, _, _) => Task.FromResult<DesktopLinkOrigin?>(sessionId == "work-session" ? new("work", "/projects/contoso") : null),
            (address, browser, profile) => { plans.Add((browser, profile)); return new LinkLaunch("/bin/chrome", [address]); },
            launch => { started.Add(launch); return true; }, address => { opened.Add(address); return true; });

        // Without a setting, the system opens the link.
        Assert.IsTrue(await opener.OpenAsync(Address, "work-session", null, null, default));
        Assert.AreEqual(0, plans.Count);
        CollectionAssert.AreEqual(new[] { Address }, opened);

        Assert.IsTrue((await fixture.Spaces.UpdateSettingsAsync("work", settings => settings with { Links = new SpaceLinks("chrome", "Work") })).Succeeded);
        Assert.IsTrue(await opener.OpenAsync(Address, "work-session", null, null, default));
        Assert.AreEqual(("chrome", "Work"), plans.Single());
        Assert.AreEqual(1, started.Count);

        project = new SpaceLinks(null, "Contoso");
        Assert.IsTrue(await opener.OpenAsync(Address, "work-session", null, null, default));
        Assert.AreEqual(("chrome", "Contoso"), plans[^1], "The project replaces the profile of its space.");

        // A link of another space, here the default one without setting, opens as the system opens it.
        Assert.IsTrue(await opener.OpenAsync(Address, "other-session", null, null, default));
        Assert.AreEqual(2, opened.Count);
    }

    [TestMethod]
    public async Task ABrowserThatCannotStart_LeavesTheLinkToTheSystem()
    {
        using var fixture = new Fixture();
        Assert.IsTrue((await fixture.Spaces.UpdateSettingsAsync("work", settings => settings with { Links = new SpaceLinks("firefox") })).Succeeded);
        var opened = 0;
        var origin = (string? _, string? _, string? _, CancellationToken _) => Task.FromResult<DesktopLinkOrigin?>(new("work", null));
        var failing = new DesktopLinkOpener(fixture.Spaces, (_, _) => Task.FromResult<SpaceLinks?>(null), origin,
            (address, _, _) => new LinkLaunch("/missing/firefox", [address]), _ => false, _ => { opened++; return true; });
        Assert.IsTrue(await failing.OpenAsync(Address, "session", null, null, default));
        var unknown = new DesktopLinkOpener(fixture.Spaces, (_, _) => Task.FromResult<SpaceLinks?>(null), origin,
            (_, _, _) => null, _ => throw new AssertFailedException("nothing to start"), _ => { opened++; return true; });
        Assert.IsTrue(await unknown.OpenAsync(Address, "session", null, null, default));
        Assert.AreEqual(2, opened);
    }

    [TestMethod]
    public async Task AMarkdownLink_IsOpenedWithTheSessionTheProjectAndTheWindowItWasFollowedFrom()
    {
        var calls = new List<(string Address, string? Session, string? Project, string? Window)>();
        var service = new MarkdownLinksService(Epoch, _ => throw new AssertFailedException("the opener that knows where the link was followed opens it"))
        {
            OpenFrom = (address, session, project, window, _) => { calls.Add((address, session, project, window)); return Task.FromResult(true); },
        };
        Assert.AreEqual("ok", (await service.OpenFromAsync(new(Epoch, Address, "session", "project"), "window-2", default)).Status);
        Assert.AreEqual((Address, "session", "project", "window-2"), calls.Single());
        var failing = new MarkdownLinksService(Epoch, _ => true) { OpenFrom = (_, _, _, _, _) => throw new IOException("private") };
        Assert.AreEqual("failed", (await failing.OpenAsync(new(Epoch, Address), default)).Status);
    }

    [TestMethod]
    public async Task ADocumentOutsideASession_UsesTheSpaceItsWindowShows_EvenWhenItsProjectIsInSeveralSpaces()
    {
        using var fixture = new Fixture();
        var clients = (await fixture.Spaces.CreateAsync("Clients")).Space!;
        var contoso = Path.Combine(fixture.Root, "contoso");
        Directory.CreateDirectory(contoso);
        var project = await fixture.Projects.UpsertFromPathAsync(contoso);
        Assert.IsTrue((await fixture.Spaces.AssignAsync(project.Id, ["work", clients.Id], null)).Succeeded);

        var spaceView = new DesktopSpaceView();
        spaceView.SetShown("main", "work");
        spaceView.SetShown("window-2", clients.Id);

        Assert.AreEqual(new DesktopLinkOrigin("work", project.ProjectPath),
            await DesktopApplication.LinkOriginAsync(fixture.Spaces, fixture.Projects, (_, _) => ValueTask.FromResult<(string? SpaceId, string? ProjectId)?>(null),
                (_, _) => ValueTask.FromResult<string?>(null), spaceView, null, project.Id, "main", default));
        Assert.AreEqual(new DesktopLinkOrigin(clients.Id, project.ProjectPath),
            await DesktopApplication.LinkOriginAsync(fixture.Spaces, fixture.Projects, (_, _) => ValueTask.FromResult<(string? SpaceId, string? ProjectId)?>(null),
                (_, _) => ValueTask.FromResult<string?>(null), spaceView, null, project.Id, "window-2", default));
        Assert.AreEqual(new DesktopLinkOrigin("work", null),
            await DesktopApplication.LinkOriginAsync(fixture.Spaces, fixture.Projects, (_, _) => ValueTask.FromResult<(string? SpaceId, string? ProjectId)?>(null),
                (_, _) => ValueTask.FromResult<string?>(null), spaceView, null, null, "main", default));
        // A window that shows a space the project is not in, as the default space does with the projects of the others,
        // leaves the document to the space of its project.
        var fabrikam = Path.Combine(fixture.Root, "fabrikam");
        Directory.CreateDirectory(fabrikam);
        var alone = await fixture.Projects.UpsertFromPathAsync(fabrikam);
        Assert.IsTrue((await fixture.Spaces.AssignAsync(alone.Id, ["work"], null)).Succeeded);
        spaceView.SetShown("main", SpaceDescriptor.DefaultId);
        Assert.AreEqual(new DesktopLinkOrigin("work", alone.ProjectPath),
            await DesktopApplication.LinkOriginAsync(fixture.Spaces, fixture.Projects, (_, _) => ValueTask.FromResult<(string? SpaceId, string? ProjectId)?>(null),
                (_, _) => ValueTask.FromResult<string?>(null), spaceView, null, alone.Id, "main", default));
        // A session keeps its own recorded space whatever space the window shows.
        Assert.AreEqual(new DesktopLinkOrigin(clients.Id, contoso),
            await DesktopApplication.LinkOriginAsync(fixture.Spaces, fixture.Projects,
                (sessionId, _) => ValueTask.FromResult<(string? SpaceId, string? ProjectId)?>(sessionId == "s1" ? (clients.Id, project.Id) : null),
                (_, _) => ValueTask.FromResult<string?>(contoso), spaceView, "s1", null, "main", default));
    }

    [TestMethod]
    public async Task TheCardOfTheLinks_SavesTheSetting_AndListsTheBrowsers()
    {
        using var fixture = new Fixture();
        var service = new SpaceLinksService(fixture.Spaces, () => [new LinkBrowser("chrome", "Google Chrome", true, ["Privat", "Work"], true)], Epoch);
        var read = await service.GetAsync(new(Epoch, "work"), default);
        Assert.AreEqual(("ok", null), (read.Status, read.Links));
        Assert.AreEqual("Google Chrome", read.Browsers.Single().Name);

        var saved = await service.UpdateAsync(new(Epoch, "work", "Chrome", " Work "), default);
        Assert.AreEqual("ok", saved.Status);
        Assert.AreEqual(new SpaceLinksItem("chrome", "Work"), saved.Links);
        Assert.AreEqual(new SpaceLinks("chrome", "Work"), (await fixture.Spaces.GetAsync("work"))!.Settings.Links);

        Assert.AreEqual("invalid", (await service.UpdateAsync(new(Epoch, "work", "not a browser", null), default)).Status);
        Assert.AreEqual("ok", (await service.UpdateAsync(new(Epoch, "work", "", null), default)).Status);
        Assert.IsNull((await fixture.Spaces.GetAsync("work"))!.Settings.Links, "Back to the default browser, the space sets nothing.");
        Assert.AreEqual("stale_epoch", (await service.GetAsync(new("other", "work"), default)).Status);
        Assert.AreEqual("not_found", (await service.GetAsync(new(Epoch, "missing"), default)).Status);
        Assert.AreEqual("unavailable", (await new SpaceLinksService().GetAsync(new(Epoch, "work"), default)).Status);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "codealta-links-" + Guid.NewGuid().ToString("N"));

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Projects = new ProjectCatalog(new CatalogOptions { GlobalRoot = Path.Combine(Root, "global") });
            Spaces = new SpaceCatalog(Projects);
            Spaces.CreateAsync("Work").GetAwaiter().GetResult();
        }

        public ProjectCatalog Projects { get; }
        public SpaceCatalog Spaces { get; }

        public void Dispose() => Directory.Delete(Root, true);
    }
}
