using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Harness;

namespace CodeAlta.Tests;

[TestClass]
public sealed class HarnessToolEndpointsTests
{
    [TestMethod]
    public async Task Endpoint_AnswersTheHarnessThatHasItsSecret()
    {
        await using var endpoints = new LoopbackHarnessToolEndpoints();
        await using var endpoint = await endpoints.OpenAsync(CreateServer());
        using var client = new HttpClient();

        Assert.AreEqual("127.0.0.1", endpoint.Url.Host);
        using var list = await PostAsync(client, endpoint.Url, endpoint.Headers, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        Assert.AreEqual(HttpStatusCode.OK, list.StatusCode);
        Assert.AreEqual("application/json", list.Content.Headers.ContentType?.MediaType);
        using var listed = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.AreEqual("alta", listed.RootElement.GetProperty("result").GetProperty("tools")[0].GetProperty("name").GetString());

        // On the same connection.
        using var call = await PostAsync(client, endpoint.Url, endpoint.Headers,
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"alta","arguments":{"args":["x"]}}}""");
        using var called = JsonDocument.Parse(await call.Content.ReadAsStringAsync());
        Assert.AreEqual("ran", called.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());

        using var notification = await PostAsync(client, endpoint.Url, endpoint.Headers, """{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        Assert.AreEqual(HttpStatusCode.Accepted, notification.StatusCode);
    }

    [TestMethod]
    public async Task Endpoint_RefusesAWrongSecretAnUnknownPathAndAStream()
    {
        await using var endpoints = new LoopbackHarnessToolEndpoints();
        await using var endpoint = await endpoints.OpenAsync(CreateServer());
        using var client = new HttpClient();

        using var wrongSecret = await PostAsync(client, endpoint.Url, [new("Authorization", "Bearer nope")], """{"jsonrpc":"2.0","id":1,"method":"ping"}""");
        Assert.AreEqual(HttpStatusCode.Unauthorized, wrongSecret.StatusCode);

        var otherPath = new Uri(endpoint.Url, "/mcp/0000");
        using var unknown = await PostAsync(client, otherPath, endpoint.Headers, """{"jsonrpc":"2.0","id":1,"method":"ping"}""");
        Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);

        using var get = new HttpRequestMessage(HttpMethod.Get, endpoint.Url);
        get.Headers.TryAddWithoutValidation("Authorization", endpoint.Headers[0].Value);
        using var stream = await client.SendAsync(get);
        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, stream.StatusCode);
    }

    [TestMethod]
    public async Task Endpoint_IsGoneOnceClosed()
    {
        await using var endpoints = new LoopbackHarnessToolEndpoints();
        var endpoint = await endpoints.OpenAsync(CreateServer());
        using var client = new HttpClient();
        await endpoint.DisposeAsync();

        using var response = await PostAsync(client, endpoint.Url, endpoint.Headers, """{"jsonrpc":"2.0","id":1,"method":"ping"}""");
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task Endpoint_ReadsAChunkedBodyAndABatch()
    {
        await using var endpoints = new LoopbackHarnessToolEndpoints();
        await using var endpoint = await endpoints.OpenAsync(CreateServer());
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, endpoint.Url.Port);
        var stream = tcp.GetStream();
        var body = """[{"jsonrpc":"2.0","id":1,"method":"ping"},{"jsonrpc":"2.0","method":"notifications/initialized"}]""";
        var half = body.Length / 2;
        var request = $"POST {endpoint.Url.AbsolutePath} HTTP/1.1\r\nHost: 127.0.0.1\r\nAuthorization: {endpoint.Headers[0].Value}\r\n" +
                      "Transfer-Encoding: chunked\r\nConnection: close\r\n\r\n" +
                      $"{half:x}\r\n{body[..half]}\r\n{body.Length - half:x}\r\n{body[half..]}\r\n0\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var response = await reader.ReadToEndAsync();

        StringAssert.StartsWith(response, "HTTP/1.1 200 OK");
        StringAssert.Contains(response, "Connection: close");
        var json = response[(response.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
        using var answers = JsonDocument.Parse(json);
        Assert.AreEqual(1, answers.RootElement.GetArrayLength());
        Assert.AreEqual(1, answers.RootElement[0].GetProperty("id").GetInt32());
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, Uri url, IReadOnlyList<KeyValuePair<string, string>> headers, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return await client.SendAsync(request);
    }

    private static HarnessMcpToolServer CreateServer()
    {
        var tool = new AgentToolDefinition(
            new AgentToolSpec("alta", "The gateway.", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone()),
            static (_, _) => Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text("ran")])));
        return new HarnessMcpToolServer(
            "codealta",
            () => new Dictionary<string, AgentToolDefinition> { ["alta"] = tool },
            (call, cancellationToken) => tool.Handler(new AgentToolInvocation(new ModelProviderId("fake"), "s1", "c1", call.Name, call.Arguments), cancellationToken));
    }
}
