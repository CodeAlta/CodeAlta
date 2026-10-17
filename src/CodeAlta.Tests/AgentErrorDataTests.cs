using System.Net;
using System.Text.Json;
using CodeAlta.Agent;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AgentErrorDataTests
{
    [TestMethod]
    public void Collect_KeepsTheStatusAndTheEntriesOfAnException()
    {
        var exception = new HttpRequestException("Refused.", null, HttpStatusCode.TooManyRequests);
        AgentErrorData.Set(exception, AgentErrorData.Code, "rate_limit_exceeded");
        AgentErrorData.Set(exception, AgentErrorData.Attempts, 3);
        AgentErrorData.Set(exception, AgentErrorData.Param, "   ");
        exception.Data[AgentErrorData.Transport] = new object();

        var data = AgentErrorData.Collect(exception)!;

        Assert.AreEqual("429", data[AgentErrorData.HttpStatus]);
        Assert.AreEqual("rate_limit_exceeded", data[AgentErrorData.Code]);
        Assert.AreEqual("3", data[AgentErrorData.Attempts]);
        Assert.IsFalse(data.ContainsKey(AgentErrorData.Param));
        Assert.IsFalse(data.ContainsKey(AgentErrorData.Transport));
        Assert.AreEqual(3, data.Count);
    }

    [TestMethod]
    public void Collect_LeavesOutTheEntriesAProviderKeepsForItself()
    {
        // The raw headers of a response (rate limits, organization, turn state, cookies) are not recorded.
        var exception = new InvalidOperationException("Failed.");
        exception.Data["Authorization"] = "Bearer abc";
        exception.Data["x-codex-turn-state"] = "state";
        exception.Data["openai-organization"] = "org-1";
        exception.Data[42] = "not a string key";
        AgentErrorData.Set(exception, AgentErrorData.Code, "server_error");

        var data = AgentErrorData.Collect(exception)!;

        CollectionAssert.AreEqual(new[] { AgentErrorData.Code }, data.Keys.ToArray());
    }

    [TestMethod]
    public void Collect_RedactsSecretsAndBoundsValues()
    {
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwiZW1haWwiOiJ1c2VyQGV4YW1wbGUuY29tIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U";
        var exception = new InvalidOperationException("Failed.");
        AgentErrorData.Set(exception, AgentErrorData.Type, "sent Bearer abc.def-123 to the server");
        AgentErrorData.Set(exception, AgentErrorData.Param, jwt);
        // A token that the limit would cut is redacted before the cut.
        AgentErrorData.Set(exception, AgentErrorData.RequestId, new string('r', AgentErrorData.MaxValueLength - 40) + " " + jwt);
        AgentErrorData.Set(exception, AgentErrorData.Payload, new string('p', AgentErrorData.MaxPayloadLength + 100));
        AgentErrorData.Set(exception, AgentErrorData.Code, new string('c', AgentErrorData.MaxValueLength + 100));

        var data = AgentErrorData.Collect(exception)!;

        Assert.AreEqual($"sent Bearer {AgentErrorData.Redacted} to the server", data[AgentErrorData.Type]);
        Assert.AreEqual(AgentErrorData.Redacted, data[AgentErrorData.Param]);
        Assert.IsFalse(data[AgentErrorData.RequestId].Contains("eyJ", StringComparison.Ordinal));
        StringAssert.EndsWith(data[AgentErrorData.RequestId], AgentErrorData.Redacted);
        Assert.AreEqual(AgentErrorData.MaxPayloadLength + 1, data[AgentErrorData.Payload].Length);
        Assert.AreEqual(AgentErrorData.MaxValueLength + 1, data[AgentErrorData.Code].Length);
    }

    [TestMethod]
    public void SetIfMissing_KeepsTheEntryOfAWrappedException()
    {
        var inner = AgentErrorData.Set(new InvalidOperationException("inner"), AgentErrorData.Transport, "websocket");
        var outer = new InvalidOperationException("outer", inner);

        AgentErrorData.SetIfMissing(outer, AgentErrorData.Transport, "http");
        AgentErrorData.SetIfMissing(outer, AgentErrorData.Attempts, "2");

        Assert.IsFalse(outer.Data.Contains(AgentErrorData.Transport));
        Assert.AreEqual("2", outer.Data[AgentErrorData.Attempts]);
    }

    [TestMethod]
    public void ExceptionInfo_KeepsTheEntriesOfTheChainThroughTheJournal()
    {
        var inner = AgentErrorData.Set(new InvalidOperationException("inner"), AgentErrorData.Code, "server_error");
        AgentErrorData.Set(inner, AgentErrorData.Attempts, "1");
        var outer = AgentErrorData.Set(new InvalidOperationException("outer", inner), AgentErrorData.Attempts, "6");
        var errorEvent = new AgentErrorEvent(new("codex"), "session", DateTimeOffset.UnixEpoch, "outer", outer);

        var json = JsonSerializer.Serialize<AgentEvent>(errorEvent, AgentJsonSerializerContext.Default.AgentEvent);
        var read = (AgentErrorEvent)JsonSerializer.Deserialize(json, AgentJsonSerializerContext.Default.AgentEvent)!;

        var data = read.ExceptionInfo!.CollectData();
        Assert.AreEqual("server_error", data[AgentErrorData.Code]);
        // The outer exception says how many requests the turn sent: it wins over the inner one.
        Assert.AreEqual("6", data[AgentErrorData.Attempts]);
    }

    [TestMethod]
    public void WriteDiagnostics_WritesTheMessageAndTheProviderFields()
    {
        var inner = AgentErrorData.Set(new HttpRequestException("Overloaded.", null, HttpStatusCode.ServiceUnavailable), AgentErrorData.Code, "server_is_overloaded");
        AgentErrorData.Set(inner, AgentErrorData.RequestId, "req_9");
        var exception = new InvalidOperationException("The request failed with Bearer abc", inner);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            AgentErrorData.WriteDiagnostics(writer, exception);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        var root = document.RootElement;
        Assert.AreEqual($"InvalidOperationException: The request failed with Bearer {AgentErrorData.Redacted}", root.GetProperty("error").GetString());
        Assert.AreEqual("server_is_overloaded", root.GetProperty("providerCode").GetString());
        Assert.AreEqual("503", root.GetProperty(AgentErrorData.HttpStatus).GetString());
        Assert.AreEqual("req_9", root.GetProperty(AgentErrorData.RequestId).GetString());
    }
}
