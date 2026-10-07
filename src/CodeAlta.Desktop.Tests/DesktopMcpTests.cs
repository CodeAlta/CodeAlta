using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CodeAlta.Desktop.Mcp;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Desktop.Ui;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The MCP server of the application: where it listens, which requests it answers, what a client gets from it,
/// and the switch of its settings page.
/// </summary>
[TestClass]
public sealed class DesktopMcpTests
{
    private static DesktopLaunchOptions? Parse(params string[] args)
        => DesktopCommandLine.TryParse(args, _ => false, _ => false, out var options, out _) ? options : null;

    [TestMethod]
    public void CommandLine_SaysWhereTheServerListens_WithAnyStartThatOpensTheWindow()
    {
        Assert.IsNull(Parse()!.McpPort);
        Assert.IsNull(Parse()!.McpHost);
        Assert.AreEqual(4100, Parse("--mcp-port", "4100")!.McpPort);
        Assert.AreEqual(0, Parse("--mcp-port", "0")!.McpPort);
        Assert.AreEqual("localhost", Parse("--mcp-host", "localhost")!.McpHost);
        var developer = Parse("--dev", "--mcp-host", "::1", "--mcp-port", "4101")!;
        Assert.IsTrue(developer.Developer);
        Assert.AreEqual(("::1", 4101), (developer.McpHost, developer.McpPort));
        Assert.IsTrue(Parse("--mcp-port", "4100", "--wait")!.Wait);
        // The start is otherwise the one without the options.
        Assert.AreEqual(Parse()!.DataRoot, Parse("--mcp-port", "4100")!.DataRoot);
        Assert.AreEqual(Parse("--dev")!.StateRoot, Parse("--mcp-port", "4100", "--dev")!.StateRoot);
        var root = Path.Combine(Path.GetTempPath(), "codealta-mcp-" + Guid.NewGuid().ToString("N"));
        Assert.AreEqual(4102, Parse("--data-root", root, "--mcp-port", "4102")!.McpPort);

        // An option given twice, without a value or with one that names no port or no address is refused.
        foreach (var args in new string[][]
        {
            ["--mcp-port"], ["--mcp-host"], ["--mcp-port", "4100", "--mcp-port", "4101"], ["--mcp-host", "localhost", "--mcp-host", "127.0.0.1"],
            ["--mcp-port", "65536"], ["--mcp-port", "-1"], ["--mcp-port", "http"], ["--mcp-port", "+80"], ["--mcp-host", "example.com"], ["--mcp-host", "2582"],
            ["--mcp-host", "fe80::1%eth0"], ["--mcp-host", ""], ["--mcp-port", "4100", "--unknown"],
            // A start that only asks the running application to exit opens no server.
            ["--exit", "--mcp-port", "4100"], ["--dev", "--exit", "--mcp-host", "localhost"],
        })
        {
            Assert.IsNull(Parse(args), string.Join(' ', args));
        }

        // The start that leaves its terminal hands the options over to the application it starts.
        CollectionAssert.AreEqual(Array.Empty<string>(), DesktopTerminalStart.Arguments(Parse()!).ToArray());
        CollectionAssert.AreEqual(new[] { "--dev", "--mcp-host", "::1", "--mcp-port", "4101" }, DesktopTerminalStart.Arguments(developer).ToArray());
        Assert.AreEqual(developer.McpPort, Parse([.. DesktopTerminalStart.Arguments(developer)])!.McpPort);
    }

    [TestMethod]
    public void Help_NamesTheServerAndItsOptions()
    {
        using var output = new StringWriter();
        Assert.AreEqual(0, DesktopCommandLine.Run(["--help"], output, TextWriter.Null, _ => throw new AssertFailedException("Native startup was entered.")));
        var help = output.ToString();
        foreach (var expected in new[] { "--mcp-port", "--mcp-host", "http://127.0.0.1:2582/mcp", "2583" }) StringAssert.Contains(help, expected);
    }

