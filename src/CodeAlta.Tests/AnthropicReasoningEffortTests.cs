using System.Net;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Anthropic;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AnthropicReasoningEffortTests
{
    // The Models API of Anthropic: Claude Haiku 4.5 thinks with a budget and takes no effort, Claude Opus 4.6 takes
    // adaptive thinking and its efforts, both take images, a text-only model says so, and an endpoint that says nothing
    // of a model gives no capabilities.
    private const string ModelsJson =
        """
        {
          "data": [
            {
              "type": "model", "id": "claude-haiku-4-5", "display_name": "Claude Haiku 4.5", "created_at": "2025-10-01T00:00:00Z",
              "max_input_tokens": 200000, "max_tokens": 64000,
              "capabilities": {
                "effort": { "supported": false, "low": { "supported": false }, "medium": { "supported": false }, "high": { "supported": false }, "max": { "supported": false } },
                "image_input": { "supported": true },
                "pdf_input": { "supported": true },
                "thinking": { "supported": true, "types": { "adaptive": { "supported": false }, "enabled": { "supported": true }, "disabled": { "supported": true } } }
              }
            },
            {
              "type": "model", "id": "claude-opus-4-6", "display_name": "Claude Opus 4.6", "created_at": "2026-02-01T00:00:00Z",
              "max_input_tokens": 200000, "max_tokens": 64000,
              "capabilities": {
                "effort": { "supported": true, "low": { "supported": true }, "medium": { "supported": true }, "high": { "supported": true }, "max": { "supported": true } },
                "image_input": { "supported": true },
                "pdf_input": { "supported": true },
                "thinking": { "supported": true, "types": { "adaptive": { "supported": true }, "enabled": { "supported": true }, "disabled": { "supported": true } } }
              }
            },
            {
              "type": "model", "id": "claude-text-test", "display_name": "Claude Text Test", "created_at": "2026-02-01T00:00:00Z",
              "max_input_tokens": 200000, "max_tokens": 64000,
              "capabilities": {
                "image_input": { "supported": false },
                "pdf_input": { "supported": false }
              }
            },
            {
              "type": "model", "id": "claude-sonnet-4-5", "display_name": "Claude Sonnet 4.5", "created_at": "2025-09-01T00:00:00Z",
              "max_input_tokens": 200000, "max_tokens": 64000
            }
          ],
          "has_more": false, "first_id": "claude-haiku-4-5", "last_id": "claude-sonnet-4-5"
        }
        """;

    private const string MessageStream =
        """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_test","type":"message","role":"assistant","model":"claude-test","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":0}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"ok"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":1}}

        event: message_stop
        data: {"type":"message_stop"}

        """;

    [TestMethod]
    public async Task AnthropicModelProviderRuntime_ListModels_ReadsTheEffortsOfTheModelsApi()
    {
        await using var providerRuntime = CreateRuntime(new List<string>());

        var models = await providerRuntime.ListModelsAsync().ConfigureAwait(false);

        var haiku = models.Single(static model => model.Id == "claude-haiku-4-5");
        Assert.AreEqual(0, haiku.SupportedReasoningEfforts!.Count);
        Assert.IsTrue(AgentReasoningEffortCapability.IsListComplete(haiku));
        var opus = models.Single(static model => model.Id == "claude-opus-4-6");
        // Max is offered as the highest effort, as a session names it.
        CollectionAssert.AreEqual(
            new[] { AgentReasoningEffort.Low, AgentReasoningEffort.Medium, AgentReasoningEffort.High, AgentReasoningEffort.XHigh },
            opus.SupportedReasoningEfforts!.ToArray());
        Assert.IsTrue(AgentReasoningEffortCapability.IsListComplete(opus));
        var sonnet = models.Single(static model => model.Id == "claude-sonnet-4-5");
        Assert.IsNull(sonnet.SupportedReasoningEfforts);
        Assert.IsFalse(AgentReasoningEffortCapability.IsListComplete(sonnet));
    }

    [TestMethod]
    public async Task AnthropicModelProviderRuntime_ListModels_ReadsTheImageInputOfTheModelsApi()
    {
        await using var providerRuntime = CreateRuntime(new List<string>());

        var models = await providerRuntime.ListModelsAsync().ConfigureAwait(false);

        Assert.AreEqual(true, AgentImageInputCapability.Read(models.Single(static model => model.Id == "claude-haiku-4-5")));
        Assert.AreEqual(true, AgentImageInputCapability.Read(models.Single(static model => model.Id == "claude-opus-4-6")));
        Assert.AreEqual(false, AgentImageInputCapability.Read(models.Single(static model => model.Id == "claude-text-test")));
        // An endpoint that says nothing of images leaves the capability unknown.
        Assert.IsNull(AgentImageInputCapability.Read(models.Single(static model => model.Id == "claude-sonnet-4-5")));
    }

    [TestMethod]
    public async Task AnthropicModelProviderRuntime_SendsAPromptImageAsAnImageBlock()
    {
        var bodies = new List<string>();
        await using var providerRuntime = CreateRuntime(bodies);
        using var temp = TestTempDirectory.Create();
        var imagePath = Path.Combine(temp.Path, "red.png");
        await File.WriteAllBytesAsync(imagePath, Convert.FromBase64String(OnePixelPng)).ConfigureAwait(false);

        using var body = await SendAsync(providerRuntime, temp.Path, "claude-haiku-4-5", bodies,
            new AgentInputItem.Text("What color is this square?"), new AgentInputItem.LocalImage(imagePath, "red.png", "image/png")).ConfigureAwait(false);

        var content = body.RootElement.GetProperty("messages")[0].GetProperty("content").EnumerateArray().ToArray();
        var image = content.Single(static block => block.GetProperty("type").GetString() == "image");
        var source = image.GetProperty("source");
        Assert.AreEqual("base64", source.GetProperty("type").GetString());
        Assert.AreEqual("image/png", source.GetProperty("media_type").GetString());
        Assert.AreEqual(OnePixelPng, source.GetProperty("data").GetString());
    }

    private const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==";

    [TestMethod]
    public async Task AnthropicModelProviderRuntime_SendsTheEffortOnlyToAModelThatTakesIt()
    {
        var bodies = new List<string>();
        await using var providerRuntime = CreateRuntime(bodies);
        using var temp = TestTempDirectory.Create();

        // A model that takes no effort is sent none, and no thinking.
        using (var body = await SendAsync(providerRuntime, temp.Path, "claude-haiku-4-5", bodies).ConfigureAwait(false))
        {
            Assert.IsFalse(body.RootElement.TryGetProperty("output_config", out _));
            Assert.IsFalse(body.RootElement.TryGetProperty("thinking", out _));
        }

        // A model that takes adaptive thinking is sent it, with its effort.
        using (var body = await SendAsync(providerRuntime, temp.Path, "claude-opus-4-6", bodies).ConfigureAwait(false))
        {
            Assert.AreEqual("adaptive", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
            Assert.AreEqual("high", body.RootElement.GetProperty("output_config").GetProperty("effort").GetString());
        }

        // A model the endpoint says nothing of, older than adaptive thinking, is sent the thinking budget of the effort,
        // never adaptive thinking or output_config.effort.
        using (var body = await SendAsync(providerRuntime, temp.Path, "claude-sonnet-4-5", bodies).ConfigureAwait(false))
        {
            var thinking = body.RootElement.GetProperty("thinking");
            Assert.AreEqual("enabled", thinking.GetProperty("type").GetString());
            Assert.IsTrue(thinking.GetProperty("budget_tokens").GetInt64() >= 1024);
            Assert.IsFalse(body.RootElement.TryGetProperty("output_config", out _));
        }
    }

    private static Task<JsonDocument> SendAsync(AnthropicModelProviderRuntime providerRuntime, string workingDirectory, string modelId, List<string> bodies)
        => SendAsync(providerRuntime, workingDirectory, modelId, bodies, new AgentInputItem.Text("Hello"));

    private static async Task<JsonDocument> SendAsync(AnthropicModelProviderRuntime providerRuntime, string workingDirectory, string modelId, List<string> bodies,
        params AgentInputItem[] input)
    {
        bodies.Clear();
        await using var session = await providerRuntime.CreateSessionAsync(new AgentSessionCreateOptions
        {
            Model = modelId,
            ReasoningEffort = AgentReasoningEffort.High,
            WorkingDirectory = workingDirectory,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        }).ConfigureAwait(false);
        _ = await session.SendAsync(new AgentSendOptions
        {
            Input = new AgentInput(input),
        }).ConfigureAwait(false);

        Assert.AreEqual(1, bodies.Count, $"One request for {modelId}.");
        return JsonDocument.Parse(bodies[0]);
    }

    private static AnthropicModelProviderRuntime CreateRuntime(List<string> bodies)
        => new(new AnthropicModelProviderRuntimeOptions
        {
            StateRootPath = Path.GetTempPath(),
            Providers =
            {
                new AnthropicProviderOptions
                {
                    ProviderKey = "anthropic",
                    IsDefault = true,
                    ApiKey = "test-key",
                    BaseUri = new Uri("https://api.anthropic.test"),
                    HttpClient = new HttpClient(new StubHandler(request =>
                    {
                        if (request.RequestUri!.AbsolutePath == "/v1/models")
                        {
                            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ModelsJson, Encoding.UTF8, "application/json") };
                        }

                        Assert.AreEqual("/v1/messages", request.RequestUri.AbsolutePath);
                        bodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(MessageStream, Encoding.UTF8, "text/event-stream") };
                    })),
                },
            },
        });

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
