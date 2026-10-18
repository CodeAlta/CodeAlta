using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using CodeAlta.Agent.TypeSafe;

namespace CodeAlta.Tests;

/// <summary>Tests of <see cref="TypeSafeClient"/>: the requests it sends to the System One API and how it reads the answers.</summary>
[TestClass]
public sealed class TypeSafeClientTests
{
    private const string AnswerBody = """
        {
          "model": "jev-1.13.0",
          "answers": {
            "flaky": { "type": "noul", "noul": 0.83 },
            "area": { "type": "choice", "choice": "parser", "probabilities": { "parser": 0.7, "io": 0.3 }, "confidence": 0.62 }
          },
          "usage": { "input_tokens": 120, "output_tokens": 4 }
        }
        """;

    [TestMethod]
    public async Task Evaluate_PostsTheModelStateAndQuestions_WithTheKey()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, AnswerBody));
        using var http = new HttpClient(handler);
        var client = new TypeSafeClient(http, "  secret-key  ", null);

        var evaluation = await client.EvaluateAsync(" jev-latest ", JsonValue.Create("the failing test output"), Questions(), CancellationToken.None);

        var request = handler.Requests.Single();
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual(new Uri("https://api.typesafe.ai/v1/systemone"), request.Uri);
        Assert.AreEqual("Bearer secret-key", request.Authorization, "The key is trimmed and sent as a bearer token.");
        CollectionAssert.Contains(request.Accept, "application/json");
        Assert.AreEqual("application/json", request.ContentType);
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.AreEqual("jev-latest", body["model"]!.GetValue<string>(), "The model is trimmed.");
        Assert.AreEqual("the failing test output", body["state"]!.GetValue<string>());
        Assert.AreEqual("noul", body["questions"]!["flaky"]!["type"]!.GetValue<string>());
        Assert.AreEqual(3, body.Count);

        Assert.AreEqual("jev-1.13.0", evaluation.Model);
        Assert.AreEqual(0.83, evaluation.Answers["flaky"]!["noul"]!.GetValue<double>());
        Assert.AreEqual("parser", evaluation.Answers["area"]!["choice"]!.GetValue<string>());
        Assert.AreEqual(120L, evaluation.InputTokens);
        Assert.AreEqual(4L, evaluation.OutputTokens);
    }

    [TestMethod]
    public async Task Evaluate_CopiesTheQuestions_AndReadsAnAnswerWithoutUsage()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, """{"answers": {"flaky": {"type": "noul", "noul": 0.1}}}"""));
        using var http = new HttpClient(handler);
        var client = new TypeSafeClient(http, "key", null);
        var questions = Questions();
        var state = new JsonObject { ["diff"] = "+ one line" };

        var evaluation = await client.EvaluateAsync("jev-latest", state, questions, CancellationToken.None);

        // The caller's nodes are not taken over by the request: they keep no parent and can be used again.
        Assert.IsNull(questions.Parent);
        Assert.IsNull(state.Parent);
        Assert.AreEqual("+ one line", JsonNode.Parse(handler.Requests.Single().Body!)!["state"]!["diff"]!.GetValue<string>());
        Assert.IsNull(evaluation.Model);
        Assert.IsNull(evaluation.InputTokens);
        Assert.IsNull(evaluation.OutputTokens);
        Assert.AreEqual(0.1, evaluation.Answers["flaky"]!["noul"]!.GetValue<double>());
    }

    [TestMethod]
    public async Task Evaluate_AnAnswerWithoutAnswers_IsAnError()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, """{"model": "jev-1.13.0"}"""));
        using var http = new HttpClient(handler);
        var client = new TypeSafeClient(http, "key", null);

        var failure = await Assert.ThrowsExactlyAsync<TypeSafeApiException>(() => client.EvaluateAsync("jev-latest", JsonValue.Create("state"), Questions(), CancellationToken.None));

        Assert.AreEqual(HttpStatusCode.OK, failure.StatusCode);
        StringAssert.Contains(failure.Message, "answers");
    }

    [TestMethod]
    public async Task Evaluate_InvalidQuestions_AreRefusedBeforeAnyRequest()
    {
        var handler = new RecordingHandler(_ => throw new AssertFailedException("No request is sent for questions that break a rule."));
        using var http = new HttpClient(handler);
        var client = new TypeSafeClient(http, "key", null);
        var questions = new JsonObject { ["q"] = new JsonObject { ["type"] = "maybe", ["instructions"] = "Judge it." } };

        var failure = await Assert.ThrowsExactlyAsync<ArgumentException>(() => client.EvaluateAsync("jev-latest", JsonValue.Create("state"), questions, CancellationToken.None));

        StringAssert.Contains(failure.Message, "`q`");
        Assert.AreEqual(0, handler.Requests.Count);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => client.EvaluateAsync(" ", JsonValue.Create("state"), Questions(), CancellationToken.None));
    }

    [TestMethod]
    [DataRow("https://host/proxy", "https://host/proxy/v1/systemone")]
    [DataRow("https://host/proxy/", "https://host/proxy/v1/systemone")]
    [DataRow("https://host", "https://host/v1/systemone")]
    [DataRow("https://host/proxy?tenant=a", "https://host/proxy/v1/systemone?tenant=a")]
    public void Combine_KeepsThePathAndTheQueryOfTheBaseAddress(string baseUrl, string expected)
    {
        using var http = new HttpClient(new RecordingHandler(_ => Json(HttpStatusCode.OK, "{}")));
        var client = new TypeSafeClient(http, "key", new Uri(baseUrl));

        Assert.AreEqual(new Uri(expected), client.Combine("/v1/systemone"));
    }

    [TestMethod]
    public async Task Requests_GoBelowAnAddressWithAPath()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, "[]")
            : Json(HttpStatusCode.OK, AnswerBody));
        using var http = new HttpClient(handler);
        var client = new TypeSafeClient(http, "key", new Uri("https://host/proxy"));

        await client.EvaluateAsync("jev-latest", JsonValue.Create("state"), Questions(), CancellationToken.None);
        await client.ListModelsAsync(CancellationToken.None);

        Assert.AreEqual(new Uri("https://host/proxy/v1/systemone"), handler.Requests[0].Uri);
        Assert.AreEqual(new Uri("https://host/proxy/v1/models"), handler.Requests[1].Uri);
        Assert.AreEqual(HttpMethod.Get, handler.Requests[1].Method);
        Assert.AreEqual("Bearer key", handler.Requests[1].Authorization);
    }

    [TestMethod]
    public void Constructor_RefusesABlankKeyOrARelativeAddress()
    {
        using var http = new HttpClient(new RecordingHandler(_ => Json(HttpStatusCode.OK, "{}")));
        Assert.ThrowsExactly<ArgumentException>(() => new TypeSafeClient(http, " ", null));
        Assert.ThrowsExactly<ArgumentNullException>(() => new TypeSafeClient(http, null!, null));
        Assert.ThrowsExactly<ArgumentException>(() => new TypeSafeClient(http, "key", new Uri("/relative", UriKind.Relative)));
        Assert.ThrowsExactly<ArgumentNullException>(() => new TypeSafeClient(null!, "key", null));
    }

    [TestMethod]
    public async Task ABusyService_IsAskedAgain_AfterTheDelayItAsksFor()
    {
        var responses = new Queue<Func<HttpResponseMessage>>(
        [
            () => Json(HttpStatusCode.TooManyRequests, """{"error": {"message": "slow down"}}"""),
            () => Json((HttpStatusCode)529, "overloaded", retryAfter: TimeSpan.FromSeconds(2)),
            () => Json(HttpStatusCode.OK, AnswerBody),
        ]);
        var handler = new RecordingHandler(_ => responses.Dequeue()());
        using var http = new HttpClient(handler);
        var delays = new List<TimeSpan>();
        var client = Create(http, delays);

        var evaluation = await client.EvaluateAsync("jev-latest", JsonValue.Create("state"), Questions(), CancellationToken.None);

        Assert.AreEqual("jev-1.13.0", evaluation.Model);
        Assert.AreEqual(3, handler.Requests.Count);
        // A growing delay when the service names none, the delay it names otherwise.
        CollectionAssert.AreEqual(new[] { TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2) }, delays);
        // Each attempt is a new request with the same body and the key.
        Assert.IsTrue(handler.Requests.All(static request => request.Authorization == "Bearer key"));
        Assert.AreEqual(1, handler.Requests.Select(static request => request.Body).Distinct().Count());
    }

    [TestMethod]
    public async Task ABusyService_IsAskedThreeTimesAtMost_WithACappedDelay()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.TooManyRequests, """{"error": {"message": "rate limited"}}""", retryAfter: TimeSpan.FromSeconds(30)));
        using var http = new HttpClient(handler);
        var delays = new List<TimeSpan>();
        var client = Create(http, delays);

        var failure = await Assert.ThrowsExactlyAsync<TypeSafeApiException>(() => client.EvaluateAsync("jev-latest", JsonValue.Create("state"), Questions(), CancellationToken.None));

        Assert.AreEqual(3, handler.Requests.Count);
        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10) }, delays, "A delay the service asks for is capped.");
        Assert.AreEqual(HttpStatusCode.TooManyRequests, failure.StatusCode);
        Assert.IsTrue(failure.IsBusy);
        Assert.IsFalse(failure.IsAuthenticationFailure);
        Assert.IsFalse(failure.IsInvalidRequest);
        StringAssert.Contains(failure.Message, "429");
        StringAssert.Contains(failure.Message, "rate limited");
    }

    [TestMethod]
    public async Task AGrowingDelay_DoublesBetweenAttempts()
    {
        var handler = new RecordingHandler(_ => Json((HttpStatusCode)529, ""));
        using var http = new HttpClient(handler);
        var delays = new List<TimeSpan>();
        var client = Create(http, delays);

        var failure = await Assert.ThrowsExactlyAsync<TypeSafeApiException>(() => client.ListModelsAsync(CancellationToken.None));

        CollectionAssert.AreEqual(new[] { TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1) }, delays);
        Assert.IsTrue(failure.IsBusy);
        StringAssert.Contains(failure.Message, "overloaded");
    }

    [TestMethod]
    public async Task ARefusedKey_IsAnAuthenticationFailure_AndIsNotSentAgain()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.Unauthorized, """{"error": {"message": "invalid api key"}}"""));
        using var http = new HttpClient(handler);
        var delays = new List<TimeSpan>();
        var client = Create(http, delays);

        var failure = await Assert.ThrowsExactlyAsync<TypeSafeApiException>(() => client.EvaluateAsync("jev-latest", JsonValue.Create("state"), Questions(), CancellationToken.None));

        Assert.AreEqual(1, handler.Requests.Count);
        Assert.AreEqual(0, delays.Count);
        Assert.AreEqual(HttpStatusCode.Unauthorized, failure.StatusCode);
        Assert.IsTrue(failure.IsAuthenticationFailure);
        Assert.IsFalse(failure.IsBusy);
        StringAssert.Contains(failure.Message, "401");
        StringAssert.Contains(failure.Message, "invalid api key");
        Assert.IsTrue(new TypeSafeApiException(HttpStatusCode.Forbidden, "no").IsAuthenticationFailure);
    }

    [TestMethod]
    public async Task AFailure_GivesTheMessageOfTheDetailTheServiceWrites()
    {
        // The body the API answers with (2026-10-18): the message is in `detail.message`, beside its `error_type`.
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.Unauthorized,
            """{"detail":{"error_type":"authentication_error","message":"Cannot authenticate with the server. Please check your API key and try again."}}"""));
        using var http = new HttpClient(handler);
        var client = Create(http, []);

        var failure = await Assert.ThrowsExactlyAsync<TypeSafeApiException>(() => client.ListModelsAsync(CancellationToken.None));

        Assert.AreEqual("TypeSafe API 401: the API key is missing or invalid: Cannot authenticate with the server. Please check your API key and try again.", failure.Message);
    }

    [TestMethod]
    public async Task AnInvalidRequest_SaysWhatTheServiceFound()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.UnprocessableEntity, """{"detail": [{"loc": ["questions", "q"], "msg": "criteria required"}]}"""));
        using var http = new HttpClient(handler);
        var client = Create(http, []);

        var failure = await Assert.ThrowsExactlyAsync<TypeSafeApiException>(() => client.EvaluateAsync("jev-latest", JsonValue.Create("state"), Questions(), CancellationToken.None));

        Assert.AreEqual(1, handler.Requests.Count);
        Assert.IsTrue(failure.IsInvalidRequest);
        StringAssert.Contains(failure.Message, "422");
        StringAssert.Contains(failure.Message, "criteria required");
        foreach (var status in new[] { HttpStatusCode.BadRequest, HttpStatusCode.RequestEntityTooLarge, HttpStatusCode.UnsupportedMediaType })
            Assert.IsTrue(new TypeSafeApiException(status, "no").IsInvalidRequest, status.ToString());
        Assert.IsFalse(new TypeSafeApiException(null, "no").IsInvalidRequest);
    }

    [TestMethod]
    public async Task AFailureWithAPlainBody_GivesTheStartOfTheBody()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.InternalServerError, new string('x', 2000)));
        using var http = new HttpClient(handler);
        var client = Create(http, []);

        var failure = await Assert.ThrowsExactlyAsync<TypeSafeApiException>(() => client.ListModelsAsync(CancellationToken.None));

        Assert.AreEqual(1, handler.Requests.Count, "A failure of the service is not a busy service: it is not sent again.");
        StringAssert.Contains(failure.Message, "500");
        StringAssert.Contains(failure.Message, "the service failed");
        StringAssert.EndsWith(failure.Message, "…");
        Assert.IsTrue(failure.Message.Length < 800, failure.Message);
    }

    [TestMethod]
    public async Task ASuccessThatIsNotJson_IsAnError()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, "<html>"));
        using var http = new HttpClient(handler);
        var client = Create(http, []);

        var failure = await Assert.ThrowsExactlyAsync<TypeSafeApiException>(() => client.ListModelsAsync(CancellationToken.None));

        StringAssert.Contains(failure.Message, "not JSON");
    }

    [TestMethod]
    public async Task AServiceThatCannotBeReached_IsAnErrorWithoutStatus()
    {
        var handler = new RecordingHandler(_ => throw new HttpRequestException("connection refused"));
        using var http = new HttpClient(handler);
        var client = Create(http, []);

        var failure = await Assert.ThrowsExactlyAsync<TypeSafeApiException>(() => client.ListModelsAsync(CancellationToken.None));

        Assert.IsNull(failure.StatusCode);
        Assert.IsFalse(failure.IsBusy || failure.IsAuthenticationFailure || failure.IsInvalidRequest);
        StringAssert.Contains(failure.Message, "connection refused");
    }

    [TestMethod]
    public async Task ACanceledRequest_IsCanceled()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, "[]"));
        using var http = new HttpClient(handler);
        var client = Create(http, []);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => client.ListModelsAsync(cancellation.Token));
    }

    [TestMethod]
    public async Task ListModels_ReadsTheDataArray_AndSkipsEntriesWithoutName()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, """
            {"data": [
              {"name": "jev-latest", "description": "The latest stable Jev", "release_date": "2026-09-01"},
              {"id": "jev-preview"},
              {"description": "no name"},
              "not an object"
            ]}
            """));
        using var http = new HttpClient(handler);
        var client = Create(http, []);

        var models = await client.ListModelsAsync(CancellationToken.None);

        Assert.AreEqual(new Uri("https://api.typesafe.ai/v1/models"), handler.Requests.Single().Uri);
        CollectionAssert.AreEqual(
            new[] { new TypeSafeModel("jev-latest", "The latest stable Jev", "2026-09-01"), new TypeSafeModel("jev-preview", null, null) },
            models.ToArray());
    }

    [TestMethod]
    [DataRow("""[{"name": "jev-latest"}, {"name": "jev-1.13.0"}]""")]
    [DataRow("""{"models": [{"name": "jev-latest"}, {"name": "jev-1.13.0"}]}""")]
    public async Task ListModels_ReadsABareArrayOrAModelsArray(string body)
    {
        using var http = new HttpClient(new RecordingHandler(_ => Json(HttpStatusCode.OK, body)));
        var client = Create(http, []);

        var models = await client.ListModelsAsync(CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "jev-latest", "jev-1.13.0" }, models.Select(static model => model.Name).ToArray());
    }

    [TestMethod]
    public async Task ListModels_OfAnUnknownShape_IsEmpty()
    {
        using var http = new HttpClient(new RecordingHandler(_ => Json(HttpStatusCode.OK, """{"object": "list"}""")));
        var client = Create(http, []);

        Assert.AreEqual(0, (await client.ListModelsAsync(CancellationToken.None)).Count);
    }

    private static TypeSafeClient Create(HttpClient http, List<TimeSpan> delays)
        => new(http, "key", null, TimeSpan.FromSeconds(30), (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });

    private static JsonObject Questions() => new()
    {
        ["flaky"] = new JsonObject { ["type"] = "noul", ["instructions"] = "The failure comes from timing." },
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string body, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (retryAfter is { } delay) response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        return response;
    }

    /// <summary>One request as the handler saw it, read before the client disposes it.</summary>
    private sealed record RecordedRequest(HttpMethod Method, Uri? Uri, string? Authorization, string[] Accept, string? ContentType, string? Body);

    /// <summary>Answers each request with a new response and records what was sent.</summary>
    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri,
                request.Headers.Authorization?.ToString(),
                [.. request.Headers.Accept.Select(static value => value.MediaType ?? string.Empty)],
                request.Content?.Headers.ContentType?.MediaType,
                body));
            return respond(request);
        }
    }
}
