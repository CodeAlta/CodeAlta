using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Agent.TypeSafe;
using CodeAlta.Catalog;
using CodeAlta.LiveTool;

namespace CodeAlta.Tests;

/// <summary>Tests of <c>alta decide</c>: an agent consults a decision model with typed questions and reads typed answers.</summary>
[TestClass]
public sealed class AltaDecideCommandsTests
{
    private const string Request = """
        {"state": "the failing test output", "questions": {"flaky": {"type": "noul", "instructions": "The failure comes from timing."}}}
        """;

    [TestMethod]
    public async Task DecideGroup_ExistsOnlyWithADecisionService()
    {
        var none = Fixture.Create(service: null);
        Assert.AreEqual(AltaExitCodes.Usage, (await none.RunAsync(["decide", "--stdin"], Request)).ExitCode);
        Assert.AreEqual(AltaExitCodes.Usage, (await none.RunAsync(["decide", "providers"])).ExitCode);
        Assert.IsFalse(Paths(await none.RunAsync(["tool", "list", "--detailed"])).Any(static path => path.StartsWith("decide", StringComparison.Ordinal)));

        var window = Fixture.Create(new FakeDecisionService());
        Assert.IsTrue((await window.RunAsync(["decide", "--help"])).IsHelp);
        StringAssert.Contains((await window.RunAsync(["decide", "--help"])).Stdout, "noul");
        var policy = Records(await window.RunAsync(["tool", "list", "--detailed"]))
            .Single(static line => line.TryGetProperty("path", out var path) && path.GetString() == "decide");
        Assert.IsFalse(policy.GetProperty("isMutating").GetBoolean(), "A decision changes nothing of the user's.");
        Assert.IsFalse(policy.GetProperty("isDisruptive").GetBoolean());
    }

    [TestMethod]
    public async Task Decide_WritesTheAnswersWithASummary()
    {
        var service = new FakeDecisionService
        {
            Answers = JsonNode.Parse("""
                {
                  "flaky": {"type": "noul", "noul": 0.83},
                  "area": {"type": "choice", "choice": "parser", "probabilities": {"parser": 0.7, "io": 0.3}, "confidence": 0.62},
                  "risk": {"type": "score", "score": 2.6, "legend": {"1": "low", "2": "medium", "3": "high"}, "confidence": 0.4}
                }
                """)!.AsObject(),
        };
        var fixture = Fixture.Create(service);

        var result = await fixture.OkAsync(["decide", "--stdin", "--provider", "jev", "--model", " jev-preview "], """
            {"state": {"diff": "+ line"}, "questions": {"flaky": {"type": "noul", "instructions": "Timing."}}}
            """);

        var call = service.Calls.Single();
        Assert.AreEqual("jev", call.ProviderKey);
        Assert.AreEqual("jev-preview", call.Model);
        Assert.AreEqual("+ line", call.State["diff"]!.GetValue<string>());
        Assert.AreEqual("noul", call.Questions["flaky"]!["type"]!.GetValue<string>());

        var record = Records(result).Single(static line => Type(line) == "alta.decide.result");
        Assert.AreEqual(1, record.GetProperty("version").GetInt32());
        Assert.AreEqual("jev", record.GetProperty("providerKey").GetString());
        Assert.AreEqual("jev-1.13.0", record.GetProperty("model").GetString());
        var summary = record.GetProperty("summary");
        Assert.AreEqual("yes (p(yes)=0.83, certainty 0.66)", summary.GetProperty("flaky").GetString());
        Assert.AreEqual("parser (confidence 0.62)", summary.GetProperty("area").GetString());
        Assert.AreEqual("2.6 ≈ \"high\" (confidence 0.4)", summary.GetProperty("risk").GetString());
        Assert.AreEqual(0.83, record.GetProperty("answers").GetProperty("flaky").GetProperty("noul").GetDouble());
        Assert.AreEqual(0.7, record.GetProperty("answers").GetProperty("area").GetProperty("probabilities").GetProperty("parser").GetDouble());
        Assert.AreEqual(120, record.GetProperty("usage").GetProperty("inputTokens").GetInt64());
        Assert.AreEqual(4, record.GetProperty("usage").GetProperty("outputTokens").GetInt64());
    }

