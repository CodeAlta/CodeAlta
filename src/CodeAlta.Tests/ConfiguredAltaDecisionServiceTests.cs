using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CodeAlta.Catalog;
using CodeAlta.LiveTool;

namespace CodeAlta.Tests;

/// <summary>Tests of <see cref="ConfiguredAltaDecisionService"/>: which decision provider of the configuration answers, with which key.</summary>
[TestClass]
public sealed class ConfiguredAltaDecisionServiceTests
{
    private const string AnswerBody = """{"model": "jev-1.13.0", "answers": {"flaky": {"type": "noul", "noul": 0.9}}, "usage": {"input_tokens": 10, "output_tokens": 1}}""";

    [TestMethod]
    public async Task NoDecisionProvider_IsSaid()
    {
        var handler = new RecordingHandler(_ => throw new AssertFailedException("No request without a provider."));
        using var http = new HttpClient(handler);
        // Other providers, and a decision provider that is disabled, do not count.
        var service = Create(http, [Provider("openai", "openai-chat", apiKey: "k"), Provider("typesafe", apiKey: "k", enabled: false)]);

        var failure = await Assert.ThrowsExactlyAsync<AltaDecisionException>(() => service.DecideAsync(null, null, JsonValue.Create("state"), Questions(), CancellationToken.None));
        Assert.AreEqual("decide.noProvider", failure.Code);
        Assert.AreEqual("decide.noProvider", (await Assert.ThrowsExactlyAsync<AltaDecisionException>(() => service.ListModelsAsync(null, CancellationToken.None))).Code);
        Assert.AreEqual(0, service.ListProviders().Count);
        Assert.AreEqual(0, handler.Requests.Count);
    }

    [TestMethod]
    public async Task AnUnknownKey_NamesTheDecisionProviders()
    {
        using var http = new HttpClient(new RecordingHandler(_ => throw new AssertFailedException("No request for an unknown provider.")));
        var service = Create(http, [Provider("jev-a", apiKey: "k"), Provider("jev-b", apiKey: "k"), Provider("openai", "openai-chat", apiKey: "k")]);

        var failure = await Assert.ThrowsExactlyAsync<AltaDecisionException>(() => service.DecideAsync("openai", null, JsonValue.Create("state"), Questions(), CancellationToken.None));

        Assert.AreEqual("decide.providerNotFound", failure.Code, "A provider that is not a decision provider is not one to consult.");
        StringAssert.Contains(failure.Message, "'openai'");
        StringAssert.Contains(failure.Message, "jev-a, jev-b");
    }

    [TestMethod]
    public async Task AProviderWithoutKey_IsSaid_AndItsVariableNamed()
    {
        using var http = new HttpClient(new RecordingHandler(_ => throw new AssertFailedException("No request without a key.")));
        var service = Create(http, [Provider("typesafe", apiKeyEnv: "CODEALTA_TYPESAFE_API_KEY")], environment: _ => "  ");

        var failure = await Assert.ThrowsExactlyAsync<AltaDecisionException>(() => service.DecideAsync(null, null, JsonValue.Create("state"), Questions(), CancellationToken.None));

        Assert.AreEqual("decide.noCredential", failure.Code);
        StringAssert.Contains(failure.Message, "CODEALTA_TYPESAFE_API_KEY");
        Assert.IsFalse(service.ListProviders().Single().HasCredential);
    }

