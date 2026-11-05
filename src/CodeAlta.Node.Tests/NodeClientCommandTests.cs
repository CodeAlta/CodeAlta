using System.Text;
using System.Text.RegularExpressions;

namespace CodeAlta.Node.Tests;

/// <summary>
/// The commands of <c>altanode</c> end to end: a node that runs (<c>run --port 0</c>), its owner's <c>pair</c>, and a
/// viewer of another profile that connects, lists its machines and reads and drives the node's sessions.
/// </summary>
[TestClass]
public sealed partial class NodeClientCommandTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Pair_Connect_Machines_AndRemote_WorkAgainstARunningNode()
    {
        using var root = new NodeTestRoot();
        var token = TestContext.CancellationToken;
        var viewerRoot = Path.Combine(root.Root, "viewer-alta");
        var provider = new ScriptedNodeProvider();
        var started = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runOutput = new CapturingWriter();
        var runError = new CapturingWriter();
        var node = new NodeApplication(runOutput, runError)
        {
            InitializeLogging = false,
            ConfigureHost = options => root.Isolate(options) with { ConfigureModelProviders = provider.Register },
            Started = (_, stop) => started.TrySetResult(stop),
        };
        var running = node.RunAsync(["run", "--data-root", root.DataRoot, "--port", "0", "--name", "test-node"]);
        var stop = await started.Task.WaitAsync(Patience, token);
        try
        {
            var pairOutput = new CapturingWriter();
            var pairError = new CapturingWriter();
            var pairing = Client(pairOutput, pairError).RunAsync(["pair", "--data-root", root.DataRoot, "--minutes", "1"]);
            var link = await WaitForAsync(() => LinkPattern().Match(pairOutput.Text) is { Success: true } match ? match.Value : null, pairing, pairError, token);

            var (code, output, error) = await RunAsync(["connect", link, "--data-root", viewerRoot, "--name", "viewer"]);
            Assert.AreEqual(NodeExitCodes.Success, code, error);
            StringAssert.Contains(output, "Paired with test-node");
            Assert.AreEqual(NodeExitCodes.Success, await pairing.WaitAsync(Patience, token), pairError.Text);
            StringAssert.Contains(pairOutput.Text, "Paired with viewer");

            (code, output, error) = await RunAsync(["machines", "--data-root", viewerRoot]);
            Assert.AreEqual(NodeExitCodes.Success, code, error);
            StringAssert.Contains(output, "test-node");
            StringAssert.Contains(output, "view, drive, start");

            (code, output, error) = await RunAsync(["remote", "test-node", "info", "--data-root", viewerRoot]);
            Assert.AreEqual(NodeExitCodes.Success, code, error);
            StringAssert.Contains(output, "test-node (");

            (code, output, error) = await RunAsync(["remote", "test-node", "create", "Hello", "node", "--data-root", viewerRoot]);
            Assert.AreEqual(NodeExitCodes.Success, code, error);
            var sessionId = SessionPattern().Match(output).Groups["id"].Value;
            Assert.IsFalse(string.IsNullOrEmpty(sessionId), output);
            StringAssert.Contains(output, "its prompt: accepted");

            (code, output, error) = await RunAsync(["remote", "test-node", "sessions", "--data-root", viewerRoot]);
            Assert.AreEqual(NodeExitCodes.Success, code, error);
            StringAssert.Contains(output, sessionId);

            var history = await WaitForAsync(async () =>
            {
                var (read, text, _) = await RunAsync(["remote", "test-node", "history", sessionId, "--data-root", viewerRoot]);
                return read == NodeExitCodes.Success && text.Contains("Done.", StringComparison.Ordinal) ? text : null;
            }, token);
            StringAssert.Contains(history, "Hello node");

            (code, output, error) = await RunAsync(["remote", "test-node", "watch", sessionId, "--count", "1", "--data-root", viewerRoot]);
            Assert.AreEqual(NodeExitCodes.Success, code, error);
            StringAssert.Contains(output, $"Watching {sessionId}");

            (code, _, error) = await RunAsync(["remote", "unknown-machine", "sessions", "--data-root", viewerRoot]);
            Assert.AreEqual(NodeExitCodes.Failure, code);
            StringAssert.Contains(error, "no paired machine");
        }
        finally
        {
            stop();
        }

        Assert.AreEqual(NodeExitCodes.Success, await running.WaitAsync(Patience, token), runError.Text);
        StringAssert.Contains(runOutput.Text, "test-node (");
        Assert.IsFalse(File.Exists(Path.Combine(root.DataRoot, "fleet", NodeFleet.ListenerFileName)), "The listener file goes with the node.");
    }

    [TestMethod]
    public async Task Pair_WithoutARunningNode_SaysSo()
    {
        using var root = new NodeTestRoot();
        var output = new CapturingWriter();
        var error = new CapturingWriter();

        Assert.AreEqual(NodeExitCodes.Failure, await Client(output, error).RunAsync(["pair", "--data-root", root.DataRoot]));
        StringAssert.Contains(error.Text, "no node runs on");
    }

    [TestMethod]
    public async Task Connect_RefusesTextThatIsNoPairingLink()
    {
        using var root = new NodeTestRoot();
        var output = new CapturingWriter();
        var error = new CapturingWriter();

        Assert.AreEqual(NodeExitCodes.InvalidUsage, await Client(output, error).RunAsync(["connect", "https://example.com", "--data-root", root.DataRoot]));
        StringAssert.Contains(error.Text, "not a CodeAlta pairing link");
    }

    private NodeApplication Client(TextWriter output, TextWriter error)
        => new(output, error) { InitializeLogging = false, ClientStop = TestContext.CancellationToken, ServiceEnvironment = static () => null };

    private async Task<(int Code, string Output, string Error)> RunAsync(string[] args)
    {
        var output = new CapturingWriter();
        var error = new CapturingWriter();
        var code = await Client(output, error).RunAsync(args).WaitAsync(Patience, TestContext.CancellationToken);
        return (code, output.Text, error.Text);
    }

    private static async Task<string> WaitForAsync(Func<string?> read, Task<int> command, CapturingWriter error, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            if (read() is { } value) return value;
            if (command.IsCompleted) Assert.Fail($"The command ended with {command.Result}: {error.Text}");
            if (DateTime.UtcNow > deadline) Assert.Fail("The command never printed what was expected.");
            await Task.Delay(50, cancellationToken);
        }
    }

    private static async Task<string> WaitForAsync(Func<Task<string?>> read, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            if (await read() is { } value) return value;
            if (DateTime.UtcNow > deadline) Assert.Fail("The node never gave what was expected.");
            await Task.Delay(100, cancellationToken);
        }
    }

    [GeneratedRegex(@"codealta-pair://\S+")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"Created session (?<id>\S+) on")]
    private static partial Regex SessionPattern();

    // A writer a test reads while a command writes to it.
    private sealed class CapturingWriter : TextWriter
    {
        private readonly Lock _gate = new();
        private readonly StringBuilder _text = new();

        public override Encoding Encoding => Encoding.UTF8;

        public string Text
        {
            get
            {
                lock (_gate) return _text.ToString();
            }
        }

        public override void Write(char value)
        {
            lock (_gate) _text.Append(value);
        }

        public override void Write(string? value)
        {
            lock (_gate) _text.Append(value);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            lock (_gate) _text.Append(buffer, index, count);
        }

        public override Task WriteAsync(string? value)
        {
            Write(value);
            return Task.CompletedTask;
        }

        public override Task WriteLineAsync(string? value)
        {
            WriteLine(value);
            return Task.CompletedTask;
        }

        public override Task WriteLineAsync()
        {
            WriteLine();
            return Task.CompletedTask;
        }

        public override Task FlushAsync() => Task.CompletedTask;

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
