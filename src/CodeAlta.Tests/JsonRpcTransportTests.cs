// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.CodexSdk;

namespace CodeAlta.Tests;

[TestClass]
public class JsonRpcTransportTests
{
    private static JsonSerializerOptions CreateOptions()
    {
        return new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            TypeInfoResolver = CodexJsonSerializerContext.Default
        };
    }

    [TestMethod]
    public async Task SendRequest_WritesJsonRpcEnvelope()
    {
        // Use a Pipe so the response data arrives only after the request is sent,
        // avoiding a race between the read loop and TCS registration.
        var pipe = new Pipe();
        var clientInput = new MemoryStream();

        await using var transport = new JsonRpcTransport(pipe.Reader.AsStream(), clientInput, CreateOptions());

        // Start the request (registers TCS, then writes request, then awaits TCS).
        var requestTask = transport.SendRequestAsync<InitializeParams, InitializeResponse>(
            "initialize",
            new InitializeParams
            {
                ClientInfo = new ClientInfo { Name = "test", Version = "1.0" }
            });

        // Now feed the response so the read loop can deliver it.
        var response = """{"id":1,"result":{"userAgent":"codex/1.0"}}""" + "\n";
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(response));
        await pipe.Writer.CompleteAsync();

        // Act
        var result = await requestTask;

        // Assert
        Assert.AreEqual("codex/1.0", result.UserAgent);

        // Verify the request was written to clientInput
        clientInput.Position = 0;
        var written = Encoding.UTF8.GetString(clientInput.ToArray());
        Assert.IsTrue(written.Contains("\"method\":\"initialize\""), $"Expected method in: {written}");
        Assert.IsTrue(written.Contains("\"id\":1"), $"Expected id in: {written}");
    }

    [TestMethod]
    public async Task SendRequest_ThrowsJsonRpcException_OnErrorResponse()
    {
        var pipe = new Pipe();
        var clientInput = new MemoryStream();

        await using var transport = new JsonRpcTransport(pipe.Reader.AsStream(), clientInput, CreateOptions());

        // Start the request (registers TCS before the response is available).
        var requestTask = transport.SendRequestAsync<InitializeParams, InitializeResponse>(
            "initialize",
            new InitializeParams
            {
                ClientInfo = new ClientInfo { Name = "test", Version = "1.0" }
            });

        // Feed the error response.
        var response = """{"id":1,"error":{"code":-32600,"message":"Not initialized"}}""" + "\n";
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(response));
        await pipe.Writer.CompleteAsync();

        // Act & Assert
        var ex = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => requestTask);

        Assert.AreEqual(-32600, ex.Code);
        Assert.AreEqual("Not initialized", ex.Message);
    }

    [TestMethod]
    public async Task ReceivesServerNotification()
    {
        // Arrange: server sends a notification
        var serverOutput = new MemoryStream();
        var clientInput = new MemoryStream();

        var notification = """{"method":"thread/started","params":{"thread":{"id":"thr_1","preview":"test","modelProvider":"openai","createdAt":1730910000,"updatedAt":1730910000,"cliVersion":"1.0","cwd":"/tmp","path":"/tmp/rollout","source":"cli","turns":[]}}}""" + "\n";
        serverOutput.Write(Encoding.UTF8.GetBytes(notification));
        serverOutput.Position = 0;

        await using var transport = new JsonRpcTransport(serverOutput, clientInput, CreateOptions());

        // Act: read the notification
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var message = await transport.Messages.ReadAsync(cts.Token);

        // Assert
        Assert.AreEqual("thread/started", message.Method);
        Assert.IsNull(message.RequestId);
    }

    [TestMethod]
    public async Task ReceivesServerRequest_WithId()
    {
        // Arrange: server sends a request (approval)
        var serverOutput = new MemoryStream();
        var clientInput = new MemoryStream();

        var request = """{"method":"item/commandExecution/requestApproval","id":42,"params":{"itemId":"item_1","threadId":"thr_1","turnId":"turn_1","command":"rm -rf /"}}""" + "\n";
        serverOutput.Write(Encoding.UTF8.GetBytes(request));
        serverOutput.Position = 0;

        await using var transport = new JsonRpcTransport(serverOutput, clientInput, CreateOptions());

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var message = await transport.Messages.ReadAsync(cts.Token);

        // Assert
        Assert.AreEqual("item/commandExecution/requestApproval", message.Method);
        Assert.IsInstanceOfType<RequestId.IntegerValue>(message.RequestId);
        Assert.AreEqual(42L, ((RequestId.IntegerValue)message.RequestId!).Value);
    }

    [TestMethod]
    public async Task ReceivesServerRequest_WithStringId()
    {
        var serverOutput = new MemoryStream();
        var clientInput = new MemoryStream();

        var request = """{"method":"item/commandExecution/requestApproval","id":"req-42","params":{"itemId":"item_1","threadId":"thr_1","turnId":"turn_1","command":"rm -rf /"}}""" + "\n";
        serverOutput.Write(Encoding.UTF8.GetBytes(request));
        serverOutput.Position = 0;

        await using var transport = new JsonRpcTransport(serverOutput, clientInput, CreateOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var message = await transport.Messages.ReadAsync(cts.Token);

        Assert.AreEqual("item/commandExecution/requestApproval", message.Method);
        Assert.IsInstanceOfType<RequestId.StringValue>(message.RequestId);
        Assert.AreEqual("req-42", ((RequestId.StringValue)message.RequestId!).Value);
    }

    [TestMethod]
    public async Task SendNotification_WritesMethod_WithoutId()
    {
        var serverOutput = new MemoryStream(); // no server messages needed
        var clientInput = new MemoryStream();

        // We need the server stream to be empty but readable, so use a pipe
        // For simplicity, just close the output immediately
        serverOutput.Position = 0;

        await using var transport = new JsonRpcTransport(serverOutput, clientInput, CreateOptions());

        // Act
        await transport.SendNotificationAsync("initialized");

        // Assert
        clientInput.Position = 0;
        var written = Encoding.UTF8.GetString(clientInput.ToArray());
        Assert.IsTrue(written.Contains("\"method\":\"initialized\""), $"Expected method in: {written}");
        Assert.IsFalse(written.Contains("\"id\""), $"Notification should not have id: {written}");
    }

    [TestMethod]
    public async Task SendErrorResponse_WritesJsonRpcErrorEnvelope()
    {
        var serverOutput = new MemoryStream();
        var clientInput = new MemoryStream();

        await using var transport = new JsonRpcTransport(serverOutput, clientInput, CreateOptions());

        await transport.SendErrorResponseAsync(
            new RequestId.IntegerValue { Value = 77 },
            code: -32601,
            message: "Unsupported request");

        clientInput.Position = 0;
        var written = Encoding.UTF8.GetString(clientInput.ToArray());
        Assert.IsTrue(written.Contains("\"id\":77"), $"Expected id in: {written}");
        Assert.IsTrue(written.Contains("\"error\""), $"Expected error in: {written}");
        Assert.IsTrue(written.Contains("\"code\":-32601"), $"Expected code in: {written}");
        Assert.IsTrue(written.Contains("\"message\":\"Unsupported request\""), $"Expected message in: {written}");
    }

    [TestMethod]
    public async Task ReadLoop_SkipsMalformedJson_AndContinuesToNextMessage()
    {
        var pipe = new Pipe();
        var clientInput = new MemoryStream();

        await using var transport = new JsonRpcTransport(pipe.Reader.AsStream(), clientInput, CreateOptions());

        var payload = "not-json\n" +
                      """{"method":"thread/closed","params":{"threadId":"thr_123"}}""" + "\n";
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(payload));
        await pipe.Writer.CompleteAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var message = await transport.Messages.ReadAsync(cts.Token);

        Assert.AreEqual("thread/closed", message.Method);
        Assert.IsNull(message.RequestId);
    }
}
