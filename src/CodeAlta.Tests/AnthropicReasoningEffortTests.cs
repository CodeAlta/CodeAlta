using System.Net;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Anthropic;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Compaction;

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
              "type": "model", "id": "claude-retired-test", "display_name": "Claude Retired Test", "created_at": "2024-02-01T00:00:00Z",
              "max_input_tokens": 200000, "max_tokens": 4096, "lifecycle": "retired", "deprecated_at": "2025-06-01T00:00:00Z", "retires_at": "2025-12-01T00:00:00Z"
            },
            {
              "type": "model", "id": "claude-opus-4-6", "display_name": "Claude Opus 4.6", "created_at": "2026-02-01T00:00:00Z",
              "max_input_tokens": 200000, "max_tokens": 64000,
              "lifecycle": "deprecated", "deprecated_at": "2026-09-01T00:00:00Z", "retires_at": "2027-03-01T00:00:00Z",
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
            },
            {
              "type": "model", "id": "claude-nothink-test", "display_name": "Claude No Thinking Test", "created_at": "2025-09-01T00:00:00Z",
              "max_input_tokens": 200000, "max_tokens": 64000,
              "capabilities": {
                "effort": { "supported": false },
                "thinking": { "supported": false, "types": { "adaptive": { "supported": false }, "enabled": { "supported": false }, "disabled": { "supported": true } } }
              }
            },
            {
              "type": "model", "id": "claude-sonnet-5-5", "display_name": "Claude Sonnet 5.5", "created_at": "2026-09-01T00:00:00Z",
              "max_input_tokens": 1000000, "max_tokens": 128000,
              "capabilities": {
                "effort": { "supported": true, "low": { "supported": true }, "medium": { "supported": true }, "high": { "supported": true }, "xhigh": { "supported": true }, "max": { "supported": true } },
                "image_input": { "supported": true },
                "thinking": { "supported": true, "types": { "adaptive": { "supported": true }, "enabled": { "supported": false }, "disabled": { "supported": true } } }
              }
            },
            {
              "type": "model", "id": "claude-opus-5-0-test", "display_name": "Claude Opus 5 (max only)", "created_at": "2026-09-01T00:00:00Z",
              "max_input_tokens": 1000000, "max_tokens": 128000,
              "capabilities": {
                "effort": { "supported": true, "low": { "supported": true }, "medium": { "supported": true }, "high": { "supported": true }, "xhigh": { "supported": false }, "max": { "supported": true } }
              }
            }
          ],
          "has_more": false, "first_id": "claude-haiku-4-5", "last_id": "claude-opus-5-0-test"
        }
        """;

    // The Models API of Kimi for Coding, as it answers: the efforts of a model under reasoning.effort, its context for the
    // plan of the account, and a model that always thinks and takes no effort.
    private const string KimiModelsJson =
        """
        {
          "data": [
            {
              "id": "kimi-for-coding", "created": 1761264000, "created_at": "2025-10-24T00:00:00Z", "object": "model", "display_name": "K2.8 Preview",
              "type": "model", "max_tokens": 32768, "context_length": 1048576, "supports_reasoning": true, "supports_image_in": true, "supports_thinking_type": "only",
              "think_efforts": { "support": true, "valid_efforts": ["low", "high", "max"], "default_effort": "max" },
              "reasoning": { "type": "only", "effort": { "valid": ["low", "high", "max"], "default": "max" } }, "limit": { "context": 1048576 }
            },
            {
              "id": "kimi-for-coding-highspeed", "created": 1761264000, "created_at": "2025-10-24T00:00:00Z", "object": "model", "display_name": "K2.7 Code Highspeed",
              "type": "model", "max_tokens": 32768, "context_length": 262144, "supports_reasoning": true, "supports_image_in": true, "supports_thinking_type": "only",
              "reasoning": { "type": "only" }, "limit": { "context": 262144 }
            },
            {
              "id": "k3.1-pro", "created": 1761264000, "created_at": "2025-10-24T00:00:00Z", "object": "model", "display_name": "K3.1 Pro",
              "type": "model", "max_tokens": 32768, "context_length": 262144, "supports_reasoning": true, "supports_image_in": true, "supports_thinking_type": "only",
              "think_efforts": { "support": true, "valid_efforts": ["low", "high", "max"], "default_effort": "high" },
              "reasoning": { "type": "only", "effort": { "valid": ["low", "high", "max"], "default": "high" } }, "limit": { "context": 262144 }
            }
          ],
          "object": "list", "first_id": "kimi-for-coding", "last_id": "k3.1-pro", "has_more": false
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
    public async Task AnthropicModelProviderRuntime_ContextSizesUseTheAdvertisedInputBudget()
    {
        await using var runtime = CreateRuntime([]);
        var models = await runtime.ListModelsAsync();
        foreach (var (id, tokens) in new[] { ("claude-haiku-4-5", 200000L), ("claude-sonnet-5-5", 1000000L) })
        {
            var model = models.Single(model => model.Id == id);
            Assert.IsNotNull(model.ContextSizes);
            Assert.IsTrue(model.ContextSizes.IsInputBudget);
            CollectionAssert.AreEqual(new[] { tokens }, model.ContextSizes.TokenCounts.ToArray(),
                "A maximum input limit is one verified budget, not evidence of a second context mode.");
            var selected = AgentModelContextResolver.Resolve(model, id, model.Provider!, tokens, null)!;
            Assert.AreEqual(tokens, AgentModelContextSizes.DefaultCapacity(model, null));
            Assert.AreEqual(tokens, AgentTokenBudgetResolver.Resolve(selected, AgentCompactionSettings.Default).InputContextLimit);
        }
    }

    [TestMethod]
    public async Task AnthropicModelProviderRuntime_ListModels_ReadsTheEffortsOfTheModelsApi()
    {
        await using var providerRuntime = CreateRuntime(new List<string>());

        var models = await providerRuntime.ListModelsAsync().ConfigureAwait(false);

        // A model that takes no effort but thinks with a budget offers the efforts the budget of thinking stands for.
        var haiku = models.Single(static model => model.Id == "claude-haiku-4-5");
        CollectionAssert.AreEqual(
            new[] { AgentReasoningEffort.Low, AgentReasoningEffort.Medium, AgentReasoningEffort.High, AgentReasoningEffort.XHigh },
            haiku.SupportedReasoningEfforts!.ToArray());
        Assert.IsTrue(AgentReasoningEffortCapability.IsListComplete(haiku));
        // A model that takes neither offers none, and the list is complete.
        var noThinking = models.Single(static model => model.Id == "claude-nothink-test");
        Assert.AreEqual(0, noThinking.SupportedReasoningEfforts!.Count);
        Assert.IsTrue(AgentReasoningEffortCapability.IsListComplete(noThinking));
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
    public async Task AnthropicModelProviderRuntime_ListModels_LeavesOutRetiredModels_AndMarksDeprecatedOnes()
    {
        await using var providerRuntime = CreateRuntime(new List<string>());

        var models = await providerRuntime.ListModelsAsync().ConfigureAwait(false);

        Assert.IsFalse(models.Any(static model => model.Id == "claude-retired-test"), "A retired model cannot run a request.");
        var opus = models.Single(static model => model.Id == "claude-opus-4-6");
        Assert.AreEqual("deprecated", opus.Capabilities!["status"]);
        Assert.AreEqual(DateTimeOffset.Parse("2027-03-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), opus.Capabilities["retiresAt"]);
        var haiku = models.Single(static model => model.Id == "claude-haiku-4-5");
        Assert.IsFalse(haiku.Capabilities!.ContainsKey("status"), "An active model has no status of its own.");
    }

    [TestMethod]
    public async Task AnthropicModelProviderRuntime_ARetiredModel_FailsTheTurnWithAClearMessage()
    {
        var executor = AnthropicModelProviderRuntime.CreateTurnExecutor(new AnthropicProviderOptions
        {
            ProviderKey = "anthropic",
            ApiKey = "test-key",
            BaseUri = new Uri("https://api.anthropic.test"),
            HttpClient = new HttpClient(new StubHandler(static _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"type":"error","error":{"type":"not_found_error","message":"model: claude-3-opus-20240229"}}""", Encoding.UTF8, "application/json"),
            })),
        });

        var failure = await Assert.ThrowsExactlyAsync<AgentTurnExecutionException>(() => executor.ExecuteTurnAsync(
            AnthropicStopReasonTests.CreateRequest() with { ModelId = "claude-3-opus-20240229", ModelInfo = new AgentModelInfo("claude-3-opus-20240229") },
            static (_, _) => ValueTask.CompletedTask)).ConfigureAwait(false);

        StringAssert.Contains(failure.Message, "claude-3-opus-20240229");
        StringAssert.Contains(failure.Message, "retired");
        StringAssert.Contains(failure.Message, "another model");
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

    [TestMethod]
    public async Task AnthropicModelProviderRuntime_SendsAPromptImageWithinTheLimitsOfTheApi()
    {
        var bodies = new List<string>();
        await using var providerRuntime = CreateRuntime(bodies);
        using var temp = TestTempDirectory.Create();
        // A screenshot larger than the 2000 pixels the API takes in a request of many images, and a bitmap, a format
        // the API does not read.
        var largePath = Path.Combine(temp.Path, "large.png");
        await File.WriteAllBytesAsync(largePath, AgentToolImagesTests.CreateImage(3000, 2000, SkiaSharp.SKEncodedImageFormat.Png)).ConfigureAwait(false);
        var bitmapPath = Path.Combine(temp.Path, "small.bmp");
        await File.WriteAllBytesAsync(bitmapPath, AgentToolImagesTests.TwoPixelBitmap).ConfigureAwait(false);
        var notAnImagePath = Path.Combine(temp.Path, "broken.png");
        await File.WriteAllTextAsync(notAnImagePath, "This is not an image.").ConfigureAwait(false);

        using var body = await SendAsync(providerRuntime, temp.Path, "claude-haiku-4-5", bodies,
            new AgentInputItem.Text("What is on these images?"),
            new AgentInputItem.LocalImage(largePath, "large.png", "image/png"),
            new AgentInputItem.LocalImage(bitmapPath, "small.bmp", "image/bmp"),
            new AgentInputItem.LocalImage(notAnImagePath, "broken.png", "image/png")).ConfigureAwait(false);

        var content = body.RootElement.GetProperty("messages")[0].GetProperty("content").EnumerateArray().ToArray();
        var images = content.Where(static block => block.GetProperty("type").GetString() == "image").Select(static block => block.GetProperty("source")).ToArray();
        Assert.AreEqual(2, images.Length);
        Assert.AreEqual("image/png", images[0].GetProperty("media_type").GetString());
        Assert.AreEqual((2000, 1333), AgentToolImagesTests.Measure(Convert.FromBase64String(images[0].GetProperty("data").GetString()!)));
        Assert.AreEqual("image/png", images[1].GetProperty("media_type").GetString());
        Assert.AreEqual((2, 2), AgentToolImagesTests.Measure(Convert.FromBase64String(images[1].GetProperty("data").GetString()!)));
        // An image that cannot be sent is named in the prompt instead.
        var text = string.Join("\n", content.Where(static block => block.GetProperty("type").GetString() == "text").Select(static block => block.GetProperty("text").GetString()));
        StringAssert.Contains(text, "[Image omitted: broken.png.");
    }

    [TestMethod]
    public async Task AnthropicModelProviderRuntime_SendsAnAttachedPdfAsADocument_ToAModelThatReadsPdfs()
    {
        var bodies = new List<string>();
        await using var providerRuntime = CreateRuntime(bodies);
        using var temp = TestTempDirectory.Create();
        var pdfPath = Path.Combine(temp.Path, "spec.pdf");
        var pdf = "%PDF-1.4\n1 0 obj << /Type /Catalog >> endobj\ntrailer << /Root 1 0 R >>\n%%EOF\n"u8.ToArray();
        await File.WriteAllBytesAsync(pdfPath, pdf).ConfigureAwait(false);

        var models = await providerRuntime.ListModelsAsync().ConfigureAwait(false);
        Assert.AreEqual(true, models.Single(static model => model.Id == "claude-haiku-4-5").Capabilities!["supportsPdfInput"]);

        using (var body = await SendAsync(providerRuntime, temp.Path, "claude-haiku-4-5", bodies,
            new AgentInputItem.Text("Summarize the spec."), new AgentInputItem.File(pdfPath, "spec.pdf")).ConfigureAwait(false))
        {
            var content = body.RootElement.GetProperty("messages")[0].GetProperty("content").EnumerateArray().ToArray();
            var document = content.Single(static block => block.GetProperty("type").GetString() == "document");
            Assert.AreEqual("application/pdf", document.GetProperty("source").GetProperty("media_type").GetString());
            Assert.AreEqual(Convert.ToBase64String(pdf), document.GetProperty("source").GetProperty("data").GetString());
        }

        // A model that does not read PDFs gets the path, as for any other file.
        using (var body = await SendAsync(providerRuntime, temp.Path, "claude-text-test", bodies,
            new AgentInputItem.Text("Summarize the spec."), new AgentInputItem.File(pdfPath, "spec.pdf")).ConfigureAwait(false))
        {
            var content = body.RootElement.GetProperty("messages")[0].GetProperty("content").EnumerateArray().ToArray();
            Assert.IsFalse(content.Any(static block => block.GetProperty("type").GetString() == "document"));
            StringAssert.Contains(string.Join("\n", content.Select(static block => block.GetProperty("text").GetString())), $"File: {pdfPath}");
        }
    }

    [TestMethod]
    public async Task AnthropicModelProviderRuntime_SendsAnImageUrlWithoutAnExtension()
    {
        var bodies = new List<string>();
        await using var providerRuntime = CreateRuntime(bodies);
        using var temp = TestTempDirectory.Create();

        using var body = await SendAsync(providerRuntime, temp.Path, "claude-haiku-4-5", bodies,
            new AgentInputItem.Text("What is on this picture?"), new AgentInputItem.ImageUrl("https://images.example.test/render?id=42")).ConfigureAwait(false);

        // The media type of the URL is not known: the image was dropped from the request.
        var content = body.RootElement.GetProperty("messages")[0].GetProperty("content").EnumerateArray().ToArray();
        var image = content.Single(static block => block.GetProperty("type").GetString() == "image");
        Assert.AreEqual("url", image.GetProperty("source").GetProperty("type").GetString());
        Assert.AreEqual("https://images.example.test/render?id=42", image.GetProperty("source").GetProperty("url").GetString());
    }

    private const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==";

    [TestMethod]
    public async Task AnthropicModelProviderRuntime_SendsTheEffortOnlyToAModelThatTakesIt()
    {
        var bodies = new List<string>();
        await using var providerRuntime = CreateRuntime(bodies);
        using var temp = TestTempDirectory.Create();

        // A model that takes no effort but thinks with a budget is sent the budget of the effort, and no effort.
        using (var body = await SendAsync(providerRuntime, temp.Path, "claude-haiku-4-5", bodies).ConfigureAwait(false))
        {
            Assert.IsFalse(body.RootElement.TryGetProperty("output_config", out _));
            var thinking = body.RootElement.GetProperty("thinking");
            Assert.AreEqual("enabled", thinking.GetProperty("type").GetString());
            Assert.AreEqual(16384, thinking.GetProperty("budget_tokens").GetInt64());
            Assert.IsTrue(thinking.GetProperty("budget_tokens").GetInt64() < body.RootElement.GetProperty("max_tokens").GetInt64());
        }

        // A model that takes neither is sent no thinking.
        using (var body = await SendAsync(providerRuntime, temp.Path, "claude-nothink-test", bodies).ConfigureAwait(false))
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

    [TestMethod]
    public async Task AnthropicModelProviderRuntime_OffersMaxAsItsOwnEffort_WhereTheModelListsXHighAndMax()
    {
        var bodies = new List<string>();
        await using var providerRuntime = CreateRuntime(bodies);
        using var temp = TestTempDirectory.Create();

        var models = await providerRuntime.ListModelsAsync().ConfigureAwait(false);
        CollectionAssert.AreEqual(
            new[] { AgentReasoningEffort.Low, AgentReasoningEffort.Medium, AgentReasoningEffort.High, AgentReasoningEffort.XHigh, AgentReasoningEffort.Max },
            models.Single(static model => model.Id == "claude-sonnet-5-5").SupportedReasoningEfforts!.ToArray());
        // A model that lists max and not xhigh keeps one highest effort, sent as max.
        CollectionAssert.DoesNotContain(models.Single(static model => model.Id == "claude-opus-5-0-test").SupportedReasoningEfforts!.ToArray(), AgentReasoningEffort.Max);

        using (var body = await SendAsync(providerRuntime, temp.Path, "claude-sonnet-5-5", bodies, AgentReasoningEffort.Max).ConfigureAwait(false))
        {
            Assert.AreEqual("max", body.RootElement.GetProperty("output_config").GetProperty("effort").GetString());
        }
    }

    [TestMethod]
    public async Task AnthropicModelProviderRuntime_SendsTheHighestEffortAsXHighWhereTheModelListsIt()
    {
        var bodies = new List<string>();
        await using var providerRuntime = CreateRuntime(bodies);
        using var temp = TestTempDirectory.Create();

        // A model that lists xhigh and max is sent xhigh, the cheaper of the two.
        using (var body = await SendAsync(providerRuntime, temp.Path, "claude-sonnet-5-5", bodies, AgentReasoningEffort.XHigh).ConfigureAwait(false))
        {
            Assert.AreEqual("xhigh", body.RootElement.GetProperty("output_config").GetProperty("effort").GetString());
        }

        // A model that lists max and not xhigh is sent max.
        using (var body = await SendAsync(providerRuntime, temp.Path, "claude-opus-5-0-test", bodies, AgentReasoningEffort.XHigh).ConfigureAwait(false))
        {
            Assert.AreEqual("max", body.RootElement.GetProperty("output_config").GetProperty("effort").GetString());
        }

        using (var body = await SendAsync(providerRuntime, temp.Path, "claude-opus-4-6", bodies, AgentReasoningEffort.XHigh).ConfigureAwait(false))
        {
            Assert.AreEqual("max", body.RootElement.GetProperty("output_config").GetProperty("effort").GetString());
        }
    }

    [TestMethod]
    public async Task AnthropicModelProviderRuntime_ListModels_ReadsTheEffortsKimiForCodingLists()
    {
        await using var providerRuntime = CreateRuntime(new List<string>(), KimiModelsJson);

        var models = await providerRuntime.ListModelsAsync().ConfigureAwait(false);

        // low, high and max: max is the highest effort, sent as max.
        var k31 = models.Single(static model => model.Id == "k3.1-pro");
        CollectionAssert.AreEqual(
            new[] { AgentReasoningEffort.Low, AgentReasoningEffort.High, AgentReasoningEffort.XHigh },
            k31.SupportedReasoningEfforts!.ToArray());
        Assert.IsTrue(AgentReasoningEffortCapability.IsListComplete(k31));
        Assert.AreEqual(AgentReasoningEffort.High, k31.DefaultReasoningEffort);
        // The context of the plan of the account, which the catalog of models.dev cannot know.
        Assert.AreEqual(262_144L, k31.Capabilities!["contextWindow"]);
        Assert.AreEqual(true, AgentImageInputCapability.Read(k31));
        Assert.AreEqual(AgentReasoningEffort.XHigh, models.Single(static model => model.Id == "kimi-for-coding").DefaultReasoningEffort);
        // A model that always thinks and names no effort offers none.
        var highspeed = models.Single(static model => model.Id == "kimi-for-coding-highspeed");
        Assert.AreEqual(0, highspeed.SupportedReasoningEfforts!.Count);
        Assert.IsTrue(AgentReasoningEffortCapability.IsListComplete(highspeed));
    }

    [TestMethod]
    public async Task AnthropicModelProviderRuntime_SendsAdaptiveThinkingAndItsEffort_ToAModelOfKimiForCoding()
    {
        var bodies = new List<string>();
        await using var providerRuntime = CreateRuntime(bodies, KimiModelsJson);
        using var temp = TestTempDirectory.Create();

        using (var body = await SendAsync(providerRuntime, temp.Path, "k3.1-pro", bodies, AgentReasoningEffort.Low).ConfigureAwait(false))
        {
            Assert.AreEqual("adaptive", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
            Assert.AreEqual("low", body.RootElement.GetProperty("output_config").GetProperty("effort").GetString());
        }

        using (var body = await SendAsync(providerRuntime, temp.Path, "k3.1-pro", bodies, AgentReasoningEffort.XHigh).ConfigureAwait(false))
        {
            Assert.AreEqual("max", body.RootElement.GetProperty("output_config").GetProperty("effort").GetString());
        }

        // A model that takes no effort is sent no thinking: it thinks all the same.
        using (var body = await SendAsync(providerRuntime, temp.Path, "kimi-for-coding-highspeed", bodies).ConfigureAwait(false))
        {
            Assert.IsFalse(body.RootElement.TryGetProperty("thinking", out _));
            Assert.IsFalse(body.RootElement.TryGetProperty("output_config", out _));
        }
    }

    private static Task<JsonDocument> SendAsync(AnthropicModelProviderRuntime providerRuntime, string workingDirectory, string modelId, List<string> bodies)
        => SendAsync(providerRuntime, workingDirectory, modelId, bodies, new AgentInputItem.Text("Hello"));

    private static Task<JsonDocument> SendAsync(AnthropicModelProviderRuntime providerRuntime, string workingDirectory, string modelId, List<string> bodies,
        AgentReasoningEffort reasoningEffort)
        => SendAsync(providerRuntime, workingDirectory, modelId, bodies, reasoningEffort, new AgentInputItem.Text("Hello"));

    private static Task<JsonDocument> SendAsync(AnthropicModelProviderRuntime providerRuntime, string workingDirectory, string modelId, List<string> bodies,
        params AgentInputItem[] input)
        => SendAsync(providerRuntime, workingDirectory, modelId, bodies, AgentReasoningEffort.High, input);

    private static async Task<JsonDocument> SendAsync(AnthropicModelProviderRuntime providerRuntime, string workingDirectory, string modelId, List<string> bodies,
        AgentReasoningEffort reasoningEffort, params AgentInputItem[] input)
    {
        bodies.Clear();
        await using var session = await providerRuntime.CreateSessionAsync(new AgentSessionCreateOptions
        {
            Model = modelId,
            ReasoningEffort = reasoningEffort,
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
        => CreateRuntime(bodies, ModelsJson);

    private static AnthropicModelProviderRuntime CreateRuntime(List<string> bodies, string modelsJson)
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
                            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(modelsJson, Encoding.UTF8, "application/json") };
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
