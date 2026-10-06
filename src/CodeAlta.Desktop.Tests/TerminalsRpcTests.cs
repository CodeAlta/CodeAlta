using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Desktop.Terminals;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class TerminalsRpcTests
{
    private const string Epoch = "epoch-1";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    // A real project catalog over temporary folders; the programs of the terminals are fakes.
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;

        private Fixture(string root, ProjectCatalog projects, ProjectDescriptor project)
        {
            (_root, Projects, Project) = (root, projects, project);
            var shell = OperatingSystem.IsWindows() ? @"C:\shell\sh" : "/shell/sh";
            var profiles = new TerminalProfiles(name => name == "SHELL" ? shell : null, path => path == shell, static () => [], static _ => [], windows: false, mac: false);
            Terminals = new DesktopTerminals("1.2.3", profiles, start =>
            {
                Starts.Add(start);
                var program = new FakeTerminalProgram();
                Programs.Add(program);
                return program;
            }, TimeProvider.System);
            Service = new TerminalsService(Terminals, projects, Epoch, (id, _) => Task.FromResult(SessionFolders.GetValueOrDefault(id)), address =>
            {
                Opened.Add(address);
                return true;
            });
        }

        public ProjectCatalog Projects { get; }
        public ProjectDescriptor Project { get; }
        public DesktopTerminals Terminals { get; }
        public TerminalsService Service { get; }
        public List<FakeTerminalProgram> Programs { get; } = [];
        public List<PseudoTerminalStart> Starts { get; } = [];
        public Dictionary<string, string?> SessionFolders { get; } = [];
        public List<string> Opened { get; } = [];
        public string Folder(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "CodeAlta-terminals-rpc-" + Guid.NewGuid().ToString("N"));
            var projects = new ProjectCatalog(new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(root, "global")).FullName });
            var project = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "project")).FullName);
            return new Fixture(root, projects, project);
        }

        public async ValueTask DisposeAsync()
        {
            await Terminals.CloseAsync();
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task WithoutAnOwnedHost_EveryOperationIsUnavailable_AndAnotherEpochIsRefused()
    {
        var unavailable = new TerminalsService();
        Assert.AreEqual("unavailable", (await unavailable.ProfilesAsync(new(Epoch), default)).Status);
        Assert.AreEqual("unavailable", (await unavailable.CreateAsync(new(Epoch, null, null, null, null, null), default)).Status);
        Assert.AreEqual("unavailable", unavailable.Input(new(Epoch, "id", "x")).Status);
        Assert.AreEqual("unavailable", unavailable.Resize(new(Epoch, "id", 80, 24)).Status);
        Assert.AreEqual("unavailable", unavailable.Rename(new(Epoch, "id", "t")).Status);
        Assert.AreEqual("unavailable", unavailable.Close(new(Epoch, "id")).Status);
        Assert.AreEqual("unavailable", unavailable.Open(new(Epoch, "https://example.com/")).Status);
        Assert.AreEqual("unavailable", unavailable.Attach(new(Epoch, "feed", "id")).Status);
        Assert.AreEqual("unavailable", unavailable.Detach(new(Epoch, "feed", "id")).Status);
        Assert.AreEqual("unavailable", unavailable.Show(new(Epoch, "feed", "id", true)).Status);
        Assert.AreEqual("unavailable", unavailable.Acknowledge(new(Epoch, "feed", "id", 1)).Status);
        await foreach (var _ in unavailable.WatchAsync(new(Epoch), default)) Assert.Fail("A window without a host is told nothing.");

        await using var fixture = await Fixture.CreateAsync();
        Assert.AreEqual("stale_epoch", (await fixture.Service.ProfilesAsync(new("another"), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Service.CreateAsync(new("another", fixture.Project.Id, null, null, null, null), default)).Status);
        Assert.AreEqual("stale_epoch", fixture.Service.Input(new(null, "id", "x")).Status);
        Assert.AreEqual("stale_epoch", fixture.Service.Open(new("another", "https://example.com/")).Status);
        await foreach (var _ in fixture.Service.WatchAsync(new("another"), default)) Assert.Fail("A stale page is told nothing.");
        Assert.AreEqual(0, fixture.Starts.Count);
    }

    [TestMethod]
    public async Task ATerminal_StartsInTheFolderOfItsProject_OfItsSession_OrOfTheUser()
    {
        await using var fixture = await Fixture.CreateAsync();
        var profiles = await fixture.Service.ProfilesAsync(new(Epoch), default);
        Assert.AreEqual(("ok", "sh", OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux"), (profiles.Status, profiles.Profiles.Single().Id, profiles.Platform));
        Assert.AreEqual(OperatingSystem.IsWindows(), profiles.Build > 0);

        var created = await fixture.Service.CreateAsync(new(Epoch, fixture.Project.Id, null, null, 100, 40), default);
        Assert.AreEqual("ok", created.Status);
        var start = fixture.Starts.Single();
        Assert.AreEqual((Path.GetFullPath(fixture.Project.ProjectPath), 100, 40), (start.WorkingDirectory, start.Columns, start.Rows));
        var terminal = created.Terminal!;
        Assert.AreEqual((fixture.Project.Id, null, start.WorkingDirectory, false, true, false, false, 100, 40),
            (terminal.ProjectId, terminal.SessionId, terminal.Title, terminal.Titled, terminal.Running, terminal.Open, terminal.Agent, terminal.Columns, terminal.Rows));

        // A session can work in a folder of its own: its terminal starts there, and is listed under its project.
        var worktree = fixture.Folder("worktree");
        fixture.SessionFolders["session-1"] = worktree;
        var session = await fixture.Service.CreateAsync(new(Epoch, fixture.Project.Id, "session-1", null, null, null), default);
        Assert.AreEqual(("ok", worktree, fixture.Project.Id, "session-1"), (session.Status, fixture.Starts[^1].WorkingDirectory, session.Terminal!.ProjectId, session.Terminal.SessionId));
        // One whose folder is gone starts in the folder of its project; a session of no project has no other folder.
        fixture.SessionFolders["session-2"] = Path.Combine(worktree, "gone");
        Assert.AreEqual(Path.GetFullPath(fixture.Project.ProjectPath), fixture.Starts[(await Created(fixture, fixture.Project.Id, "session-2"))].WorkingDirectory);
        Assert.AreEqual("no_folder", (await fixture.Service.CreateAsync(new(Epoch, null, "session-2", null, null, null), default)).Status);
        Assert.AreEqual("missing_session", (await fixture.Service.CreateAsync(new(Epoch, fixture.Project.Id, "unknown", null, null, null), default)).Status);

        // Without a project or a session it starts in the home folder of the user.
        var home = await fixture.Service.CreateAsync(new(Epoch, null, null, null, null, null), default);
        Assert.AreEqual(("ok", null, Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))), (home.Status, home.Terminal!.ProjectId, fixture.Starts[^1].WorkingDirectory));

        Assert.AreEqual("unknown_project", (await fixture.Service.CreateAsync(new(Epoch, "no-such-project", null, null, null, null), default)).Status);
        Assert.AreEqual("invalid", (await fixture.Service.CreateAsync(new(Epoch, fixture.Project.Id, "bad\u0007id", null, null, null), default)).Status);
        Assert.AreEqual("invalid", (await fixture.Service.CreateAsync(new(Epoch, fixture.Project.Id, null, new string('p', TerminalsService.MaximumIdUnits + 1), null, null), default)).Status);

        static async Task<int> Created(Fixture fixture, string? project, string? session)
        {
            Assert.AreEqual("ok", (await fixture.Service.CreateAsync(new(Epoch, project, session, null, null, null), default)).Status);
            return fixture.Starts.Count - 1;
        }
    }

    [TestMethod]
    public async Task ThePage_TypesSizesNamesAndClosesATerminal()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = (await fixture.Service.CreateAsync(new(Epoch, fixture.Project.Id, null, null, null, null), default)).Terminal!.Id;
        var program = fixture.Programs.Single();

        Assert.AreEqual("ok", fixture.Service.Input(new(Epoch, id, "dir\r")).Status);
        await Until(() => program.Typed == "dir\r");
        Assert.AreEqual("not_found", fixture.Service.Input(new(Epoch, "other", "x")).Status);
        Assert.AreEqual("invalid", fixture.Service.Input(new(Epoch, id, null)).Status);
        Assert.AreEqual("invalid", fixture.Service.Input(new(Epoch, id, new string('x', TerminalsService.MaximumInputUnits + 1))).Status);

        Assert.AreEqual("ok", fixture.Service.Resize(new(Epoch, id, 132, 43)).Status);
        CollectionAssert.AreEqual(new[] { (132, 43) }, program.Sizes.ToArray());
        Assert.AreEqual("invalid", fixture.Service.Resize(new(Epoch, id, 1, 43)).Status);
        Assert.AreEqual("invalid", fixture.Service.Resize(new(Epoch, id, 132, PseudoTerminal.MaximumSize + 1)).Status);
        Assert.AreEqual("not_found", fixture.Service.Resize(new(Epoch, "other", 80, 24)).Status);

        Assert.AreEqual("ok", fixture.Service.Rename(new(Epoch, id, " build ")).Status);
        Assert.AreEqual(("build", true), (fixture.Terminals.List().Single().Title, fixture.Terminals.List().Single().Titled));
        Assert.AreEqual("ok", fixture.Service.Rename(new(Epoch, id, null)).Status);
        Assert.IsFalse(fixture.Terminals.List().Single().Titled);
        Assert.AreEqual("invalid", fixture.Service.Rename(new(Epoch, id, new string('t', DesktopTerminal.MaximumTitleLength + 1))).Status);
        Assert.AreEqual("not_found", fixture.Service.Rename(new(Epoch, "other", "t")).Status);

        Assert.AreEqual("not_found", fixture.Service.Close(new(Epoch, "other")).Status);
        Assert.AreEqual("ok", fixture.Service.Close(new(Epoch, id)).Status);
        await Until(() => fixture.Terminals.List().Count == 0);
        Assert.IsTrue(program.Killed);
        Assert.AreEqual("not_found", fixture.Service.Input(new(Epoch, id, "x")).Status);
    }

    [TestMethod]
    public async Task ThePage_IsToldTheNameOfItsFeed_TheTerminals_AndWhatThoseItShowsWrite()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = (await fixture.Service.CreateAsync(new(Epoch, fixture.Project.Id, null, null, null, null), default)).Terminal!.Id;
        var program = fixture.Programs.Single();
        program.Show("hello");
        await Until(() => fixture.Terminals.Find(id)!.End == 5);

        using var stop = new CancellationTokenSource(Patience);
        await using var events = fixture.Service.WatchAsync(new(Epoch), stop.Token).GetAsyncEnumerator(stop.Token);
        async Task<TerminalEvent> Next()
        {
            Assert.IsTrue(await events.MoveNextAsync());
            return events.Current;
        }

        var first = await Next();
        Assert.AreEqual("feed", first.Kind);
        var feed = first.Feed!;
        var list = await Next();
        var listed = list.Terminals!.Single();
        Assert.AreEqual(("list", id, false), (list.Kind, listed.Id, listed.Open));

        // The calls about what the page shows name its feed: another name is of no page.
        Assert.AreEqual("no_feed", fixture.Service.Attach(new(Epoch, "other", id)).Status);
        Assert.AreEqual("no_feed", fixture.Service.Acknowledge(new(Epoch, null, id, 5)).Status);
        Assert.AreEqual("not_found", fixture.Service.Attach(new(Epoch, feed, "other")).Status);
        Assert.AreEqual("ok", fixture.Service.Attach(new(Epoch, feed, id)).Status);
        Assert.IsTrue((await Next()).Terminals!.Single().Open);
        var start = await Next();
        Assert.AreEqual(("start", id, string.Empty, DesktopTerminals.DefaultColumns, DesktopTerminals.DefaultRows), (start.Kind, start.Id, start.Data, start.Columns, start.Rows));
        var data = await Next();
        Assert.AreEqual(("data", id, "hello", true), (data.Kind, data.Id, data.Data, data.Replayed));
        Assert.AreEqual("ok", fixture.Service.Acknowledge(new(Epoch, feed, id, 5)).Status);

        Assert.AreEqual("ok", fixture.Service.Show(new(Epoch, feed, id, true)).Status);
        Assert.AreEqual("list", (await Next()).Kind);
        Assert.IsTrue(fixture.Terminals.List().Single().Visible);
        Assert.AreEqual("ok", fixture.Service.Detach(new(Epoch, feed, id)).Status);
        var detached = await Next();
        Assert.IsFalse(detached.Terminals!.Single().Open);
        Assert.IsFalse(fixture.Terminals.List().Single().Visible);

        // What the page reads is what it is sent, name for name.
        Assert.AreEqual("{\"kind\":\"data\",\"feed\":null,\"id\":\"t\",\"terminals\":null,\"data\":\"x\",\"columns\":0,\"rows\":0,\"replayed\":false}",
            JsonSerializer.Serialize(new TerminalEvent("data", null, "t", null, "x", 0, 0, false), DesktopJsonContext.Default.TerminalEvent));

        // A page that stops listening is forgotten: its feed is no longer one.
        stop.Cancel();
        try
        {
            await events.MoveNextAsync();
        }
        catch (OperationCanceledException)
        {
            // The watch was stopped.
        }
        await events.DisposeAsync();
        Assert.AreEqual("no_feed", fixture.Service.Attach(new(Epoch, feed, id)).Status);
    }

    [TestMethod]
    public async Task ALinkOfATerminal_IsOpenedOnlyWhenItIsAnAddressOfTheWeb()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.AreEqual("ok", fixture.Service.Open(new(Epoch, "https://example.com/a b?q=1#x")).Status);
        Assert.AreEqual("ok", fixture.Service.Open(new(Epoch, "http://localhost:5173")).Status);
        CollectionAssert.AreEqual(new[] { "https://example.com/a%20b?q=1#x", "http://localhost:5173/" }, fixture.Opened);
        foreach (var address in new[] { null, string.Empty, "example.com", "file:///C:/Windows/System32/calc.exe", "javascript:alert(1)", "ms-settings:display", "C:\\Windows\\notepad.exe",
            "https://", "https://example.com/\u0007", "https://example.com/" + new string('a', DesktopLinks.MaximumLength) })
        {
            Assert.AreEqual("invalid", fixture.Service.Open(new(Epoch, address)).Status, address);
        }
        Assert.AreEqual(2, fixture.Opened.Count);
    }

    private static async Task Until(Func<bool> condition)
    {
        var limit = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.IsTrue(DateTime.UtcNow < limit, "It did not happen in time.");
            await Task.Delay(10);
        }
    }
}
