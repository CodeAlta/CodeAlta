using System.Text.Json.Nodes;

namespace CodeAlta.Plugin.Mobile.Tests;

[TestClass]
public sealed class MobileSessionsTests
{
    [TestMethod]
    public async Task ArchiveChildren_ChecksAllDescendantsAndRespectsAssignedSpaces()
    {
        var alta = new FakeAlta()
            .Answer("session status parent", FakeAlta.Status("parent", "p1", "personal"))
            .Answer("session children parent", FakeAlta.Session("child", "p1", "personal", "2026-10-24T10:00:00Z"),
                FakeAlta.Session("old", "p1", "personal", "2026-10-24T10:00:00Z", state: "archived"), new JsonObject { ["type"] = "alta.session.children.summary" })
            .Answer("session archive parent", new JsonObject { ["type"] = "alta.session.archived", ["changed"] = new JsonArray("parent", "child") });
        var api = new MobileApi(alta, new MobileSessions(alta, new FakeSpaces("personal"), new MobileLive()));
        Assert.IsTrue((bool)(await api.ArchiveChildrenAsync("parent", default))!["hasChildren"]!);
        var archived = await api.ArchiveAsync("parent", true, default);
        CollectionAssert.AreEqual(new[] { "parent", "child" }, archived!["changed"]!.AsArray().Select(x => (string)x!).ToArray());
        CollectionAssert.Contains(alta.CallsOf("session archive")[0], "--with-children");
        alta.Answer("session children parent", FakeAlta.Session("other", "p1", "work", "2026-10-24T10:00:00Z"), new JsonObject { ["type"] = "alta.session.children.summary" });
        var refused = await api.ArchiveAsync("parent", true, default);
        Assert.IsFalse((bool)refused!["ok"]!);
        Assert.AreEqual(1, alta.CallsOf("session archive").Length);
        alta.Answer("session children parent", FakeAlta.Session("child", "p1", "personal", "2026-10-24T10:00:00Z"),
            FakeAlta.Session("gone", "p1", "work", "2026-10-24T10:00:00Z", state: "archived"), new JsonObject { ["type"] = "alta.session.children.summary" });
        Assert.IsTrue((bool)(await api.ArchiveAsync("parent", true, default))!["ok"]!, "a child of another space archived before does not stop it");
        Assert.AreEqual(2, alta.CallsOf("session archive").Length);
        alta.Cut = args => args[1] == "children";
        Assert.IsNotNull((await api.ArchiveChildrenAsync("parent", default))!["error"]);
        alta.Cut = null;
        alta.Answer("session children parent", FakeAlta.Session("old", "p1", "personal", "2026-10-24T10:00:00Z", state: "archived"), new JsonObject { ["type"] = "alta.session.children.summary" });
        Assert.IsFalse((bool)(await api.ArchiveChildrenAsync("parent", default))!["hasChildren"]!);
        alta.Answer("session children parent");
        Assert.IsNotNull((await api.ArchiveChildrenAsync("parent", default))!["error"]);
    }

    [TestMethod]
    public async Task List_GivesThePageOfTheSpacesOfThePlugin_LatestFirst()
    {
        var alta = new FakeAlta()
            .Answer("project list", FakeAlta.Project("p1", "Alpha", @"C:\alpha"), FakeAlta.Project("p2", "Beta", @"C:\beta"))
            .Answer("session list", (args, _) => args[^1] switch
            {
                "personal" => FakeAlta.Transcript(0, FakeAlta.Session("a", "p1", "personal", "2026-10-24T10:00:00Z"), FakeAlta.Session("b", "p2", "personal", "2026-10-24T08:00:00Z")),
                "family" => FakeAlta.Transcript(0, FakeAlta.Session("c", "p2", "family", "2026-10-24T09:00:00Z", running: true), FakeAlta.Session("a", "p1", "personal", "2026-10-24T10:00:00Z")),
                _ => FakeAlta.Transcript(0),
            });
        var sessions = new MobileSessions(alta, new FakeSpaces("personal", "family"), new MobileLive());

        var page = await sessions.ListAsync(0, 2, null, default);

        CollectionAssert.AreEqual(new[] { "a", "c" }, Ids(page), "Each session once, the latest activity first.");
        Assert.AreEqual((2, true), ((int)page["next"]!, (bool)page["hasMore"]!));
        var first = (JsonObject)page["sessions"]![0]!;
        Assert.AreEqual(("Alpha", "p1", "Summary of a"), ((string?)first["project"], (string?)first["projectId"], (string?)first["summary"]));
        var running = (JsonObject)page["sessions"]![1]!["live"]!;
        Assert.AreEqual(("running", true), ((string?)running["live"], (bool)running["canSteer"]!), "A turn the plugin did not see start runs as alta says.");
        CollectionAssert.AreEqual(new[] { "session", "list", "--limit", "3", "--space", "personal" }, alta.CallsOf("session list")[0], "One list per space, up to the end of the page and one more.");
        Assert.AreEqual(2, alta.CallsOf("session list").Length);

        var next = await sessions.ListAsync(2, 2, null, default);
        CollectionAssert.AreEqual(new[] { "b" }, Ids(next));
        Assert.IsFalse((bool)next["hasMore"]!);
    }

    [TestMethod]
    public async Task ASession_SaysItsModelItsAgentItsSpaceAndItsWorktree()
    {
        var item = FakeAlta.Session("a", "p1", "work", "2026-10-24T10:00:00Z");
        item["modelSelection"] = new JsonObject { ["providerKey"] = "codex", ["modelId"] = "gpt-6.1-sol", ["reasoningEffort"] = "low", ["agentPromptId"] = "plan" };
        item["worktreeDirectory"] = @"C:\Users\me\.alta\worktrees\alpha\quiet-fox";
        item["parentSessionId"] = "parent-1";
        var alta = new FakeAlta()
            .Answer("project list", FakeAlta.Project("p1", "Alpha", @"C:\alpha"))
            .Answer("space list", new JsonObject { ["type"] = "alta.space.item", ["id"] = "work", ["name"] = "Work", ["color"] = "#2d72d2", ["icon"] = "briefcase" })
            .Answer("model list", new JsonObject { ["type"] = "alta.model.item", ["providerKey"] = "codex", ["provider"] = "Codex", ["modelId"] = "gpt-6.1-sol", ["displayName"] = "GPT-6.1 Sol" })
            .Answer("prompt list", new JsonObject { ["type"] = "alta.prompt", ["promptId"] = "plan", ["name"] = "Plan", ["promptKind"] = "agent" })
            .Answer("session list", item);
        var sessions = new MobileSessions(alta, new FakeSpaces("work"), new MobileLive());

        var page = await sessions.ListAsync(0, 6, null, default);
        var session = (JsonObject)page["sessions"]![0]!;

        Assert.AreEqual(("Codex", "GPT-6.1 Sol", "low"), ((string?)session["model"]!["providerName"], (string?)session["model"]!["name"], (string?)session["model"]!["reasoning"]));
        Assert.AreEqual(("plan", "Plan"), ((string?)session["agent"]!["id"], (string?)session["agent"]!["name"]));
        Assert.AreEqual(("work", "Work", "#2d72d2"), ((string?)session["space"]!["id"], (string?)session["space"]!["name"], (string?)session["space"]!["color"]));
        Assert.AreEqual(("quiet-fox", "parent-1"), ((string?)session["worktree"], (string?)session["parentId"]));
        Assert.AreEqual("Work", (string?)page["spaces"]![0]!["name"], "The page says which spaces the phone sees.");

        await sessions.ListAsync(6, 6, null, default);
        Assert.AreEqual(1, alta.CallsOf("space list").Length, "The names are read once for a while, not for each page.");
    }

