using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeAlta.Plugin.Mobile.Tests;

[TestClass]
public sealed class MobileServerTests
{
    private const string Token = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFG";

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
    public async Task TheApi_NeedsTheTokenOfNow()
    {
        await using var fixture = await Fixture.StartAsync();
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await fixture.Client.GetAsync("/api/sessions")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await fixture.PostAsync("/api/login", new JsonObject { ["token"] = "wrong" })).StatusCode);

        using var login = await fixture.PostAsync("/api/login", new JsonObject { ["token"] = Token });
        Assert.AreEqual(HttpStatusCode.NoContent, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie").Single();
        StringAssert.Contains(cookie, MobileServer.CookieName + "=");
        StringAssert.Contains(cookie, "httponly");
        StringAssert.Contains(cookie, "samesite=strict");
        Assert.AreEqual(HttpStatusCode.OK, (await fixture.Client.GetAsync("/api/sessions")).StatusCode, "The cookie signs the phone in.");

        // A reset of the token disconnects every phone.
        fixture.Token = "ZYXWVUTSRQPONMLKJIHGFEDCBAzyxwvutsrqponmlkji";
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await fixture.Client.GetAsync("/api/sessions")).StatusCode);

        using var bearer = new HttpRequestMessage(HttpMethod.Get, "/api/sessions");
        bearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token);
        Assert.AreEqual(HttpStatusCode.OK, (await fixture.Client.SendAsync(bearer)).StatusCode);
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

        using var form = new StringContent("text=hello", Encoding.UTF8, "application/x-www-form-urlencoded");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await fixture.Client.PostAsync("/api/sessions/known/prompt", form)).StatusCode, "A form of another site is no JSON.");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await fixture.PostAsync("/api/sessions", new JsonObject { ["project"] = "p1" })).StatusCode, "An answer that is only an error.");
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

        internal int Port { get; }

        internal string Token { get; set; } = MobileServerTests.Token;

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
            Fixture? fixture = null;
            var server = new MobileServer(new MobileServerOptions
            {
                Port = port, Token = () => fixture!.Token, Hosts = () => ["desk.tail1234.ts.net:2584"], Files = files, Sessions = sessions,
                Api = new MobileApi(alta, sessions), Live = live, Logger = XenoAtom.Logging.LogManager.GetLogger("mobile-tests"),
            });
            await server.StartAsync(default);
            var client = new HttpClient(new HttpClientHandler { UseCookies = true }) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            fixture = new Fixture(server, client, alta, port);
            return fixture;
        }

        internal Task<HttpResponseMessage> PostAsync(string path, JsonObject body)
            => Client.PostAsync(path, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));

        internal async Task SignInAsync()
        {
            using var login = await PostAsync("/api/login", new JsonObject { ["token"] = Token });
            Assert.AreEqual(HttpStatusCode.NoContent, login.StatusCode);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _server.DisposeAsync();
        }
    }
}
