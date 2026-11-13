using System.Text.Json;
using CodeAlta.Fleet.NodeApi;

namespace CodeAlta.Fleet.Tests;

/// <summary>The result of <c>sessions.state</c> on the wire, between nodes and desktops of different versions.</summary>
[TestClass]
public sealed class FleetSessionStateContractTests
{
    private static readonly Guid Runtime = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

    [TestMethod]
    public void SessionState_CarriesTheSuggestionAndItsRun_RoundTrip()
    {
        var state = new FleetSessionState("s1", Runtime, 3, null, false) { PromptSuggestion = "commit it", PromptSuggestionRunId = "run-1" };

        var json = JsonSerializer.Serialize(state, FleetNodeJsonContext.Default.FleetSessionState);
        var read = JsonSerializer.Deserialize(json, FleetNodeJsonContext.Default.FleetSessionState);

        StringAssert.Contains(json, "\"promptSuggestion\":\"commit it\"");
        StringAssert.Contains(json, "\"promptSuggestionRunId\":\"run-1\"");
        Assert.AreEqual(state, read);
    }

    [TestMethod]
    public void SessionState_WithoutASuggestion_WritesNoneOfItsMembers()
    {
        var json = JsonSerializer.Serialize(new FleetSessionState("s1", Runtime, null, "run-2", true), FleetNodeJsonContext.Default.FleetSessionState);

        Assert.IsFalse(json.Contains("promptSuggestion", StringComparison.Ordinal), json);
    }

    [TestMethod]
    public void SessionState_OfAnOlderNode_ReadsWithoutASuggestion()
    {
        var json = $$"""{"sessionId":"s1","runtimeInstanceId":"{{Runtime}}","attachmentGeneration":2,"activeRunId":null,"queueDrainInProgress":false}""";

        var read = JsonSerializer.Deserialize(json, FleetNodeJsonContext.Default.FleetSessionState);

        Assert.IsNotNull(read);
        Assert.AreEqual(("s1", 2L), (read.SessionId, read.AttachmentGeneration));
        Assert.AreEqual((null, null), (read.PromptSuggestion, read.PromptSuggestionRunId));
    }

    [TestMethod]
    public void SessionState_OfANewerNode_ReadsWithMembersItDoesNotKnow()
    {
        // An older desktop reads a newer node the same way: members it does not know are passed over.
        var json = $$$"""{"sessionId":"s1","runtimeInstanceId":"{{{Runtime}}}","queueDrainInProgress":false,"promptSuggestion":"x","somethingLater":{"a":1}}""";

        var read = JsonSerializer.Deserialize(json, FleetNodeJsonContext.Default.FleetSessionState);

        Assert.AreEqual("x", read?.PromptSuggestion);
    }
}