    [TestMethod]
    public async Task List_GivesTheParentsOfItsSubAgents_ThatNoPageGaveYet()
    {
        // A root that started a sub-agent that started another: the sub-agents are the latest active, the root is older
        // than the page; a sub-agent of a session of another space is shown without it, named by nothing.
        static JsonObject Child(JsonObject item, string parent) { item["parentSessionId"] = parent; return item; }
        var list = FakeAlta.Transcript(0,
            Child(FakeAlta.Session("grand", "p1", "work", "2026-10-24T10:00:00Z", "Grandchild"), "kid"),
            Child(FakeAlta.Session("kid", "p1", "work", "2026-10-24T09:00:00Z", "Kid"), "root"),
            Child(FakeAlta.Session("stray", "p1", "work", "2026-10-24T08:00:00Z", "Stray"), "theirs"),
            FakeAlta.Session("other", "p1", "work", "2026-10-24T07:00:00Z", "Other"));
        var alta = new FakeAlta()
            .Answer("session list", (_, _) => list)
            .Answer("session status root", FakeAlta.Status("root", "p1", "work"))
            .Answer("session status theirs", FakeAlta.Status("theirs", "p1", "home"));
        var sessions = new MobileSessions(alta, new FakeSpaces("work"), new MobileLive());

        var page = await sessions.ListAsync(0, 2, null, default);

        CollectionAssert.AreEqual(new[] { "grand", "kid" }, Ids(page));
        CollectionAssert.AreEqual(new[] { "root" }, page["parents"]!.AsArray().Select(static item => (string?)item!["id"]).ToArray(), "The page holds the kid: the root only is added.");
        Assert.AreEqual(("Kid", "Title of root"), ((string?)page["sessions"]![0]!["parentTitle"], (string?)page["sessions"]![1]!["parentTitle"]));
        Assert.AreEqual(2, (int)page["next"]!, "The parents do not count in the pages.");

        var next = await sessions.ListAsync(2, 2, null, default);
        CollectionAssert.AreEqual(new[] { "stray", "other" }, Ids(next));
        Assert.AreEqual(0, next["parents"]!.AsArray().Count, "A parent of another space is not given.");
        Assert.IsNull((string?)next["sessions"]![0]!["parentTitle"], "Nor named.");

        var filtered = await sessions.ListAsync(0, 2, ["p2"], default);
        Assert.AreEqual(0, filtered["parents"]!.AsArray().Count, "A parent out of the projects the phone keeps to is not given.");
    }

    [TestMethod]
    public void TheSummary_IsOneLineOfPlainText()
    {
        Assert.AreEqual("Quick Setup 1. Open your phone. Done", MobileSessions.PlainSummary("## Quick Setup\n\n1. Open your **phone**.\n```cs\nvar x = 1;\n```\n- Done"));
        Assert.IsNull(MobileSessions.PlainSummary("  "));
        Assert.AreEqual(300, MobileSessions.PlainSummary(new string('a', 500))!.Length);
    }

    [TestMethod]
    public async Task List_InEverySpace_AsksForEverySession_AndKeepsToAProject()
    {
        var alta = new FakeAlta().Answer("session list", FakeAlta.Session("a", "p1", "work", "2026-10-24T10:00:00Z"), FakeAlta.Session("x", "p1", "work", "2026-10-24T09:00:00Z", state: "archived"));
        var sessions = new MobileSessions(alta, new FakeSpaces(), new MobileLive());

        var page = await sessions.ListAsync(0, 6, ["p1"], default);

        CollectionAssert.AreEqual(new[] { "a" }, Ids(page), "An archived session is not listed.");
        CollectionAssert.AreEqual(new[] { "session", "list", "--limit", "7", "--project", "p1" }, alta.CallsOf("session list").Single());
        Assert.AreEqual("p1", (string?)page["projects"]![0]);
    }

    [TestMethod]
    public async Task List_KeepsToSeveralProjects_InEachSpace_LatestFirst()
    {
        var alta = new FakeAlta().Answer("session list", (args, _) => (args[5], args[^1]) switch
        {
            ("p1", "personal") => FakeAlta.Transcript(0, FakeAlta.Session("a", "p1", "personal", "2026-10-24T08:00:00Z")),
            ("p2", "personal") => FakeAlta.Transcript(0, FakeAlta.Session("b", "p2", "personal", "2026-10-24T10:00:00Z")),
            ("p2", "family") => FakeAlta.Transcript(0, FakeAlta.Session("c", "p2", "family", "2026-10-24T09:00:00Z")),
            _ => FakeAlta.Transcript(0),
        });
        var sessions = new MobileSessions(alta, new FakeSpaces("personal", "family"), new MobileLive());

        // An id twice is asked once, and what is no id is left out.
        var page = await sessions.ListAsync(0, 6, ["p1", "p2", "p1", "--help"], default);

        CollectionAssert.AreEqual(new[] { "b", "c", "a" }, Ids(page), "The sessions of both projects, the latest activity first.");
        Assert.AreEqual(4, alta.CallsOf("session list").Length, "One list per space and project.");
        CollectionAssert.AreEqual(new[] { "p1", "p2" }, page["projects"]!.AsArray().Select(static project => (string?)project).ToArray());
    }

    [TestMethod]
    public async Task ASessionOfAnotherSpace_IsNotFound_NorActedOn()
    {
        var alta = new FakeAlta()
            .Answer("session status mine", FakeAlta.Status("mine", "p1", "personal"))
            .Answer("session status theirs", FakeAlta.Status("theirs", "p1", "work"))
            .Answer("session status old", FakeAlta.Status("old", "p1", "personal", state: "archived"));
        var sessions = new MobileSessions(alta, new FakeSpaces("personal"), new MobileLive());
        var api = new MobileApi(alta, sessions);

        Assert.IsNotNull(await sessions.GetAsync("mine", default));
        Assert.IsNull(await sessions.GetAsync("theirs", default));
        Assert.IsNull(await sessions.GetAsync("old", default), "An archived session is not opened.");
        Assert.IsNotNull(await sessions.FindAsync("old", default), "It can still be restored.");
        Assert.IsNull(await sessions.EventsAsync("theirs", 0, default));
        Assert.IsNull(await api.PromptAsync("theirs", new JsonObject { ["text"] = "Hello" }, default));
        Assert.IsNull(await api.AbortAsync("theirs", default));
        Assert.IsNull(await api.ArchiveAsync("theirs", default));
        Assert.IsNull(await sessions.FindAsync("--help", default), "An option is no session id.");
        Assert.AreEqual(0, alta.CallsOf("session send").Length + alta.CallsOf("session abort").Length + alta.CallsOf("session archive").Length);
    }

    [TestMethod]
    public async Task Events_AreReadAPageAtATime_WithoutToolOutput()
    {
        var reads = 0;
        var alta = new FakeAlta()
            .Answer("session status a", FakeAlta.Status("a", "p1", "personal"))
            .Answer("session events a", (args, _) =>
            {
                reads++;
                var since = long.Parse(args[args.ToList().IndexOf("--since") + 1], System.Globalization.CultureInfo.InvariantCulture);
                var events = since == 0
                    ? Enumerable.Range(1, 400).Select(static index => Event(index, "assistant.message", "Line " + index)).ToArray()
                    : [Event(401, "host.usageupdated", ""), Event(402, "activity.tool.started", "", "tool-1")];
                return FakeAlta.Transcript(0, [.. events]);
            });
        var sessions = new MobileSessions(alta, new FakeSpaces(), new MobileLive());

        var page = await sessions.EventsAsync("a", 0, default);

        Assert.IsNotNull(page);
        Assert.AreEqual((402L, 2), ((long)page["since"]!, reads), "A full page is followed by the next one.");
        var events = (JsonArray)page["events"]!;
        Assert.AreEqual(401, events.Count, "What only counts tokens is left out.");
        Assert.AreEqual(("activity.tool.started", "tool-1"), ((string?)events[^1]!["kind"], (string?)events[^1]!["activityId"]));
        var read = alta.CallsOf("session events").First();
        CollectionAssert.IsSubsetOf(new[] { "--no-tool-output", "--since", "0" }, read);
    }

    // A journal as `alta session tail` reads it: the last events before a number, in order, and whether more come before them.
    private static FakeAlta TailOf(params JsonObject[] journal)
        => new FakeAlta().Answer("session status a", FakeAlta.Status("a", "p1", "personal")).Answer("session tail a", (args, _) =>
        {
            var last = int.Parse(args[args.ToList().IndexOf("--last") + 1], System.Globalization.CultureInfo.InvariantCulture);
            var at = args.ToList().IndexOf("--before");
            var before = at < 0 ? int.MaxValue : int.Parse(args[at + 1], System.Globalization.CultureInfo.InvariantCulture);
            var held = journal.Where(record => (int)record["sequenceNumber"]! < before).ToArray();
            return FakeAlta.Transcript(0, [.. held.TakeLast(last), new JsonObject { ["type"] = "alta.session.tail.summary", ["truncated"] = held.Length > last }]);
        });

    private static long[] Numbers(JsonObject page) => [.. ((JsonArray)page["events"]!).Select(static item => (long)item!["seq"]!)];

