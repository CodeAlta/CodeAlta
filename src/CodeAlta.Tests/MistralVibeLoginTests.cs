using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent.Mistral;

namespace CodeAlta.Tests;

// The console of Mistral is played by a handler: no network, and a temporary state root.
[TestClass]
public sealed class MistralVibeLoginTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Login_OpensThePage_WaitsForTheUser_AndStoresTheKeyOfTheExchange()
    {
        using var temp = TestTempDirectory.Create();
        var console = new FakeConsole(["pending", "pending", "completed"]);
        using var http = new HttpClient(console);
        var delays = new List<TimeSpan>();
        var manager = new MistralVibeLoginManager(http) { Delay = (delay, _) => { delays.Add(delay); return Task.CompletedTask; } };
        var options = new MistralVibeLoginOptions("vibe", temp.Path);
        Uri? opened = null;

        var result = await manager.LoginWithBrowserAsync(options, (authorization, _) => { opened = authorization.SignInUri; return ValueTask.CompletedTask; }, TestContext.CancellationToken);

        Assert.AreEqual("https://console.mistral.ai/vibe/sign-in/process-1", opened?.AbsoluteUri);
        Assert.AreEqual(2, delays.Count, "the console is asked again while the sign-in is pending");
        // PKCE: the verifier of the exchange is the one of the challenge of the start.
        var verifier = console.Exchange!.RootElement.GetProperty("code_verifier").GetString()!;
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.AreEqual(challenge, console.Start!.RootElement.GetProperty("code_challenge").GetString());
        Assert.AreEqual("S256", console.Start.RootElement.GetProperty("code_challenge_method").GetString());
        Assert.AreEqual("exchange-1", console.Exchange.RootElement.GetProperty("exchange_token").GetString());

        Assert.AreEqual("vibe-api-key", await MistralVibeLoginManager.GetApiKeyAsync(options, TestContext.CancellationToken));
        Assert.AreEqual(result, await MistralVibeLoginManager.GetCredentialStatusAsync(options, TestContext.CancellationToken));
        var file = Path.Combine(temp.Path, "auth", "mistral-vibe", "vibe.json");
        Assert.IsTrue(File.Exists(file));
        if (OperatingSystem.IsWindows())
        {
            Assert.IsTrue(File.GetAttributes(file).HasFlag(FileAttributes.Hidden));
        }

        // Signed in again: the new key replaces the hidden file.
        await manager.LoginWithBrowserAsync(options, static (_, _) => ValueTask.CompletedTask, TestContext.CancellationToken);
        Assert.AreEqual("vibe-api-key", await MistralVibeLoginManager.GetApiKeyAsync(options, TestContext.CancellationToken));

        await MistralVibeLoginManager.DeleteCredentialAsync(options, TestContext.CancellationToken);
        Assert.IsNull(await MistralVibeLoginManager.GetCredentialStatusAsync(options, TestContext.CancellationToken));
        var signedOut = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => MistralVibeLoginManager.GetApiKeyAsync(options, TestContext.CancellationToken).AsTask());
        StringAssert.Contains(signedOut.Message, "not signed in");
    }

    [TestMethod]
    [DataRow("https://evil.example/vibe/sign-in/process-1", "https://console.mistral.ai/api/vibe/sign-in/poll/1")]
    [DataRow("https://console.mistral.ai/vibe/sign-in/process-1", "https://console.mistral.ai/apievil/poll/1")]
    [DataRow("https://console.mistral.ai/vibe/sign-in/process-1", "http://console.mistral.ai/api/vibe/sign-in/poll/1")]
    public async Task Login_RefusesAPageOrAPollAddressOutsideTheConsole(string signInUrl, string pollUrl)
    {
        using var temp = TestTempDirectory.Create();
        var console = new FakeConsole(["completed"]) { SignInUrl = signInUrl, PollUrl = pollUrl };
        using var http = new HttpClient(console);
        var opened = false;

        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => new MistralVibeLoginManager(http).LoginWithBrowserAsync(
            new MistralVibeLoginOptions("vibe", temp.Path), (_, _) => { opened = true; return ValueTask.CompletedTask; }, TestContext.CancellationToken).AsTask());

        StringAssert.Contains(failure.Message, "not a sign-in");
        Assert.IsFalse(opened, "no page is opened");
    }

    [TestMethod]
    [DataRow("denied", "denied")]
    [DataRow("workspace", "workspace")]
    public async Task Login_SaysWhyTheConsoleRefused(string outcome, string expected)
    {
        using var temp = TestTempDirectory.Create();
        var console = new FakeConsole([outcome == "denied" ? "denied" : "completed"]) { RefuseWorkspace = outcome == "workspace" };
        using var http = new HttpClient(console);

        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => new MistralVibeLoginManager(http).LoginWithBrowserAsync(
            new MistralVibeLoginOptions("vibe", temp.Path), static (_, _) => ValueTask.CompletedTask, TestContext.CancellationToken).AsTask());

        StringAssert.Contains(failure.Message, expected);
        Assert.IsNull(await MistralVibeLoginManager.GetCredentialStatusAsync(new MistralVibeLoginOptions("vibe", temp.Path), TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Login_ExpiresWhenTheUserDoesNotFinish()
    {
        using var temp = TestTempDirectory.Create();
        var console = new FakeConsole(["pending", "gone"]);
        using var http = new HttpClient(console);
        var manager = new MistralVibeLoginManager(http) { Delay = static (_, _) => Task.CompletedTask };

        await Assert.ThrowsExactlyAsync<TimeoutException>(() => manager.LoginWithBrowserAsync(
            new MistralVibeLoginOptions("vibe", temp.Path), static (_, _) => ValueTask.CompletedTask, TestContext.CancellationToken).AsTask());
    }

    private sealed class FakeConsole(string[] polls) : HttpMessageHandler
    {
        private int _poll;

        public string SignInUrl { get; init; } = "https://console.mistral.ai/vibe/sign-in/process-1";

        public string PollUrl { get; init; } = "https://console.mistral.ai/api/vibe/sign-in/poll/token-1";

        public bool RefuseWorkspace { get; init; }

        public JsonDocument? Start { get; private set; }

        public JsonDocument? Exchange { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsoluteUri;
            if (request.Method == HttpMethod.Post && path == "https://console.mistral.ai/api/vibe/sign-in")
            {
                Start = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                return Json(HttpStatusCode.OK, new Dictionary<string, string>
                {
                    ["process_id"] = "process-1",
                    ["sign_in_url"] = SignInUrl,
                    ["poll_url"] = PollUrl,
                    ["expires_at"] = DateTimeOffset.UtcNow.AddMinutes(10).ToString("O"),
                });
            }

            if (request.Method == HttpMethod.Get && path == PollUrl)
            {
                var status = polls[Math.Min(_poll++, polls.Length - 1)];
                return status == "gone"
                    ? new HttpResponseMessage(HttpStatusCode.Gone)
                    : Json(HttpStatusCode.OK, status == "completed"
                        ? new Dictionary<string, string> { ["status"] = status, ["exchange_token"] = "exchange-1" }
                        : new Dictionary<string, string> { ["status"] = status });
            }

            if (request.Method == HttpMethod.Post && path == "https://console.mistral.ai/api/vibe/sign-in/process-1/exchange")
            {
                Exchange = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                return RefuseWorkspace
                    ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"error_code":9004,"detail":"no"}""", Encoding.UTF8, "application/json") }
                    : Json(HttpStatusCode.OK, new Dictionary<string, string> { ["api_key"] = "vibe-api-key" });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, Dictionary<string, string> body)
            => new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}
