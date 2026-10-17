using System.Text.Json;
using CodeAlta.Agent.OpenAI.Codex;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ChatGptPlanRequestNormalizerTests
{
    [TestMethod]
    public void HttpRequest_EnforcesPlanContractAndGroupsLocalTools()
    {
        var payload = BinaryData.FromString("""
            {
              "model":"account-model", "store":true, "stream":false,
              "previous_response_id":"previous", "client_metadata":{"session_id":"session"},
              "background":true, "conversation":"conversation", "max_output_tokens":100,
              "max_tool_calls":10, "metadata":{}, "moderation":{}, "multi_agent":{},
              "prompt":{}, "prompt_cache_retention":"24h", "safety_identifier":"id",
              "temperature":0.5, "top_logprobs":5, "top_p":0.9, "truncation":"auto", "user":"user",
              "input":[{"type":"message", "role":"system", "content":"instructions"}],
              "tools":[{"type":"function", "name":"shell", "parameters":{"type":"object"}}],
              "parallel_tool_calls":true, "service_tier":"priority"
            }
            """);

        using var document = JsonDocument.Parse(ChatGptPlanRequestNormalizer.Normalize(payload, isHttp: true).ToMemory());
        var root = document.RootElement;

        Assert.IsFalse(root.GetProperty("store").GetBoolean());
        Assert.IsTrue(root.GetProperty("stream").GetBoolean());
        CollectionAssert.AreEquivalent(
            new[] { "model", "input", "tools", "parallel_tool_calls", "service_tier", "store", "stream" },
            root.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.AreEqual("developer", root.GetProperty("input")[0].GetProperty("role").GetString());
        var toolNamespace = root.GetProperty("tools")[0];
        Assert.AreEqual("namespace", toolNamespace.GetProperty("type").GetString());
        Assert.AreEqual("codealta", toolNamespace.GetProperty("name").GetString());
        Assert.AreEqual("shell", toolNamespace.GetProperty("tools")[0].GetProperty("name").GetString());
    }

    [TestMethod]
    public void WebSocketRequest_PreservesConnectionLocalContinuationButNotHttpStreamFlags()
    {
        var payload = BinaryData.FromString("""{"model":"model", "input":[], "previous_response_id":"previous", "store":true, "stream":true}""");

        using var document = JsonDocument.Parse(ChatGptPlanRequestNormalizer.Normalize(payload, isHttp: false).ToMemory());

        Assert.AreEqual("previous", document.RootElement.GetProperty("previous_response_id").GetString());
        Assert.IsFalse(document.RootElement.GetProperty("store").GetBoolean());
        Assert.IsFalse(document.RootElement.TryGetProperty("stream", out _));
    }

    [TestMethod]
    public void Request_KeepsWebSearch_BesideTheNamespaceOfTheLocalTools()
    {
        var payload = BinaryData.FromString("""
            {"model":"model", "input":[], "tools":[
              {"type":"function", "name":"shell", "parameters":{"type":"object"}},
              {"type":"web_search", "search_context_size":"low"},
              {"type":"custom", "name":"apply_patch"}]}
            """);

        using var document = JsonDocument.Parse(ChatGptPlanRequestNormalizer.Normalize(payload, isHttp: true).ToMemory());
        var tools = document.RootElement.GetProperty("tools");

        // The endpoint takes web search at the top level only: inside the namespace it wants functions and custom tools.
        Assert.AreEqual(2, tools.GetArrayLength());
        CollectionAssert.AreEqual(new[] { "shell", "apply_patch" }, tools[0].GetProperty("tools").EnumerateArray().Select(static tool => tool.GetProperty("name").GetString()).ToArray());
        Assert.AreEqual("web_search", tools[1].GetProperty("type").GetString());
        Assert.AreEqual("low", tools[1].GetProperty("search_context_size").GetString());

        // Web search alone: no empty namespace.
        using var alone = JsonDocument.Parse(ChatGptPlanRequestNormalizer.Normalize(BinaryData.FromString("""{"model":"model", "input":[], "tools":[{"type":"web_search"}]}"""), isHttp: true).ToMemory());
        Assert.AreEqual("web_search", alone.RootElement.GetProperty("tools").EnumerateArray().Single().GetProperty("type").GetString());
    }

    [TestMethod]
    [DataRow("image_generation")]
    [DataRow("file_search")]
    [DataRow("code_interpreter")]
    [DataRow("computer_use_preview")]
    [DataRow("mcp")]
    [DataRow("tool_search")]
    // The endpoint answers "Unsupported tool type" for it on every model of the plan, though one of its messages names it.
    [DataRow("programmatic_tool_calling")]
    public void Request_RejectsUnsupportedHostedTools(string toolType)
    {
        var payload = BinaryData.FromString($$"""{"model":"model", "input":[], "tools":[{"type":"function", "name":"shell"}, {"type":"{{toolType}}"}]}""");

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => ChatGptPlanRequestNormalizer.Normalize(payload, isHttp: true));
        StringAssert.Contains(exception.Message, toolType);
    }
}
