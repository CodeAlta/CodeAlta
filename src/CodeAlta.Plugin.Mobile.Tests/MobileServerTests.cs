using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Mobile.Tests;

[TestClass]
public sealed class MobileServerTests
{
    [TestMethod]
    public async Task ThePage_IsServedFromTheFilesOfTheApplication_WithItsPolicy()
    {
        await using var fixture = await Fixture.StartAsync();

        using var page = await fixture.Client.GetAsync("/");
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
        Assert.AreEqual("<!doctype html><p>Phone</p>", await page.Content.ReadAsStringAsync());
        StringAssert.Contains(page.Headers.GetValues("Content-Security-Policy").Single(), "script-src 'self'");
        Assert.AreEqual("no-cache", page.Headers.CacheControl?.ToString());
        Assert.AreEqual("DENY", page.Headers.GetValues("X-Frame-Options").Single());

        using var script = await fixture.Client.GetAsync("/assets/mobile-abc.js");
        Assert.AreEqual("public, max-age=31536000, immutable", script.Headers.CacheControl?.ToString());
        Assert.AreEqual("text/javascript", script.Content.Headers.ContentType?.MediaType);
        using var worker = await fixture.Client.GetAsync("/sw.js");
        Assert.AreEqual(HttpStatusCode.OK, worker.StatusCode, "The service worker is served at the root, whose scope it has.");
        Assert.AreEqual(HttpStatusCode.NotFound, (await fixture.Client.GetAsync("/assets/other.js")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await fixture.Client.GetAsync("/index.html")).StatusCode, "Only the files of the page of the phones.");
    }