    [TestMethod]
    public async Task TheKey_IsReadFromTheVariableItNames_WhenNoneIsWritten()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, AnswerBody));
        using var http = new HttpClient(handler);
        var asked = new List<string>();
        var service = Create(http, [Provider("typesafe", apiKeyEnv: " MY_TYPESAFE_KEY ")], environment: name =>
        {
            asked.Add(name);
            return name == "MY_TYPESAFE_KEY" ? " from-environment " : null;
        });

        var result = await service.DecideAsync(null, null, JsonValue.Create("state"), Questions(), CancellationToken.None);

        Assert.AreEqual("Bearer from-environment", handler.Requests.Single().Authorization);
        CollectionAssert.Contains(asked, "MY_TYPESAFE_KEY");
        Assert.IsTrue(result.Provider.HasCredential);
        Assert.IsTrue(service.ListProviders().Single().HasCredential);
    }

    [TestMethod]
    public async Task AWrittenKey_ComesBeforeTheVariable()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, AnswerBody));
        using var http = new HttpClient(handler);
        var service = Create(http, [Provider("typesafe", apiKey: " written ", apiKeyEnv: "MY_TYPESAFE_KEY")], environment: _ => "from-environment");

        await service.DecideAsync(null, null, JsonValue.Create("state"), Questions(), CancellationToken.None);

        Assert.AreEqual("Bearer written", handler.Requests.Single().Authorization);
    }

    [TestMethod]
    public async Task TheFirstEnabledDecisionProvider_Answers_WithItsModelAndEndpoint()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, AnswerBody));
        using var http = new HttpClient(handler);
        var service = Create(http,
        [
            Provider("openai", "openai-chat", apiKey: "other"),
            Provider("off", apiKey: "disabled-key", enabled: false),
            Provider("first", apiKey: "first-key", model: " jev-preview ", apiUrl: "https://proxy.example/typesafe", displayName: " Jev "),
            Provider("second", apiKey: "second-key"),
        ]);

        var result = await service.DecideAsync(null, null, JsonValue.Create("state"), Questions(), CancellationToken.None);

        var request = handler.Requests.Single();
        Assert.AreEqual(new Uri("https://proxy.example/typesafe/v1/systemone"), request.Uri);
        Assert.AreEqual("Bearer first-key", request.Authorization);
        Assert.AreEqual("jev-preview", JsonNode.Parse(request.Body!)!["model"]!.GetValue<string>());
        Assert.AreEqual(new AltaDecisionProvider("first", "Jev", "jev-preview", true), result.Provider);
        Assert.AreEqual("jev-1.13.0", result.Evaluation.Model);
        Assert.AreEqual(10L, result.Evaluation.InputTokens);
        Assert.AreEqual(0.9, result.Evaluation.Answers["flaky"]!["noul"]!.GetValue<double>());
        CollectionAssert.AreEqual(new[] { "first", "second" }, service.ListProviders().Select(static provider => provider.Key).ToArray());
    }

    [TestMethod]
    public async Task ANamedProviderAndModel_AreAsked()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, AnswerBody));
        using var http = new HttpClient(handler);
        var service = Create(http, [Provider("first", apiKey: "first-key"), Provider("second", apiKey: "second-key")]);

        var result = await service.DecideAsync(" SECOND ", " jev-1.12.0 ", JsonValue.Create("state"), Questions(), CancellationToken.None);

        var request = handler.Requests.Single();
        Assert.AreEqual("Bearer second-key", request.Authorization);
        Assert.AreEqual(new Uri("https://api.typesafe.ai/v1/systemone"), request.Uri);
        Assert.AreEqual("jev-1.12.0", JsonNode.Parse(request.Body!)!["model"]!.GetValue<string>());
        // A provider without a name or a model shows its key and the default model.
        Assert.AreEqual(new AltaDecisionProvider("second", "second", "jev-latest", true), result.Provider);
    }

    [TestMethod]
    public async Task ARefusedKey_IsUnauthorized()
    {
        using var http = new HttpClient(new RecordingHandler(_ => Json(HttpStatusCode.Unauthorized, """{"error": {"message": "bad key"}}""")));
        var service = Create(http, [Provider("typesafe", apiKey: "wrong", displayName: "TypeSafe Jev")]);

        var decide = await Assert.ThrowsExactlyAsync<AltaDecisionException>(() => service.DecideAsync(null, null, JsonValue.Create("state"), Questions(), CancellationToken.None));
        var models = await Assert.ThrowsExactlyAsync<AltaDecisionException>(() => service.ListModelsAsync(null, CancellationToken.None));

        Assert.AreEqual("decide.unauthorized", decide.Code);
        Assert.AreEqual("decide.unauthorized", models.Code);
        StringAssert.StartsWith(decide.Message, "TypeSafe Jev (typesafe): ");
        StringAssert.Contains(decide.Message, "bad key");
        Assert.IsInstanceOfType<CodeAlta.Agent.TypeSafe.TypeSafeApiException>(decide.InnerException);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.UnprocessableEntity, "decide.invalidRequest")]
    [DataRow(HttpStatusCode.Forbidden, "decide.unauthorized")]
    [DataRow(HttpStatusCode.InternalServerError, "decide.failed")]
    [DataRow(HttpStatusCode.NotFound, "decide.failed")]
    public async Task AFailureOfTheService_HasItsCode(HttpStatusCode status, string code)
    {
        using var http = new HttpClient(new RecordingHandler(_ => Json(status, "{}")));
        var service = Create(http, [Provider("typesafe", apiKey: "k")]);

        var failure = await Assert.ThrowsExactlyAsync<AltaDecisionException>(() => service.DecideAsync(null, null, JsonValue.Create("state"), Questions(), CancellationToken.None));

        Assert.AreEqual(code, failure.Code);
    }

    [TestMethod]
    public async Task InvalidQuestions_AreRefused_BeforeAProviderIsLookedFor()
    {
        using var http = new HttpClient(new RecordingHandler(_ => throw new AssertFailedException("No request for invalid questions.")));
        var service = Create(http, []);
        var questions = new JsonObject { ["q"] = new JsonObject { ["type"] = "score", ["instructions"] = "Rate.", ["criteria"] = new JsonArray("one") } };

        var failure = await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.DecideAsync(null, null, JsonValue.Create("state"), questions, CancellationToken.None));

        StringAssert.Contains(failure.Message, "`q`");
    }

    [TestMethod]
    public async Task ListModels_AsksTheNamedProvider()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, """{"data": [{"name": "jev-latest"}, {"name": "jev-preview"}]}"""));
        using var http = new HttpClient(handler);
        var service = Create(http, [Provider("first", apiKey: "first-key"), Provider("second", apiKey: "second-key", model: "jev-preview")]);

        var (provider, models) = await service.ListModelsAsync("second", CancellationToken.None);

        Assert.AreEqual("second", provider.Key);
        Assert.AreEqual("jev-preview", provider.Model);
        CollectionAssert.AreEqual(new[] { "jev-latest", "jev-preview" }, models.Select(static model => model.Name).ToArray());
        Assert.AreEqual(new Uri("https://api.typesafe.ai/v1/models"), handler.Requests.Single().Uri);
        Assert.AreEqual("Bearer second-key", handler.Requests.Single().Authorization);
    }

    [TestMethod]
    public void TheConfiguration_IsReadOnEachRequest()
    {
        using var http = new HttpClient(new RecordingHandler(_ => Json(HttpStatusCode.OK, "[]")));
        IReadOnlyList<CodeAltaProviderDocument> definitions = [];
        var service = new ConfiguredAltaDecisionService(() => definitions, static _ => null, http);

        Assert.AreEqual(0, service.ListProviders().Count);
        definitions = [Provider("typesafe", apiKey: "k")];
        Assert.AreEqual("typesafe", service.ListProviders().Single().Key);
    }

    [TestMethod]
    public void TheServiceOverAStore_ReadsTheDecisionProvidersOfTheConfiguration()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "decision-service-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "config.toml"), """
                [providers.jev]
                type = "typesafe-ai"
                api_key = "secret"

                [providers.spare]
                enabled = false
                type = "typesafe"
                api_key_env = "CODEALTA_TYPESAFE_TEST_KEY_UNSET"

                [providers.local]
                type = "openai-chat"
                api_url = "http://127.0.0.1:9999/v1"
                api_key = "other"
                """);
            using var http = new HttpClient(new RecordingHandler(_ => Json(HttpStatusCode.OK, "[]")));
            var service = new ConfiguredAltaDecisionService(new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root }), http);

            Assert.AreEqual(new AltaDecisionProvider("jev", "jev", "jev-latest", true), service.ListProviders().Single());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ConfiguredAltaDecisionService Create(HttpClient http, IReadOnlyList<CodeAltaProviderDocument> definitions, Func<string, string?>? environment = null)
        => new(() => definitions, environment ?? (static _ => null), http);

    private static CodeAltaProviderDocument Provider(string key, string type = "typesafe", string? apiKey = null, string? apiKeyEnv = null, bool enabled = true,
        string? model = null, string? apiUrl = null, string? displayName = null)
        => new()
        {
            ProviderKey = key,
            ProviderType = type,
            Enabled = enabled,
            ApiKey = apiKey,
            ApiKeyEnv = apiKeyEnv,
            Model = model,
            ApiUrl = apiUrl,
            DisplayName = displayName,
        };

    private static JsonObject Questions() => new()
    {
        ["flaky"] = new JsonObject { ["type"] = "noul", ["instructions"] = "The failure comes from timing." },
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record RecordedRequest(Uri? Uri, string? Authorization, string? Body);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.RequestUri, request.Headers.Authorization?.ToString(), body));
            return respond(request);
        }
    }
}