    [TestMethod]
    public async Task Decide_TakesTheModelOfTheRequest_AndLeavesOutWhatTheServiceDidNotSay()
    {
        var service = new FakeDecisionService { Model = null, InputTokens = null, OutputTokens = null };
        var fixture = Fixture.Create(service);

        var result = await fixture.OkAsync(["decide", "--stdin"], """
            {"model": "jev-1.12.0", "state": "text", "questions": {"flaky": {"type": "noul", "instructions": "Timing."}}}
            """);

        Assert.IsNull(service.Calls.Single().ProviderKey);
        Assert.AreEqual("jev-1.12.0", service.Calls.Single().Model);
        var record = Records(result).Single(static line => Type(line) == "alta.decide.result");
        Assert.IsFalse(record.TryGetProperty("usage", out _));
        Assert.IsFalse(record.TryGetProperty("model", out _));

        // The option comes before the field of the request.
        await fixture.OkAsync(["decide", "--stdin", "--model", "jev-preview"], """
            {"model": "jev-1.12.0", "state": "text", "questions": {"flaky": {"type": "noul", "instructions": "Timing."}}}
            """);
        Assert.AreEqual("jev-preview", service.Calls[1].Model);
    }

    [TestMethod]
    public void Summarize_SaysEachAnswerInOneLine()
    {
        var summary = BuiltInAltaCommandContributor.Summarize(JsonNode.Parse("""
            {
              "no": {"type": "noul", "noul": 0.2},
              "unsure": {"type": "noul", "noul": 0.5},
              "sure": {"type": "noul", "noul": 1},
              "pick": {"type": "choice", "choice": "b"},
              "plain": {"type": "score", "score": 3, "confidence": 0.9},
              "between": {"type": "score", "score": 1.5, "legend": {"1": "low", "2": "high"}, "confidence": 0.55},
              "unnamed": {"type": "score", "score": 7, "legend": {"1": "low"}, "confidence": 0.1},
              "nothing": {"type": "noul"},
              "wrong": {"type": "choice", "choice": 3},
              "unknown": {"type": "other", "other": 1},
              "text": "not an object"
            }
            """)!.AsObject());

        Assert.AreEqual("no (p(yes)=0.2, certainty 0.6)", summary["no"]);
        Assert.AreEqual("yes (p(yes)=0.5, certainty 0)", summary["unsure"]);
        Assert.AreEqual("yes (p(yes)=1, certainty 1)", summary["sure"]);
        Assert.AreEqual("b (confidence ?)", summary["pick"], "A confidence the answer does not give is shown as unknown.");
        Assert.AreEqual("3 (confidence 0.9)", summary["plain"]);
        Assert.AreEqual("1.5 ≈ \"high\" (confidence 0.55)", summary["between"], "The level is the nearest, half away from zero.");
        Assert.AreEqual("7 (confidence 0.1)", summary["unnamed"]);
        CollectionAssert.AreEquivalent(new[] { "no", "unsure", "sure", "pick", "plain", "between", "unnamed" }, summary.Keys.ToArray());
    }

