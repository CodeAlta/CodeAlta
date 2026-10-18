using System.Text.Json;
using CodeAlta.Agent;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AgentInputTests
{
    [TestMethod]
    public void TheIdentityOfASendIsRecordedWithTheInputOnlyWhenThereIsOne()
    {
        var sent = new AgentInput([new AgentInputItem.Text("Hello")]) { ClientRequestId = "send-1" };
        var json = JsonSerializer.Serialize(sent, AgentJsonSerializerContext.Default.AgentInput);
        StringAssert.Contains(json, "\"clientRequestId\":\"send-1\"");
        Assert.AreEqual("send-1", JsonSerializer.Deserialize(json, AgentJsonSerializerContext.Default.AgentInput)!.ClientRequestId);
        Assert.IsFalse(JsonSerializer.Serialize(AgentInput.Text("Hello"), AgentJsonSerializerContext.Default.AgentInput).Contains("clientRequestId", StringComparison.Ordinal));
        Assert.AreEqual("send-1", (sent with { Items = [] }).ClientRequestId);
    }
}
