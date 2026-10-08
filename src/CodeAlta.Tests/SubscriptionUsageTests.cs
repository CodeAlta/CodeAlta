using System.Net;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Claude;
using CodeAlta.Agent.Copilot;
using CodeAlta.Agent.OpenAI.Codex;

namespace CodeAlta.Tests;

/// <summary>What each subscription provider reports as its usage, read from literal answers: no network, no CLI.</summary>
[TestClass]
public sealed class SubscriptionUsageTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Copilot_ReadsTheQuotasOfAPlanBilledInCredits()
    {
        var usage = CopilotAccountUsage.Parse("""
            {
              "login": "someone", "copilot_plan": "individual_max", "token_based_billing": true,
              "quota_reset_date": "2026-11-01", "quota_reset_date_utc": "2026-11-01T00:00:00.000Z",
              "quota_snapshots": {
                "chat": { "entitlement": 0, "remaining": 0, "percent_remaining": 100.0, "unlimited": true },
                "completions": { "entitlement": 0, "remaining": 0, "percent_remaining": 100.0, "unlimited": true },
                "premium_interactions": { "entitlement": 20000, "remaining": 15321, "quota_remaining": 15321.2, "percent_remaining": 76.6,
                  "credits_used": 4678, "unlimited": false, "quota_reset_at": 0, "token_based_billing": true },
                "something_new": null
              }
            }
            """, Now);

        Assert.AreEqual("individual_max", usage.Plan);
        Assert.AreEqual(Now, usage.ObservedAt);
        // What the plan meters comes first, whatever the order of the answer.
        CollectionAssert.AreEqual(new[] { "premium_interactions", "chat", "completions" }, usage.Limits.Select(static limit => limit.Id).ToArray());
        var credits = usage.Limits[0];
        Assert.AreEqual(23.4, credits.UsedPercent!.Value, 0.001);
        Assert.AreEqual(4678, credits.Used);
        Assert.AreEqual(20000, credits.Total);
        Assert.AreEqual("credits", credits.Unit);
        Assert.AreEqual(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), credits.ResetsAt);
        Assert.AreEqual(31 * 24 * 60, credits.WindowMinutes, "The period of a quota is the month that ends at its reset.");
        Assert.IsFalse(credits.Unlimited);
        Assert.IsTrue(usage.Limits[1].Unlimited);
        Assert.IsNull(usage.Limits[1].UsedPercent);
    }

    [TestMethod]
    public void Copilot_ReadsRequestsAFreePlanAndWhatIsWrittenAsText()
    {
        var requests = CopilotAccountUsage.Parse("""
            { "copilot_plan": "individual_pro", "quota_reset_date": "2026-11-01",
              "quota_snapshots": { "premium_interactions": { "entitlement": "300", "remaining": 120, "percent_remaining": 40, "unlimited": false, "quota_reset_at": 1793491200 },
                                   "chat": { "entitlement": -1, "unlimited": false }, "completions": { "entitlement": 0, "unlimited": false } } }
            """, Now).Limits;
        Assert.HasCount(2, requests, "A plan that includes none of something has nothing to show for it.");
        Assert.AreEqual(60, requests[0].UsedPercent);
        Assert.AreEqual(180, requests[0].Used);
        Assert.AreEqual(300, requests[0].Total);
        Assert.AreEqual("requests", requests[0].Unit);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1793491200), requests[0].ResetsAt, "The reset of the quota itself comes before the one of the plan.");
        Assert.IsTrue(requests[1].Unlimited, "A negative entitlement is no limit.");

        var free = CopilotAccountUsage.Parse("""
            { "copilot_plan": "free", "limited_user_reset_date": "2026-11-05",
              "monthly_quotas": { "chat": 50, "completions": 2000 }, "limited_user_quotas": { "chat": 20, "completions": 2000 } }
            """, Now).Limits;
        CollectionAssert.AreEqual(new[] { "chat", "completions" }, free.Select(static limit => limit.Id).ToArray());
        Assert.AreEqual(60, free[0].UsedPercent);
        Assert.AreEqual(30, free[0].Used);
        Assert.AreEqual(0, free[1].UsedPercent);
        Assert.AreEqual(new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero), free[0].ResetsAt);

        Assert.IsEmpty(CopilotAccountUsage.Parse("{ \"copilot_plan\": \"business\" }", Now).Limits);
        Assert.ThrowsExactly<JsonException>(() => CopilotAccountUsage.Parse("[]", Now));
        Assert.AreEqual("https://api.github.com/copilot_internal/user", CopilotAccountUsage.CreateUri(null).ToString());
        Assert.AreEqual("https://api.github.com/copilot_internal/user", CopilotAccountUsage.CreateUri(" https://github.com/ ").ToString());
        Assert.AreEqual("https://api.example.ghe.com/copilot_internal/user", CopilotAccountUsage.CreateUri("example.ghe.com").ToString());
    }

    [TestMethod]
    public async Task Copilot_AsksGitHubWithTheStoredToken_AndReportsAnAccountWithoutOne()
    {
        using var root = new TempDirectory();
        var handler = new Handler(HttpStatusCode.OK, """{ "copilot_plan": "individual", "quota_snapshots": { "premium_interactions": { "entitlement": 300, "remaining": 150, "percent_remaining": 50 } } }""");
        using var http = new HttpClient(handler);

        Assert.AreEqual(AgentSubscriptionUsageReading.SignedOut, (await CopilotAccountUsage.ReadAsync(http, root.Path, "copilot", null, null, default)).Status);
        Assert.IsEmpty(handler.Requests, "Without a token, GitHub is not asked.");

        Directory.CreateDirectory(Path.Combine(root.Path, "auth", "copilot"));
        File.WriteAllText(Path.Combine(root.Path, "auth", "copilot", "copilot.json"), """{ "github_token": "gho_test", "enterprise_domain": "example.ghe.com" }""");
        var reading = await CopilotAccountUsage.ReadAsync(http, root.Path, "copilot", null, null, default);
        Assert.AreEqual(AgentSubscriptionUsageReading.Ok, reading.Status);
        Assert.AreEqual(50, reading.Usage!.Limits.Single().UsedPercent);
        var request = handler.Requests.Single();
        Assert.AreEqual("https://api.example.ghe.com/copilot_internal/user", request.Uri);
        Assert.AreEqual("Bearer gho_test", request.Authorization);

        handler.Status = HttpStatusCode.Unauthorized;
        Assert.AreEqual(AgentSubscriptionUsageReading.SignedOut, (await CopilotAccountUsage.ReadAsync(http, root.Path, "copilot", null, null, default)).Status);
        handler.Status = HttpStatusCode.NotFound;
        Assert.AreEqual(AgentSubscriptionUsageReading.Unavailable, (await CopilotAccountUsage.ReadAsync(http, root.Path, "copilot", null, null, default)).Status);
        handler.Status = HttpStatusCode.InternalServerError;
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => CopilotAccountUsage.ReadAsync(http, root.Path, "copilot", null, null, default));
    }

    [TestMethod]
    public void Codex_ReadsTheWindowsTheOtherLimitsAndTheCreditsOfAPlan()
    {
        using var document = JsonDocument.Parse("""
            { "rateLimits": { "limitId": "codex", "limitName": null, "planType": "pro",
                "primary": { "usedPercent": 42, "windowDurationMins": 300, "resetsAt": 1791450000 },
                "secondary": { "usedPercent": 5, "windowDurationMins": 10080, "resetsAt": 1791949466 },
                "credits": { "hasCredits": true, "unlimited": false, "balance": "62500.4" } },
              "rateLimitsByLimitId": {
                "codex_other": { "limitId": "codex_other", "limitName": "GPT-6 Luna", "primary": { "usedPercent": 88, "windowDurationMins": 30, "resetsAt": 1791450000 }, "secondary": null },
                "codex": { "limitId": "codex", "planType": "pro",
                  "primary": { "usedPercent": 42, "windowDurationMins": 300, "resetsAt": 1791450000 },
                  "secondary": { "usedPercent": 5, "windowDurationMins": 10080, "resetsAt": 1791949466 } } } }
            """);

        var usage = CodexAccountUsage.Parse(document.RootElement, Now);

        Assert.AreEqual("pro", usage.Plan);
        // The limits of the plan first, then the ones of a model, then the credits.
        CollectionAssert.AreEqual(new[] { "codex:primary", "codex:secondary", "codex_other:primary", "credits" }, usage.Limits.Select(static limit => limit.Id).ToArray());
        Assert.AreEqual(42, usage.Limits[0].UsedPercent);
        Assert.AreEqual(300, usage.Limits[0].WindowMinutes);
        Assert.IsNull(usage.Limits[0].Name);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1791450000), usage.Limits[0].ResetsAt);
        Assert.AreEqual(7 * 24 * 60, usage.Limits[1].WindowMinutes);
        Assert.AreEqual("GPT-6 Luna", usage.Limits[2].Name);
        Assert.AreEqual(62500, usage.Limits[3].Remaining);
        Assert.AreEqual("credits", usage.Limits[3].Unit);

        using var single = JsonDocument.Parse("""{ "rateLimits": { "planType": "plus", "primary": { "usedPercent": 1, "windowDurationMins": 10080 }, "credits": { "hasCredits": true, "unlimited": true } } }""");
        var older = CodexAccountUsage.Parse(single.RootElement, Now);
        CollectionAssert.AreEqual(new[] { "codex:primary", "credits" }, older.Limits.Select(static limit => limit.Id).ToArray());
        Assert.IsTrue(older.Limits[1].Unlimited);
        using var nothing = JsonDocument.Parse("""{ "rateLimits": { "planType": "free", "primary": null, "credits": { "hasCredits": false, "balance": "0" } } }""");
        Assert.IsEmpty(CodexAccountUsage.Parse(nothing.RootElement, Now).Limits);
    }

    [TestMethod]
    public async Task Codex_AsksTheCliForTheLimitsOfItsAccount_WhenItIsTheAccountOfCodeAlta()
    {
        const string Limits = """{"id":3,"result":{"rateLimits":{"planType":"plus","primary":{"usedPercent":10,"windowDurationMins":300,"resetsAt":1791450000}}}}""";
        var server = new AppServer
        {
            Account = """{"id":2,"result":{"account":{"type":"chatgpt","email":"someone@example.test","planType":"plus"},"requiresOpenaiAuth":true}}""",
            Limits = Limits,
        };

        var reading = await CodexAccountUsage.ExchangeAsync(server.Send, server.Receive, "Someone@Example.test", Now, default);

        Assert.AreEqual(AgentSubscriptionUsageReading.Ok, reading.Status);
        Assert.AreEqual("plus", reading.Usage!.Plan);
        Assert.AreEqual(10, reading.Usage.Limits.Single().UsedPercent);
        // The handshake, the account, then the limits: nothing that signs in, refreshes a token or starts a turn.
        CollectionAssert.AreEqual(new[] { "initialize", "initialized", "account/read", "account/rateLimits/read" }, server.Methods);
        StringAssert.Contains(server.Sent[2], "\"refreshToken\":false");

        var other = new AppServer { Account = server.Account, Limits = Limits };
        Assert.AreEqual(AgentSubscriptionUsageReading.ToolSignedOut, (await CodexAccountUsage.ExchangeAsync(other.Send, other.Receive, "another@example.test", Now, default)).Status);
        Assert.IsFalse(other.Methods.Contains("account/rateLimits/read"), "The usage of another account is not asked for.");

        var key = new AppServer { Account = """{"id":2,"result":{"account":{"type":"apiKey"},"requiresOpenaiAuth":true}}""", Limits = Limits };
        Assert.AreEqual(AgentSubscriptionUsageReading.ToolSignedOut, (await CodexAccountUsage.ExchangeAsync(key.Send, key.Receive, null, Now, default)).Status);
        var signedOut = new AppServer { Account = """{"id":2,"result":{"account":null,"requiresOpenaiAuth":true}}""", Limits = Limits };
        Assert.AreEqual(AgentSubscriptionUsageReading.ToolSignedOut, (await CodexAccountUsage.ExchangeAsync(signedOut.Send, signedOut.Receive, null, Now, default)).Status);
        var refused = new AppServer { Account = server.Account, Limits = """{"id":3,"error":{"code":-32600,"message":"chatgpt authentication required to read rate limits"}}""" };
        Assert.AreEqual(AgentSubscriptionUsageReading.Unavailable, (await CodexAccountUsage.ExchangeAsync(refused.Send, refused.Receive, null, Now, default)).Status);
        var silent = new AppServer { Account = server.Account, Limits = null };
        Assert.AreEqual(AgentSubscriptionUsageReading.Unavailable, (await CodexAccountUsage.ExchangeAsync(silent.Send, silent.Receive, null, Now, default)).Status,
            "A CLI that ends without an answer has no usage.");
    }

    [TestMethod]
    public void Codex_IsStartedAsItsAppServer_AndIsNotFoundWhereItIsNot()
    {
        using var folder = new TempDirectory();
        Assert.IsNull(CodexAccountUsage.ResolveCli(Path.Combine(folder.Path, "codex.exe")));
        Assert.AreEqual(AgentSubscriptionUsageReading.ToolMissing,
            CodexAccountUsage.ReadAsync(null, default, Path.Combine(folder.Path, "codex.exe")).GetAwaiter().GetResult().Status, "Without the CLI, the usage says what is missing.");
        var cli = Path.Combine(folder.Path, OperatingSystem.IsWindows() ? "codex.exe" : "codex");
        File.WriteAllText(cli, string.Empty);
        Assert.AreEqual(cli, CodexAccountUsage.ResolveCli(cli));

        var start = CodexAccountUsage.CreateStartInfo(cli);
        Assert.AreEqual(cli, start.FileName);
        CollectionAssert.AreEqual(new[] { "app-server" }, start.ArgumentList.ToArray());
        Assert.IsTrue(start.RedirectStandardInput && start.RedirectStandardOutput && !start.UseShellExecute && start.CreateNoWindow);
        if (OperatingSystem.IsWindows())
        {
            // A script that a package manager installs runs through the command interpreter.
            var script = CodexAccountUsage.CreateStartInfo(@"C:\Program Files\npm\codex.CMD");
            StringAssert.EndsWith(script.FileName, "cmd.exe", StringComparison.OrdinalIgnoreCase);
            Assert.AreEqual("/d /s /c \"\"C:\\Program Files\\npm\\codex.CMD\" app-server\"", script.Arguments);
        }
    }

    [TestMethod]
    public void ClaudeCode_ReadsTheWindowsOfThePlanFromTheAnswerOfTheCli()
    {
        using var document = JsonDocument.Parse("""
            { "subscription_type": "max", "rate_limits_available": true,
              "rate_limits": {
                "five_hour": { "utilization": 5, "resets_at": "2026-10-08T11:00:00.048777+00:00", "limit_dollars": null },
                "seven_day": { "utilization": 52.4, "resets_at": "2026-10-12T05:00:00+00:00" },
                "seven_day_opus": null, "seven_day_sonnet": { "utilization": 12, "resets_at": "2026-10-12T05:00:00+00:00" },
                "tangelo": null,
                "model_scoped": [ { "display_name": "Sonnet", "utilization": 12, "resets_at": "2026-10-12T05:00:00+00:00" },
                                  { "display_name": "Fable", "utilization": 0, "resets_at": "2026-10-12T05:00:00+00:00" } ],
                "extra_usage": { "is_enabled": true, "utilization": 30, "monthly_limit": 5000, "used_credits": 1500 } } }
            """);

        var reading = ClaudeCodeAccountUsage.Parse(document.RootElement, Now);

        Assert.AreEqual(AgentSubscriptionUsageReading.Ok, reading.Status);
        Assert.AreEqual("max", reading.Usage!.Plan);
        CollectionAssert.AreEqual(new[] { "five_hour", "seven_day", "seven_day:sonnet", "seven_day:fable", "extra_usage" }, reading.Usage.Limits.Select(static limit => limit.Id).ToArray());
        Assert.AreEqual(5, reading.Usage.Limits[0].UsedPercent);
        Assert.AreEqual(300, reading.Usage.Limits[0].WindowMinutes);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 8, 11, 0, 0, TimeSpan.Zero).AddTicks(487770), reading.Usage.Limits[0].ResetsAt);
        Assert.AreEqual(52.4, reading.Usage.Limits[1].UsedPercent);
        Assert.AreEqual(7 * 24 * 60, reading.Usage.Limits[1].WindowMinutes);
        Assert.AreEqual("Sonnet", reading.Usage.Limits[2].Name, "A model that has a window of its own is listed once.");
        Assert.AreEqual("Fable", reading.Usage.Limits[3].Name);
        Assert.AreEqual(30, reading.Usage.Limits[4].UsedPercent);

        using var none = JsonDocument.Parse("""{ "subscription_type": null, "rate_limits_available": false, "rate_limits": null }""");
        Assert.AreEqual(AgentSubscriptionUsageReading.Unavailable, ClaudeCodeAccountUsage.Parse(none.RootElement, Now).Status);
        using var empty = JsonDocument.Parse("""{ "subscription_type": "pro", "rate_limits": { "five_hour": { "utilization": null }, "extra_usage": { "is_enabled": false, "utilization": 3 } } }""");
        Assert.AreEqual(AgentSubscriptionUsageReading.Unavailable, ClaudeCodeAccountUsage.Parse(empty.RootElement, Now).Status);
    }

    // An app server that answers from literal lines, with a notification before each answer.
    private sealed class AppServer
    {
        private readonly Queue<string> _lines = new();

        public required string Account { get; init; }
        public required string? Limits { get; init; }
        public List<string> Sent { get; } = [];
        public List<string?> Methods => Sent.Select(static line => JsonDocument.Parse(line).RootElement.GetProperty("method").GetString()).ToList();

        public ValueTask Send(string line, CancellationToken cancellationToken)
        {
            Sent.Add(line);
            using var document = JsonDocument.Parse(line);
            _lines.Enqueue("""{"method":"account/updated","params":{}}""");
            switch (document.RootElement.GetProperty("method").GetString())
            {
                case "initialize": _lines.Enqueue("""{"id":1,"result":{"userAgent":"codex"}}"""); break;
                case "account/read": _lines.Enqueue("not json"); _lines.Enqueue(Account); break;
                case "account/rateLimits/read" when Limits is not null: _lines.Enqueue(Limits); break;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask<string?> Receive(CancellationToken cancellationToken) => ValueTask.FromResult(_lines.TryDequeue(out var line) ? line : null);
    }

    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = status;
        public string Body { get; set; } = body;
        public List<(string Uri, string? Authorization)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString()));
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory() => Directory.CreateDirectory(Path);

        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodeAlta-usage-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }
}