    [TestMethod]
    public void Endpoint_IsTheLoopbackAddress_OnThePortOfTheInstance()
    {
        Assert.AreEqual(new DesktopMcpEndpoint("127.0.0.1", 2582, false), DesktopMcpEndpoint.For(Parse()!));
        Assert.AreEqual(new DesktopMcpEndpoint("127.0.0.1", 2583, false), DesktopMcpEndpoint.For(Parse("--dev")!));
        Assert.AreEqual(new DesktopMcpEndpoint("localhost", 4100, true), DesktopMcpEndpoint.For(Parse("--mcp-host", "localhost", "--mcp-port", "4100")!));
        // A port of the user's own choice stays one when it is the one the instance would take.
        Assert.IsTrue(DesktopMcpEndpoint.For(Parse("--mcp-port", "2582")!).Chosen);
        // Tests and automation run several instances at once: each takes a free port.
        var root = Path.Combine(Path.GetTempPath(), "codealta-mcp-" + Guid.NewGuid().ToString("N"));
        Assert.AreEqual(new DesktopMcpEndpoint("127.0.0.1", 0, false), DesktopMcpEndpoint.For(Parse("--data-root", root)!));
        Assert.AreEqual(0, DesktopMcpEndpoint.For(Parse()! with { Owned = new(root, root, root, root) }).Port);

        Assert.AreEqual("http://127.0.0.1:2582/mcp", new DesktopMcpEndpoint("127.0.0.1", 2582, false).Url);
        Assert.AreEqual("http://localhost:4100/mcp", new DesktopMcpEndpoint("localhost", 2582, false).On(4100).Url);
        Assert.AreEqual("http://[::1]:2583/mcp", new DesktopMcpEndpoint("::1", 2583, false).Url);
        Assert.AreEqual("http://192.168.1.20:2582/mcp", new DesktopMcpEndpoint("192.168.1.20", 2582, false).Url);
        // A server on every address is reached by the name of the computer.
        Assert.AreEqual($"http://{Dns.GetHostName()}:2582/mcp", new DesktopMcpEndpoint("0.0.0.0", 2582, false).Url);

        foreach (var host in new[] { "localhost", "LOCALHOST", "127.0.0.1", "127.8.9.10", "::1", "[::1]" }) Assert.IsTrue(DesktopMcpEndpoint.IsLoopbackHost(host), host);
        foreach (var host in new[] { null, "", "0.0.0.0", "::", "192.168.1.20", "example.com", "localhost.example.com", "127.0.0.1.example.com" })
            Assert.IsFalse(DesktopMcpEndpoint.IsLoopbackHost(host), host);
        Assert.IsTrue(new DesktopMcpEndpoint("localhost", 1, false).IsLoopback);
        Assert.IsFalse(new DesktopMcpEndpoint("0.0.0.0", 1, false).IsLoopback);
    }