    [TestMethod]
    public async Task LatestEvents_GiveAPageOfMessagesFromTheEnd_StartingOnAPrompt()
    {
        // Four turns of six messages each.
        var journal = Enumerable.Range(0, 24).Select(index => Event(index + 1, index % 6 == 0 ? "user.message" : "assistant.message", "Line " + index)).ToArray();
        var alta = TailOf(journal);
        var sessions = new MobileSessions(alta, new FakeSpaces(), new MobileLive());

        var page = await sessions.LatestEventsAsync("a", null, default);

        Assert.IsNotNull(page);
        var numbers = Numbers(page);
        Assert.AreEqual(7L, numbers[0], "Twenty messages reach into the first turn: the page starts on the next prompt, so that no turn is cut.");
        Assert.AreEqual((24L, 7L, true), ((long)page["since"]!, (long)page["before"]!, (bool)page["hasMore"]!));
        CollectionAssert.IsSubsetOf(new[] { "--no-tool-output", "--tool-images", "user,assistant,reasoning,tool,host,workflow" }, alta.CallsOf("session tail").First());
        Assert.IsFalse(alta.CallsOf("session tail").First().Contains("--before"), "The latest events have no number to start from.");

        var older = await sessions.LatestEventsAsync("a", 7, default);

        Assert.IsNotNull(older);
        CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4, 5, 6 }, Numbers(older));
        Assert.AreEqual((1L, false), ((long)older["before"]!, (bool)older["hasMore"]!), "No event is left before the first one.");
    }

    [TestMethod]
    public async Task LatestEvents_OfALongTurn_StopAfterAFewReads_AndLeaveOutWhatOnlyCountsTokens()
    {
        // A turn longer than a page of reads, without a prompt: what was read is given, and the next page goes on from it.
        var count = MobileSessions.MaximumPageReads * 400 + 3;
        var journal = Enumerable.Range(1, count).Select(index => Event(index, index % 2 == 0 ? "host.usageupdated" : "activity.tool.started", "", "tool-" + index)).ToArray();
        var alta = TailOf(journal);
        var sessions = new MobileSessions(alta, new FakeSpaces(), new MobileLive());

        var page = await sessions.LatestEventsAsync("a", null, default);

        Assert.IsNotNull(page);
        Assert.AreEqual(MobileSessions.MaximumPageReads, alta.CallsOf("session tail").Length);
        Assert.AreEqual((4L, true, (long)count), ((long)page["before"]!, (bool)page["hasMore"]!, (long)page["since"]!), "The number of what is left out still counts.");
        Assert.IsTrue(((JsonArray)page["events"]!).All(static item => (string?)item!["kind"] == "activity.tool.started"));
    }

    [TestMethod]
    public async Task LatestEvents_ReadAgainWithFewerEvents_WhatTheHostCut()
    {
        var journal = Enumerable.Range(1, 3).Select(index => Event(index, "assistant.message", "Line " + index)).ToArray();
        var alta = TailOf(journal);
        var cuts = 2;
        alta.Cut = args => args[1] == "tail" && cuts-- > 0;
        var sessions = new MobileSessions(alta, new FakeSpaces(), new MobileLive());

        var page = await sessions.LatestEventsAsync("a", null, default);

        Assert.IsNotNull(page);
        CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, Numbers(page), "The latest events, which the host cuts first, are not lost.");
        CollectionAssert.AreEqual(new[] { "400", "200", "100" }, alta.CallsOf("session tail").Select(static args => args[args.ToList().IndexOf("--last") + 1]).ToArray());
    }

    [TestMethod]
    public async Task LatestEvents_ThatFail_SayItAndCanBeAskedAgain()
    {
        var alta = new FakeAlta()
            .Answer("session status a", FakeAlta.Status("a", "p1", "personal"))
            .Answer("session tail a", (_, _) => FakeAlta.Transcript(1, new JsonObject { ["type"] = "alta.error", ["message"] = "The journal cannot be read." }));
        var sessions = new MobileSessions(alta, new FakeSpaces(), new MobileLive());

        var older = await sessions.LatestEventsAsync("a", 50, default);

        Assert.IsNotNull(older);
        Assert.AreEqual(("The journal cannot be read.", 50L, true), ((string?)older["error"], (long)older["before"]!, (bool)older["hasMore"]!));
        var latest = await sessions.LatestEventsAsync("a", null, default);
        Assert.IsNotNull(latest);
        Assert.AreEqual(("The journal cannot be read.", false), ((string?)latest["error"], (bool)latest["hasMore"]!));
        Assert.IsNull(await sessions.LatestEventsAsync("theirs", null, default), "A session the phone does not see is not read.");
    }

    [TestMethod]
    public async Task Create_PutsTheSessionInASpaceOfThePluginThatHasTheProject()
    {
        var alta = new FakeAlta()
            .Answer("project list --detailed --space personal", FakeAlta.Project("p1", "Alpha", @"C:\alpha", "personal"))
            .Answer("session create", new JsonObject { ["type"] = "alta.session.created", ["sessionId"] = "new-1" })
            .Answer("session send", new JsonObject { ["type"] = "alta.session.submitted", ["sessionId"] = "new-1" });
        var api = new MobileApi(alta, new MobileSessions(alta, new FakeSpaces("personal"), new MobileLive()));

        var created = await api.CreateAsync(new JsonObject { ["project"] = "p1", ["text"] = "Fix the build", ["tier"] = "quick", ["prompt"] = "plan", ["permissionMode"] = "acceptEdits", ["worktree"] = true }, default);

        Assert.AreEqual(("new-1", null), ((string?)created["id"], (string?)created["error"]));
        CollectionAssert.AreEqual(new[] { "session", "create", "--project", "p1", "--space", "personal", "--tier", "quick", "--prompt-id", "plan", "--permission-mode", "acceptEdits", "--worktree" },
            alta.CallsOf("session create").Single());
        Assert.AreEqual("Fix the build", alta.Calls.Single(static call => call.Args[1] == "send").Stdin, "The prompt goes on the standard input, never as an argument.");

        Assert.AreEqual("Unknown project.", (string?)(await api.CreateAsync(new JsonObject { ["project"] = "p9", ["text"] = "Hi" }, default))["error"], "A project of another space.");
        Assert.AreEqual("Unknown tier.", (string?)(await api.CreateAsync(new JsonObject { ["project"] = "p1", ["text"] = "Hi", ["tier"] = "huge" }, default))["error"]);
        Assert.IsNotNull((await api.CreateAsync(new JsonObject { ["project"] = "p1", ["text"] = "  " }, default))["error"]);
        await api.CreateAsync(new JsonObject { ["project"] = "p1", ["text"] = "Hi", ["provider"] = "--global", ["model"] = "m" }, default);
        Assert.IsFalse(alta.CallsOf("session create").Any(static args => args.Contains("--global")), "A value that looks like an option is not passed.");
    }

    [TestMethod]
    public async Task Prompt_SendsSteersOrQueues_AndDoesNotWaitForTheTurn()
    {
        var alta = new FakeAlta()
            .Answer("session status a", FakeAlta.Status("a", "p1", "personal", running: true))
            .Answer("session steer", new JsonObject { ["type"] = "alta.session.submitted" });
        var api = new MobileApi(alta, new MobileSessions(alta, new FakeSpaces(), new MobileLive()));

        var steered = await api.PromptAsync("a", new JsonObject { ["text"] = "Use the other file", ["mode"] = "steer" }, default);

        Assert.AreEqual((true, "steer"), ((bool)steered!["ok"]!, (string?)steered["mode"]));
        Assert.AreEqual("Use the other file", alta.Calls.Single(static call => call.Args[1] == "steer").Stdin);
        Assert.IsNotNull((await api.PromptAsync("a", new JsonObject { ["text"] = "x", ["mode"] = "shout" }, default))!["error"]);
    }

    [TestMethod]
    public async Task Prompt_NamesWhatTakesThePromptBack()
    {
        var alta = new FakeAlta()
            .Answer("session status a", FakeAlta.Status("a", "p1", "personal", running: true))
            .Answer("session steer", new JsonObject { ["type"] = "alta.session.steered", ["withdrawalId"] = "steer-1" })
            .Answer("session queue", new JsonObject { ["type"] = "alta.session.queued", ["queueItemId"] = "queue-1" })
            .Answer("session send", new JsonObject { ["type"] = "alta.session.queued", ["queueItemId"] = "queue-2" });
        var api = new MobileApi(alta, new MobileSessions(alta, new FakeSpaces(), new MobileLive()));

        Assert.AreEqual("steer-1", (string?)(await api.PromptAsync("a", new JsonObject { ["text"] = "x", ["mode"] = "steer" }, default))!["withdrawalId"]);
        Assert.AreEqual("queue-1", (string?)(await api.PromptAsync("a", new JsonObject { ["text"] = "x", ["mode"] = "queue" }, default))!["queueItemId"]);
        Assert.AreEqual("queue-2", (string?)(await api.PromptAsync("a", new JsonObject { ["text"] = "x" }, default))!["queueItemId"], "A send to a session that works is queued.");
    }

    [TestMethod]
    public async Task Queue_ListsThePromptsOfAPerson_AndARemovalSaysWhatItBecame()
    {
        var alta = new FakeAlta()
            .Answer("session status a", FakeAlta.Status("a", "p1", "personal"))
            .Answer("session status theirs", FakeAlta.Status("theirs", "p1", "work"))
            .Answer("session queued a",
                new JsonObject { ["type"] = "alta.session.queued.item", ["queueItemId"] = "queue-1", ["kind"] = "queue", ["state"] = "queued", ["prompt"] = "Then the docs", ["createdAt"] = "2026-11-05T10:00:00Z",
                    ["files"] = new JsonArray(new JsonObject { ["title"] = "a.txt", ["mediaType"] = "text/plain" }) },
                new JsonObject { ["type"] = "alta.session.queued.item", ["queueItemId"] = "queue-2", ["kind"] = "send", ["state"] = "queued", ["prompt"] = "Sent while it worked" },
                new JsonObject { ["type"] = "alta.session.queued.item", ["queueItemId"] = "queue-3", ["kind"] = "message", ["state"] = "queued", ["prompt"] = "From another agent" })
            .Answer("session unqueue a queue-1", new JsonObject { ["type"] = "alta.session.unqueued", ["status"] = "removed" })
            .Answer("session unsteer a steer-1", new JsonObject { ["type"] = "alta.session.unsteered", ["withdrawn"] = true })
            .Answer("session unsteer a steer-2", new JsonObject { ["type"] = "alta.session.unsteered", ["withdrawn"] = false });
        var api = new MobileApi(alta, new MobileSessions(alta, new FakeSpaces("personal"), new MobileLive()));

        var items = (await api.QueueAsync("a", default))!["items"]!.AsArray();
        CollectionAssert.AreEqual(new[] { "queue-1", "queue-2" }, items.Select(static item => (string?)item!["id"]).ToArray(), "The messages of agents are not the phone's to take back.");
        Assert.AreEqual(("Then the docs", "a.txt"), ((string?)items[0]!["text"], (string?)items[0]!["files"]![0]!["title"]));

        Assert.AreEqual("removed", (string?)(await api.UnqueueAsync("a", "queue-1", default))!["status"]);
        Assert.IsTrue((bool)(await api.UnsteerAsync("a", "steer-1", default))!["withdrawn"]!);
        Assert.IsFalse((bool)(await api.UnsteerAsync("a", "steer-2", default))!["withdrawn"]!);

        // A session of another space stays unknown, and nothing of it is asked.
        Assert.IsNull(await api.QueueAsync("theirs", default));
        Assert.IsNull(await api.UnqueueAsync("theirs", "queue-1", default));
        Assert.IsNull(await api.UnsteerAsync("theirs", "steer-1", default));
        Assert.IsNull(await api.UnqueueAsync("a", "--all", default), "An id is one argument of alta, never an option.");
        Assert.AreEqual(1, alta.CallsOf("session unqueue").Length);
    }

    [TestMethod]
    public async Task Tool_ReadsOneCallOnDemand_WhatItReportedIsNotAFailureOfTheRead()
    {
        var alta = new FakeAlta()
            .Answer("session status a", FakeAlta.Status("a", "p1", "personal"))
            .Answer("session status theirs", FakeAlta.Status("theirs", "p1", "work"))
            .Answer("session tool a call-1", new JsonObject
            {
                ["type"] = "alta.session.tool", ["activityId"] = "call-1", ["phase"] = "failed", ["name"] = "Bash", ["command"] = "git status", ["workingDirectory"] = "C:/work/tree",
                ["sessionFolder"] = "C:/work", ["exitCode"] = 3, ["error"] = "it broke", ["output"] = "the end", ["outputLength"] = 7, ["modifiedFiles"] = new JsonArray("a.txt"), ["correlationId"] = "internal",
            })
            .Answer("session tool a call-2", (_, _) => FakeAlta.Transcript(3));
        var api = new MobileApi(alta, new MobileSessions(alta, new FakeSpaces("personal"), new MobileLive()));

        var call = await api.ToolAsync("a", "call-1", 41, default);
        Assert.IsNull((string?)call!["error"]);
        Assert.AreEqual(("Bash", "git status", "C:/work/tree", 3, "it broke", "the end"),
            ((string?)call["name"], (string?)call["command"], (string?)call["workingDirectory"], (int)call["exitCode"]!, (string?)call["failure"], (string?)call["output"]));
        Assert.IsFalse(call.ContainsKey("correlationId"));
        CollectionAssert.AreEqual(new[] { "session", "tool", "a", "call-1", "--max-text", "12000", "--max-output", "2000", "--since", "41" }, alta.CallsOf("session tool a call-1").Single());

        Assert.IsNotNull((string?)(await api.ToolAsync("a", "call-2", null, default))!["error"]);
        Assert.IsNull(await api.ToolAsync("theirs", "call-1", null, default));
        Assert.IsNull(await api.ToolAsync("a", "--all", null, default));
    }

    [TestMethod]
    public async Task ToolOutput_ReadsAPageAtATime_WhereThePhoneAsks_AndNothingElseOfTheCall()
    {
        var alta = new FakeAlta()
            .Answer("session status a", FakeAlta.Status("a", "p1", "personal"))
            .Answer("session status theirs", FakeAlta.Status("theirs", "p1", "work"))
            .Answer("session tool a call-1", new JsonObject
            {
                ["type"] = "alta.session.tool", ["name"] = "Bash", ["command"] = "git status", ["output"] = "a page", ["outputOffset"] = 8000, ["outputLength"] = 90000,
            })
            .Answer("session tool a call-2", (_, _) => FakeAlta.Transcript(3));
        var api = new MobileApi(alta, new MobileSessions(alta, new FakeSpaces("personal"), new MobileLive()));
        string[] Asked() => [.. alta.CallsOf("session tool a call-1").Last()];
        const string Page = "32000";

        var page = await api.ToolOutputAsync("a", "call-1", 41, null, null, default);
        Assert.AreEqual(("a page", 8000, 90000), ((string?)page!["output"], (int)page["outputOffset"]!, (int)page["outputLength"]!));
        Assert.IsNull((string?)page["error"]);
        Assert.IsFalse(page.ContainsKey("command"));
        CollectionAssert.AreEqual(new[] { "session", "tool", "a", "call-1", "--max-text", "1", "--max-output", Page, "--since", "41" }, Asked(), "Where the output says the most.");

        await api.ToolOutputAsync("a", "call-1", null, 32000, null, default);
        CollectionAssert.AreEqual(new[] { "session", "tool", "a", "call-1", "--max-text", "1", "--max-output", Page, "--output-offset", "32000" }, Asked(), "The page after the one it has.");
        await api.ToolOutputAsync("a", "call-1", null, null, 40000, default);
        CollectionAssert.AreEqual(new[] { "session", "tool", "a", "call-1", "--max-text", "1", "--max-output", Page, "--output-offset", "8000" }, Asked(), "The page before the one it has.");
        await api.ToolOutputAsync("a", "call-1", null, null, 100, default);
        CollectionAssert.AreEqual(new[] { "session", "tool", "a", "call-1", "--max-text", "1", "--max-output", "100", "--output-offset", "0" }, Asked(), "What is left before it, and no more.");

        Assert.IsNotNull((string?)(await api.ToolOutputAsync("a", "call-2", null, null, null, default))!["error"]);
        Assert.IsNull(await api.ToolOutputAsync("theirs", "call-1", null, null, null, default));
        Assert.IsNull(await api.ToolOutputAsync("a", "--all", null, null, null, default));
        Assert.IsNull(await api.ToolOutputAsync("a", "call-1", null, -1, null, default));
        Assert.IsNull(await api.ToolOutputAsync("a", "call-1", null, null, 0, default));
    }

    [TestMethod]
    public async Task Choices_GiveWhatTheComposerOffers_WithTheNamesOfTheSelection()
    {
        var alta = new FakeAlta()
            .Answer("session status a", FakeAlta.Status("a", "p1", "personal"))
            .Answer("session status theirs", FakeAlta.Status("theirs", "p1", "work"))
            .Answer("model list", new JsonObject { ["type"] = "alta.model.item", ["providerKey"] = "claude", ["provider"] = "Claude Code", ["modelId"] = "opus" })
            .Answer("session choices a", new JsonObject
            {
                ["type"] = "alta.session.choices", ["sessionId"] = "a",
                // As alta writes it: a null field is left out (no permission mode: the provider's).
                ["current"] = new JsonObject { ["providerKey"] = "claude", ["modelId"] = "opus", ["reasoningEffort"] = "high", ["agentPromptId"] = "plan" },
                ["models"] = new JsonArray(
                    new JsonObject { ["id"] = "opus", ["name"] = "Opus 5.5", ["efforts"] = new JsonArray("low", "high"), ["startEffort"] = "high", ["imageInput"] = true, ["effortsComplete"] = true },
                    new JsonObject { ["id"] = "haiku", ["efforts"] = new JsonArray() }),
                ["prompts"] = new JsonArray(new JsonObject { ["id"] = "default", ["name"] = "Default" }, new JsonObject { ["id"] = "plan", ["name"] = "Plan" }),
                ["permissionModes"] = new JsonArray(new JsonObject { ["id"] = "default", ["name"] = "Ask first" }, new JsonObject { ["id"] = "acceptEdits", ["name"] = "Accept edits", ["description"] = "Accept file edits." }),
                ["defaultPermissionMode"] = "default", ["tier"] = "complex", ["modelsOrder"] = "name",
                ["providers"] = new JsonArray(new JsonObject { ["providerKey"] = "claude", ["name"] = "Claude Code" }, new JsonObject { ["providerKey"] = "codex" }),
            });
        var api = new MobileApi(alta, new MobileSessions(alta, new FakeSpaces("personal"), new MobileLive()));

        var choices = await api.ChoicesAsync("a", default);

        Assert.IsNotNull(choices);
        var current = (JsonObject)choices["current"]!;
        Assert.AreEqual(("claude", "Claude Code", "opus", "Opus 5.5", "high"), ((string?)current["provider"], (string?)current["providerName"], (string?)current["model"], (string?)current["modelName"], (string?)current["reasoning"]));
        Assert.AreEqual(("plan", "Plan", null), ((string?)current["agent"], (string?)current["agentName"], (string?)current["permissionMode"]));
        var models = (JsonArray)choices["models"]!;
        Assert.AreEqual(("haiku", "haiku", 0), ((string?)models[1]!["id"], (string?)models[1]!["name"], ((JsonArray)models[1]!["efforts"]!).Count), "A model without a name is named by its id.");
        CollectionAssert.AreEqual(new[] { "low", "high" }, ((JsonArray)models[0]!["efforts"]!).Select(static effort => (string?)effort).ToArray());
        Assert.AreEqual(true, (bool?)models[0]!["imageInput"], "A model that reads images says so, as the composer's choices do.");
        Assert.IsFalse(models[1]!.AsObject().ContainsKey("imageInput"), "A model that does not say is left without the field.");
        CollectionAssert.AreEqual(new[] { "default", "acceptEdits" }, ((JsonArray)choices["permissionModes"]!).Select(static mode => (string?)mode!["id"]).ToArray());
        Assert.AreEqual("name", (string?)choices["modelsOrder"], "the order the settings of the provider put its models in");
        var providers = (JsonArray)choices["providers"]!;
        Assert.AreEqual(("claude", "Claude Code"), ((string?)providers[0]!["id"], (string?)providers[0]!["name"]));
        Assert.AreEqual(("codex", "codex"), ((string?)providers[1]!["id"], (string?)providers[1]!["name"]), "A provider without a name is named by its key.");
        Assert.IsNull(choices["error"]);

        Assert.IsNull(await api.ChoicesAsync("theirs", default), "A session of another space has no choices on the phone.");
        Assert.AreEqual(1, alta.CallsOf("session choices").Length);
        alta.Answer("session choices a", (_, _) => FakeAlta.Transcript(1, new JsonObject { ["type"] = "alta.error", ["message"] = "Unknown command." }));
        Assert.AreEqual("Unknown command.", (string?)(await api.ChoicesAsync("a", default))!["error"], "An alta without the command says why.");
    }

    [TestMethod]
    public async Task Provider_MovesASessionThePhoneSees_AndSaysWhatRefusedIt()
    {
        var alta = new FakeAlta()
            .Answer("session status a", FakeAlta.Status("a", "p1", "personal"))
            .Answer("session status theirs", FakeAlta.Status("theirs", "p1", "work"))
            .Answer("session set_provider a", new JsonObject { ["type"] = "alta.session.provider_set", ["sessionId"] = "a", ["providerKey"] = "codex" });
        var api = new MobileApi(alta, new MobileSessions(alta, new FakeSpaces("personal"), new MobileLive()));

        var moved = await api.ProviderAsync("a", new JsonObject { ["provider"] = "codex" }, default);
        Assert.AreEqual((true, null), ((bool?)moved!["ok"], (string?)moved["error"]));
        CollectionAssert.AreEqual(new[] { "session", "set_provider", "a", "--provider", "codex" }, alta.CallsOf("session set_provider").Single());

        Assert.IsNull(await api.ProviderAsync("theirs", new JsonObject { ["provider"] = "codex" }, default), "A session of another space is not moved from the phone.");
        Assert.AreEqual(false, (bool?)(await api.ProviderAsync("a", new JsonObject(), default))!["ok"], "A provider is needed.");
        Assert.AreEqual(1, alta.CallsOf("session set_provider").Length);

        alta.Answer("session set_provider a", (_, _) => FakeAlta.Transcript(1, new JsonObject { ["type"] = "alta.error", ["message"] = "A prompt waits in the queue of the session." }));
        var refused = await api.ProviderAsync("a", new JsonObject { ["provider"] = "codex" }, default);
        Assert.AreEqual((false, "A prompt waits in the queue of the session."), ((bool?)refused!["ok"], (string?)refused["error"]));
    }

    [TestMethod]
    public async Task Prompt_CarriesTheSelectionOfThePhone_ToASendOrAQueue_NotToASteer()
    {
        var alta = new FakeAlta()
            .Answer("session status a", FakeAlta.Status("a", "p1", "personal", running: true))
            .Answer("session send", new JsonObject { ["type"] = "alta.session.submitted" })
            .Answer("session queue", new JsonObject { ["type"] = "alta.session.queued" })
            .Answer("session steer", new JsonObject { ["type"] = "alta.session.submitted" });
        var api = new MobileApi(alta, new MobileSessions(alta, new FakeSpaces(), new MobileLive()));
        JsonObject Body(string mode) => new() { ["text"] = "Go on", ["mode"] = mode, ["model"] = "opus", ["reasoning"] = "high", ["agent"] = "plan", ["permissionMode"] = "acceptEdits" };

        await api.PromptAsync("a", Body("send"), default);
        await api.PromptAsync("a", Body("queue"), default);
        await api.PromptAsync("a", Body("steer"), default);
        await api.PromptAsync("a", new JsonObject { ["text"] = "Hi", ["model"] = "--global", ["permissionMode"] = "provider" }, default);

        var options = new[] { "--model", "opus", "--reasoning", "high", "--prompt-id", "plan", "--permission-mode", "acceptEdits" };
        CollectionAssert.AreEqual(new[] { "session", "send", "a", "--stdin" }.Concat(options).Concat(["--queue-if-busy", "--no-wait"]).ToArray(), alta.CallsOf("session send")[0],
            "A send that meets a turn started on the computer waits in the queue: it is never steered into it.");
        CollectionAssert.AreEqual(new[] { "session", "queue", "a", "--stdin" }.Concat(options).ToArray(), alta.CallsOf("session queue").Single());
        CollectionAssert.AreEqual(new[] { "session", "steer", "a", "--stdin" }, alta.CallsOf("session steer").Single(), "A steer joins the running turn: it takes no selection.");
        CollectionAssert.AreEqual(new[] { "session", "send", "a", "--stdin", "--permission-mode", "provider", "--queue-if-busy", "--no-wait" }, alta.CallsOf("session send")[1], "A value that looks like an option is not passed.");
        Assert.AreEqual("Go on", alta.Calls.First(static call => call.Args[1] == "queue").Stdin);
        // The user typed every prompt on the phone: the timelines show it as theirs, not as the plugin's.
        Assert.IsTrue(alta.Options.All(static options => options is { OnBehalfOfUser: true }), "Each command runs for the user.");
    }

    [TestMethod]
    public async Task Options_GiveThePermissionModesOfEachProviderThatHasSome()
    {
        var alta = new FakeAlta()
            .Answer("provider list --detailed",
                new JsonObject { ["type"] = "alta.provider.item", ["providerKey"] = "claude", ["permissionModes"] = new JsonArray(new JsonObject { ["id"] = "default", ["name"] = "Ask first" }, "acceptEdits") },
                new JsonObject { ["type"] = "alta.provider.item", ["providerKey"] = "codex" });
        var api = new MobileApi(alta, new MobileSessions(alta, new FakeSpaces(), new MobileLive()));

        var options = await api.OptionsAsync(default);

        var modes = (JsonObject)options["permissionModes"]!;
        Assert.AreEqual(1, modes.Count, "A provider that offers no mode is left out.");
        Assert.AreEqual(("default", "Ask first", "acceptEdits", null), ((string?)modes["claude"]![0]!["id"], (string?)modes["claude"]![0]!["name"], (string?)modes["claude"]![1]!["id"], (string?)modes["claude"]![1]!["name"]));
    }

    [TestMethod]
    public async Task Options_GiveTheOrderOfTheModelsOfEachProvider()
    {
        var alta = new FakeAlta()
            .Answer("provider list --detailed",
                new JsonObject { ["type"] = "alta.provider.item", ["providerKey"] = "acp", ["modelsOrder"] = "name_version" },
                new JsonObject { ["type"] = "alta.provider.item", ["providerKey"] = "codex", ["modelsOrder"] = "provider" },
                new JsonObject { ["type"] = "alta.provider.item", ["providerKey"] = "older" });
        var api = new MobileApi(alta, new MobileSessions(alta, new FakeSpaces(), new MobileLive()));

        var orders = (JsonObject)(await api.OptionsAsync(default))["modelsOrders"]!;

        Assert.AreEqual(("name_version", "provider"), ((string?)orders["acp"], (string?)orders["codex"]));
        Assert.IsFalse(orders.ContainsKey("older"), "A CodeAlta that does not say leaves the provider in the order it gives.");
    }

    [TestMethod]
    public async Task Options_NameTheProviderOfAModel_AsTheWindowNamesIt()
    {
        var alta = new FakeAlta()
            .Answer("model list --detailed",
                new JsonObject { ["type"] = "alta.model.item", ["providerKey"] = "codex", ["provider"] = "codex", ["modelId"] = "gpt-6.1-sol" },
                new JsonObject { ["type"] = "alta.model.item", ["providerKey"] = "local", ["provider"] = "local", ["modelId"] = "qwen" })
            .Answer("provider list --detailed", new JsonObject { ["type"] = "alta.provider.item", ["providerKey"] = "codex", ["displayName"] = "Codex" });
        var api = new MobileApi(alta, new MobileSessions(alta, new FakeSpaces(), new MobileLive()));

        var models = (JsonArray)(await api.OptionsAsync(default))["models"]!;

        CollectionAssert.AreEqual(new[] { "Codex", "local" }, models.Select(static model => (string?)model!["providerName"]).ToArray(), "A provider without a name keeps the one of its models.");
    }

    [TestMethod]
    public async Task Prompt_SendsOrQueuesFilesWithThePrompt_AndNeverSteersThem()
    {
        var saved = new List<string>();
        var alta = new FakeAlta()
            .Answer("session", (args, stdin) =>
            {
                var list = args.ToArray();
                var at = Array.IndexOf(list, "--attachments");
                if (at > 0) saved.Add(File.ReadAllText(list[at + 1]));
                Assert.AreEqual("Read this", stdin);
                return FakeAlta.Transcript(0, new JsonObject { ["type"] = "alta.session.submitted" });
            })
            .Answer("session status a", FakeAlta.Status("a", "p1", "personal"));
        var api = new MobileApi(alta, new MobileSessions(alta, new FakeSpaces(), new MobileLive()));
        JsonObject Attachment(string name, string base64 = "aGk+") => new() { ["title"] = name, ["mediaType"] = "text/plain", ["base64"] = base64 };

        var sent = await api.PromptAsync("a", new JsonObject { ["text"] = "Read this", ["attachments"] = new JsonArray(Attachment("notes.txt")) }, default);

        Assert.AreEqual((true, null), ((bool)sent!["ok"]!, (string?)sent["error"]));
        var send = alta.CallsOf("session send").Single();
        CollectionAssert.AreEqual(new[] { "session", "send", "a", "--stdin", "--queue-if-busy", "--no-wait", "--attachments" }, send.Take(7).ToArray(),
            "A turn that started on the computer meanwhile gets the prompt in its queue, with its files; the answer comes once alta handed it over.");
        Assert.AreEqual(@"[{""title"":""notes.txt"",""mediaType"":""text/plain"",""base64"":""aGk+""}]", saved[0], "The base64 is written as the phone sent it, without escaping its '+'.");
        StringAssert.StartsWith(Path.GetFileName(send[^1]), MobileApi.AttachmentFilePrefix + Environment.ProcessId + "-", "The file names the process it waits for.");
        var path = send[^1];
        for (var wait = 0; File.Exists(path) && wait < 50; wait++) await Task.Delay(20);
        Assert.IsFalse(File.Exists(path), "The file of the command is removed when the command ends.");

        var queued = await api.PromptAsync("a", new JsonObject { ["text"] = "Read this", ["mode"] = "queue", ["attachments"] = new JsonArray(Attachment("later.txt")) }, default);
        Assert.AreEqual(true, (bool?)queued!["ok"]);
        CollectionAssert.AreEqual(new[] { "session", "queue", "a", "--stdin", "--attachments" }, alta.CallsOf("session queue").Single().Take(5).ToArray(), "A queued prompt keeps its files.");

        var before = alta.Calls.Count;
        Assert.AreEqual("Files are not steered into a turn: they go with Send or Queue.",
            (string?)(await api.PromptAsync("a", new JsonObject { ["text"] = "x", ["mode"] = "steer", ["attachments"] = new JsonArray(Attachment("a.txt")) }, default))!["error"]);
        Assert.AreEqual(before, alta.Calls.Count, "A steer with files is refused before alta is asked.");
    }

    [TestMethod]
    public async Task Prompt_RefusesWhatTheHostWouldRefuse_BeforeAltaIsAsked()
    {
        var alta = new FakeAlta().Answer("session status a", FakeAlta.Status("a", "p1", "personal"));
        var api = new MobileApi(alta, new MobileSessions(alta, new FakeSpaces(), new MobileLive()));
        async Task<string?> RefusalAsync(string? text, params JsonObject[] files)
        {
            var body = new JsonObject { ["text"] = text, ["attachments"] = new JsonArray([.. files]) };
            return (string?)(await api.PromptAsync("a", body, default))!["error"];
        }

        JsonObject File(string title, string base64, string mediaType = "application/octet-stream") => new() { ["title"] = title, ["mediaType"] = mediaType, ["base64"] = base64 };
        // The canonical base64 of zeros of a size.
        static string Of(long bytes) => new string('A', (int)(bytes / 3 * 4)) + (bytes % 3) switch { 1 => "AA==", 2 => "AAA=", _ => "" };

        Assert.AreEqual("A prompt has at most 100,000 characters.", await RefusalAsync(new string('x', MobileApi.MaximumPrompt + 1), File("a.bin", "aGk=")),
            "A long prompt with files is refused, never sent without its text.");
        Assert.AreEqual("A prompt has at most 100,000 characters.", await RefusalAsync(new string('x', MobileApi.MaximumPrompt + 1)));
        Assert.AreEqual("A prompt has at most 64 attachments.", await RefusalAsync("x", [.. Enumerable.Range(0, 65).Select(index => File($"f{index}.bin", "aGk="))]));
        Assert.AreEqual("A file can be from 1 byte to 20 MB.", await RefusalAsync("x", File("big.bin", Of(20L * 1024 * 1024 + 1))));
        Assert.AreEqual("A file can be from 1 byte to 20 MB.", await RefusalAsync("x", File("empty.bin", "")));
        Assert.AreEqual("A prompt's files are limited to 64 MB in all.", await RefusalAsync("x", [.. Enumerable.Range(0, 4).Select(index => File($"f{index}.bin", Of(17L * 1024 * 1024)))]));
        foreach (var invalid in new[] { "aG=k", "a===", "aGk", "aG!=", "aGl=", "aGk=aGk=" })
            Assert.AreEqual("The content of bad.bin is not valid base64.", await RefusalAsync("x", File("bad.bin", invalid)), invalid);
        Assert.AreEqual("An attachment needs a name, a type and its content.", await RefusalAsync("x", File("../x", "aGk=")));
        Assert.AreEqual("An attachment needs a name, a type and its content.", await RefusalAsync("x", File("x.txt", "aGk=", "text/plain; charset=utf-8")));
        // An image is a PNG the host reads: a JPEG named as one, or an interlaced PNG, is refused on the server too.
        Assert.AreEqual("photo.png is not an image the host reads: an 8-bit RGB or RGBA PNG, not interlaced.",
            await RefusalAsync("x", File("photo.png", Convert.ToBase64String([0xFF, 0xD8, 0xFF, 0xE0, .. new byte[60]]), "image/png")));
        Assert.AreEqual("photo.png is not an image the host reads: an 8-bit RGB or RGBA PNG, not interlaced.",
            await RefusalAsync("x", File("photo.png", Convert.ToBase64String(Png(interlaced: true)), "image/png")));
        Assert.IsEmpty(alta.CallsOf("session send"));

        alta.Answer("session send", new JsonObject { ["type"] = "alta.session.submitted" });
        Assert.IsNull(await RefusalAsync(null, File("photo.png", Convert.ToBase64String(Png(interlaced: false)), "image/png")), "Files alone are a prompt.");
        Assert.AreEqual("", alta.Calls.Last().Stdin);
    }

    [TestMethod]
    public async Task Prompt_WaitsUntilAltaHandedThePromptOver_NotAFixedTime()
    {
        var clock = new ManualClock();
        var handedOver = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var alta = new FakeAlta()
            .Answer("session status a", FakeAlta.Status("a", "p1", "personal"))
            .Answer("session send", (_, _) => handedOver.Task);
        var api = new MobileApi(alta, new MobileSessions(alta, new FakeSpaces(), new MobileLive()), clock: clock, promptPatience: TimeSpan.FromMinutes(1));

        var prompt = api.PromptAsync("a", new JsonObject { ["text"] = "Convert these", ["attachments"] = new JsonArray(new JsonObject { ["title"] = "a.docx", ["mediaType"] = "application/octet-stream", ["base64"] = "aGk=" }) }, default);
        await clock.TimerCreated.Task.WithinAsync(TestTime.Seconds(30));
        // The files take long to prepare (a document converted, a model checked): well after the old three seconds, the phone still waits.
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.IsFalse(prompt.IsCompleted, "The request waits for alta, not for a fixed time.");
        handedOver.SetResult(FakeAlta.Transcript(1, new JsonObject { ["type"] = "alta.error", ["message"] = "This model does not read images." }));

        var answer = await prompt.WithinAsync(TestTime.Seconds(30));
        Assert.AreEqual((false, "This model does not read images."), ((bool?)answer!["ok"], (string?)answer["error"]), "A refusal after the files were prepared reaches the phone.");

        // Only a command that hangs ends the wait, at the bound the phone and Tailscale Serve would give up at.
        var hanging = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        alta.Answer("session send", (_, _) => hanging.Task);
        clock.TimerCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = api.PromptAsync("a", new JsonObject { ["text"] = "Go on" }, default);
        await clock.TimerCreated.Task.WithinAsync(TestTime.Seconds(30));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.AreEqual(true, (bool?)(await waiting.WithinAsync(TestTime.Seconds(30)))!["pending"]);
        hanging.SetResult(FakeAlta.Transcript(0));
    }

    [TestMethod]
    public async Task Options_SayWhetherAModelReadsImages()
    {
        var alta = new FakeAlta()
            .Answer("model list --detailed",
                new JsonObject { ["type"] = "alta.model.item", ["providerKey"] = "claude", ["modelId"] = "opus", ["imageInput"] = true },
                new JsonObject { ["type"] = "alta.model.item", ["providerKey"] = "local", ["modelId"] = "text", ["imageInput"] = false },
                new JsonObject { ["type"] = "alta.model.item", ["providerKey"] = "local", ["modelId"] = "unknown" });
        var api = new MobileApi(alta, new MobileSessions(alta, new FakeSpaces(), new MobileLive()));

        var models = (JsonArray)(await api.OptionsAsync(default))["models"]!;

        Assert.AreEqual((true, false, null), ((bool?)models[0]!["imageInput"], (bool?)models[1]!["imageInput"], models[2]!["imageInput"] as JsonNode));
    }

    [TestMethod]
    public async Task Create_SendsFilesWithTheFirstPrompt_AndDropsTheFileWhenTheCommandEnds()
    {
        var directory = Path.Combine(Path.GetTempPath(), "alta-mobile-create-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var alta = new FakeAlta()
                .Answer("project list --detailed --space personal", FakeAlta.Project("p1", "Alpha", @"C:\alpha", "personal"))
                .Answer("session create", new JsonObject { ["type"] = "alta.session.created", ["sessionId"] = "new-1" })
                .Answer("session send", (args, stdin) =>
                {
                    Assert.AreEqual("", stdin);
                    var list = args.ToArray();
                    var at = Array.IndexOf(list, "--attachments");
                    Assert.IsTrue(at > 0 && File.ReadAllText(list[at + 1]).Contains("notes.txt", StringComparison.Ordinal));
                    return FakeAlta.Transcript(0, new JsonObject { ["type"] = "alta.session.submitted" });
                });
            var api = new MobileApi(alta, new MobileSessions(alta, new FakeSpaces("personal"), new MobileLive()), directory);
            var created = await api.CreateAsync(new JsonObject
            {
                ["project"] = "p1", ["tier"] = "quick",
                ["attachments"] = new JsonArray(new JsonObject { ["title"] = "notes.txt", ["mediaType"] = "text/plain", ["base64"] = "aGk=" }),
            }, default);

            Assert.AreEqual("new-1", (string?)created["id"]);
            Assert.IsNull((string?)created["sendError"]);
            var send = alta.CallsOf("session send").Single();
            CollectionAssert.AreEqual(new[] { "session", "send", "new-1", "--stdin", "--no-wait", "--attachments" }, send.Take(6).ToArray());
            for (var wait = 0; File.Exists(send[^1]) && wait < 50; wait++) await Task.Delay(20);
            Assert.IsFalse(File.Exists(send[^1]));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void RemoveStaleAttachments_DeletesFilesLeftFromAnEarlierRun_NotThoseOfAnotherInstanceThatRuns()
    {
        var directory = Path.Combine(Path.GetTempPath(), "alta-mobile-stale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        System.Diagnostics.Process? other = null;
        try
        {
            // A command of every system that waits about 30 seconds: Windows has no `sleep` without Git's usr/bin on its PATH.
            other = System.Diagnostics.Process.Start(OperatingSystem.IsWindows()
                ? new System.Diagnostics.ProcessStartInfo("ping", "-n 31 127.0.0.1") { UseShellExecute = false, RedirectStandardOutput = true }
                : new System.Diagnostics.ProcessStartInfo("sleep", "30") { UseShellExecute = false })!;
            string Write(string name)
            {
                var path = Path.Combine(directory, name);
                File.WriteAllText(path, "[]");
                return path;
            }

            var ended = Write($"{MobileApi.AttachmentFilePrefix}{ExitedProcessId()}-1.json");
            var legacy = Write(MobileApi.AttachmentFilePrefix + "old.json");
            var running = Write($"{MobileApi.AttachmentFilePrefix}{other.Id}-2.json");
            var mine = Write($"{MobileApi.AttachmentFilePrefix}{Environment.ProcessId}-3.json");
            var forgotten = Write($"{MobileApi.AttachmentFilePrefix}{other.Id}-4.json");
            File.SetLastWriteTimeUtc(forgotten, DateTime.UtcNow - TimeSpan.FromDays(2));
            var kept = Write("mobile.json");

            MobileApi.RemoveStaleAttachments(directory);

            Assert.IsFalse(File.Exists(ended), "A file of a CodeAlta that ended.");
            Assert.IsFalse(File.Exists(legacy));
            Assert.IsTrue(File.Exists(running), "A file of the other instance (the developer one, or the normal one) waits for its command.");
            Assert.IsTrue(File.Exists(mine), "A file of this process: the plugin restarted while a prompt was handed over.");
            Assert.IsFalse(File.Exists(forgotten), "A day old: no command waits that long.");
            Assert.IsTrue(File.Exists(kept));
        }
        finally
        {
            if (other is not null)
            {
                if (!other.HasExited) other.Kill();
                other.Dispose();
            }

            Directory.Delete(directory, recursive: true);
        }

        static int ExitedProcessId()
        {
            using var ended = System.Diagnostics.Process.Start(OperatingSystem.IsWindows()
                ? new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false }
                : new System.Diagnostics.ProcessStartInfo("true") { UseShellExecute = false })!;
            ended.WaitForExit();
            return ended.Id;
        }
    }

    [TestMethod]
    public async Task Events_NameTheFilesWithoutTheirPaths()
    {
        var alta = new FakeAlta()
            .Answer("session status a", FakeAlta.Status("a", "p1", "personal"))
            .Answer("session events", new JsonObject
            {
                ["type"] = "alta.session.event", ["sequenceNumber"] = 3, ["timestamp"] = "2026-10-24T10:00:00Z", ["kind"] = "user.message",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["text"] = "Read these\nLocal image (photo.png): /Users/me/.alta/sessions/2026/10/24/a.attachments/20261024-01-photo-1a2b3c4d.png"
                        + "\r\nAttached file (notes.txt): C:\\Users\\me\\.alta\\sessions\\a.attachments\\20261024-02-notes-1a2b3c4d.txt",
                }),
                ["metadata"] = new JsonObject
                {
                    ["attachments"] = new JsonArray(
                        new JsonObject { ["title"] = "photo.png", ["mediaType"] = "image/png", ["id"] = "20261024-01-photo-1a2b3c4d.png" },
                        new JsonObject { ["title"] = "notes.txt", ["mediaType"] = "text/plain", ["id"] = "20261024-02-notes-1a2b3c4d.txt" },
                        new JsonObject { ["title"] = "odd", ["mediaType"] = "text/plain", ["id"] = "../x" }),
                },
            });
        var api = new MobileSessions(alta, new FakeSpaces(), new MobileLive());

        var page = await api.EventsAsync("a", 0, default);

        var events = (JsonArray)page!["events"]!;
        Assert.AreEqual("Read these", (string?)events[0]!["text"], "The phone gets the words, never the paths of this computer.");
        CollectionAssert.AreEqual(new[] { ("photo.png", "image/png", "20261024-01-photo-1a2b3c4d.png"), ("notes.txt", "text/plain", "20261024-02-notes-1a2b3c4d.txt") },
            ((JsonArray)events[0]!["attachments"]!).Select(static file => ((string?)file!["title"], (string?)file["mediaType"], (string?)file["id"])).ToArray(),
            "Each file has its title, its type and an id without a folder; an id with one is left out.");
        Assert.AreEqual("Just words\nAttached file (x): typed by the user", MobileSessions.WithoutPaths("Just words\nAttached file (x): typed by the user",
            new JsonArray(new JsonObject { ["title"] = "x", ["id"] = "y.txt" })).Text, "A line that does not name the copy of a file stays.");
    }

    private static byte[] Png(bool interlaced)
    {
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 1);
        header[8] = 8;
        header[9] = 6;
        header[12] = interlaced ? (byte)1 : (byte)0;
        Chunk("IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write([0, 0, 0, 0, 0]);
        Chunk("IDAT", compressed.ToArray());
        Chunk("IEND", []);
        return output.ToArray();

        void Chunk(string name, byte[] bytes)
        {
            Span<byte> length = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            output.Write(length);
            var type = System.Text.Encoding.ASCII.GetBytes(name);
            output.Write(type);
            output.Write(bytes);
            var crc = uint.MaxValue;
            foreach (var b in type.Concat(bytes))
            {
                crc ^= b;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
            }

            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, ~crc);
            output.Write(length);
        }
    }

    [TestMethod]
    public async Task Events_SayWhatTheHostSent_TheImagesAndTheRecordsOfScripts()
    {
        JsonObject With(JsonObject record, JsonObject metadata) { record["metadata"] = metadata; return record; }
        var alta = new FakeAlta()
            .Answer("session status a", FakeAlta.Status("a", "p1", "personal"))
            .Answer("session events a", (_, _) => FakeAlta.Transcript(0,
                With(Event(1, "user.message", "[CodeAlta background job]\nJob: job-1"), new JsonObject { ["provenance"] = "background-job" }),
                With(Event(2, "user.message", "Done."), new JsonObject { ["provenance"] = "agent-session", ["sourceSessionId"] = "child-1" }),
                With(Event(3, "user.message", "Odd"), new JsonObject { ["provenance"] = "Not One!", ["sourceSessionId"] = "--help" }),
                With(Event(4, "activity.toolcall.started", "exec"), new JsonObject { ["activityId"] = "call-1" }),
                With(Event(5, "activity.toolcall.started", "read_file"), new JsonObject { ["activityId"] = "call-2", ["parentActivityId"] = "call-1" }),
                With(Event(6, "tool.output", "the text of the tool"), new JsonObject
                {
                    ["parentActivityId"] = "call-2",
                    ["images"] = new JsonArray(new JsonObject { ["title"] = "Shot", ["mediaType"] = "image/png", ["id"] = "20261024-tool-shot.png" }, new JsonObject { ["title"] = "x", ["id"] = "../x.png" }),
                }),
                With(Event(7, "workflow.stepsDeclared", "Compare"), new JsonObject
                {
                    ["runId"] = "run-1", ["callId"] = "call-1", ["title"] = "Compare", ["steps"] = new JsonArray(new JsonObject { ["id"] = "research", ["title"] = "Research" }),
                }),
                With(Event(8, "workflow.stepEnded", ""), new JsonObject { ["runId"] = "run-1", ["callId"] = "call-1", ["stepId"] = "research", ["status"] = "completed", ["durationMs"] = 1200 }),
                Event(9, "assistant.message", "Here: ![The shot](/tmp/shot.png)\n\n```\n![not](a.png)\n```"),
                With(Event(10, "workflow.runStarted", "Review"), new JsonObject { ["runId"] = "run-2", ["callId"] = "call-3", ["forUser"] = true }),
                With(Event(11, "workflow.presented", "## Findings\n- one"), new JsonObject { ["runId"] = "run-2", ["callId"] = "call-3" })));
        var sessions = new MobileSessions(alta, new FakeSpaces(), new MobileLive());

        var events = (JsonArray)(await sessions.EventsAsync("a", 0, default))!["events"]!;

        CollectionAssert.IsSubsetOf(new[] { "--no-tool-output", "--tool-images", "user,assistant,reasoning,tool,host,workflow" }, alta.CallsOf("session events").First());
        Assert.AreEqual("background-job", (string?)events[0]!["provenance"]);
        Assert.AreEqual(("agent-session", "child-1"), ((string?)events[1]!["provenance"], (string?)events[1]!["sourceSessionId"]));
        Assert.IsNull(events[2]!["provenance"] ?? events[2]!["sourceSessionId"], "What is no provenance, and no id of a session, is left out.");
        Assert.IsNull(events[3]!["parentId"]);
        Assert.AreEqual("call-1", (string?)events[4]!["parentId"], "A call of a script names the call that runs the script.");
        Assert.AreEqual(("", "call-2"), ((string?)events[5]!["text"], (string?)events[5]!["parentId"]), "The output goes for its images, without its text.");
        Assert.AreEqual(("Shot", "20261024-tool-shot.png", 1), ((string?)events[5]!["images"]![0]!["title"], (string?)events[5]!["images"]![0]!["id"], ((JsonArray)events[5]!["images"]!).Count),
            "An image whose id names a folder is left out.");
        Assert.AreEqual(("run-1", "call-1", "research"), ((string?)events[6]!["workflow"]!["runId"], (string?)events[6]!["workflow"]!["callId"], (string?)events[6]!["workflow"]!["steps"]![0]!["id"]));
        Assert.AreEqual(("completed", 1200L), ((string?)events[7]!["workflow"]!["status"], (long)events[7]!["workflow"]!["durationMs"]!));
        var linked = (JsonArray)events[8]!["images"]!;
        Assert.AreEqual(1, linked.Count, "A picture written in code is none.");
        Assert.AreEqual(("0", "The shot", "/tmp/shot.png"), ((string?)linked[0]!["id"], (string?)linked[0]!["title"], (string?)linked[0]!["src"]));
        Assert.IsTrue((bool?)events[9]!["workflow"]!["forUser"], "A run the host started for the user says so.");
        Assert.AreEqual(("## Findings\n- one", "run-2"), ((string?)events[10]!["text"], (string?)events[10]!["workflow"]!["runId"]), "What a script presented is the text of its record.");
    }

    [TestMethod]
    public void LinkedImages_AreThePicturesOfTheDiskOutsideCode()
    {
        var (full, address) = OperatingSystem.IsWindows() ? (@"C:\shots\h.png", "file:///C:/shots/i.png") : ("/shots/h.png", "file:///shots/i.png");
        var text = "![a](one.png) `![b](two.png)` ![c](<with space.png> \"title\") ![d](https://example.com/x.png) ![e](data:image/png;base64,AA==)\n"
            + $"    ![f](indented.png)\n~~~\n![g](fenced.png)\n~~~\n![h]({full}) ![i]({address}) ![j](my%20shot.png)";

        CollectionAssert.AreEqual(new[] { ("a", "one.png"), ("c", "with space.png"), ("h", full), ("i", address), ("j", "my%20shot.png") },
            MobileTimeline.LinkedImages(text).ToArray());
        var folder = Path.GetFullPath("/start");
        Assert.IsNull(MobileImages.Locate("~/x.png", folder), "A path of the shell is not resolved.");
        Assert.IsNull(MobileImages.Locate("x.png", null), "A relative path needs the folder of the session.");
        Assert.AreEqual(Path.Combine(folder, "my shot.png"), MobileImages.Locate("my%20shot.png", folder));
        Assert.AreEqual(Path.Combine(folder, "a%20b.png"), MobileImages.Locate("a%2520b.png", folder), "A target is decoded once: this file has a % in its name.");
    }

    private static JsonObject Event(int sequence, string kind, string text, string? activity = null)
        => new()
        {
            ["type"] = "alta.session.event", ["sequenceNumber"] = sequence, ["timestamp"] = "2026-10-24T10:00:00Z", ["kind"] = kind,
            ["content"] = new JsonArray(new JsonObject { ["text"] = text }), ["metadata"] = new JsonObject { ["activityId"] = activity },
        };

    private static string?[] Ids(JsonObject page) => [.. ((JsonArray)page["sessions"]!).Select(static session => (string?)session!["id"])];
}
