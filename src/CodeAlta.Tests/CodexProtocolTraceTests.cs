using CodeAlta.Agent;
using CodeAlta.Agent.OpenAI;
using CodeAlta.Agent.OpenAI.Codex;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

// The protocol trace of Codex names the tier that ran a response, which may not be the one asked, and keeps the small
// metadata event of Codex; the text of an answer stays out of it.
[TestClass]
public sealed class CodexProtocolTraceTests
{
    [TestMethod]
    public void TheTrace_NamesTheTierOfAResponse_AndKeepsTheMetadataOfCodex()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeAlta.CodexTrace." + Guid.NewGuid().ToString("N"));
        try
        {
            var trace = OpenAIProtocolTraceLogger.Create(new OpenAIProtocolTraceOptions { Enabled = true, StateRootPath = root }, CreateRequest())!;

            CodexSubscriptionHttpStreamSession.TraceEventDetails(trace, "response.created",
                """{"type":"response.created","response":{"id":"resp_1","status":"in_progress","model":"gpt-6-astra","service_tier":"auto","output":[]}}""");
            CodexSubscriptionHttpStreamSession.TraceEventDetails(trace, "response.metadata",
                """{"type":"response.metadata","response_id":"resp_1","metadata":{"generation":{}}}""");
            CodexSubscriptionHttpStreamSession.TraceEventDetails(trace, "response.output_text.delta", """{"type":"response.output_text.delta","delta":"the answer"}""");
            CodexSubscriptionHttpStreamSession.TraceEventDetails(trace, "response.completed",
                """{"type":"response.completed","response":{"id":"resp_1","status":"completed","model":"gpt-6-astra","service_tier":"default","output":[]}}""");

            var text = File.ReadAllText(trace.TraceFilePath);
            StringAssert.Contains(text, "<<< SSE response: status=in_progress model=gpt-6-astra service_tier=auto");
            StringAssert.Contains(text, "<<< SSE response: status=completed model=gpt-6-astra service_tier=default");
            StringAssert.Contains(text, "\"metadata\":{\"generation\":{}}");
            Assert.DoesNotContain("the answer", text, "the text of an answer is not written");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static AgentTurnRequest CreateRequest()
        => new()
        {
            Provider = new ModelProviderRuntimeDescriptor
            {
                ProtocolFamily = "codex", ProviderKey = "codex", DisplayName = "Codex", TransportKind = AgentTransportKind.OpenAIResponses,
            },
            ProviderId = new ModelProviderId("codex"),
            SessionId = "session-1",
            RunId = new AgentRunId("run-1"),
            ModelId = "gpt-6-astra",
            Conversation = [],
            Tools = [],
            State = new AgentSessionState { SessionId = "session-1", ProtocolFamily = "codex", ProviderKey = "codex", UpdatedAt = DateTimeOffset.UtcNow },
        };
}
