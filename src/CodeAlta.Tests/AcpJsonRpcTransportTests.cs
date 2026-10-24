using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using CodeAlta.Acp;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AcpJsonRpcTransportTests
{
    [TestMethod]
    public async Task SendRequestAsync_WritesEnvelope_AndCorrelatesResponse()
    {
        var pipe = new Pipe();
        var clientInput = new MemoryStream();

        await using var transport = new AcpJsonRpcTransport(
            pipe.Reader.AsStream(),
            clientInput,
            AcpClient.CreateJsonSerializerOptions());

        var requestTask = transport.SendRequestAsync<InitializeRequest, InitializeResponse>(
            "initialize",
            new InitializeRequest
            {
                ClientInfo = new Implementation { Name = "test", Version = "1.0.0" },
                ClientCapabilities = new ClientCapabilities
                {
                    Auth = new AuthCapabilities(),
                    Fs = new FileSystemCapabilities(),
                },
                ProtocolVersion = new ProtocolVersion { Value = JsonSerializer.SerializeToElement("1") }
            });

        var response = """
                       {"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"1","agentCapabilities":{"auth":{},"mcpCapabilities":{"http":true,"sse":false},"promptCapabilities":{},"sessionCapabilities":{}}}}
                       """ + "\n";
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(response));
        await pipe.Writer.CompleteAsync();

        var result = await requestTask.ConfigureAwait(false);

        Assert.AreEqual("1", result.ProtocolVersion.Value.GetString());
        clientInput.Position = 0;
        var written = Encoding.UTF8.GetString(clientInput.ToArray());
        StringAssert.Contains(written, @"""jsonrpc"":""2.0""");
        StringAssert.Contains(written, @"""method"":""initialize""");
        StringAssert.Contains(written, @"""id"":1");
    }

    [TestMethod]
    public async Task StreamAsync_ReceivesServerNotificationAndRequest()
    {
        var serverOutput = new MemoryStream();
        var clientInput = new MemoryStream();

        var payload = """
                      {"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"session-1","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Hello"}}}}
                      {"jsonrpc":"2.0","id":42,"method":"session/request_permission","params":{"sessionId":"session-1","toolCall":{"toolCallId":"tool-1","kind":"shell","title":"Run command","status":"pending"},"options":[]}}
                      """ + "\n";
        serverOutput.Write(Encoding.UTF8.GetBytes(payload));
        serverOutput.Position = 0;

        await using var transport = new AcpJsonRpcTransport(
            serverOutput,
            clientInput,
            AcpClient.CreateJsonSerializerOptions());

        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var notification = await transport.Messages.ReadAsync(cancellationTokenSource.Token).ConfigureAwait(false);
        var request = await transport.Messages.ReadAsync(cancellationTokenSource.Token).ConfigureAwait(false);

        Assert.AreEqual("session/update", notification.Method);
        Assert.IsNull(notification.RequestId);
        Assert.AreEqual("session/request_permission", request.Method);
        Assert.IsNotNull(request.RequestId);
    }

    [TestMethod]
    public async Task SendResponseAsync_WritesRawRequestId()
    {
        var serverOutput = new MemoryStream();
        var clientInput = new MemoryStream();

        await using var transport = new AcpJsonRpcTransport(
            serverOutput,
            clientInput,
            AcpClient.CreateJsonSerializerOptions());

        await transport.SendResponseAsync(
            new RequestId { Value = JsonSerializer.SerializeToElement(99) },
            new RequestPermissionResponse
            {
                Outcome = new RequestPermissionOutcome
                {
                    Value = JsonSerializer.SerializeToElement(new { outcome = "allow_once" })
                }
            });

        clientInput.Position = 0;
        var written = Encoding.UTF8.GetString(clientInput.ToArray());
        StringAssert.Contains(written, @"""id"":99");
        StringAssert.Contains(written, @"""outcome"":{""outcome"":""allow_once""}");
        Assert.IsFalse(written.Contains(@"""id"":{"), written);
        Assert.IsFalse(written.Contains(@"""value"""), written);
    }

    [TestMethod]
    public async Task DisposeAsync_CancelsPendingRequests()
    {
        var pipe = new Pipe();
        var clientInput = new MemoryStream();

        await using var transport = new AcpJsonRpcTransport(
            pipe.Reader.AsStream(),
            clientInput,
            AcpClient.CreateJsonSerializerOptions());

        var requestTask = transport.SendRequestAsync<InitializeRequest, InitializeResponse>(
            "initialize",
            new InitializeRequest
            {
                ClientInfo = new Implementation { Name = "test", Version = "1.0.0" },
                ClientCapabilities = new ClientCapabilities
                {
                    Auth = new AuthCapabilities(),
                    Fs = new FileSystemCapabilities(),
                },
                ProtocolVersion = new ProtocolVersion { Value = JsonSerializer.SerializeToElement("1") }
            });

        await transport.DisposeAsync();

        await Assert.ThrowsExactlyAsync<AcpConnectionClosedException>(() => requestTask).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task EndOfOutput_FailsPendingRequestsWithTheDescriptionOfTheExit()
    {
        var pipe = new Pipe();
        var reader = new StreamReader(pipe.Reader.AsStream());
        await using var transport = new AcpJsonRpcTransport(
            reader.ReadLineAsync,
            new MemoryStream(),
            AcpClient.CreateJsonSerializerOptions(),
            logger: null,
            describeClose: static () => "The ACP agent 'fake' exited with code 3.");

        var requestTask = transport.SendRequestAsync<InitializeRequest, InitializeResponse>("initialize", CreateInitializeRequest());
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("starting the agent...\n"));
        await pipe.Writer.CompleteAsync();

        var error = await Assert.ThrowsExactlyAsync<AcpConnectionClosedException>(() => requestTask).ConfigureAwait(false);
        Assert.AreEqual("The ACP agent 'fake' exited with code 3.", error.Message);
        await transport.Completion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<AcpConnectionClosedException>(
                () => transport.SendRequestAsync<InitializeRequest, InitializeResponse>("initialize", CreateInitializeRequest()))
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task SendRequestAsync_TimesOutAndAsksTheAgentToCancel()
    {
        var pipe = new Pipe();
        var clientInput = new MemoryStream();
        await using var transport = new AcpJsonRpcTransport(pipe.Reader.AsStream(), clientInput, AcpClient.CreateJsonSerializerOptions());

        await Assert.ThrowsExactlyAsync<TimeoutException>(
                () => transport.SendRequestAsync<InitializeRequest, InitializeResponse>(
                    "initialize",
                    CreateInitializeRequest(),
                    TimeSpan.FromMilliseconds(50)))
            .ConfigureAwait(false);

        var written = Encoding.UTF8.GetString(clientInput.ToArray());
        StringAssert.Contains(written, @"""method"":""$/cancel_request""");
        StringAssert.Contains(written, @"""requestId"":1");
        await pipe.Writer.CompleteAsync();
    }

    [TestMethod]
    public async Task SendRequestAsync_CorrelatesAResponseWhoseIdIsAString()
    {
        var pipe = new Pipe();
        await using var transport = new AcpJsonRpcTransport(pipe.Reader.AsStream(), new MemoryStream(), AcpClient.CreateJsonSerializerOptions());

        var requestTask = transport.SendRequestAsync<InitializeRequest, InitializeResponse>("initialize", CreateInitializeRequest());
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","id":"1","result":{"protocolVersion":1,"agentCapabilities":{}}}""" + "\n"));

        var result = await requestTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.AreEqual(1, result.ProtocolVersion.Value.GetInt32());
        await pipe.Writer.CompleteAsync();
    }

    [TestMethod]
    public async Task SendErrorResponseAsync_WritesAJsonRpcError()
    {
        var clientInput = new MemoryStream();
        await using var transport = new AcpJsonRpcTransport(new MemoryStream(), clientInput, AcpClient.CreateJsonSerializerOptions());

        await transport.SendErrorResponseAsync(new RequestId { Value = JsonSerializer.SerializeToElement(7) }, -32601, "Method not found");

        var written = Encoding.UTF8.GetString(clientInput.ToArray());
        StringAssert.Contains(written, @"""id"":7");
        StringAssert.Contains(written, @"""error"":{""code"":-32601,""message"":""Method not found""}");
    }

    [TestMethod]
    public async Task StartAsync_ReportsTheExitAndTheStandardErrorOfAnAgentThatFails()
    {
        var options = new AcpClientOptions
        {
            ProcessOptions = new AcpProcessOptions
            {
                FileName = "dotnet",
                Arguments = ["this-assembly-does-not-exist.dll"],
            },
            ClientInfo = new Implementation { Name = "test", Version = "1.0.0" },
            ClientCapabilities = new ClientCapabilities(),
            InitializeTimeout = TimeSpan.FromSeconds(60),
        };

        var error = await Assert.ThrowsExactlyAsync<AcpConnectionClosedException>(() => AcpClient.StartAsync(options)).ConfigureAwait(false);

        StringAssert.Contains(error.Message, "'dotnet' exited with code");
        var lines = error.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.IsGreaterThan(1, lines.Length, error.Message);
    }

    private static InitializeRequest CreateInitializeRequest()
        => new()
        {
            ClientInfo = new Implementation { Name = "test", Version = "1.0.0" },
            ClientCapabilities = new ClientCapabilities(),
            ProtocolVersion = new ProtocolVersion { Value = JsonSerializer.SerializeToElement(1) }
        };
}
