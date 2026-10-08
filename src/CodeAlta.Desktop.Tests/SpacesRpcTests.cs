using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class SpacesRpcTests
{
    private const string Epoch = "5f1c2d3e-8a7b-4c6d-9e0f-1a2b3c4d5e6f";

    [TestMethod]
    public async Task TheSpaces_AreListedWithTheirProjects_AndChangedFromThePage()
    {
        using var fixture = new Fixture();
        var alpha = await fixture.Projects.UpsertFromPathAsync(fixture.Folder("alpha"));
        var beta = await fixture.Projects.UpsertFromPathAsync(fixture.Folder("beta"));

        var listed = await fixture.Service.ListAsync(new(Epoch), default);
        Assert.AreEqual("ok", listed.Status);
        var only = listed.Spaces.Single();
        Assert.AreEqual(("default", "Default", true, (string?)null), (only.Id, only.Name, only.IsDefault, only.File));
        CollectionAssert.AreEqual(new[] { alpha.Id, beta.Id }, only.ProjectIds.ToArray());

        var created = await fixture.Service.CreateAsync(new(Epoch, "Work", "Paid work.", "briefcase", "#2d72d2", [alpha.Id]), default);
        Assert.AreEqual(("ok", "work", "Work"), (created.Status, created.Space!.Id, created.Space.Name));
        Assert.AreEqual("ok", (await fixture.Service.CreateAsync(new(Epoch, "Personal", null, null, null, null), default)).Status);
        listed = await fixture.Service.ListAsync(new(Epoch), default);
        CollectionAssert.AreEqual(new[] { "default", "work", "personal" }, listed.Spaces.Select(static space => space.Id).ToArray());
        var work = listed.Spaces[1];
        Assert.AreEqual(("Paid work.", "briefcase", "#2d72d2", false), (work.Description, work.Icon, work.Color, work.IsDefault));
        CollectionAssert.AreEqual(new[] { alpha.Id }, work.ProjectIds.ToArray());
        StringAssert.EndsWith(work.File, Path.Combine("spaces", "work.md"));
        StringAssert.Contains(JsonSerializer.Serialize(listed, DesktopJsonContext.Default.SpacesResponse), "\"isDefault\":true");

        // A project moves from a space to another in one request, and is in several at once.
        Assert.AreEqual("ok", (await fixture.Service.AssignAsync(new(Epoch, beta.Id, ["work", "personal"], null), default)).Status);
        Assert.AreEqual("ok", (await fixture.Service.AssignAsync(new(Epoch, alpha.Id, ["personal"], ["work"]), default)).Status);
        listed = await fixture.Service.ListAsync(new(Epoch), default);
        CollectionAssert.AreEqual(new[] { beta.Id }, listed.Spaces[1].ProjectIds.ToArray());
        CollectionAssert.AreEqual(new[] { alpha.Id, beta.Id }, listed.Spaces[2].ProjectIds.ToArray());

        // A rename keeps the identifier; an empty value removes what was there; a null one leaves it.
        var renamed = await fixture.Service.UpdateAsync(new(Epoch, "work", "Day job", null, null, ""), default);
        Assert.AreEqual(("ok", "work", "Day job", "briefcase", (string?)null), (renamed.Status, renamed.Space!.Id, renamed.Space.Name, renamed.Space.Icon, renamed.Space.Color));
        Assert.AreEqual("ok", (await fixture.Service.ReorderAsync(new(Epoch, ["personal", "work"]), default)).Status);
        CollectionAssert.AreEqual(new[] { "default", "personal", "work" }, (await fixture.Service.ListAsync(new(Epoch), default)).Spaces.Select(static space => space.Id).ToArray());

        // What cannot be done says why, and writes nothing.
        var refused = await fixture.Service.DeleteAsync(new(Epoch, "default"), default);
        Assert.AreEqual("invalid", refused.Status);
        StringAssert.Contains(refused.Message, "default space");
        Assert.AreEqual("invalid", (await fixture.Service.CreateAsync(new(Epoch, "day job", null, null, null, null), default)).Status);
        Assert.AreEqual("invalid", (await fixture.Service.UpdateAsync(new(Epoch, "work", null, null, null, "blue"), default)).Status);
        Assert.AreEqual("not_found", (await fixture.Service.UpdateAsync(new(Epoch, "missing", "X", null, null, null), default)).Status);
        Assert.AreEqual("not_found", (await fixture.Service.AssignAsync(new(Epoch, beta.Id, ["missing"], null), default)).Status);
        Assert.AreEqual("invalid", (await fixture.Service.AssignAsync(new(Epoch, beta.Id, ["default"], null), default)).Status);

        // Deleting a space leaves its projects where they are.
        Assert.AreEqual("ok", (await fixture.Service.DeleteAsync(new(Epoch, "personal"), default)).Status);
        listed = await fixture.Service.ListAsync(new(Epoch), default);
        CollectionAssert.AreEqual(new[] { "default", "work" }, listed.Spaces.Select(static space => space.Id).ToArray());
        Assert.AreEqual(2, listed.Spaces[0].ProjectIds.Count);
        Assert.IsFalse((await fixture.Projects.LoadAsync()).Any(static project => project.Spaces.Contains("personal")));
    }

    [TestMethod]
    public async Task ARequestOfAnotherHost_OrOfAWindowWithoutSpaces_IsRefused()
    {
        using var fixture = new Fixture();
        Assert.AreEqual("stale_epoch", (await fixture.Service.ListAsync(new("other"), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Service.CreateAsync(new("other", "Work", null, null, null, null), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Service.ActivityAsync(new("other"), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Service.ShownAsync(new("other", "default"), default)).Status);
        Assert.IsFalse(Directory.Exists(fixture.Options.SpacesRoot));

        var none = new SpacesService();
        Assert.AreEqual("unavailable", (await none.ListAsync(new(Epoch), default)).Status);
        Assert.AreEqual("unavailable", (await none.DeleteAsync(new(Epoch, "work"), default)).Status);
        Assert.AreEqual("unavailable", (await none.ActivityAsync(new(Epoch), default)).Status);
        Assert.IsEmpty(await Collect(none.WatchAsync(new(Epoch), default)));
        Assert.IsEmpty(await Collect(fixture.Service.WatchAsync(new("other"), default)));
    }

    [TestMethod]
    public async Task ThePageSaysWhichSpaceItShows_AndIsToldWhatACommandAsksOfIt()
    {
        using var fixture = new Fixture();
        Assert.IsNull(fixture.View.ShownSpaceId);
        Assert.IsFalse(fixture.View.Show("work"), "No page listens yet: nothing shows the space.");
        Assert.AreEqual("ok", (await fixture.Service.ShownAsync(new(Epoch, "work"), default)).Status);
        Assert.AreEqual("work", fixture.View.ShownSpaceId);
        Assert.AreEqual("invalid", (await fixture.Service.ShownAsync(new(Epoch, "Not An Id"), default)).Status);
        Assert.AreEqual("work", fixture.View.ShownSpaceId);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var events = fixture.Service.WatchAsync(new(Epoch), stop.Token).GetAsyncEnumerator(stop.Token);
        var next = events.MoveNextAsync();
        // The page listens from its first read on: the request is made once it does.
        for (var turn = 0; turn < 200 && !fixture.View.Show("personal"); turn++) await Task.Delay(10, stop.Token);
        Assert.IsTrue(await next);
        Assert.AreEqual(("show", "personal"), (events.Current.Kind, events.Current.SpaceId));
        fixture.View.NotifyChanged();
        Assert.IsTrue(await events.MoveNextAsync());
        Assert.AreEqual(("changed", (string?)null), (events.Current.Kind, events.Current.SpaceId));
        // A request to show changes what the window shows, and not what it said it shows: the page says that itself.
        Assert.AreEqual("work", fixture.View.ShownSpaceId);
    }

    [TestMethod]
    public async Task WhatTheSessionsDo_IsGivenForTheOnesWithSomethingToSay_TheOnesThatWaitFirst()
    {
        using var fixture = new Fixture();
        fixture.Runs =
        [
            new("idle", "p1", "Idle", false, 0, false),
            new("running", "p1", "Running", true, 0, false),
            new("background", "p2", "Background", false, 250, false),
            new("failed", null, new string('x', 300), false, 0, true),
            new("asks", "p2", "Asks", false, 0, false),
            new("reviews", "p1", "Reviews", true, 1, false),
        ];
        fixture.Waiting = ["asks", "reviews", "gone"];
        var reply = await fixture.Service.ActivityAsync(new(Epoch), default);
        Assert.AreEqual(("ok", false), (reply.Status, reply.Truncated));
        CollectionAssert.AreEqual(new[] { "asks", "reviews", "failed", "background", "running" }, reply.Sessions.Select(static session => session.SessionId).ToArray());
        var asks = reply.Sessions[0];
        Assert.AreEqual(("p2", "Asks", false, 0, false, true), (asks.ProjectId, asks.Title, asks.Running, asks.BackgroundTasks, asks.Failed, asks.Waiting));
        Assert.AreEqual((true, 1, true), (reply.Sessions[1].Running, reply.Sessions[1].BackgroundTasks, reply.Sessions[1].Waiting));
        Assert.AreEqual((null, 256, true), (reply.Sessions[2].ProjectId, reply.Sessions[2].Title.Length, reply.Sessions[2].Failed));
        Assert.AreEqual(99, reply.Sessions[3].BackgroundTasks);
        StringAssert.Contains(JsonSerializer.Serialize(reply, DesktopJsonContext.Default.SpacesActivityResponse), "\"waiting\":true");

        // More sessions than a reading holds: the ones that wait are kept.
        fixture.Runs = [.. Enumerable.Range(0, SpacesService.MaximumActivitySessions + 20).Select(index => new SessionRuntimeOverview($"s{index:000}", "p1", "S", true, 0, false)),
            new("last", "p1", "Waits", false, 0, false)];
        fixture.Waiting = ["last"];
        reply = await fixture.Service.ActivityAsync(new(Epoch), default);
        Assert.AreEqual((SpacesService.MaximumActivitySessions, true, "last"), (reply.Sessions.Count, reply.Truncated, reply.Sessions[0].SessionId));
    }

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> source)
    {
        var items = new List<T>();
        await foreach (var item in source) items.Add(item);
        return items;
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("codealta-spaces-rpc-").FullName;
        public CatalogOptions Options { get; }
        public ProjectCatalog Projects { get; }
        public DesktopSpaceView View { get; } = new();
        public SpacesService Service { get; }
        public IReadOnlyList<SessionRuntimeOverview> Runs { get; set; } = [];
        public IReadOnlyList<string> Waiting { get; set; } = [];

        public Fixture()
        {
            Options = new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(Root, "global")).FullName };
            Projects = new ProjectCatalog(Options);
            Service = new SpacesService(new SpaceCatalog(Projects), View, () => Runs, () => ValueTask.FromResult(Waiting), Epoch);
        }

        public string Folder(string name) => Directory.CreateDirectory(Path.Combine(Root, "folders", name)).FullName;

        public void Dispose() => Directory.Delete(Root, true);
    }
}
