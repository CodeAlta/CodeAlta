using System.Text.Json.Nodes;

namespace CodeAlta.Plugin.Mobile.Tests;

[TestClass]
public sealed class MobileSessionsTests
{
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

        var page = await sessions.ListAsync(0, 6, "p1", default);

        CollectionAssert.AreEqual(new[] { "a" }, Ids(page), "An archived session is not listed.");
        CollectionAssert.AreEqual(new[] { "session", "list", "--limit", "7", "--project", "p1" }, alta.CallsOf("session list").Single());
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
    public async Task Choices_GiveWhatTheComposerOffers_WithTheNamesOfTheSelection()
    {
        var alta = new FakeAlta()
            .Answer("session status a", FakeAlta.Status("a", "p1", "personal"))
            .Answer("session status theirs", FakeAlta.Status("theirs", "p1", "work"))
            .Answer("model list", new JsonObject { ["type"] = "alta.model.item", ["providerKey"] = "claude", ["provider"] = "Claude Code", ["modelId"] = "opus" })
            .Answer("session choices a", new JsonObject
            {
                ["type"] = "alta.session.choices", ["sessionId"] = "a",
                ["current"] = new JsonObject { ["providerKey"] = "claude", ["modelId"] = "opus", ["reasoningEffort"] = "high", ["agentPromptId"] = "plan", ["permissionMode"] = null },
                ["models"] = new JsonArray(
                    new JsonObject { ["id"] = "opus", ["name"] = "Opus 5.5", ["efforts"] = new JsonArray("low", "high"), ["startEffort"] = "high", ["imageInput"] = true },
                    new JsonObject { ["id"] = "haiku", ["name"] = null, ["efforts"] = new JsonArray() }),
                ["prompts"] = new JsonArray(new JsonObject { ["id"] = "default", ["name"] = "Default" }, new JsonObject { ["id"] = "plan", ["name"] = "Plan" }),
                ["permissionModes"] = new JsonArray(new JsonObject { ["id"] = "default", ["name"] = "Ask first", ["description"] = null }, new JsonObject { ["id"] = "acceptEdits", ["name"] = "Accept edits" }),
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
        CollectionAssert.AreEqual(new[] { "default", "acceptEdits" }, ((JsonArray)choices["permissionModes"]!).Select(static mode => (string?)mode!["id"]).ToArray());
        Assert.IsNull(choices["error"]);

        Assert.IsNull(await api.ChoicesAsync("theirs", default), "A session of another space has no choices on the phone.");
        Assert.AreEqual(1, alta.CallsOf("session choices").Length);
        alta.Answer("session choices a", (_, _) => FakeAlta.Transcript(1, new JsonObject { ["type"] = "alta.error", ["message"] = "Unknown command." }));
        Assert.AreEqual("Unknown command.", (string?)(await api.ChoicesAsync("a", default))!["error"], "An alta without the command says why.");
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
        CollectionAssert.AreEqual(new[] { "session", "send", "a", "--stdin" }.Concat(options).ToArray(), alta.CallsOf("session send")[0]);
        CollectionAssert.AreEqual(new[] { "session", "queue", "a", "--stdin" }.Concat(options).ToArray(), alta.CallsOf("session queue").Single());
        CollectionAssert.AreEqual(new[] { "session", "steer", "a", "--stdin" }, alta.CallsOf("session steer").Single(), "A steer joins the running turn: it takes no selection.");
        CollectionAssert.AreEqual(new[] { "session", "send", "a", "--stdin", "--permission-mode", "provider" }, alta.CallsOf("session send")[1], "A value that looks like an option is not passed.");
        Assert.AreEqual("Go on", alta.Calls.First(static call => call.Args[1] == "queue").Stdin);
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

    private static JsonObject Event(int sequence, string kind, string text, string? activity = null)
        => new()
        {
            ["type"] = "alta.session.event", ["sequenceNumber"] = sequence, ["timestamp"] = "2026-10-24T10:00:00Z", ["kind"] = kind,
            ["content"] = new JsonArray(new JsonObject { ["text"] = text }), ["metadata"] = new JsonObject { ["activityId"] = activity },
        };

    private static string?[] Ids(JsonObject page) => [.. ((JsonArray)page["sessions"]!).Select(static session => (string?)session!["id"])];
}