    [TestMethod]
    public void Guard_AnswersTheProgramsOfThisComputer_AndNoPageOfABrowser()
    {
        // A program of this computer: no origin, and the host it connected to.
        Assert.AreEqual(DesktopMcpGuard.Allowed, DesktopMcpGuard.Check("127.0.0.1", null, null, null));
        Assert.AreEqual(DesktopMcpGuard.Allowed, DesktopMcpGuard.Check("localhost", "", null, null));
        Assert.AreEqual(DesktopMcpGuard.Allowed, DesktopMcpGuard.Check("[::1]", null, "Bearer anything", null));
        // A tool that runs in a browser on this computer.
        Assert.AreEqual(DesktopMcpGuard.Allowed, DesktopMcpGuard.Check("127.0.0.1", "http://localhost:6274", null, null));
        Assert.AreEqual(DesktopMcpGuard.Allowed, DesktopMcpGuard.Check("127.0.0.1", "http://127.0.0.1:5173", null, null));
        // The page of a site, a page without an origin, and a name an attacker resolves to this computer.
        Assert.AreEqual(403, DesktopMcpGuard.Check("127.0.0.1", "https://evil.example", null, null));
        Assert.AreEqual(403, DesktopMcpGuard.Check("127.0.0.1", "null", null, null));
        Assert.AreEqual(403, DesktopMcpGuard.Check("127.0.0.1", "file://", null, null));
        Assert.AreEqual(403, DesktopMcpGuard.Check("evil.example", null, null, null));
        Assert.AreEqual(403, DesktopMcpGuard.Check("localhost.evil.example", "http://localhost.evil.example:2582", null, null));
        Assert.AreEqual(403, DesktopMcpGuard.Check(null, null, null, null));

        // A server other computers reach asks every request for its token, whatever it comes from.
        Assert.AreEqual(DesktopMcpGuard.Allowed, DesktopMcpGuard.Check("build-box", null, "Bearer secret-token", "secret-token"));
        Assert.AreEqual(DesktopMcpGuard.Allowed, DesktopMcpGuard.Check("build-box", "https://tool.example", "bearer  secret-token ", "secret-token"));
        Assert.AreEqual(401, DesktopMcpGuard.Check("127.0.0.1", null, null, "secret-token"));
        Assert.AreEqual(401, DesktopMcpGuard.Check("127.0.0.1", null, "Bearer other", "secret-token"));
        Assert.AreEqual(401, DesktopMcpGuard.Check("127.0.0.1", null, "Basic secret-token", "secret-token"));
        Assert.AreEqual(401, DesktopMcpGuard.Check("127.0.0.1", null, "secret-token", "secret-token"));
    }

