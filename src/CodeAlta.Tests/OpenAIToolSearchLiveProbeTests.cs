using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Agent.OpenAI;
using CodeAlta.Agent.OpenAI.Codex;

namespace CodeAlta.Tests;

/// <summary>
/// Opt-in probes of the tool search of the Responses API: deferred functions in a namespace and a <c>tool_search</c>
/// tool run by the client. Each costs a few cents.
/// <list type="bullet">
/// <item>The ChatGPT subscription, with <c>CODEALTA_TEST_CODEX_SUBSCRIPTION_ROOT</c> (the state folder whose
/// <c>auth/openai-codex-subscription/codex.credential</c> is signed in, such as <c>~/.alta</c>) and
/// <c>CODEALTA_TEST_CODEX_MODEL</c> (a model of the subscription). It refuses <c>tool_search</c> (October 2026), so
/// CodeAlta keeps its own <c>tool_search</c> function there; the probe fails the day it is taken.</item>
/// <item>A platform API key, with <c>CODEALTA_TEST_OPENAI_API_KEY</c> and <c>CODEALTA_TEST_OPENAI_MODEL</c>
/// (<c>gpt-5.4</c> or later): the whole round of a search, its output and the call of the loaded tool.</item>
/// </list>
/// </summary>
[TestClass]
public sealed class OpenAIToolSearchLiveProbeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Live_TheSubscriptionEndpoint_RefusesAClientToolSearch()
    {
        var root = Environment.GetEnvironmentVariable("CODEALTA_TEST_CODEX_SUBSCRIPTION_ROOT");
        if (string.IsNullOrWhiteSpace(root))
        {
            Assert.Inconclusive("CODEALTA_TEST_CODEX_SUBSCRIPTION_ROOT is not set.");
            return;
        }

        var model = Environment.GetEnvironmentVariable("CODEALTA_TEST_CODEX_MODEL") is { Length: > 0 } named ? named : "gpt-6-luna";
        using var http = new HttpClient();
        var auth = new OpenAICodexSubscriptionAuthManager(new FileOpenAICodexSubscriptionCredentialStore(root), new OpenAICodexSubscriptionOAuthClient(http), "codex");
        var identity = await CodexSubscriptionHttpRequestFactory.CreateIdentityAsync(auth, new OpenAICodexSubscriptionOptions(), TestContext.CancellationToken);
        var first = await PostAsync(http, request => CodexSubscriptionHttpRequestFactory.ApplyIdentity(request, identity), Request(model, Input()), TestContext.CancellationToken);
        TestContext.WriteLine($"subscription: {first.Status} {first.Summary}");

        Assert.AreEqual(400, first.Status, "the subscription takes tool_search now: the OpenAI provider can use it (plan step 4.6)");
        StringAssert.Contains(first.Error, "subscription_sharing_unsupported_capability");
    }

    [TestMethod]
    public async Task Live_TheApiTakesAClientToolSearch_AndTheLoadedTool()
    {
        var key = Environment.GetEnvironmentVariable("CODEALTA_TEST_OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            Assert.Inconclusive("CODEALTA_TEST_OPENAI_API_KEY is not set.");
            return;
        }

        var model = Environment.GetEnvironmentVariable("CODEALTA_TEST_OPENAI_MODEL") is { Length: > 0 } named ? named : "gpt-5.4-mini";
        using var http = new HttpClient();
        void Authorize(HttpRequestMessage request) => request.Headers.Authorization = new("Bearer", key);
        var input = Input();
        var first = await PostAsync(http, Authorize, Request(model, input), TestContext.CancellationToken);
        TestContext.WriteLine($"first: {first.Status} {first.Summary}");
        Assert.IsTrue(first.Ok, first.Error);
        var call = first.Output.FirstOrDefault(item => item?["type"]?.GetValue<string>() == "tool_search_call");
        Assert.IsNotNull(call, "the model searched for a tool");
        TestContext.WriteLine($"tool_search_call: {call.ToJsonString()}");

        // Back with the tools found: the call of the model, then its output with the loaded tool.
        foreach (var item in first.Output)
        {
            if (item is not null && item["type"]?.GetValue<string>() is "tool_search_call" or "reasoning")
            {
                input.Add(item.DeepClone());
            }
        }

        input.Add(new JsonObject
        {
            ["type"] = "tool_search_output",
            ["call_id"] = call["call_id"]?.DeepClone(),
            ["execution"] = "client",
            ["status"] = "completed",
            ["tools"] = new JsonArray { Namespace(deferred: true) },
        });
        var second = await PostAsync(http, Authorize, Request(model, input), TestContext.CancellationToken);
        TestContext.WriteLine($"second: {second.Status} {second.Summary}");
        Assert.IsTrue(second.Ok, second.Error);
        var function = second.Output.FirstOrDefault(item => item?["type"]?.GetValue<string>() == "function_call");
        Assert.IsNotNull(function, "the model called the loaded tool");
        TestContext.WriteLine($"function_call: {function.ToJsonString()}");
    }

    private static JsonArray Input()
        => new()
        {
            new JsonObject { ["role"] = "developer", ["content"] = "Tools are deferred: find the one you need with tool_search, then call it. Answer in one short sentence." },
            new JsonObject { ["role"] = "user", ["content"] = "Post the message 'build is green' on the #general channel of slack." },
        };

    private static JsonObject Request(string model, JsonArray input)
        => new()
        {
            ["model"] = model,
            ["instructions"] = "You are a test agent.",
            ["input"] = input.DeepClone(),
            ["store"] = false,
            ["stream"] = true,
            ["tools"] = new JsonArray
            {
                Namespace(deferred: true),
                new JsonObject
                {
                    ["type"] = "tool_search",
                    ["execution"] = "client",
                    ["description"] = "Finds the tools of CodeAlta that are not loaded yet, by what they do.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject { ["query"] = new JsonObject { ["type"] = "string", ["description"] = "What the tool should do." } },
                        ["required"] = new JsonArray { "query" },
                        ["additionalProperties"] = false,
                    },
                },
            },
        };

    private static JsonObject Namespace(bool deferred)
    {
        var tool = new JsonObject
        {
            ["type"] = "function",
            ["name"] = "slack_post_message",
            ["description"] = "Posts a message on a Slack channel.",
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["channel"] = new JsonObject { ["type"] = "string" },
                    ["text"] = new JsonObject { ["type"] = "string" },
                },
                ["required"] = new JsonArray { "channel", "text" },
                ["additionalProperties"] = false,
            },
            ["strict"] = true,
        };
        if (deferred)
        {
            tool["defer_loading"] = true;
        }

        return new JsonObject { ["type"] = "namespace", ["name"] = "mcp_slack", ["description"] = "The tools of the Slack MCP server.", ["tools"] = new JsonArray { tool } };
    }

    private static async Task<ProbeResult> PostAsync(HttpClient http, Action<HttpRequestMessage> authorize, JsonObject body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, CodexSubscriptionHttpRequestFactory.ResolveEndpoint(null, "responses"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        authorize(request);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new ProbeResult(false, (int)response.StatusCode, [], text.Length > 2000 ? text[..2000] : text);
        }

        // The stream ends with response.completed, whose response has every output item.
        JsonArray output = [];
        string? failure = null;
        foreach (var line in text.Split('\n'))
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal) || line.Length <= 6)
            {
                continue;
            }

            JsonNode? data;
            try { data = JsonNode.Parse(line[6..]); }
            catch (JsonException) { continue; }

            switch (data?["type"]?.GetValue<string>())
            {
                case "response.completed":
                    output = data["response"]?["output"]?.AsArray().DeepClone().AsArray() ?? [];
                    break;
                case "response.failed" or "error":
                    failure = data.ToJsonString();
                    break;
            }
        }

        return new ProbeResult(failure is null, (int)response.StatusCode, output, failure);
    }

    private sealed record ProbeResult(bool Ok, int Status, JsonArray Output, string? Error)
    {
        public string Summary => Ok ? string.Join(", ", Output.Select(item => item?["type"]?.GetValue<string>())) : (Error ?? "").ReplaceLineEndings(" ");
    }
}
