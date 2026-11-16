using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Node.Tests;

public sealed partial class NodeClientCommandTests
{
    [TestMethod]
    public async Task Mobile_TurnsThePageOn_ShowsACodeForOnePhone_AndTurnsItOff()
    {
        using var root = new NodeTestRoot();
        var token = TestContext.CancellationToken;
        var page = Path.Combine(root.Root, "mobile-page");
        Directory.CreateDirectory(page);
        var html = System.Text.Encoding.UTF8.GetBytes("<!doctype html><p>Phone</p>");
        File.WriteAllBytes(Path.Combine(page, "mobile.html"), html);
        File.WriteAllText(Path.Combine(page, "files.json"),
            $$"""{"files":[{"path":"mobile.html","bytes":{{html.Length}},"sha256":"{{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(html))}}"}]}""");
        var network = new TestNetwork();
        var started = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runError = new CapturingWriter();
        var node = new NodeApplication(new CapturingWriter(), runError)
        {
            InitializeLogging = false,
            // The plugins run, as on a node: the Mobile one publishes through a network of the test, never a real one.
            ConfigureHost = options => root.Isolate(options) with { StartPlugins = true, MobilePageRoot = page, PrivateNetworks = network },
            Started = (_, stop) => started.TrySetResult(stop),
        };
        var running = node.RunAsync(["run", "--data-root", root.DataRoot, "--port", "0", "--name", "test-node"]);
        var stop = await started.Task.WithinAsync(Patience, token);
        try
        {
            var (code, output, error) = await RunAsync(["mobile", "--data-root", root.DataRoot]);
            Assert.AreEqual(NodeExitCodes.Success, code, error);
            StringAssert.StartsWith(output, "Mobile: off.");

            (code, _, error) = await RunAsync(["mobile", "reset", "--data-root", root.DataRoot]);
            Assert.AreEqual(NodeExitCodes.Failure, code);
            StringAssert.Contains(error, "altanode mobile on");

            var port = FreePort();
            (code, output, error) = await RunAsync(["mobile", "on", "--port", port.ToString(), "--https-port", "8443", "--data-root", root.DataRoot]);
            Assert.AreEqual(NodeExitCodes.Success, code, error);
            StringAssert.Contains(output, "Mobile: served at https://node.test:8443 through Test network.");
            StringAssert.Contains(output, "within 5 minutes; it connects one phone");
            StringAssert.Contains(output, "\u2588", "the QR code is drawn");
            StringAssert.Contains(output, "No phone is connected yet.");
            Assert.AreEqual(port, network.Published?.LocalPort);
            var first = CodePattern().Match(output).Groups["token"].Value;
            Assert.IsTrue(first.Length >= 40, output);

            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            // The code connects one phone: a second one that scans the same code is refused.
            using var signedIn = await http.PostAsJsonAsync("/api/login", new JsonObject { ["token"] = first }, token);
            Assert.AreEqual(HttpStatusCode.OK, signedIn.StatusCode);
            using var again = await http.PostAsJsonAsync("/api/login", new JsonObject { ["token"] = first }, token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, again.StatusCode);

            // Each time it is asked for, the code is a new one, and the one of before connects nobody.
            (code, output, error) = await RunAsync(["mobile", "--data-root", root.DataRoot]);
            Assert.AreEqual(NodeExitCodes.Success, code, error);
            StringAssert.Contains(output, "1 phone is connected. altanode mobile reset disconnects every phone.");
            var second = CodePattern().Match(output).Groups["token"].Value;
            Assert.AreNotEqual(first, second);
            (code, output, error) = await RunAsync(["mobile", "--data-root", root.DataRoot]);
            Assert.AreEqual(NodeExitCodes.Success, code, error);
            using var replaced = await http.PostAsJsonAsync("/api/login", new JsonObject { ["token"] = second }, token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, replaced.StatusCode);

            (code, output, error) = await RunAsync(["mobile", "reset", "--data-root", root.DataRoot]);
            Assert.AreEqual(NodeExitCodes.Success, code, error);
            (code, output, error) = await RunAsync(["mobile", "--data-root", root.DataRoot]);
            StringAssert.Contains(output, "No phone is connected yet.");

            // A machine that lets another person in serves no phone, and refuses to be asked.
            var control = NodeLocalControl.Read(Hosting.CodeAltaInstanceProfile.Create(root.DataRoot, developer: false));
            var (client, identity) = await control.ConnectAsync(token);
            using (identity)
            await using (client)
            {
                await client.SetPeopleAsync(new FleetNodePeopleChange("person-1", ["view"]), token);
            }

            (code, _, error) = await RunAsync(["mobile", "on", "--data-root", root.DataRoot]);
            Assert.AreEqual(NodeExitCodes.Failure, code);
            StringAssert.Contains(error, "lets other people in");
            await WaitForAsync(async () =>
            {
                var (_, shown, _) = await RunAsync(["mobile", "--data-root", root.DataRoot]);
                return shown.StartsWith("Mobile: off.", StringComparison.Ordinal) ? shown : null;
            }, token);
            StringAssert.Contains((await RunAsync(["mobile", "--data-root", root.DataRoot])).Output, "lets other people in");

            (code, _, error) = await RunAsync(["mobile", "set", "--data-root", root.DataRoot]);
            Assert.AreEqual(NodeExitCodes.InvalidUsage, code);
            (code, _, error) = await RunAsync(["mobile", "reset", "--port", "3000", "--data-root", root.DataRoot]);
            Assert.AreEqual(NodeExitCodes.InvalidUsage, code);
        }
        finally
        {
            stop();
        }

        Assert.AreEqual(NodeExitCodes.Success, await running.WithinAsync(Patience, token), runError.Text);
        Assert.IsNull(network.Published, "what was published goes with the plugin");
    }