    [TestMethod]
    public async Task ThePairingToken_SignsAPhoneIn_WithAKeyOfItsOwn_AndTheResetSignsItOut()
    {
        await using var fixture = await Fixture.StartAsync();
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await fixture.Client.GetAsync("/api/sessions")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await fixture.PostAsync("/api/login", new JsonObject { ["token"] = "wrong" })).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await fixture.PostAsync("/api/login", new JsonObject { ["token"] = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFG" })).StatusCode, "A token of the right shape that is no pairing token.");

        using var login = await fixture.PostAsync("/api/login", new JsonObject { ["token"] = fixture.Token });
        Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
        var key = (string)JsonNode.Parse(await login.Content.ReadAsStringAsync())!["key"]!;
        Assert.IsTrue(MobileToken.IsValid(key) && key != fixture.Token, "The phone gets a key of its own.");
        var cookie = login.Headers.GetValues("Set-Cookie").Single();
        StringAssert.Contains(cookie, MobileServer.CookieName + "=" + key);
        StringAssert.Contains(cookie, "httponly");
        StringAssert.Contains(cookie, "samesite=strict");
        Assert.AreEqual(HttpStatusCode.OK, (await fixture.Client.GetAsync("/api/sessions")).StatusCode, "The cookie signs the phone in.");
        // Without the cookie of the client.
        using var bare = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = fixture.Client.BaseAddress };
        Assert.AreEqual(HttpStatusCode.OK, (await bare.SendAsync(Bearer(key))).StatusCode, "So does its key.");
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await bare.SendAsync(Bearer(fixture.Token))).StatusCode, "The pairing token only signs in.");
        using var again = await fixture.PostAsync("/api/login", new JsonObject { ["token"] = key });
        Assert.AreEqual(key, (string?)JsonNode.Parse(await again.Content.ReadAsStringAsync())!["key"], "The phone signs in again with its key, and keeps it.");

        // A reset signs every phone out.
        var before = fixture.Token;
        await fixture.Access.ResetAsync(CancellationToken.None);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await fixture.Client.GetAsync("/api/sessions")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await bare.SendAsync(Bearer(key))).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await fixture.PostAsync("/api/login", new JsonObject { ["token"] = before })).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await fixture.PostAsync("/api/login", new JsonObject { ["token"] = fixture.Token })).StatusCode, "The new code signs in.");

        static HttpRequestMessage Bearer(string value)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/sessions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", value);
            return request;
        }
    }

    [TestMethod]
    public async Task ARequestThatNamesAnotherHost_IsRefused()
    {
        await using var fixture = await Fixture.StartAsync();
        // A page of another site whose name resolves to the loopback address.
        using var rebound = new HttpRequestMessage(HttpMethod.Get, "/") { Headers = { Host = "evil.example:" + fixture.Port } };
        Assert.AreEqual(HttpStatusCode.MisdirectedRequest, (await fixture.Client.SendAsync(rebound)).StatusCode);
        using var tailnet = new HttpRequestMessage(HttpMethod.Get, "/") { Headers = { Host = "desk.tail1234.ts.net:2584" } };
        Assert.AreEqual(HttpStatusCode.OK, (await fixture.Client.SendAsync(tailnet)).StatusCode, "The name of this computer on the tailnet, as Tailscale Serve forwards it.");
        using var local = new HttpRequestMessage(HttpMethod.Get, "/") { Headers = { Host = "localhost:" + fixture.Port } };
        Assert.AreEqual(HttpStatusCode.OK, (await fixture.Client.SendAsync(local)).StatusCode);
    }

    [TestMethod]
    public async Task Jobs_AreAuthenticatedAndScopedToTheirSession()
    {
        await using var fixture = await Fixture.StartAsync();
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await fixture.Client.GetAsync("/api/sessions/a/jobs")).StatusCode);
        await fixture.SignInAsync();
        fixture.Alta.Answer("session status a", FakeAlta.Status("a", "p1", "personal"));
        var job = new JsonObject { ["type"] = "alta.job", ["id"] = "job-a", ["sessionId"] = "a", ["state"] = "running", ["command"] = "npm test" };
        fixture.Alta.Answer("job list", job);
        var status = (JsonObject)job.DeepClone(); status["type"] = "alta.job.status";
        fixture.Alta.Answer("job status job-a", status)
            .Answer("job output job-a", new JsonObject { ["type"] = "alta.job.output", ["text"] = "Running tests" });
        var page = JsonNode.Parse(await fixture.Client.GetStringAsync("/api/sessions/a/jobs"))!;
        Assert.AreEqual("job-a", (string?)page["jobs"]![0]!["id"]);
        var output = JsonNode.Parse(await fixture.Client.GetStringAsync("/api/sessions/a/jobs/job-a"))!;
        Assert.AreEqual("Running tests", (string?)output["text"]);
        Assert.AreEqual(HttpStatusCode.OK, (await fixture.PostAsync("/api/sessions/a/jobs/job-a/stop", new JsonObject())).StatusCode);
        Assert.AreEqual(1, fixture.Alta.CallsOf("job cancel job-a").Length);
        Assert.AreEqual(HttpStatusCode.NotFound, (await fixture.Client.GetAsync("/api/sessions/unknown/jobs/job-a")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await fixture.PostAsync("/api/sessions/unknown/jobs/job-a/stop", new JsonObject())).StatusCode);
    }

    [TestMethod]
    public async Task TheApi_AnswersWhatAltaSays()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.SignInAsync();
        fixture.Alta.Answer("session status known", FakeAlta.Status("known", "p1", "personal"))
            .Answer("project list", FakeAlta.Project("p1", "Alpha", @"C:\alpha"));

        var known = JsonNode.Parse(await fixture.Client.GetStringAsync("/api/sessions/known"))!;
        Assert.AreEqual(("known", "Alpha"), ((string?)known["id"], (string?)known["project"]));
        using var unknown = await fixture.Client.GetAsync("/api/sessions/unknown");
        Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.AreEqual("Unknown session.", (string?)JsonNode.Parse(await unknown.Content.ReadAsStringAsync())!["error"]);
        Assert.AreEqual(HttpStatusCode.NotFound, (await fixture.Client.GetAsync("/api/sessions/unknown/choices")).StatusCode, "The choices of a session the phone does not see.");
        fixture.Alta.Answer("session choices known", new JsonObject
        {
            ["type"] = "alta.session.choices", ["current"] = new JsonObject { ["providerKey"] = "codex", ["modelId"] = "gpt" }, ["models"] = new JsonArray(), ["prompts"] = new JsonArray(),
        });
        var choices = JsonNode.Parse(await fixture.Client.GetStringAsync("/api/sessions/known/choices"))!;
        Assert.AreEqual(("codex", "gpt"), ((string?)choices["current"]!["provider"], (string?)choices["current"]!["model"]));

        // The list keeps to each project the query repeats.
        fixture.Alta.Answer("session list", FakeAlta.Session("known", "p1", "personal", "2026-10-24T10:00:00Z"));
        var listed = JsonNode.Parse(await fixture.Client.GetStringAsync("/api/sessions?offset=0&limit=6&project=p1&project=p2"))!;
        CollectionAssert.AreEqual(new[] { "p1", "p2" }, listed["projects"]!.AsArray().Select(static project => (string?)project).ToArray());
        Assert.IsTrue(fixture.Alta.CallsOf("session list").Any(static call => call.Contains("p2")), "a list asked for the second project");

        using var form = new StringContent("text=hello", Encoding.UTF8, "application/x-www-form-urlencoded");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await fixture.Client.PostAsync("/api/sessions/known/prompt", form)).StatusCode, "A form of another site is no JSON.");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await fixture.PostAsync("/api/sessions", new JsonObject { ["project"] = "p1" })).StatusCode, "An answer that is only an error.");
    }

    [TestMethod]
    public async Task ThePermissionRequestsOfASession_AreReadAndAnswered_AsTheWindowAnswersThem()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.SignInAsync();
        fixture.Alta.Answer("session status known", FakeAlta.Status("known", "p1", "personal"));
        Assert.AreEqual(HttpStatusCode.NotFound, (await fixture.Client.GetAsync("/api/sessions/unknown/permissions")).StatusCode, "The requests of a session the phone does not see.");
        Assert.IsEmpty((JsonArray)JsonNode.Parse(await fixture.Client.GetStringAsync("/api/sessions/known/permissions"))!["requests"]!, "A session that waits for nothing.");

        var id = Guid.NewGuid().ToString();
        fixture.Permissions.Pending["known"] =
        [
            new PluginPermissionRequest { RequestId = id, Kind = "commandExecution", Command = "git push", WorkingDirectory = "C:/code", Reason = "To publish.", Shortened = true, OutsideSandbox = true },
            new PluginPermissionRequest { RequestId = Guid.NewGuid().ToString(), Kind = "skill", SkillServer = "hf", SkillName = "transformers" },
        ];
        var requests = (JsonArray)JsonNode.Parse(await fixture.Client.GetStringAsync("/api/sessions/known/permissions"))!["requests"]!;
        Assert.AreEqual((id, "commandExecution", "git push", "C:/code", "To publish."),
            ((string?)requests[0]!["id"], (string?)requests[0]!["kind"], (string?)requests[0]!["command"], (string?)requests[0]!["workingDirectory"], (string?)requests[0]!["reason"]));
        Assert.IsTrue((bool)requests[0]!["shortened"]!);
        Assert.IsTrue((bool)requests[0]!["outsideSandbox"]!);
        Assert.IsNull(requests[0]!["skill"]);
        Assert.AreEqual(("hf", "transformers"), ((string?)requests[1]!["skill"]!["server"], (string?)requests[1]!["skill"]!["name"]));

        async Task<JsonNode> ResolveAsync(string session, string request, string decision)
            => JsonNode.Parse(await (await fixture.PostAsync($"/api/sessions/{session}/permissions/resolve", new JsonObject { ["requestId"] = request, ["decision"] = decision })).Content.ReadAsStringAsync())!;

        var allowed = await ResolveAsync("known", id, "allow_once");
        Assert.AreEqual((true, "resolved"), ((bool)allowed["ok"]!, (string?)allowed["status"]));
        Assert.IsNull(allowed["error"]);
        Assert.IsTrue(fixture.Permissions.Answers.TryDequeue(out var sent));
        Assert.AreEqual(("known", id, PluginPermissionDecision.AllowOnce), sent);
        await ResolveAsync("known", id, "deny");
        Assert.IsTrue(fixture.Permissions.Answers.TryDequeue(out sent));
        Assert.AreEqual(PluginPermissionDecision.Deny, sent.Decision);

        // What is not the choice of a request that can be named reaches no session; "always" is not a choice of the phone.
        foreach (var (request, decision) in new[] { (id, "allow_always"), (id, "cancel"), (id, ""), ("not-a-guid", "allow_once"), (Guid.Empty.ToString(), "allow_once") })
        {
            var refused = await ResolveAsync("known", request, decision);
            Assert.AreEqual((false, "invalid"), ((bool)refused["ok"]!, (string?)refused["status"]), $"{request} {decision}");
        }

        Assert.IsTrue(fixture.Permissions.Answers.IsEmpty, "Nothing refused reaches the session.");
        Assert.AreEqual(HttpStatusCode.NotFound, (await fixture.PostAsync("/api/sessions/unknown/permissions/resolve", new JsonObject { ["requestId"] = id, ["decision"] = "allow_once" })).StatusCode);
        Assert.IsTrue(fixture.Permissions.Answers.IsEmpty, "A session the phone does not see.");

        fixture.Permissions.Outcome = "rejected";
        var stale = await ResolveAsync("known", id, "allow_once");
        Assert.AreEqual((false, "rejected", MobilePermissions.Message("rejected")), ((bool)stale["ok"]!, (string?)stale["status"], (string?)stale["error"]));
        fixture.Permissions.Outcome = null;

        // A host that takes long is not said to have the answer: the phone does not send it again.
        fixture.Permissions.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = await ResolveAsync("known", id, "allow_once");
        Assert.AreEqual((false, "uncertain"), ((bool)waiting["ok"]!, (string?)waiting["status"]));
        fixture.Permissions.Hold.SetResult();
        fixture.Permissions.Hold = null;
    }

    [TestMethod]
    public async Task ThePermissionRequests_AreForThePhonesThatSignedIn()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Alta.Answer("session status known", FakeAlta.Status("known", "p1", "personal"));
        var id = Guid.NewGuid().ToString();
        fixture.Permissions.Pending["known"] = [new PluginPermissionRequest { RequestId = id, Kind = "commandExecution", Command = "git push", WorkingDirectory = "C:/code" }];
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await fixture.Client.GetAsync("/api/sessions/known/permissions")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await fixture.PostAsync("/api/sessions/known/permissions/resolve", new JsonObject { ["requestId"] = id, ["decision"] = "allow_once" })).StatusCode);
        Assert.IsTrue(fixture.Permissions.Answers.IsEmpty);
    }

    [TestMethod]
    public async Task TheAskOfASession_IsReadAndAnswered_AsTheWindowAnswersIt()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.SignInAsync();
        fixture.Alta.Answer("session status known", FakeAlta.Status("known", "p1", "personal"));
        Assert.AreEqual(HttpStatusCode.NotFound, (await fixture.Client.GetAsync("/api/sessions/unknown/ask")).StatusCode, "The ask of a session the phone does not see.");
        Assert.IsNull(JsonNode.Parse(await fixture.Client.GetStringAsync("/api/sessions/known/ask"))!["ask"], "A session that waits for nothing.");

        fixture.Asks.Pending["known"] = FakeAsks.Ask("known", "ask-7", "2", null, new PluginAskQuestion
        {
            Title = "Database", Question = "Which database?", Description = "For the cache.",
            Choices = [new PluginAskChoice { Title = "SQLite" }, new PluginAskChoice { Title = "Postgres", Description = "A server." }],
            Freeform = new PluginAskFreeform { Title = "Why", Placeholder = "Optional" },
        });
        var ask = JsonNode.Parse(await fixture.Client.GetStringAsync("/api/sessions/known/ask"))!["ask"]!;
        Assert.AreEqual(("ask-7", "2", "pending", false), ((string?)ask["id"], (string?)ask["revision"], (string?)ask["state"], (bool)ask["immediate"]!));
        var question = ask["questions"]![0]!;
        Assert.AreEqual(("Database", "Which database?", "For the cache."), ((string?)question["title"], (string?)question["question"], (string?)question["description"]));
        Assert.AreEqual(("Postgres", "A server."), ((string?)question["choices"]![1]!["title"], (string?)question["choices"]![1]!["description"]));
        Assert.AreEqual(("Why", "Optional"), ((string?)question["freeform"]!["title"], (string?)question["freeform"]!["placeholder"]));
        Assert.IsNull(ask["review"]);

        var action = Guid.NewGuid();
        JsonObject Answer(int choice) => new()
        {
            ["actionId"] = action.ToString(), ["askId"] = "ask-7", ["revision"] = "2",
            ["answers"] = new JsonArray(new JsonObject { ["question"] = 0, ["choices"] = new JsonArray(choice), ["text"] = "It is small." }),
        };
        var answered = JsonNode.Parse(await (await fixture.PostAsync("/api/sessions/known/ask/answer", Answer(1))).Content.ReadAsStringAsync())!;
        Assert.AreEqual((true, "admitted"), ((bool)answered["ok"]!, (string?)answered["status"]));
        Assert.IsTrue(fixture.Asks.Answers.TryDequeue(out var sent));
        Assert.AreEqual(new PluginAskReference(action, "known", "ask-7", "2"), sent.Ask);
        Assert.AreEqual((0, 1, "It is small."), (sent.Answers[0].QuestionIndex, sent.Answers[0].SelectedChoiceIndexes.Single(), sent.Answers[0].FreeformText));
        Assert.IsNull(sent.Review);

        Assert.AreEqual(HttpStatusCode.BadRequest, (await fixture.PostAsync("/api/sessions/known/ask/answer", Answer(20))).StatusCode, "A choice beyond the limit of an ask.");
        var badReview = Answer(0);
        badReview["review"] = new JsonObject { ["verdict"] = "merged" };
        Assert.AreEqual(HttpStatusCode.BadRequest, (await fixture.PostAsync("/api/sessions/known/ask/answer", badReview)).StatusCode, "A verdict a review does not have.");
        var noAction = Answer(0);
        noAction.Remove("actionId");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await fixture.PostAsync("/api/sessions/known/ask/answer", noAction)).StatusCode, "An answer needs its id, which makes it sent once.");
        Assert.AreEqual(HttpStatusCode.NotFound, (await fixture.PostAsync("/api/sessions/unknown/ask/answer", Answer(0))).StatusCode);
        Assert.IsTrue(fixture.Asks.Answers.IsEmpty, "Nothing refused reaches the session.");

        var review = Answer(0);
        review["review"] = new JsonObject { ["verdict"] = "changes_requested", ["overall"] = "Rename the cache." };
        fixture.Asks.Outcome = "rejected";
        var stale = JsonNode.Parse(await (await fixture.PostAsync("/api/sessions/known/ask/answer", review)).Content.ReadAsStringAsync())!;
        Assert.AreEqual((false, "rejected", MobileAsks.Message("rejected")), ((bool)stale["ok"]!, (string?)stale["status"], (string?)stale["error"]));
        Assert.IsTrue(fixture.Asks.Answers.TryDequeue(out sent));
        Assert.AreEqual(new PluginAskReviewAnswer("changes_requested", "Rename the cache."), sent.Review);

        fixture.Asks.Outcome = null;
        var cancel = new JsonObject { ["actionId"] = Guid.NewGuid().ToString(), ["askId"] = "ask-7", ["revision"] = "2" };
        var cancelled = JsonNode.Parse(await (await fixture.PostAsync("/api/sessions/known/ask/cancel", cancel)).Content.ReadAsStringAsync())!;
        Assert.AreEqual((true, "cancelled"), ((bool)cancelled["ok"]!, (string?)cancelled["status"]));
        Assert.IsTrue(fixture.Asks.Cancels.TryDequeue(out var cancelledAsk));
        Assert.AreEqual(("known", "ask-7", "2"), (cancelledAsk.SessionId, cancelledAsk.AskId, cancelledAsk.Revision));
    }

    [TestMethod]
    public async Task AnAnswerTheSessionTakesLong_IsSaidPending_AndGoesOn()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.SignInAsync();
        fixture.Alta.Answer("session status known", FakeAlta.Status("known", "p1", "personal"));
        fixture.Asks.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var body = new JsonObject { ["actionId"] = Guid.NewGuid().ToString(), ["askId"] = "ask-1", ["revision"] = "0", ["answers"] = new JsonArray() };

        var answered = JsonNode.Parse(await (await fixture.PostAsync("/api/sessions/known/ask/answer", body)).Content.ReadAsStringAsync())!;
        Assert.AreEqual((true, true, "submitting"), ((bool)answered["ok"]!, (bool)answered["pending"]!, (string?)answered["status"]));
        fixture.Asks.Hold.SetResult();

        // The host says the answer still goes (a retry while the first is on its way): it is pending too, never an error.
        fixture.Asks.Hold = null;
        fixture.Asks.Outcome = "submitting";
        answered = JsonNode.Parse(await (await fixture.PostAsync("/api/sessions/known/ask/answer", body)).Content.ReadAsStringAsync())!;
        Assert.AreEqual((true, true, "submitting"), ((bool)answered["ok"]!, (bool)answered["pending"]!, (string?)answered["status"]));
        Assert.IsNull(answered["error"]);
    }

    [TestMethod]
    public async Task AnswersLongerThanTheHostTakes_AreRefused_WithWhatTheLimitIs()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.SignInAsync();
        fixture.Alta.Answer("session status known", FakeAlta.Status("known", "p1", "personal"));
        JsonObject Answer(params string[] texts) => new()
        {
            ["actionId"] = Guid.NewGuid().ToString(), ["askId"] = "ask-1", ["revision"] = "0",
            ["answers"] = new JsonArray([.. texts.Select((text, index) => (JsonNode)new JsonObject { ["question"] = index, ["text"] = text })]),
        };
        var half = new string('a', PluginAskLimits.AnswerText / 2);

        Assert.AreEqual(HttpStatusCode.OK, (await fixture.PostAsync("/api/sessions/known/ask/answer", Answer(half, half))).StatusCode, "The limit is that of all the answers together.");
        using var tooLong = await fixture.PostAsync("/api/sessions/known/ask/answer", Answer(half, half + "a"));
        Assert.AreEqual(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.AreEqual(MobileAsks.AnswerTooLong, (string?)JsonNode.Parse(await tooLong.Content.ReadAsStringAsync())!["error"]);
        StringAssert.Contains(MobileAsks.AnswerTooLong, "8192");

        var review = Answer();
        review["review"] = new JsonObject { ["verdict"] = "commented", ["overall"] = new string('b', PluginAskLimits.OverallComment) };
        Assert.AreEqual(HttpStatusCode.OK, (await fixture.PostAsync("/api/sessions/known/ask/answer", review)).StatusCode, "An overall comment has the limit of the review store.");
        review["actionId"] = Guid.NewGuid().ToString();
        review["review"]!["overall"] = new string('b', PluginAskLimits.OverallComment + 1);
        using var overall = await fixture.PostAsync("/api/sessions/known/ask/answer", review);
        Assert.AreEqual(MobileAsks.OverallTooLong, (string?)JsonNode.Parse(await overall.Content.ReadAsStringAsync())!["error"]);
    }

    [TestMethod]
    public async Task AQuestionOfAScript_IsSaidImmediate()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.SignInAsync();
        fixture.Alta.Answer("session status known", FakeAlta.Status("known", "p1", "personal"));
        fixture.Asks.Pending["known"] = FakeAsks.Ask("known", questions: new PluginAskQuestion { Title = "Go", Question = "Go on?", Freeform = new PluginAskFreeform() }) with { Immediate = true };

        var ask = JsonNode.Parse(await fixture.Client.GetStringAsync("/api/sessions/known/ask"))!["ask"]!;
        Assert.IsTrue((bool)ask["immediate"]!, "the phone shows it while the turn runs");
    }

    [TestMethod]
    public async Task ADiagram_OpensAtALinkThatNeedsNoSignIn_InASandbox()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.SignInAsync();

        using var posted = await fixture.PostAsync("/api/diagrams", new JsonObject { ["svg"] = "<svg><text>Build</text></svg>", ["background"] = "#101010", ["dark"] = true });
        var url = (string?)JsonNode.Parse(await posted.Content.ReadAsStringAsync())!["url"];
        Assert.IsNotNull(url);
        StringAssert.StartsWith(url, "/d/");

        using var anonymous = new HttpClient { BaseAddress = fixture.Client.BaseAddress };
        using var page = await anonymous.GetAsync(url);
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
        StringAssert.Contains(await page.Content.ReadAsStringAsync(), "<svg><text>Build</text></svg>");
        StringAssert.Contains(page.Headers.GetValues("Content-Security-Policy").Single(), "sandbox");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await fixture.PostAsync("/api/diagrams", new JsonObject { ["svg"] = "<script>alert(1)</script>" })).StatusCode);
    }

    [TestMethod]
    public async Task APromptWithFiles_MayBeLargerThanOtherRequests_AndOneIsHandledAtATime()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.SignInAsync();
        var first = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = 0;
        fixture.Alta.Answer("session status known", FakeAlta.Status("known", "p1", "personal"))
            .Answer("session send", (_, stdin) => Interlocked.Increment(ref sends) == 1 && stdin == "first" ? first.Task : Task.FromResult(FakeAlta.Transcript(0)));
        JsonObject Prompt(string text, int bytes) => new()
        {
            ["text"] = text, ["attachments"] = new JsonArray(new JsonObject { ["title"] = "big.bin", ["mediaType"] = "application/octet-stream", ["base64"] = new string('A', bytes / 3 * 4) }),
        };

        // Two megabytes: a prompt and its files pass, the other routes keep their small limit.
        var large = fixture.PostAsync("/api/sessions/known/prompt", Prompt("first", 2 * 1024 * 1024));
        await WaitUntilAsync(() => fixture.Alta.CallsOf("session send").Length == 1);
        var waiting = fixture.PostAsync("/api/sessions/known/prompt", Prompt("second", 2 * 1024 * 1024));
        using var text = await fixture.PostAsync("/api/sessions/known/prompt", new JsonObject { ["text"] = "words" });
        Assert.AreEqual(HttpStatusCode.OK, text.StatusCode, "A prompt of text does not wait for the files of another.");
        Assert.AreEqual(2, fixture.Alta.CallsOf("session send").Length, "The second prompt with files waits for the first to be handed over.");
        Assert.IsFalse(waiting.IsCompleted);

        first.SetResult(FakeAlta.Transcript(0));
        using (var answered = await large) Assert.AreEqual(HttpStatusCode.OK, answered.StatusCode);
        using (var answered = await waiting) Assert.AreEqual(HttpStatusCode.OK, answered.StatusCode);
        Assert.AreEqual(3, fixture.Alta.CallsOf("session send").Length);
        Assert.AreEqual("second", fixture.Alta.Calls.Last(static call => call.Args[1] == "send").Stdin);

        var login = new JsonObject { ["token"] = fixture.Token, ["padding"] = new string('x', 2 * 1024 * 1024) };
        using var refused = await fixture.PostAsync("/api/login", login);
        Assert.AreEqual(HttpStatusCode.Unauthorized, refused.StatusCode, "Any other route refuses a body over 1 MB.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; !condition(); attempt++)
        {
            if (attempt > 1500) throw new TimeoutException("The condition never held.");
            await Task.Delay(20);
        }
    }

    [TestMethod]
    public async Task Images_AreServedByTheirIdFromTheRecordsOfTheSession_NeverByAPath()
    {
        await using var fixture = await Fixture.StartAsync();
        byte[] png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3, 4];
        using var folder = new TempFolder();
        await File.WriteAllBytesAsync(Path.Combine(folder.Path, "shot.png"), png);
        await File.WriteAllBytesAsync(Path.Combine(folder.Path, "fake.png"), "not a picture"u8.ToArray());
        var status = FakeAlta.Status("a", "p1", "personal");
        status["workingDirectory"] = folder.Path;
        fixture.Alta
            .Answer("session status a", status)
            .Answer("session status b", FakeAlta.Status("b", "p1", "personal"))
            .Answer("session image a shot-1.png", new JsonObject { ["type"] = "alta.session.image", ["mediaType"] = "image/png", ["base64"] = Convert.ToBase64String(png) })
            .Answer("session image a notes.txt", new JsonObject { ["type"] = "alta.session.image", ["mediaType"] = "image/png", ["base64"] = Convert.ToBase64String("text"u8.ToArray()) })
            .Answer("session image b", (_, _) => FakeAlta.Transcript(3, new JsonObject { ["type"] = "alta.error", ["code"] = "session.imageNotFound", ["message"] = "No such image." }))
            .Answer("session events a", new JsonObject
            {
                ["type"] = "alta.session.event", ["sequenceNumber"] = 7,
                ["content"] = new JsonArray(new JsonObject { ["text"] = "See `![code](x.png)`\n\n![The shot](shot.png) and ![fake](fake.png) and ![web](https://example.com/a.png)" }),
            });

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await fixture.Client.GetAsync("/api/sessions/a/images/shot-1.png")).StatusCode, "An image needs a phone that signed in.");
        await fixture.SignInAsync();

        using var saved = await fixture.Client.GetAsync("/api/sessions/a/images/shot-1.png");
        Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);
        Assert.AreEqual("image/png", saved.Content.Headers.ContentType?.MediaType);
        CollectionAssert.AreEqual(png, await saved.Content.ReadAsByteArrayAsync());
        Assert.IsTrue(saved.Headers.CacheControl is { Private: true, MaxAge.TotalSeconds: 86400 }, "The phone keeps an image, which never changes; no proxy does.");
        Assert.AreEqual(HttpStatusCode.NotFound, (await fixture.Client.GetAsync("/api/sessions/a/images/notes.txt")).StatusCode, "What is no image by its content is not served.");
        Assert.AreEqual(HttpStatusCode.NotFound, (await fixture.Client.GetAsync("/api/sessions/b/images/shot-1.png")).StatusCode, "The image of another session is not one of this one.");
        Assert.AreEqual(HttpStatusCode.NotFound, (await fixture.Client.GetAsync("/api/sessions/c/images/shot-1.png")).StatusCode, "A session the phone does not see.");
        foreach (var path in (string[])["/api/sessions/a/images/..%2Fsecret.png", "/api/sessions/a/images/.hidden.png", "/api/sessions/a/images/C%3A%5Csecret.png", "/api/sessions/a/images/..%5Csecret.png"])
            Assert.AreEqual(HttpStatusCode.NotFound, (await fixture.Client.GetAsync(path)).StatusCode, path);
        Assert.IsFalse(fixture.Alta.CallsOf("session image").Any(static call => call[3].Contains('.') && (call[3].StartsWith('.') || call[3].Contains('/') || call[3].Contains('\\') || call[3].Contains(':'))),
            "A path is never asked of alta.");

        // A picture of the disk that an answer shows: by the number of the answer and its place among its pictures (code is no picture).
        using var linked = await fixture.Client.GetAsync("/api/sessions/a/linked-images/7/0");
        Assert.AreEqual(HttpStatusCode.OK, linked.StatusCode);
        CollectionAssert.AreEqual(png, await linked.Content.ReadAsByteArrayAsync());
        Assert.AreEqual(HttpStatusCode.NotFound, (await fixture.Client.GetAsync("/api/sessions/a/linked-images/7/1")).StatusCode, "A file named as a picture that is none.");
        Assert.AreEqual(HttpStatusCode.NotFound, (await fixture.Client.GetAsync("/api/sessions/a/linked-images/7/2")).StatusCode, "A picture of the web is not one of the disk.");
        Assert.AreEqual(HttpStatusCode.NotFound, (await fixture.Client.GetAsync("/api/sessions/a/linked-images/8/0")).StatusCode, "Another record than the answer asked for.");
    }

    private sealed class TempFolder : IDisposable
    {
        internal string Path { get; } = Directory.CreateTempSubdirectory("mobile-images-").FullName;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { } // A file still held by the system: the temp folder keeps it.
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly MobileServer _server;

        private Fixture(MobileServer server, HttpClient client, FakeAlta alta, int port)
        {
            _server = server;
            Client = client;
            Alta = alta;
            Port = port;
        }

        internal HttpClient Client { get; }

        internal FakeAlta Alta { get; }

        internal required FakeAsks Asks { get; init; }

        internal required FakePermissions Permissions { get; init; }

        internal int Port { get; }

        internal required MobileAccess Access { get; init; }

        /// <summary>The pairing token of the QR code.</summary>
        internal string Token => Access.PairingToken!;

        internal static async Task<Fixture> StartAsync()
        {
            var port = FreePort.Next();
            var alta = new FakeAlta();
            var files = new FakeFiles()
                .Add("mobile.html", "<!doctype html><p>Phone</p>", "text/html; charset=utf-8")
                .Add("assets/mobile-abc.js", "export {};", "text/javascript; charset=utf-8", immutable: true)
                .Add("mobile/sw.js", "self.addEventListener('fetch', () => {});", "text/javascript; charset=utf-8");
            var live = new MobileLive();
            var sessions = new MobileSessions(alta, new FakeSpaces(), live);
            var asks = new FakeAsks();
            var permissions = new FakePermissions();
            var access = await MobileAccess.LoadAsync(new MobileSettingsTests.MemoryState(), CancellationToken.None);
            await access.PairAsync(CancellationToken.None);
            var server = new MobileServer(new MobileServerOptions
            {
                Port = port, Access = access, Hosts = () => ["desk.tail1234.ts.net:2584"], Files = files, Sessions = sessions,
                // An answer the session takes long to take is said pending at once: no test waits the seconds of a phone.
                Api = new MobileApi(alta, sessions), Asks = new MobileAsks(asks, sessions, TimeSpan.FromMilliseconds(50)),
                Permissions = new MobilePermissions(permissions, sessions, TimeSpan.FromMilliseconds(50)), Live = live, Logger = XenoAtom.Logging.LogManager.GetLogger("mobile-tests"),
            });
            await server.StartAsync(default);
            var client = new HttpClient(new HttpClientHandler { UseCookies = true }) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            return new Fixture(server, client, alta, port) { Asks = asks, Permissions = permissions, Access = access };
        }

        internal Task<HttpResponseMessage> PostAsync(string path, JsonObject body)
            => Client.PostAsync(path, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));

        internal async Task SignInAsync()
        {
            using var login = await PostAsync("/api/login", new JsonObject { ["token"] = Token });
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _server.DisposeAsync();
        }
    }
}
