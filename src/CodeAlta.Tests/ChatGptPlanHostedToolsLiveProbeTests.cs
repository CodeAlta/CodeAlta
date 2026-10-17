using System.Text;
using System.Text.Json.Nodes;
using CodeAlta.Agent.OpenAI;
using CodeAlta.Agent.OpenAI.Codex;

namespace CodeAlta.Tests;

/// <summary>
/// Opt-in probes of the hosted tools the ChatGPT-subscription endpoint takes, with <c>CODEALTA_TEST_CODEX_SUBSCRIPTION_ROOT</c>
/// (the state folder whose <c>auth/openai-codex-subscription/codex.credential</c> is signed in, such as <c>~/.alta</c>)
/// and <c>CODEALTA_TEST_CODEX_MODEL</c> (a model of the subscription). Each request costs a little of the subscription.
/// </summary>
[TestClass]
public sealed class ChatGptPlanHostedToolsLiveProbeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Live_TheSubscriptionTakesWebSearch_AsTheNormalizerWritesIt()
    {
        var result = await PostAsync(ChatGptPlanRequestNormalizer.Normalize(BinaryData.FromString(Request("""{"type":"web_search"}""")), isHttp: true));
        if (result is null) return;

        Assert.AreEqual(200, result.Value.Status, result.Value.Text);
        Assert.IsFalse(result.Value.Failed, result.Value.Text);
    }

    [TestMethod]
    public async Task Live_TheSubscriptionRefusesProgrammaticToolCalling()
    {
        // Sent as the Responses API documents it: the hosted tool at the top level, the function allowed to the program.
        var tools = JsonNode.Parse(ChatGptPlanRequestNormalizer.Normalize(BinaryData.FromString(Request(null)), isHttp: true).ToString())!.AsObject();
        tools["tools"]![0]!["tools"]![0]!["allowed_callers"] = new JsonArray { "direct", "programmatic" };
        tools["tools"]!.AsArray().Add(new JsonObject { ["type"] = "programmatic_tool_calling" });
        var result = await PostAsync(BinaryData.FromString(tools.ToJsonString()));
        if (result is null) return;

        Assert.AreEqual(400, result.Value.Status, "the subscription takes programmatic_tool_calling now: ChatGptPlanRequestNormalizer can let it through");
        StringAssert.Contains(result.Value.Text, "Unsupported tool type: programmatic_tool_calling");
    }

    private static string Request(string? hostedTool)
        => $$$"""
            {"model":"{{{Model}}}", "instructions":"You are a test agent.",
             "input":[{"role":"user", "content":"Answer with the single word: ok"}],
             "tools":[{"type":"function", "name":"echo", "description":"Echoes a text.", "strict":true,
               "parameters":{"type":"object", "properties":{"text":{"type":"string"}}, "required":["text"], "additionalProperties":false}}{{{(hostedTool is null ? "" : ", " + hostedTool)}}}]}
            """;

    private static string Model => Environment.GetEnvironmentVariable("CODEALTA_TEST_CODEX_MODEL") is { Length: > 0 } named ? named : "gpt-6-luna";

    private async Task<(int Status, bool Failed, string Text)?> PostAsync(BinaryData body)
    {
        var root = Environment.GetEnvironmentVariable("CODEALTA_TEST_CODEX_SUBSCRIPTION_ROOT");
        if (string.IsNullOrWhiteSpace(root))
        {
            Assert.Inconclusive("CODEALTA_TEST_CODEX_SUBSCRIPTION_ROOT is not set.");
            return null;
        }

        using var http = new HttpClient();
        var auth = new OpenAICodexSubscriptionAuthManager(new FileOpenAICodexSubscriptionCredentialStore(root), new OpenAICodexSubscriptionOAuthClient(http), "codex");
        var identity = await CodexSubscriptionHttpRequestFactory.CreateIdentityAsync(auth, new OpenAICodexSubscriptionOptions(), TestContext.CancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, CodexSubscriptionHttpRequestFactory.ResolveEndpoint(null, "responses"))
        {
            Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json"),
        };
        CodexSubscriptionHttpRequestFactory.ApplyIdentity(request, identity);
        using var response = await http.SendAsync(request, TestContext.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.CancellationToken);
        // A refusal after the start of the stream comes as an event of the stream.
        var failed = text.Contains("\"response.failed\"", StringComparison.Ordinal) || text.Contains("\"type\":\"error\"", StringComparison.Ordinal);
        TestContext.WriteLine($"{(int)response.StatusCode} failed={failed} {text.ReplaceLineEndings(" ")[..Math.Min(400, text.Length)]}");
        return ((int)response.StatusCode, failed, text);
    }
}