    [TestMethod]
    public async Task Mobile_SaysWhenNoNodeRuns_AndWhenTheNodeRunsWithoutPlugins()
    {
        using var root = new NodeTestRoot();
        var token = TestContext.CancellationToken;
        var (code, _, error) = await RunAsync(["mobile", "--data-root", root.DataRoot]);
        Assert.AreEqual(NodeExitCodes.Failure, code);
        StringAssert.Contains(error, "no node runs on this profile");

        var started = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runError = new CapturingWriter();
        var node = new NodeApplication(new CapturingWriter(), runError) { InitializeLogging = false, ConfigureHost = root.Isolate, Started = (_, stop) => started.TrySetResult(stop) };
        var running = node.RunAsync(["run", "--data-root", root.DataRoot, "--port", "0"]);
        var stop = await started.Task.WithinAsync(Patience, token);
        try
        {
            (code, _, error) = await RunAsync(["mobile", "on", "--data-root", root.DataRoot]);
            Assert.AreEqual(NodeExitCodes.Failure, code);
            StringAssert.Contains(error, "--no-plugins");
        }
        finally
        {
            stop();
        }

        Assert.AreEqual(NodeExitCodes.Success, await running.WithinAsync(Patience, token), runError.Text);
    }

    [TestMethod]
    public void Mobile_DrawsTheCodeDarkOnLight_TwoRowsALine()
    {
        var drawn = NodeMobileCommands.Draw(["101", "010", "111"]);
        var lines = drawn.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        // Three rows and a margin of two around them: seven rows, four lines.
        Assert.AreEqual(4, lines.Length);
        Assert.IsTrue(lines.All(static line => line.Contains("\u001b[30;47m", StringComparison.Ordinal) && line.EndsWith("\u001b[0m", StringComparison.Ordinal)));
        StringAssert.Contains(lines[1], "  \u2580\u2584\u2580  ", "rows 0 and 1: 101 over 010");
        StringAssert.Contains(lines[2], "  \u2580\u2580\u2580  ", "row 2 over the margin");
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [GeneratedRegex(@"Or open on the phone: https://node\.test:8443/#k=(?<token>[\w-]+)")]
    private static partial Regex CodePattern();

    // A private network of the test: it publishes at once, at a name of its own, and remembers what it was given.
    private sealed class TestNetwork : IPluginPrivateNetworkService, IPrivateNetworkProvider, IPrivateNetworkPublisher
    {
        public PrivateNetworkPublication? Published { get; private set; }

        public IReadOnlyList<IPrivateNetworkProvider> Providers => [this];

        public string Id => "tailscale";

        public string DisplayName => "Test network";

        public PrivateNetworkCapabilities Capabilities => PrivateNetworkCapabilities.Publish | PrivateNetworkCapabilities.PublicPort;

        public IPrivateNetworkPublisher? CreatePublisher(IPrivateNetworkHost host) => this;

        public Task<PrivateNetworkStatus> PublishAsync(PrivateNetworkPublication publication, CancellationToken cancellationToken)
        {
            Published = publication;
            return Task.FromResult(new PrivateNetworkStatus("serving", $"https://node.test:{publication.PublicPort}", "Served."));
        }

        public Task UnpublishAsync(CancellationToken cancellationToken)
        {
            Published = null;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
