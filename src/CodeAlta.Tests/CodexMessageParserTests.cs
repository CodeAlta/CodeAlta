using System.Text.Json;
using CodeAlta.CodexSdk;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodexMessageParserTests
{
    private static JsonElement ParseJsonElement(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [TestMethod]
    public void ParseNotification_HandlesThreadStatusChanged()
    {
        var options = CodexClient.CreateJsonSerializerOptions();
        var parameters = ParseJsonElement("""{ "threadId": "thr_123", "status": { "type": "notLoaded" } }""");

        var notification = CodexMessageParser.ParseNotification("thread/status/changed", parameters, options);

        Assert.IsNotNull(notification);
        Assert.IsInstanceOfType(notification, typeof(CodexNotification.ThreadStatusChanged));
        var typed = (CodexNotification.ThreadStatusChanged)notification;
        Assert.AreEqual("thr_123", typed.Data.ThreadId);
        Assert.IsInstanceOfType(typed.Data.Status, typeof(ThreadStatus.NotLoadedThreadStatus));
    }

    [TestMethod]
    public void ParseNotification_HandlesThreadClosed()
    {
        var options = CodexClient.CreateJsonSerializerOptions();
        var parameters = ParseJsonElement("""{ "threadId": "thr_123" }""");

        var notification = CodexMessageParser.ParseNotification("thread/closed", parameters, options);

        Assert.IsNotNull(notification);
        Assert.IsInstanceOfType(notification, typeof(CodexNotification.ThreadClosed));
        var typed = (CodexNotification.ThreadClosed)notification;
        Assert.AreEqual("thr_123", typed.Data.ThreadId);
    }

    [TestMethod]
    public void ParseNotification_HandlesThreadRealtimeNotifications()
    {
        var options = CodexClient.CreateJsonSerializerOptions();

        var started = CodexMessageParser.ParseNotification(
            "thread/realtime/started",
            ParseJsonElement("""{ "threadId": "thr_123", "sessionId": "sess_1" }"""),
            options);
        Assert.IsInstanceOfType(started, typeof(CodexNotification.ThreadRealtimeStarted));

        var itemAdded = CodexMessageParser.ParseNotification(
            "thread/realtime/itemAdded",
            ParseJsonElement("""{ "threadId": "thr_123", "item": { "kind": "test", "value": 1 } }"""),
            options);
        Assert.IsInstanceOfType(itemAdded, typeof(CodexNotification.ThreadRealtimeItemAdded));

        var audioDelta = CodexMessageParser.ParseNotification(
            "thread/realtime/outputAudio/delta",
            ParseJsonElement("""{ "threadId": "thr_123", "audio": { "data": "AA==", "sampleRate": 48000, "numChannels": 2, "samplesPerChannel": 120 } }"""),
            options);
        Assert.IsInstanceOfType(audioDelta, typeof(CodexNotification.ThreadRealtimeOutputAudioDelta));

        var error = CodexMessageParser.ParseNotification(
            "thread/realtime/error",
            ParseJsonElement("""{ "threadId": "thr_123", "message": "boom" }"""),
            options);
        Assert.IsInstanceOfType(error, typeof(CodexNotification.ThreadRealtimeError));

        var closed = CodexMessageParser.ParseNotification(
            "thread/realtime/closed",
            ParseJsonElement("""{ "threadId": "thr_123", "reason": "done" }"""),
            options);
        Assert.IsInstanceOfType(closed, typeof(CodexNotification.ThreadRealtimeClosed));
    }

    [TestMethod]
    public void ParseNotification_HandlesFuzzyFileSearchNotifications()
    {
        var options = CodexClient.CreateJsonSerializerOptions();

        var updated = CodexMessageParser.ParseNotification(
            "fuzzyFileSearch/sessionUpdated",
            ParseJsonElement(
                """
                {
                  "sessionId": "s1",
                  "query": "Program",
                  "files": [
                    { "file_name": "Program.cs", "path": "src/Program.cs", "root": "C:\\\\repo", "score": 10, "indices": [0,1] }
                  ]
                }
                """),
            options);
        Assert.IsInstanceOfType(updated, typeof(CodexNotification.FuzzyFileSearchSessionUpdated));

        var completed = CodexMessageParser.ParseNotification(
            "fuzzyFileSearch/sessionCompleted",
            ParseJsonElement("""{ "sessionId": "s1" }"""),
            options);
        Assert.IsInstanceOfType(completed, typeof(CodexNotification.FuzzyFileSearchSessionCompleted));
    }

    [TestMethod]
    public void ParseNotification_HandlesWindowsSandboxSetupCompleted()
    {
        var options = CodexClient.CreateJsonSerializerOptions();

        var notification = CodexMessageParser.ParseNotification(
            "windowsSandbox/setupCompleted",
            ParseJsonElement("""{ "mode": "elevated", "success": true, "error": null }"""),
            options);

        Assert.IsInstanceOfType(notification, typeof(CodexNotification.WindowsSandboxSetupCompleted));
    }

    [TestMethod]
    public void ParseNotification_HandlesServerRequestResolved()
    {
        var options = CodexClient.CreateJsonSerializerOptions();

        var notification = CodexMessageParser.ParseNotification(
            "serverRequest/resolved",
            ParseJsonElement("""{ "threadId": "thr_123", "requestId": 60 }"""),
            options);

        Assert.IsInstanceOfType(notification, typeof(CodexNotification.ServerRequestResolved));
        var typed = (CodexNotification.ServerRequestResolved)notification!;
        Assert.AreEqual("thr_123", typed.Data.ThreadId);
        Assert.IsInstanceOfType(typed.Data.RequestId, typeof(RequestId.IntegerValue));
        Assert.AreEqual(60L, ((RequestId.IntegerValue)typed.Data.RequestId).Value);
    }

    [TestMethod]
    public void ParseServerMessage_HandlesGeneratedServerRequest()
    {
        var options = CodexClient.CreateJsonSerializerOptions();

        var request = CodexMessageParser.ParseServerMessage(
            "item/commandExecution/requestApproval",
            ParseJsonElement("""{ "itemId": "item_1", "threadId": "thr_123", "turnId": "turn_456", "command": "echo hi" }"""),
            new RequestId.IntegerValue { Value = 42 },
            options);

        Assert.IsInstanceOfType(request, typeof(ServerRequest.ItemCommandExecutionRequestApprovalRequest));
        var typed = (ServerRequest.ItemCommandExecutionRequestApprovalRequest)request!;
        Assert.AreEqual(42L, ((RequestId.IntegerValue)typed.Id).Value);
        Assert.AreEqual("thr_123", typed.Params.ThreadId);
        Assert.AreEqual("turn_456", typed.Params.TurnId);
    }

    [TestMethod]
    public void ParseServerMessage_HandlesPermissionsApprovalRequest()
    {
        var options = CodexClient.CreateJsonSerializerOptions();

        var request = CodexMessageParser.ParseServerMessage(
            "item/permissions/requestApproval",
            ParseJsonElement("""{ "itemId": "item_1", "threadId": "thr_123", "turnId": "turn_456", "permissions": { "network": { "enabled": true } } }"""),
            new RequestId.IntegerValue { Value = 61 },
            options);

        Assert.IsInstanceOfType(request, typeof(ServerRequest.ItemPermissionsRequestApprovalRequest));
        var typed = (ServerRequest.ItemPermissionsRequestApprovalRequest)request!;
        Assert.AreEqual(61L, ((RequestId.IntegerValue)typed.Id).Value);
        Assert.AreEqual("thr_123", typed.Params.ThreadId);
        Assert.AreEqual("turn_456", typed.Params.TurnId);
        Assert.IsTrue(typed.Params.Permissions.Network?.Enabled);
    }

    [TestMethod]
    public void ParseServerMessage_HandlesMcpServerElicitationRequest()
    {
        var options = CodexClient.CreateJsonSerializerOptions();

        var request = CodexMessageParser.ParseServerMessage(
            "mcpServer/elicitation/request",
            ParseJsonElement("""{ "threadId": "thr_123", "turnId": "turn_456", "serverName": "docs", "mode": "form", "message": "Choose a language", "requestedSchema": { "type": "object", "properties": { "language": { "type": "string", "enum": ["csharp", "fsharp"] } }, "required": ["language"] } }"""),
            new RequestId.IntegerValue { Value = 62 },
            options);

        Assert.IsInstanceOfType(request, typeof(ServerRequest.McpServerElicitationRequestRequest));
        var typed = (ServerRequest.McpServerElicitationRequestRequest)request!;
        Assert.AreEqual(62L, ((RequestId.IntegerValue)typed.Id).Value);
        Assert.AreEqual("thr_123", typed.Params.ThreadId);
        Assert.AreEqual("turn_456", typed.Params.TurnId);
        Assert.AreEqual("docs", typed.Params.ServerName);
        Assert.IsInstanceOfType(typed.Params, typeof(McpServerElicitationRequestParams.Form));
    }

    [TestMethod]
    public void ParseServerMessage_UnknownRequest_PreservesRawPayload()
    {
        var options = CodexClient.CreateJsonSerializerOptions();

        var request = CodexMessageParser.ParseServerMessage(
            "custom/request",
            ParseJsonElement("""{ "threadId": "thr_123", "value": true }"""),
            new RequestId.StringValue { Value = "req-9" },
            options);

        Assert.IsInstanceOfType(request, typeof(CodexUnknownServerRequest));
        var typed = (CodexUnknownServerRequest)request!;
        Assert.AreEqual("custom/request", typed.Method);
        Assert.AreEqual("req-9", ((RequestId.StringValue)typed.RequestId).Value);
        Assert.AreEqual("thr_123", typed.Params.GetProperty("threadId").GetString());
    }

    [TestMethod]
    public void ParseNotification_InvalidPayload_FallsBackToUnknown()
    {
        var options = CodexClient.CreateJsonSerializerOptions();

        var notification = CodexMessageParser.ParseNotification(
            "thread/closed",
            ParseJsonElement("""{ "threadId": 123 }"""),
            options);

        Assert.IsInstanceOfType(notification, typeof(CodexNotification.Unknown));
        var typed = (CodexNotification.Unknown)notification!;
        Assert.AreEqual("thread/closed", typed.Method);
        Assert.AreEqual(123, typed.Params.GetProperty("threadId").GetInt32());
    }
}
