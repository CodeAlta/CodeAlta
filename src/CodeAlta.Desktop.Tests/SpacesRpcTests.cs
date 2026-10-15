using System.Text.Json;
using CodeAlta.Agent;
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
            new("asks", "p2", "Asks", false, 0, false) { SpaceId = "business" },
            new("reviews", "p1", "Reviews", true, 1, false) { SpaceId = "Not An Id" },
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
        // The space each session records, for the marks of the spaces: none for an older session or a wrong value.
        CollectionAssert.AreEqual(new[] { "business", null, null, null, null }, reply.Sessions.Select(static session => session.SpaceId).ToArray());
        StringAssert.Contains(JsonSerializer.Serialize(reply, DesktopJsonContext.Default.SpacesActivityResponse), "\"waiting\":true");

        // More sessions than a reading holds: the ones that wait are kept.
        fixture.Runs = [.. Enumerable.Range(0, SpacesService.MaximumActivitySessions + 20).Select(index => new SessionRuntimeOverview($"s{index:000}", "p1", "S", true, 0, false)),
            new("last", "p1", "Waits", false, 0, false)];
        fixture.Waiting = ["last"];
        reply = await fixture.Service.ActivityAsync(new(Epoch), default);
        Assert.AreEqual((SpacesService.MaximumActivitySessions, true, "last"), (reply.Sessions.Count, reply.Truncated, reply.Sessions[0].SessionId));
    }

    [TestMethod]
    public async Task ASessionThatWaitsOrWorks_NamesTheSessionsAboveIt_NearestFirst()
    {
        using var fixture = new Fixture();
        fixture.Runs =
        [
            new("root", "p1", "Root", false, 0, false),
            new("child", "p1", "Child", true, 0, false) { ParentSessionId = "root" },
            new("grandchild", "p1", "Grandchild", true, 0, false) { ParentSessionId = "CHILD" },
            new("orphan", "p1", "Orphan", false, 0, false) { ParentSessionId = "not-held" },
            new("busy", "p1", "Busy", true, 0, false) { ParentSessionId = "root" },
        ];
        fixture.Waiting = ["grandchild", "orphan"];
        var reply = await fixture.Service.ActivityAsync(new(Epoch), default);
        var sessions = reply.Sessions.ToDictionary(static session => session.SessionId);

        // The chain goes as far as the runtime holds it; the first parent is known even when it is not held.
        CollectionAssert.AreEqual(new[] { "CHILD", "root" }, sessions["grandchild"].Ancestors.ToArray());
        CollectionAssert.AreEqual(new[] { "not-held" }, sessions["orphan"].Ancestors.ToArray());
        // A session at work names them too: the session that created it shows its sub-agents at work.
        CollectionAssert.AreEqual(new[] { "root" }, sessions["busy"].Ancestors.ToArray());
        StringAssert.Contains(JsonSerializer.Serialize(reply, DesktopJsonContext.Default.SpacesActivityResponse), "\"ancestors\":[\"CHILD\",\"root\"]");
    }

    [TestMethod]
    public async Task ARecentFallback_IsGivenWithTheActivityOfItsSession()
    {
        using var fixture = new Fixture();
        var now = DateTimeOffset.UtcNow;
        IReadOnlyList<SessionFallbackRecord> fallbacks =
        [
            new("idle", now.AddMinutes(-1), AgentFailureKind.QuotaExhausted, "claude-code:opus@high", "codex:gpt-6.1@high", "Claude Code quota reached, continuing on Codex GPT-6.1 high."),
            new("old", now - SpacesService.RecentFallback - TimeSpan.FromMinutes(1), AgentFailureKind.Unavailable, "a:m", "b:m", "Old."),
        ];
        var service = new SpacesService(new SpaceCatalog(fixture.Projects), fixture.View, () => fixture.Runs, () => ValueTask.FromResult(fixture.Waiting), Epoch)
        {
            Fallbacks = () => fallbacks,
        };
        fixture.Runs = [new("idle", "p1", "Idle", false, 0, false), new("old", "p1", "Old", false, 0, false), new("running", "p1", "Running", true, 0, false)];

        var reply = await service.ActivityAsync(new(Epoch), default);

        // A session that went on with another model a short while ago is listed, idle or not; an older fallback is not news.
        CollectionAssert.AreEquivalent(new[] { "idle", "running" }, reply.Sessions.Select(static session => session.SessionId).ToArray());
        var idle = reply.Sessions.Single(static session => session.SessionId == "idle");
        Assert.AreEqual("Claude Code quota reached, continuing on Codex GPT-6.1 high.", idle.Fallback?.Message);
        Assert.IsNull(reply.Sessions.Single(static session => session.SessionId == "running").Fallback);
        StringAssert.Contains(JsonSerializer.Serialize(reply, DesktopJsonContext.Default.SpacesActivityResponse), "\"fallback\":{\"at\":");
    }

    [TestMethod]
    public async Task ARecentRestoreForARun_IsGivenWithTheActivityOfItsSession()
    {
        using var fixture = new Fixture();
        var now = DateTimeOffset.UtcNow;
        var service = new SpacesService(new SpaceCatalog(fixture.Projects), fixture.View, () => fixture.Runs, () => ValueTask.FromResult(fixture.Waiting), Epoch);
        fixture.Runs = [new("restored", "p1", "Restored", false, 0, false) { RestoredForRunAt = now.AddMinutes(-1) },
            new("old", "p1", "Old", false, 0, false) { RestoredForRunAt = now - SpacesService.RecentFallback - TimeSpan.FromMinutes(1) },
            new("running", "p1", "Running", true, 0, false)];

        var reply = await service.ActivityAsync(new(Epoch), default);

        // A session a prompt took out of the archive a short while ago is listed, idle or not; an older restore is not news.
        CollectionAssert.AreEquivalent(new[] { "restored", "running" }, reply.Sessions.Select(static session => session.SessionId).ToArray());
        Assert.IsNotNull(reply.Sessions.Single(static session => session.SessionId == "restored").RestoredForRunAt);
        Assert.IsNull(reply.Sessions.Single(static session => session.SessionId == "running").RestoredForRunAt);
        StringAssert.Contains(JsonSerializer.Serialize(reply, DesktopJsonContext.Default.SpacesActivityResponse), "\"restoredForRunAt\":\"");
    }

    [TestMethod]
    public async Task TheSpacesOfAResource_AreReadAndWrittenInTheGlobalConfiguration_AndTheAgentPromptOfASpaceIsSaved()
    {
        using var fixture = new Fixture();
        Assert.AreEqual("ok", (await fixture.Service.CreateAsync(new(Epoch, "Business", null, null, null, null), default)).Status);
        var events = fixture.View.Watch(_ => fixture.Changed++);

        Assert.AreEqual("ok", (await fixture.Service.AssignResourceAsync(new(Epoch, "mcp", "atlassian", ["business"]), default)).Status);
        Assert.AreEqual("ok", (await fixture.Service.AssignResourceAsync(new(Epoch, "skill", "terraform", ["business", "default"]), default)).Status);
        Assert.AreEqual("ok", (await fixture.Service.AssignResourceAsync(new(Epoch, "prompt", "reviewer", ["business"]), default)).Status);
        Assert.AreEqual("ok", (await fixture.Service.AssignResourceAsync(new(Epoch, "plugin", "jira", ["business"]), default)).Status);
        Assert.AreEqual("ok", (await fixture.Service.AssignResourceAsync(new(Epoch, "prompt", "reviewer", []), default)).Status, "An empty list puts it in every space.");
        Assert.AreEqual(5, fixture.Changed, "The pages read the spaces of the resources again.");
        var read = fixture.Service.Assignments(new(Epoch));
        Assert.AreEqual("ok", read.Status);
        Assert.AreEqual(("atlassian", "business"), (read.McpServers.Single().Key, read.McpServers.Single().Spaces.Single()));
        CollectionAssert.AreEqual(new[] { "business", "default" }, read.Skills.Single().Spaces.ToArray());
        Assert.AreEqual("jira", read.Plugins.Single().Key);
        Assert.AreEqual(0, read.Prompts.Count);
        StringAssert.Contains(File.ReadAllText(fixture.Options.ConfigPath), "[plugins.mcp.servers.atlassian]");

        // What is not a resource, a space or a provider of the file is refused, and nothing is written.
        Assert.AreEqual("invalid", (await fixture.Service.AssignResourceAsync(new(Epoch, "tool", "shell", ["business"]), default)).Status);
        Assert.AreEqual("invalid", (await fixture.Service.AssignResourceAsync(new(Epoch, "mcp", "docs", ["Not A Space"]), default)).Status);
        Assert.AreEqual("invalid", (await fixture.Service.AssignResourceAsync(new(Epoch, "provider", "claude-business", ["business"]), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Service.AssignResourceAsync(new("other", "mcp", "docs", ["business"]), default)).Status);

        Assert.AreEqual("ok", (await fixture.Service.SetAgentPromptAsync(new(Epoch, "business", "reviewer"), default)).Status);
        Assert.AreEqual("reviewer", (await fixture.Service.ListAsync(new(Epoch), default)).Spaces.Single(static space => space.Id == "business").AgentPrompt);
        Assert.AreEqual("ok", (await fixture.Service.SetAgentPromptAsync(new(Epoch, "business", null), default)).Status);
        Assert.IsNull((await fixture.Service.ListAsync(new(Epoch), default)).Spaces.Single(static space => space.Id == "business").AgentPrompt);
        Assert.AreEqual("not_found", (await fixture.Service.SetAgentPromptAsync(new(Epoch, "missing", "reviewer"), default)).Status);

        // The tools the sessions of a space do without.
        Assert.AreEqual("ok", (await fixture.Service.SetDisabledToolsAsync(new(Epoch, "business", ["shell_command", "shell_command", "webget"]), default)).Status);
        CollectionAssert.AreEqual(new[] { "shell_command", "webget" },
            (await fixture.Service.ListAsync(new(Epoch), default)).Spaces.Single(static space => space.Id == "business").DisabledTools.ToArray());
        Assert.AreEqual("invalid", (await fixture.Service.SetDisabledToolsAsync(new(Epoch, "business", ["rm -rf"]), default)).Status);
        Assert.AreEqual("ok", (await fixture.Service.SetDisabledToolsAsync(new(Epoch, "business", []), default)).Status);
        Assert.AreEqual(0, (await fixture.Service.ListAsync(new(Epoch), default)).Spaces.Single(static space => space.Id == "business").DisabledTools.Count);
        events.Dispose();
    }

    [TestMethod]
    public async Task ANewSpace_StartsWithTheSetupOfAnother_WithoutItsIdentityNorItsProjects()
    {
        using var fixture = new Fixture();
        var alpha = await fixture.Projects.UpsertFromPathAsync(fixture.Folder("alpha"));
        File.WriteAllText(fixture.Options.ConfigPath, "[providers.claude-business]\ntype = \"claude-code\"\n");
        Assert.AreEqual("ok", (await fixture.Service.CreateAsync(new(Epoch, "Business", null, null, null, [alpha.Id]), default)).Status);
        Assert.AreEqual("ok", (await fixture.Service.CreateAsync(new(Epoch, "Personal", null, null, null, null), default)).Status);
        var catalog = new SpaceCatalog(fixture.Projects);
        Assert.IsTrue(CodeAlta.Catalog.ModelTiers.ModelTierTarget.TryParse("codex:gpt-6.1@high", out var target, out _));
        Assert.IsTrue((await catalog.UpdateSettingsAsync("business", settings => settings with
        {
            NewSessions = new SpaceNewSessions("reviewer", new SpaceModelTiers([target], [], [])),
            Identity = new SpaceIdentity(new SpaceGitIdentity("A. Mutel", "alex@contoso.example")),
            Tools = new SpaceTools(["shell_command"]),
        })).Succeeded);
        foreach (var (kind, key, spaces) in new[]
        {
            ("provider", "claude-business", new[] { "business" }), ("mcp", "atlassian", ["business", "default"]), ("skill", "terraform", ["business"]),
            ("prompt", "reviewer", ["business"]), ("plugin", "jira", ["business"]), ("mcp", "notes", ["personal"]), ("skill", "shared-off", ["default", "personal"]),
        })
            Assert.AreEqual("ok", (await fixture.Service.AssignResourceAsync(new(Epoch, kind, key, spaces), default)).Status);

        var copied = await fixture.Service.CreateAsync(new(Epoch, "Client", null, null, null, null, "business"), default);

        Assert.AreEqual(("ok", "client"), (copied.Status, copied.Space?.Id));
        var client = (await fixture.Service.ListAsync(new(Epoch), default)).Spaces.Single(static space => space.Id == "client");
        Assert.AreEqual("reviewer", client.AgentPrompt);
        Assert.IsTrue(client.OwnTiers);
        CollectionAssert.AreEqual(new[] { "shell_command" }, client.DisabledTools.ToArray());
        Assert.IsNull(client.IdentityEmail, "The identity of a space is its own: it is not copied.");
        Assert.AreEqual(0, client.IdentityParts.Count);
        Assert.AreEqual(0, client.ProjectIds.Count, "The projects are not copied.");
        var business = (await fixture.Service.ListAsync(new(Epoch), default)).Spaces.Single(static space => space.Id == "business");
        Assert.AreEqual("alex@contoso.example", business.IdentityEmail);
        CollectionAssert.AreEqual(new[] { "git" }, business.IdentityParts.ToArray());

        // Each resource of the source space lists the new space too; the others are left as they are.
        var read = fixture.Service.Assignments(new(Epoch));
        string[] Of(IReadOnlyList<SpaceResourceAssignment> list, string key) => [.. list.Single(item => item.Key == key).Spaces];
        CollectionAssert.AreEqual(new[] { "business", "client" }, Of(read.Providers, "claude-business"));
        CollectionAssert.AreEqual(new[] { "business", "default", "client" }, Of(read.McpServers, "atlassian"));
        CollectionAssert.AreEqual(new[] { "business", "client" }, Of(read.Skills, "terraform"));
        CollectionAssert.AreEqual(new[] { "business", "client" }, Of(read.Prompts, "reviewer"));
        CollectionAssert.AreEqual(new[] { "business", "client" }, Of(read.Plugins, "jira"));
        CollectionAssert.AreEqual(new[] { "personal" }, Of(read.McpServers, "notes"));
        CollectionAssert.AreEqual(new[] { "default", "personal" }, Of(read.Skills, "shared-off"), "What the source space turned off stays out of the copy.");

        // A space to copy that does not exist creates nothing.
        Assert.AreEqual("not_found", (await fixture.Service.CreateAsync(new(Epoch, "Other", null, null, null, null, "missing"), default)).Status);
        Assert.IsFalse((await fixture.Service.ListAsync(new(Epoch), default)).Spaces.Any(static space => space.Id == "other"));
    }

    [TestMethod]
    public void TheSessionsAboveOne_StopAtALoopAndAtTheDepthLimit()
    {
        var loop = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["a"] = "b", ["b"] = "c", ["c"] = "A" };
        CollectionAssert.AreEqual(new[] { "b", "c" }, SpacesService.Ancestors("a", loop).ToArray());

        var deep = Enumerable.Range(0, 40).ToDictionary(static index => $"s{index}", static index => $"s{index + 1}", StringComparer.OrdinalIgnoreCase);
        Assert.AreEqual(SpacesService.MaximumAncestors, SpacesService.Ancestors("s0", deep).Count);
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
        public int Changed { get; set; }

        public Fixture()
        {
            Options = new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(Root, "global")).FullName };
            Projects = new ProjectCatalog(Options);
            Service = new SpacesService(new SpaceCatalog(Projects), View, () => Runs, () => ValueTask.FromResult(Waiting), Epoch)
            {
                ConfigStore = new CodeAltaConfigStore(Options),
            };
        }

        public string Folder(string name) => Directory.CreateDirectory(Path.Combine(Root, "folders", name)).FullName;

        public void Dispose() => Directory.Delete(Root, true);
    }
}
