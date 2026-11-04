using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using CodeAlta.Orchestration.Hosting;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// Fleet secrets end to end (<c>doc/fleet/secrets.md</c>) with the real programs: <c>altaserver</c> and <c>altanode</c> run as
/// processes of their own, on isolated data roots and ports that are not their defaults; this desktop drives the
/// <c>fleetServer</c> service of its window; a fake model server asks for a shell command. A secret set from the desktop,
/// sealed for the node, reaches the shell of a session on the node through <c>${secret:NAME}</c> and comes back masked; a
/// session in an environment that requires an approval starts once the desktop approved it, with the value of that
/// environment. Run with <c>CODEALTA_FLEET_E2E=1</c> after a build of the solution; skipped otherwise.
/// </summary>
[TestClass]
public sealed class FleetSecretsEndToEndTests
{
    private const string GrafanaToken = "glsa_e2e_grafana_0123456789abcdef";
    private const string DeployToken = "deploy_e2e_production_fedcba9876543210";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(120);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task AFleetSecret_ReachesTheShellOfARunOnTheNode_MaskedInItsOutput()
    {
        if (Environment.GetEnvironmentVariable("CODEALTA_FLEET_E2E") != "1") Assert.Inconclusive("Set CODEALTA_FLEET_E2E=1 to run the programs of the build.");
        var bin = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var exe = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        var altaserver = Path.Combine(bin, "CodeAlta.Server", "bin", configuration, "net10.0", "altaserver" + exe);
        var altanode = Path.Combine(bin, "CodeAlta.Node", "bin", configuration, "net10.0", "altanode" + exe);
        if (!File.Exists(altaserver) || !File.Exists(altanode)) Assert.Inconclusive($"Build the solution first: {altaserver}, {altanode}.");

        var token = TestContext.CancellationToken;
        var root = Directory.CreateTempSubdirectory("codealta-fleet-secrets-e2e-").FullName;
        string At(string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        var processes = new List<Process>();
        var output = new ConcurrentQueue<string>();
        await using var model = FakeChatServer.Start();
        try
        {
            // The server, on a port of its own.
            var serverPort = FreePort();
            var serverRoot = At("server");
            processes.Add(Launch(altaserver, ["run", "--port", serverPort.ToString(System.Globalization.CultureInfo.InvariantCulture), "--data-root", serverRoot], output, "altaserver"));
            var address = new Uri($"http://127.0.0.1:{serverPort}/");
            var setupCode = await WaitForAsync(() => Task.FromResult(ReadSetupCode(serverRoot)), token);

            // This desktop: the owner's controller device, which makes the server and approves the node.
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = At("desktop"), CurrentProjectPath = At("desktop-project"), DiscoveryScope = new(At("desktop-home"), root), BuiltInSkillRoot = At("builtin"),
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
            }, token);
            await using var servers = new DesktopFleetServers(host, "Desktop", "4.0.0", Fast);
            var epoch = Guid.NewGuid().ToString("D");
            var service = new FleetServerService(servers, epoch) { Spaces = host.SpaceCatalog, IsPaired = static _ => false };
            var created = await service.CreateAsync(new FleetServerCreateRequest(epoch, address.ToString(), setupCode, "Alex", "Home"), token);
            Assert.AreEqual("ok", created.Status, created.Message);
            var linkId = created.Server!.Id;
            var enrollment = await service.CreateTokenAsync(new FleetServerTokenRequest(epoch, linkId, 2, "e2e-node"), token);

            // The node: a fake model as its only provider, a CLI profile of its space that reads the secrets by name.
            var nodeRoot = At("node");
            File.WriteAllText(Path.Combine(nodeRoot, "config.toml"), $"""
                [chat]
                default_provider = "fake"

                [providers.fake]
                type = "openai-chat"
                api_url = "{model.Address}v1"
                api_key = "fake-model-key"
                model = "fake-model"
                """);
            var nodeSpaces = new SpaceCatalog(new ProjectCatalog(new CatalogOptions { GlobalRoot = nodeRoot, StateRoot = nodeRoot }));
            await nodeSpaces.SeedAsync(token);
            await nodeSpaces.UpdateSettingsAsync(SpaceDescriptor.DefaultId, settings => settings with
            {
                Identity = new SpaceIdentity(Cli:
                [
                    new SpaceCliProfile("grafana", [new("GRAFANA_TOKEN", "${secret:GRAFANA_TOKEN}")]),
                    new SpaceCliProfile("deploy", [new("DEPLOY_TOKEN", "${secret:DEPLOY_TOKEN}")]),
                ]),
            }, token);
            Assert.AreEqual(0, await RunAsync(altanode, ["join", address.ToString(), "--token", enrollment.Token!, "--data-root", nodeRoot, "--name", "e2e-node", "--no-wait"], output, token));
            processes.Add(Launch(altanode, ["run", "--data-root", nodeRoot, "--no-listen", "--name", "e2e-node"], output, "altanode"));
            var pending = await WaitForAsync(async () => (await service.DevicesAsync(new FleetServerLinkRequest(epoch, linkId), token)).Devices
                .FirstOrDefault(static device => device.Status == "pending"), token);
            Assert.AreEqual("ok", (await service.ApproveAsync(new FleetServerDeviceRequest(epoch, linkId, pending.Id, null, pending.Kind), token)).Status);
            var nodeId = pending.Id;

            // The desktop sets the secret for the node, from its window: sealed here, the server holds copies only.
            var overview = await WaitForAsync(async () => await service.SecretsAsync(new FleetServerLinkRequest(epoch, linkId), token) is { Status: "ok", Machines.Length: > 0 } read
                ? read : null, token);
            Assert.AreEqual(nodeId, overview.Machines.Single().Id);
            var set = await service.SetSecretAsync(new FleetSecretSetRequest(epoch, linkId, FleetSecretKinds.Secret, "GRAFANA_TOKEN", FleetSecretScopeKinds.Person, null,
                GrafanaToken, false, [nodeId]), token);
            Assert.AreEqual("ok", set.Status, set.Message);

            // A session on the node, started from this desktop through the relay of the server: its shell gets the secret.
            var identity = DeviceIdentity.LoadOrCreate(host.CatalogOptions.FleetRoot);
            var connector = servers.Connections.Single().Connector!;
            await using var node = await WaitForAsync(async () =>
            {
                try { return await FleetNodeClient.ConnectRelayedAsync(connector, nodeId, identity, null, token); }
                catch (Exception exception) when (exception is not OperationCanceledException) { return null; }
            }, token);
            // The server tells the node its values changed: the node takes its copy, protected in its store, at once.
            var nodeStore = new CodeAlta.Catalog.Secrets.LocalSecretStore(nodeRoot);
            await WaitForAsync(() => Task.FromResult(nodeStore.ListFleetCopies().Any(static copy => copy.Name == "GRAFANA_TOKEN") ? "taken" : null), token);
            Assert.IsFalse(File.ReadAllText(Path.Combine(nodeRoot, "secrets", "store.json")).Contains(GrafanaToken, StringComparison.Ordinal));
            var first = await node.CreateSessionAsync(new FleetCreateSessionParams(Prompt: "Show the token.", Selection: new FleetModelSelection("fake", "fake-model")),
                cancellationToken: token);
            await AllowShellAsync(node, first.SessionId, token);
            var shown = await WaitForAsync(() => Task.FromResult(model.ToolOutputs.FirstOrDefault(static text => text.Contains("grafana=", StringComparison.Ordinal))), token);
            StringAssert.Contains(shown, "grafana=***", $"the value reached the shell, and came back masked: {shown}");
            Assert.IsFalse(model.Requests.Any(static body => body.Contains(GrafanaToken, StringComparison.Ordinal)), "the model never saw the value");

            // An environment that requires the owner's approval: the session starts once this desktop approved it.
            var owner = (await service.SecretsAsync(new FleetServerLinkRequest(epoch, linkId), token)).People.Single().Id;
            Assert.AreEqual("ok", (await service.SetEnvironmentAsync(new FleetEnvironmentSetRequest(epoch, linkId, "production", [owner], [], [], false), token)).Status);
            var production = (await service.SecretsAsync(new FleetServerLinkRequest(epoch, linkId), token)).Environments.Single(static environment => environment.Name == "production");
            Assert.IsTrue(production.Verified);
            Assert.AreEqual("ok", (await service.SetSecretAsync(new FleetSecretSetRequest(epoch, linkId, FleetSecretKinds.Secret, "DEPLOY_TOKEN", FleetSecretScopeKinds.Environment,
                production.Id, DeployToken, false, [nodeId]), token)).Status);
            await WaitForAsync(() => Task.FromResult(nodeStore.ListFleetCopies().Any(static copy => copy.Name == "DEPLOY_TOKEN") ? "taken" : null), token);
            var second = await node.CreateSessionAsync(new FleetCreateSessionParams(Prompt: "Deploy.", Selection: new FleetModelSelection("fake", "fake-model"))
                { Environment = "production" }, cancellationToken: token);
            var waiting = await WaitForAsync(async () => (await service.SecretsAsync(new FleetServerLinkRequest(epoch, linkId), token)).Approvals
                .FirstOrDefault(approval => approval.RunId == second.SessionId && approval.MayApprove), token);
            Assert.AreEqual(("production", "e2e-node", "pending"), (waiting.Environment, waiting.Machine, waiting.Status));
            Assert.AreEqual(1, model.ToolOutputs.Count, "nothing of the second session runs before the approval");
            Assert.AreEqual("ok", (await service.ApproveRunAsync(new FleetApprovalActionRequest(epoch, linkId, waiting.Id), token)).Status);
            await AllowShellAsync(node, second.SessionId, token);
            var deployed = await WaitForAsync(() => Task.FromResult(model.ToolOutputs.Skip(1).FirstOrDefault()), token);
            StringAssert.Contains(deployed, "deploy=***", $"the value of the environment reached the shell, masked: {deployed}");
            StringAssert.Contains(deployed, "grafana=***");

            // Each use is published, the run named, never the value; nothing in clear on the server or in the node's journals.
            var grafana = (await service.SecretsAsync(new FleetServerLinkRequest(epoch, linkId), token)).Secrets.Single(static secret => secret.Name == "GRAFANA_TOKEN");
            var uses = await WaitForAsync(async () => (await servers.SecretsOf(linkId).ListUsesAsync(grafana.Id, token)).Uses is { Count: >= 2 } found ? found : null, token);
            CollectionAssert.IsSubsetOf(new[] { first.SessionId, second.SessionId }, uses.Select(static use => use.RunId).ToArray());
            await model.DisposeAsync();
            foreach (var value in new[] { GrafanaToken, DeployToken })
            {
                Assert.IsFalse(FolderHolds(serverRoot, value), "the server never holds a value");
                Assert.IsFalse(FolderHolds(Path.Combine(nodeRoot, "sessions"), value), "the journals of the node hold the masked output only");
            }

            TestContext.WriteLine($"Masked output of the node: {shown.Trim()} | {deployed.Trim()}");
            TestContext.WriteLine($"Uses published: {string.Join(", ", uses.Select(static use => $"{use.Name} v{use.Version} by {use.RunId}"))}");
        }
        finally
        {
            foreach (var process in processes)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                process.Dispose();
            }