    [TestMethod]
    public async Task Decide_RefusesARequestItCannotRead()
    {
        var service = new FakeDecisionService();
        var fixture = Fixture.Create(service);

        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.missingStdin", ["decide"], Request);
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.missingPayload", ["decide", "--stdin"], "  ");
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.invalidJson", ["decide", "--stdin"], "{not json");
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.invalidPayload", ["decide", "--stdin"], "[1, 2]");
        var unknown = await fixture.FailsAsync(AltaExitCodes.Usage, "usage.invalidPayload", ["decide", "--stdin"],
            """{"state": "text", "questions": {}, "provider": "jev"}""");
        StringAssert.Contains(unknown.GetProperty("message").GetString(), "`provider`");
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.invalidPayload", ["decide", "--stdin"], """{"state": "text"}""");
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.invalidPayload", ["decide", "--stdin"], """{"questions": {"q": {"type": "noul", "instructions": "x"}}}""");
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.invalidPayload", ["decide", "--stdin"], """{"state": "text", "questions": ["q"]}""");
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.invalidAction", ["decide", "something"]);
        Assert.AreEqual(0, service.Calls.Count);
    }

    [TestMethod]
    public async Task Decide_RefusesAnInvalidQuestion_WithoutAskingTheService()
    {
        // The real service checks the questions before it looks for a provider or sends anything.
        using var http = new HttpClient(new FailingHandler());
        var service = new ConfiguredAltaDecisionService(new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = Path.Combine(AppContext.BaseDirectory, "decide-tests", Guid.NewGuid().ToString("N")) }), http);
        var fixture = Fixture.Create(service);

        var error = await fixture.FailsAsync(AltaExitCodes.Usage, "usage.invalidQuestion", ["decide", "--stdin"],
            """{"state": "text", "questions": {"risk": {"type": "score", "instructions": "Rate.", "criteria": ["only"]}}}""");

        StringAssert.Contains(error.GetProperty("message").GetString(), "`risk`");
    }

    [TestMethod]
    [DataRow("decide.noProvider", AltaExitCodes.ServiceUnavailable)]
    [DataRow("decide.noCredential", AltaExitCodes.ServiceUnavailable)]
    [DataRow("decide.busy", AltaExitCodes.ServiceUnavailable)]
    [DataRow("decide.providerNotFound", AltaExitCodes.NotFound)]
    [DataRow("decide.unauthorized", AltaExitCodes.PolicyDenied)]
    [DataRow("decide.invalidRequest", AltaExitCodes.Usage)]
    [DataRow("decide.failed", AltaExitCodes.Failure)]
    public async Task AFailedDecision_HasTheExitCodeOfItsCode(string code, int exitCode)
    {
        var fixture = Fixture.Create(new FakeDecisionService { Failure = new AltaDecisionException(code, $"It failed: {code}.") });

        var error = await fixture.FailsAsync(exitCode, code, ["decide", "--stdin"], Request);
        Assert.AreEqual($"It failed: {code}.", error.GetProperty("message").GetString());
        Assert.AreEqual(code, (await fixture.FailsAsync(exitCode, code, ["decide", "models"])).GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task Providers_ListsTheDecisionProviders_WithoutSecrets()
    {
        var fixture = Fixture.Create(new FakeDecisionService
        {
            Providers = [new AltaDecisionProvider("jev", "TypeSafe Jev", "jev-latest", true), new AltaDecisionProvider("spare", "spare", "jev-preview", false)],
        });

        var providers = Records(await fixture.OkAsync(["decide", "providers"])).Where(static line => Type(line) == "alta.decide.provider").ToArray();

        Assert.AreEqual(2, providers.Length);
        Assert.AreEqual("jev", providers[0].GetProperty("providerKey").GetString());
        Assert.AreEqual("TypeSafe Jev", providers[0].GetProperty("displayName").GetString());
        Assert.AreEqual("jev-latest", providers[0].GetProperty("model").GetString());
        Assert.IsTrue(providers[0].GetProperty("hasCredential").GetBoolean());
        Assert.IsFalse(providers[1].GetProperty("hasCredential").GetBoolean());

        var empty = await Fixture.Create(new FakeDecisionService()).OkAsync(["decide", "providers"]);
        Assert.AreEqual("decide.noProvider", Records(empty).Single(static line => Type(line) == "alta.warning").GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task Models_ListsTheModelsOfAProvider_AndMarksItsDefault()
    {
        var service = new FakeDecisionService
        {
            Providers = [new AltaDecisionProvider("jev", "TypeSafe Jev", "jev-latest", true)],
            Models = [new AltaDecisionModel("jev-latest", "The latest stable Jev", "2026-09-01"), new AltaDecisionModel("jev-preview", null, null)],
        };
        var fixture = Fixture.Create(service);

        var models = Records(await fixture.OkAsync(["decide", "models", "--provider", "jev"])).Where(static line => Type(line) == "alta.decide.model").ToArray();

        Assert.AreEqual("jev", service.ModelsAskedOf.Single());
        Assert.AreEqual(2, models.Length);
        Assert.AreEqual("jev-latest", models[0].GetProperty("name").GetString());
        Assert.AreEqual("The latest stable Jev", models[0].GetProperty("description").GetString());
        Assert.AreEqual("2026-09-01", models[0].GetProperty("releaseDate").GetString());
        Assert.IsTrue(models[0].GetProperty("default").GetBoolean());
        Assert.AreEqual("jev", models[0].GetProperty("providerKey").GetString());
        Assert.IsFalse(models[1].TryGetProperty("default", out _));
        Assert.IsFalse(models[1].TryGetProperty("description", out _));
    }

    private static IEnumerable<string> Paths(AltaCommandResult result)
        => Records(result).Where(static line => line.TryGetProperty("path", out _)).Select(static line => line.GetProperty("path").GetString()!);

    private static string? Type(JsonElement line) => line.TryGetProperty("type", out var type) ? type.GetString() : null;

    private static List<JsonElement> Records(AltaCommandResult result)
    {
        var values = new List<JsonElement>();
        foreach (var line in result.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var document = JsonDocument.Parse(line);
            values.Add(document.RootElement.Clone());
        }

        return values;
    }

    private sealed class Fixture
    {
        // The commands are run by a caller that belongs to no session, as the MCP server of the window does.
        private static readonly AltaCallerIdentity Mcp = new() { Kind = "mcp" };

        private Fixture(AltaCommandDispatcher dispatcher) => Dispatcher = dispatcher;

        private AltaCommandDispatcher Dispatcher { get; }

        public static Fixture Create(IAltaDecisionService? service)
        {
            var services = new AltaServiceCollection();
            if (service is not null) services.Add<IAltaDecisionService>(service);
            var registry = new AltaCommandRegistry();
            services.Add(registry);
            return new Fixture(new AltaCommandDispatcher(registry, services));
        }

        public Task<AltaCommandResult> RunAsync(string[] args, string? stdin = null) => Dispatcher.InvokeAsync(args, stdin, caller: Mcp).AsTask();

        public async Task<AltaCommandResult> OkAsync(string[] args, string? stdin = null)
        {
            var result = await Dispatcher.InvokeAsync(args, stdin, caller: Mcp);
            Assert.AreEqual(AltaExitCodes.Success, result.ExitCode, $"alta {string.Join(' ', args)}: {result.Stdout}");
            return result;
        }

        public async Task<JsonElement> FailsAsync(int exitCode, string code, string[] args, string? stdin = null)
        {
            var result = await Dispatcher.InvokeAsync(args, stdin, caller: Mcp);
            Assert.AreEqual(exitCode, result.ExitCode, $"alta {string.Join(' ', args)}: {result.Stdout}");
            var error = Records(result).Single(static line => Type(line) == "alta.error");
            Assert.AreEqual(code, error.GetProperty("code").GetString(), string.Join(' ', args));
            return error;
        }
    }

    private sealed record DecideCall(string? ProviderKey, string? Model, JsonNode State, JsonObject Questions);

    private sealed class FakeDecisionService : IAltaDecisionService
    {
        public List<DecideCall> Calls { get; } = [];

        public List<string?> ModelsAskedOf { get; } = [];

        public IReadOnlyList<AltaDecisionProvider> Providers { get; init; } = [];

        public IReadOnlyList<AltaDecisionModel> Models { get; init; } = [];

        public JsonObject Answers { get; init; } = new() { ["flaky"] = new JsonObject { ["type"] = "noul", ["noul"] = 0.9 } };

        public string? Model { get; init; } = "jev-1.13.0";

        public long? InputTokens { get; init; } = 120;

        public long? OutputTokens { get; init; } = 4;

        public AltaDecisionException? Failure { get; init; }

        public IReadOnlyList<AltaDecisionProvider> ListProviders() => Providers;

        public Task<AltaDecisionResult> DecideAsync(string? providerKey, string? model, JsonNode state, JsonObject questions, CancellationToken cancellationToken)
        {
            if (Failure is not null) throw Failure;
            Calls.Add(new DecideCall(providerKey, model, state, questions));
            var provider = new AltaDecisionProvider(providerKey ?? "typesafe", "TypeSafe Jev", "jev-latest", true);
            return Task.FromResult(new AltaDecisionResult(provider, new TypeSafeEvaluation(Model, Answers, InputTokens, OutputTokens)));
        }

        public Task<(AltaDecisionProvider Provider, IReadOnlyList<AltaDecisionModel> Models)> ListModelsAsync(string? providerKey, CancellationToken cancellationToken)
        {
            if (Failure is not null) throw Failure;
            ModelsAskedOf.Add(providerKey);
            var provider = Providers.FirstOrDefault(value => value.Key == providerKey) ?? new AltaDecisionProvider("typesafe", "TypeSafe Jev", "jev-latest", true);
            return Task.FromResult((provider, Models));
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new AssertFailedException($"No request is sent: {request.RequestUri}.");
    }
}