    [TestMethod]
    public void Token_IsCreatedOnce_AndKept()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "state", "mcp_token.txt");
        var token = DesktopMcpGuard.ReadOrCreateToken(path);
        Assert.IsTrue(token.Length >= 40 && token.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'), token);
        Assert.AreEqual(token, File.ReadAllText(path));
        Assert.AreEqual(token, DesktopMcpGuard.ReadOrCreateToken(path));
        // What is no token is replaced by one.
        File.WriteAllText(path, "short");
        var replaced = DesktopMcpGuard.ReadOrCreateToken(path);
        Assert.AreNotEqual("short", replaced);
        Assert.AreNotEqual(token, replaced);
        Assert.AreEqual(replaced, DesktopMcpGuard.ReadOrCreateToken(path));
    }

    [TestMethod]
    public void Preferences_KeepTheServerOn_UntilItIsTurnedOff()
    {
        using var temp = new TempFolder();
        Assert.IsTrue(DesktopPreferences.Load(temp.Path).McpServer);
        Assert.IsTrue(DesktopPreferences.Default.McpServer);
        Assert.IsTrue(new DesktopPreferences(DesktopCloseBehavior.KeepRunning, McpServer: false).Save(temp.Path));
        Assert.AreEqual(new DesktopPreferences(DesktopCloseBehavior.KeepRunning, false), DesktopPreferences.Load(temp.Path));
        Assert.AreEqual("""{"onClose":"keep","mcpServer":false}""", File.ReadAllText(Path.Combine(temp.Path, "preferences.json")));
        Assert.IsTrue(new DesktopPreferences(DesktopCloseBehavior.Exit).Save(temp.Path));
        Assert.AreEqual(new DesktopPreferences(DesktopCloseBehavior.Exit, true), DesktopPreferences.Load(temp.Path));
        // Only the choice to turn it off is written.
        Assert.AreEqual("""{"onClose":"exit"}""", File.ReadAllText(Path.Combine(temp.Path, "preferences.json")));
        // A file written before the server existed says nothing about it: the server runs.
        foreach (var earlier in new[] { """{"onClose":"keep"}""", """{"closeToTray":false}""", """{"mcpServer":"no"}""", """{}""" })
        {
            File.WriteAllText(Path.Combine(temp.Path, "preferences.json"), earlier);
            Assert.IsTrue(DesktopPreferences.Load(temp.Path).McpServer, earlier);
        }

        File.WriteAllText(Path.Combine(temp.Path, "preferences.json"), """{"closeToTray":true,"mcpServer":false}""");
        Assert.AreEqual(new DesktopPreferences(DesktopCloseBehavior.KeepRunning, false), DesktopPreferences.Load(temp.Path));
    }

    [TestMethod]
    public async Task Server_GivesAClientItsToolsAndTheirResults()
    {
        using var temp = new TempFolder();
        var calls = new List<string>();
        var tools = new[]
        {
            Tool("take_screenshot", readOnly: false, arguments =>
            {
                lock (calls) calls.Add(arguments.GetRawText());
                return new DesktopUiToolResult("Took a screenshot.", [new DesktopUiImage(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, "image/png")], false);
            }),
            Tool("list_pages", readOnly: true, _ => new DesktopUiToolResult("## Pages\n1: CodeAlta", [], false)),
            Tool("click", readOnly: false, _ => DesktopUiToolResult.Error("No element has the uid \"9_9\".")),
            Tool("broken", readOnly: false, _ => throw new InvalidOperationException("The window is gone.")),
        };
        await using var server = new DesktopMcpServer(new DesktopMcpEndpoint("127.0.0.1", 0, false), () => tools, temp.Path, "1.2.3");
        Assert.AreEqual(new DesktopMcpStatus(DesktopMcpStatus.Stopped, true, null, null, null), server.Status);
        var changes = 0;
        server.Changed += () => Interlocked.Increment(ref changes);

        await server.StartAsync();

        var status = server.Status;
        Assert.AreEqual(DesktopMcpStatus.Running, status.State);
        Assert.IsNull(status.Token);
        StringAssert.Matches(status.Url!, new System.Text.RegularExpressions.Regex(@"^http://127\.0\.0\.1:[1-9][0-9]*/mcp$"));
        Assert.AreEqual(1, changes);
        // The address is where a script finds it, beside the lock of the instance.
        Assert.AreEqual(status.Url, File.ReadAllText(server.UrlPath).Trim());
        using var client = new HttpClient();

        var initialized = await PostAsync(client, status.Url!, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""");
        Assert.AreEqual(HttpStatusCode.OK, initialized.Status);
        var info = initialized.Body!.RootElement.GetProperty("result");
        Assert.AreEqual("CodeAlta", info.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.AreEqual("1.2.3", info.GetProperty("serverInfo").GetProperty("version").GetString());
        StringAssert.Contains(info.GetProperty("instructions").GetString(), "take_snapshot");

        var listed = await PostAsync(client, status.Url!, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var listedTools = listed.Body!.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        CollectionAssert.AreEqual(new[] { "take_screenshot", "list_pages", "click", "broken" }, listedTools.Select(static tool => tool.GetProperty("name").GetString()).ToArray());
        Assert.AreEqual("Description of take_screenshot.", listedTools[0].GetProperty("description").GetString());
        Assert.AreEqual("string", listedTools[0].GetProperty("inputSchema").GetProperty("properties").GetProperty("format").GetProperty("type").GetString());
        Assert.IsFalse(listedTools[0].GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.IsTrue(listedTools[1].GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());

        // A result is a text and the pictures that go with it; the arguments reach the tool as the client gave them.
        var shot = (await CallAsync(client, status.Url!, "take_screenshot", """{"format":"png","fullPage":false}""")).GetProperty("result");
        Assert.IsFalse(shot.TryGetProperty("isError", out var failed) && failed.GetBoolean());
        var content = shot.GetProperty("content").EnumerateArray().ToArray();
        Assert.AreEqual(("text", "Took a screenshot."), (content[0].GetProperty("type").GetString(), content[0].GetProperty("text").GetString()));
        Assert.AreEqual(("image", "image/png", "iVBORw=="), (content[1].GetProperty("type").GetString(), content[1].GetProperty("mimeType").GetString(), content[1].GetProperty("data").GetString()));
        lock (calls) Assert.AreEqual("""{"format":"png","fullPage":false}""", calls.Single());
        await CallAsync(client, status.Url!, "take_screenshot", null);
        lock (calls) Assert.AreEqual("{}", calls[1]);

        // A tool that cannot do what it was asked says so in its result, as does one that fails, and an unknown one.
        foreach (var (name, expected) in new[] { ("click", "No element has the uid \"9_9\"."), ("broken", "The tool \"broken\" failed: The window is gone."), ("missing", "Unknown tool \"missing\". Call a tool of the list of tools.") })
        {
            var refused = (await CallAsync(client, status.Url!, name, "{}")).GetProperty("result");
            Assert.IsTrue(refused.GetProperty("isError").GetBoolean(), name);
            Assert.AreEqual(expected, refused.GetProperty("content")[0].GetProperty("text").GetString());
        }

        // No page of a browser is answered, and nothing else than the endpoint is served.
        Assert.AreEqual(HttpStatusCode.Forbidden, (await PostAsync(client, status.Url!, """{"jsonrpc":"2.0","id":9,"method":"tools/list"}""",
            request => request.Headers.Add("Origin", "https://evil.example"))).Status);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await PostAsync(client, status.Url!, """{"jsonrpc":"2.0","id":9,"method":"tools/list"}""",
            request => request.Headers.Host = "evil.example")).Status);
        Assert.AreEqual(HttpStatusCode.OK, (await PostAsync(client, status.Url!, """{"jsonrpc":"2.0","id":9,"method":"tools/list"}""",
            request => request.Headers.Add("Origin", "http://localhost:6274"))).Status);
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync(status.Url!.Replace("/mcp", "/", StringComparison.Ordinal))).StatusCode);

        // Once it is disposed nothing listens, and the address is gone.
        await server.DisposeAsync();
        Assert.IsFalse(File.Exists(server.UrlPath));
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => client.PostAsync(status.Url, new StringContent("{}", Encoding.UTF8, "application/json")));
        await server.StartAsync();
        Assert.IsFalse(File.Exists(server.UrlPath));
    }

    [TestMethod]
    public async Task Server_TakesAFreePort_WhenItsOwnIsTaken_ButNotWhenTheUserNamedIt()
    {
        using var temp = new TempFolder();
        using var other = new TcpListener(IPAddress.Loopback, 0);
        other.Start();
        var taken = ((IPEndPoint)other.LocalEndpoint).Port;

        await using (var server = new DesktopMcpServer(new DesktopMcpEndpoint("127.0.0.1", taken, Chosen: false), () => [], temp.Path, "1"))
        {
            await server.StartAsync();
            Assert.AreEqual(DesktopMcpStatus.Running, server.Status.State, server.Status.Error);
            Assert.AreNotEqual($"http://127.0.0.1:{taken}/mcp", server.Status.Url);
            Assert.AreEqual(server.Status.Url, File.ReadAllText(server.UrlPath).Trim());
        }

        await using (var server = new DesktopMcpServer(new DesktopMcpEndpoint("127.0.0.1", taken, Chosen: true), () => [], temp.Path, "1"))
        {
            await server.StartAsync();
            var status = server.Status;
            Assert.AreEqual(DesktopMcpStatus.Failed, status.State);
            Assert.IsTrue(status.Enabled);
            Assert.IsNull(status.Url);
            Assert.IsFalse(string.IsNullOrWhiteSpace(status.Error));
            Assert.IsFalse(File.Exists(server.UrlPath));
        }
    }

    [TestMethod]
    public async Task SettingsPage_TurnsTheServerOnAndOff_AndKeepsTheChoice()
    {
        using var temp = new TempFolder();
        var remembered = new List<bool>();
        var tools = new[] { Tool("take_snapshot", readOnly: false, _ => new DesktopUiToolResult("snapshot", [], false)), Tool("alta", readOnly: false, _ => new DesktopUiToolResult("ok", [], false)) };
        await using var server = new DesktopMcpServer(new DesktopMcpEndpoint("127.0.0.1", 0, false), () => tools, temp.Path, "1", enabled: false, remember: remembered.Add);
        var service = new McpHostService(server, () => tools, "epoch");

        // A server that is turned off does not listen when the application starts.
        await server.StartAsync();
        Assert.AreEqual(new McpHostResponse("ok", "stopped", false, null, null, null, []), Equatable(service.Status(new("epoch"))));
        Assert.IsFalse(File.Exists(server.UrlPath));

        var on = await service.SetEnabledAsync(new("epoch", true), CancellationToken.None);
        Assert.AreEqual(("ok", "running", true), (on.Status, on.State, on.Enabled));
        CollectionAssert.AreEqual(new[] { "take_snapshot", "alta" }, on.Tools);
        Assert.AreEqual(on.Url, File.ReadAllText(server.UrlPath).Trim());
        using var client = new HttpClient();
        Assert.AreEqual(HttpStatusCode.OK, (await PostAsync(client, on.Url!, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")).Status);
        // Turning it on again changes nothing.
        Assert.AreEqual(on.Url, (await service.SetEnabledAsync(new("epoch", true), CancellationToken.None)).Url);

        var off = await service.SetEnabledAsync(new("epoch", false), CancellationToken.None);
        Assert.AreEqual(new McpHostResponse("ok", "stopped", false, null, null, null, []), Equatable(off));
        Assert.IsFalse(File.Exists(server.UrlPath));
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => client.PostAsync(on.Url, new StringContent("{}", Encoding.UTF8, "application/json")));
        CollectionAssert.AreEqual(new[] { true, true, false }, remembered);

        // A page of another run of the host, and a window without a host, change nothing.
        Assert.AreEqual("stale_epoch", (await service.SetEnabledAsync(new("other", true), CancellationToken.None)).Status);
        Assert.AreEqual("stale_epoch", service.Status(new(null)).Status);
        Assert.AreEqual(DesktopMcpStatus.Stopped, server.Status.State);
        Assert.AreEqual("unavailable", new McpHostService().Status(new("epoch")).Status);
        Assert.AreEqual("unavailable", (await new McpHostService().SetEnabledAsync(new("epoch", true), CancellationToken.None)).Status);
    }

    // A response with its tools as a value: an array is compared by reference.
    private static McpHostResponse Equatable(McpHostResponse response)
    {
        Assert.AreEqual(0, response.Tools.Length);
        return response with { Tools = [] };
    }

    private static DesktopMcpTool Tool(string name, bool readOnly, Func<JsonElement, DesktopUiToolResult> call)
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"format":{"type":"string"},"fullPage":{"type":"boolean"}}}""");
        return new DesktopMcpTool(name, $"Description of {name}.", schema.RootElement.Clone(), readOnly, (arguments, _) => Task.FromResult(call(arguments)));
    }

    private static async Task<JsonElement> CallAsync(HttpClient client, string url, string tool, string? arguments)
    {
        var parameters = arguments is null ? $$"""{"name":"{{tool}}"}""" : $$"""{"name":"{{tool}}","arguments":{{arguments}}}""";
        var reply = await PostAsync(client, url, $$"""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{{parameters}}}""");
        Assert.AreEqual(HttpStatusCode.OK, reply.Status);
        return reply.Body!.RootElement;
    }

    // A request as an MCP client sends it; the answer is JSON, or one message of an event stream.
    private static async Task<(HttpStatusCode Status, JsonDocument? Body)> PostAsync(HttpClient client, string url, string body, Action<HttpRequestMessage>? configure = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2025-06-18");
        configure?.Invoke(request);
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(text)) return (response.StatusCode, null);
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            text = text.Split('\n').Last(static line => line.StartsWith("data:", StringComparison.Ordinal))["data:".Length..];
        return (response.StatusCode, JsonDocument.Parse(text));
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codealta-mcp-" + Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }
}