            foreach (var line in output) TestContext.WriteLine(line);
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // A node never approves what an agent asks by itself: the owner's desktop allows the shell command, through the relay.
    private async Task AllowShellAsync(FleetNodeClient node, string sessionId, CancellationToken token)
    {
        var request = await WaitForAsync(async () => (await node.ListPermissionsAsync(sessionId, token)).Requests.FirstOrDefault(), token);
        Assert.AreEqual("resolved", (await node.AnswerPermissionAsync(sessionId, request.Id, "allow_once", token)).Status);
    }

    private static string? ReadSetupCode(string serverRoot)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(serverRoot, "setup-code.json")));
            return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool FolderHolds(string folder, string text)
    {
        if (!Directory.Exists(folder)) return false;
        var bytes = Encoding.UTF8.GetBytes(text);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".lock", StringComparison.Ordinal)) continue;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                if (copy.GetBuffer().AsSpan(0, (int)copy.Length).IndexOf(bytes) >= 0) return true;
            }
            catch (IOException)
            {
                // A file held without sharing by its writer holds no value in clear: values are only written protected.
            }
        }

        return false;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static Process Launch(string program, string[] arguments, ConcurrentQueue<string> output, string name)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, line) => { if (line.Data is { } data) output.Enqueue($"{name}: {data}"); };
        process.ErrorDataReceived += (_, line) => { if (line.Data is { } data) output.Enqueue($"{name}! {data}"); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task<int> RunAsync(string program, string[] arguments, ConcurrentQueue<string> output, CancellationToken token)
    {
        using var process = Launch(program, arguments, output, Path.GetFileNameWithoutExtension(program));
        await process.WaitForExitAsync(token);
        return process.ExitCode;
    }

    private static FleetConnectorOptions Fast(FleetConnectorOptions options) => new()
    {
        ServerAddress = options.ServerAddress, Identity = options.Identity, OutboxPath = options.OutboxPath, Client = options.Client, Source = options.Source,
        Statistics = options.Statistics, StatisticsFromQuarter = options.StatisticsFromQuarter, PublishesSpace = options.PublishesSpace, WatchDirectory = options.WatchDirectory,
        MetadataKeys = options.MetadataKeys, Devices = options.Devices, DeviceListPinPath = options.DeviceListPinPath, KeyRefreshInterval = TimeSpan.FromSeconds(1),
        MinimumBackoff = TimeSpan.FromMilliseconds(100), MaximumBackoff = TimeSpan.FromSeconds(1), PublishDelay = TimeSpan.FromMilliseconds(20),
        PollInterval = TimeSpan.FromMilliseconds(250), PendingApprovalRetry = TimeSpan.FromMilliseconds(200),
    };

    private static async Task<T> WaitForAsync<T>(Func<Task<T?>> read, CancellationToken cancellationToken) where T : class
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            if (await read() is { } value) return value;
            if (DateTime.UtcNow > deadline) Assert.Fail("It did not get there in time.");
            await Task.Delay(250, cancellationToken);
        }
    }

    /// <summary>
    /// A model server that speaks the Chat Completions API of OpenAI, streamed: the first turn of a session asks for a shell
    /// command that prints the secrets the session's profile gives; the turn that follows the result of a tool answers.
    /// </summary>
    private sealed class FakeChatServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        private FakeChatServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Address = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
            _loop = Task.Run(AcceptAsync);
        }

        public Uri Address { get; }

        public ConcurrentQueue<string> Requests { get; } = new();

        public ConcurrentQueue<string> ToolOutputs { get; } = new();

        public static FakeChatServer Start() => new();

        public async ValueTask DisposeAsync()
        {
            if (_stop.IsCancellationRequested) return;
            await _stop.CancelAsync();
            _listener.Stop();
            try { await _loop; }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var (path, body) = await ReadRequestAsync(stream);
                    if (path.EndsWith("/models", StringComparison.Ordinal))
                    {
                        await WriteAsync(stream, "application/json", "{\"object\":\"list\",\"data\":[{\"id\":\"fake-model\",\"object\":\"model\",\"created\":0,\"owned_by\":\"test\"}]}");
                        return;
                    }

                    Requests.Enqueue(body);
                    using var document = JsonDocument.Parse(body);
                    var tool = document.RootElement.GetProperty("messages").EnumerateArray().LastOrDefault(static message => message.GetProperty("role").GetString() == "tool");
                    var sse = new StringBuilder();
                    void Chunk(string delta, string? finish) => sse.Append("data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"fake-model\",\"choices\":[{\"index\":0,\"delta\":")
                        .Append(delta).Append(",\"finish_reason\":").Append(finish is null ? "null" : "\"" + finish + "\"").Append("}]}\n\n");
                    if (tool.ValueKind == JsonValueKind.Object)
                    {
                        ToolOutputs.Enqueue(tool.GetProperty("content").ToString());
                        Chunk("{\"role\":\"assistant\",\"content\":\"Done.\"}", null);
                        Chunk("{}", "stop");
                    }
                    else
                    {
                        var command = OperatingSystem.IsWindows()
                            ? "Write-Output \"grafana=$env:GRAFANA_TOKEN deploy=$env:DEPLOY_TOKEN\""
                            : "echo \"grafana=$GRAFANA_TOKEN deploy=$DEPLOY_TOKEN\"";
                        var arguments = JsonSerializer.Serialize(JsonSerializer.Serialize(new Dictionary<string, string> { ["command"] = command }));
                        Chunk("{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"shell_command\",\"arguments\":"
                            + arguments + "}}]}", null);
                        Chunk("{}", "tool_calls");
                    }

                    sse.Append("data: [DONE]\n\n");
                    await WriteAsync(stream, "text/event-stream", sse.ToString());
                }
                catch (Exception exception) when (exception is IOException or SocketException or JsonException or KeyNotFoundException or InvalidOperationException)
                {
                    // A request this fake does not read gets no answer.
                }
            }
        }

        private static async Task<(string Path, string Body)> ReadRequestAsync(NetworkStream stream)
        {
            var head = new List<byte>();
            var one = new byte[1];
            while (!(head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n'))
            {
                if (await stream.ReadAsync(one) == 0) throw new IOException("The request ended.");
                head.Add(one[0]);
            }

            var lines = Encoding.ASCII.GetString([.. head]).Split("\r\n");
            var path = lines[0].Split(' ')[1];
            var headers = lines.Skip(1).Where(static line => line.Contains(':')).ToDictionary(static line => line[..line.IndexOf(':')].Trim(),
                static line => line[(line.IndexOf(':') + 1)..].Trim(), StringComparer.OrdinalIgnoreCase);
            using var body = new MemoryStream();
            if (headers.TryGetValue("Content-Length", out var length) && int.TryParse(length, out var size))
            {
                var buffer = new byte[size];
                var read = 0;
                while (read < size) read += await stream.ReadAsync(buffer.AsMemory(read)) is > 0 and var count ? count : throw new IOException("The body ended.");
                body.Write(buffer);
            }
            else if (headers.TryGetValue("Transfer-Encoding", out var encoding) && encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                while (true)
                {
                    var sizeLine = await ReadLineAsync(stream);
                    var chunk = Convert.ToInt32(sizeLine.Split(';')[0], 16);
                    if (chunk == 0)
                    {
                        await ReadLineAsync(stream);
                        break;
                    }

                    var buffer = new byte[chunk];
                    var read = 0;
                    while (read < chunk) read += await stream.ReadAsync(buffer.AsMemory(read)) is > 0 and var count ? count : throw new IOException("The body ended.");
                    body.Write(buffer);
                    await ReadLineAsync(stream);
                }
            }

            return (path, Encoding.UTF8.GetString(body.ToArray()));
        }

        private static async Task<string> ReadLineAsync(NetworkStream stream)
        {
            var line = new List<byte>();
            var one = new byte[1];
            while (true)
            {
                if (await stream.ReadAsync(one) == 0) throw new IOException("The request ended.");
                if (one[0] == '\n') return Encoding.ASCII.GetString([.. line]).TrimEnd('\r');
                line.Add(one[0]);
            }
        }

        private static async Task WriteAsync(NetworkStream stream, string contentType, string content)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head);
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
        }
    }
}
