using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using CodeAlta.Orchestration.Hosting;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// Budgets end to end (<c>doc/fleet/observability.md</c>, budgets and alerts) with the real programs: <c>altaserver</c> and
/// <c>altanode</c> run as processes of their own, on isolated data roots and ports that are not their defaults; this desktop
/// drives the <c>fleetServer</c> service of its window; a fake model answers every request with 1000 tokens. The owner sets an
/// enforced budget of 500 tokens a month from this desktop; a first session on the node spends past it; the node counts it, the
/// server tells the owner at 80% and 100%; a second session is held by the node before its model is called, and starts once this
/// desktop signed Continue anyway. Run with <c>CODEALTA_FLEET_E2E=1</c> after a build of the solution; skipped otherwise.
/// </summary>
[TestClass]
public sealed class FleetBudgetsEndToEndTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(150);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ARunOverItsBudget_IsHeldOnTheNode_AndStartsOnceTheDesktopContinuesItAnyway()
    {
        if (Environment.GetEnvironmentVariable("CODEALTA_FLEET_E2E") != "1") Assert.Inconclusive("Set CODEALTA_FLEET_E2E=1 to run the programs of the build.");
        var bin = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var exe = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        var altaserver = Path.Combine(bin, "CodeAlta.Server", "bin", configuration, "net10.0", "altaserver" + exe);
        var altanode = Path.Combine(bin, "CodeAlta.Node", "bin", configuration, "net10.0", "altanode" + exe);
        if (!File.Exists(altaserver) || !File.Exists(altanode)) Assert.Inconclusive($"Build the solution first: {altaserver}, {altanode}.");

        var token = TestContext.CancellationToken;
        var root = Directory.CreateTempSubdirectory("codealta-fleet-budgets-e2e-").FullName;
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

            // This desktop: the owner's controller device, which makes the server, approves the node and sets the budget.
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

            // The node: a fake model as its only provider, which says how many tokens each request took.
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
            Assert.AreEqual(0, await RunAsync(altanode, ["join", address.ToString(), "--token", enrollment.Token!, "--data-root", nodeRoot, "--name", "e2e-node", "--no-wait"], output, token));
            processes.Add(Launch(altanode, ["run", "--data-root", nodeRoot, "--no-listen", "--name", "e2e-node"], output, "altanode"));
            var pending = await WaitForAsync(async () => (await service.DevicesAsync(new FleetServerLinkRequest(epoch, linkId), token)).Devices
                .FirstOrDefault(static device => device.Status == "pending"), token);
            Assert.AreEqual("ok", (await service.ApproveAsync(new FleetServerDeviceRequest(epoch, linkId, pending.Id, null, pending.Kind), token)).Status);
            var nodeId = pending.Id;

            // An enforced budget of 500 tokens a month for all the work of the person, which holds new runs once reached.
            await WaitForAsync(async () => (await service.BudgetsAsync(new FleetServerLinkRequest(epoch, linkId), token)) is { Status: "ok", Machines.Length: > 0 } read ? read : null, token);
            var set = await service.SetBudgetAsync(new FleetBudgetSetRequest(epoch, linkId, null, "Home tokens", FleetBudgetScopeKinds.Person, null, FleetBudgetMeasures.Tokens, 500,
                FleetBudgetPeriods.Month, FleetBudgetModes.Enforced, FleetBudgetReachedActions.Hold), token);
            Assert.AreEqual("ok", set.Status, set.Message);

            // A first session on the node, from this desktop through the relay of the server: the node holds a share of the budget, runs, spends 1000 tokens.
            var identity = DeviceIdentity.LoadOrCreate(host.CatalogOptions.FleetRoot);
            var connector = servers.Connections.Single().Connector!;
            await using var node = await WaitForAsync(async () =>
            {
                try { return await FleetNodeClient.ConnectRelayedAsync(connector, nodeId, identity, null, token); }
                catch (Exception exception) when (exception is not OperationCanceledException) { return null; }
            }, token);
            var first = await node.CreateSessionAsync(new FleetCreateSessionParams(Prompt: "Write the report.", Selection: new FleetModelSelection("fake", "fake-model")),
                cancellationToken: token);
            Assert.AreEqual("accepted", first.Send?.Status, "under its budget, the first run starts at once");
            await WaitForAsync(() => Task.FromResult(model.Requests.Count >= 1 ? "called" : null), token);

            // The node counts the tokens and publishes them; the server sums them: the budget is reached, and its owner is told once at 80% and at 100%.
            var (overview, home) = await WaitForValueAsync(async () => (await service.BudgetsAsync(new FleetServerLinkRequest(epoch, linkId), token)) is { Status: "ok" } read
                && read.Budgets.SingleOrDefault() is { State: FleetBudgetStates.Reached } budget ? (read, budget) : ((FleetBudgetsOverview, FleetBudgetView)?)null, token);
            Assert.IsGreaterThanOrEqualTo(500m, home.Used);
            CollectionAssert.IsSubsetOf(new[] { FleetNotificationKinds.BudgetWarning, FleetNotificationKinds.BudgetReached }, overview.Notifications.Select(static n => n.Kind).ToArray());

            // A second session: the node holds its run before the model is called.
            var second = await node.CreateSessionAsync(new FleetCreateSessionParams(Title: "Second report", Prompt: "Write another report.",
                Selection: new FleetModelSelection("fake", "fake-model")), cancellationToken: token);
            Assert.AreEqual(FleetBudgetCommandStatuses.Held, second.Send?.Status, "over its budget, the run waits");
            var hold = await WaitForAsync(async () => (await service.BudgetsAsync(new FleetServerLinkRequest(epoch, linkId), token)).Holds
                .FirstOrDefault(static hold => hold.Status == FleetBudgetHoldStates.Waiting), token);
            Assert.AreEqual(("Home tokens", "e2e-node", second.SessionId), (hold.BudgetName, hold.Machine, hold.RunId));
            await Task.Delay(TimeSpan.FromSeconds(2), token);
            Assert.AreEqual(1, model.Requests.Count, "nothing of the second session runs while it waits");

            // Continue anyway, signed by this desktop: the node verifies it and the run starts.
            Assert.AreEqual("ok", (await service.ContinueRunAsync(new FleetSecretRefRequest(epoch, linkId, hold.Id), token)).Status);
            await WaitForAsync(() => Task.FromResult(model.Requests.Count >= 2 ? "called" : null), token);
            var decided = (await service.BudgetsAsync(new FleetServerLinkRequest(epoch, linkId), token)).Holds.Single(other => other.Id == hold.Id);
            Assert.AreEqual(FleetBudgetHoldStates.Continued, decided.Status);

            TestContext.WriteLine($"Budget {home.Name}: {home.Used} of {home.Limit} tokens, {home.State}; notifications: {string.Join(", ", overview.Notifications.Select(static n => n.Kind))}.");
            TestContext.WriteLine($"Second run {second.SessionId}: {second.Send?.Status}, hold {hold.Id} {decided.Status}; model requests: {model.Requests.Count}.");
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

    // Connectors that poll often and keep the events of the budgets, which wake what waits on this desktop.
    private static FleetConnectorOptions Fast(FleetConnectorOptions options) => new()
    {
        ServerAddress = options.ServerAddress, Identity = options.Identity, OutboxPath = options.OutboxPath, Client = options.Client, Source = options.Source,
        Statistics = options.Statistics, StatisticsFromQuarter = options.StatisticsFromQuarter, PublishesSpace = options.PublishesSpace, WatchDirectory = options.WatchDirectory,
        MetadataKeys = options.MetadataKeys, Devices = options.Devices, DeviceListPinPath = options.DeviceListPinPath, KeyRefreshInterval = TimeSpan.FromSeconds(1),
        MinimumBackoff = TimeSpan.FromMilliseconds(100), MaximumBackoff = TimeSpan.FromSeconds(1), PublishDelay = TimeSpan.FromMilliseconds(20),
        PollInterval = TimeSpan.FromMilliseconds(250), PendingApprovalRetry = TimeSpan.FromMilliseconds(200), BudgetsChanged = options.BudgetsChanged,
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

    private static async Task<T> WaitForValueAsync<T>(Func<Task<T?>> read, CancellationToken cancellationToken) where T : struct
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            if (await read() is { } value) return value;
            if (DateTime.UtcNow > deadline) Assert.Fail("It did not get there in time.");
            await Task.Delay(500, cancellationToken);
        }
    }

    /// <summary>A model server that speaks the Chat Completions API of OpenAI, streamed: it answers each request, and says it took 800 tokens in and 200 out.</summary>
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
                    var sse = new StringBuilder();
                    sse.Append("data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"fake-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Done.\"},\"finish_reason\":null}]}\n\n");
                    sse.Append("data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"fake-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
                    sse.Append("data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"fake-model\",\"choices\":[],\"usage\":{\"prompt_tokens\":800,\"completion_tokens\":200,\"total_tokens\":1000}}\n\n");
                    sse.Append("data: [DONE]\n\n");
                    await WriteAsync(stream, "text/event-stream", sse.ToString());
                }
                catch (Exception exception) when (exception is IOException or SocketException or JsonException or InvalidOperationException)
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
