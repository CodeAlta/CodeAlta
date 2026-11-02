using CodeAlta.Hosting;

namespace CodeAlta.Node.Tests;

[TestClass]
public sealed class NodeApplicationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Run_StartsTheNode_AndStopsGracefully_ReleasingTheLock()
    {
        using var root = new NodeTestRoot();
        NodeHost? started = null;
        var app = Application(root, out var output, out var error, (node, stop) =>
        {
            started = node;
            Assert.IsTrue(NodeProfileStatus.Read(root.Profile()).IsHeld);
            stop();
        });

        var code = await app.RunAsync(["run", "--data-root", root.DataRoot, "--port", "0"]);

        Assert.AreEqual(NodeExitCodes.Success, code, error.ToString());
        Assert.IsNotNull(started);
        Assert.IsFalse(started.AcceptsWork);
        Assert.IsFalse(File.Exists(root.Profile().LockFilePath), "The lock is released once the node stopped.");
        StringAssert.Contains(output.ToString(), "Stopping CodeAlta Node");
    }

    [TestMethod]
    public async Task Run_RefusesAProfileAnotherInstanceHolds()
    {
        using var root = new NodeTestRoot();
        await using var first = await NodeHost.StartAsync(root.Options(), TestContext.CancellationToken);
        var app = Application(root, out _, out var error);

        var code = await app.RunAsync(["run", "--data-root", root.DataRoot]);

        Assert.AreEqual(NodeExitCodes.ProfileInUse, code);
        StringAssert.Contains(error.ToString(), "is in use");
        StringAssert.Contains(error.ToString(), $"PID {Environment.ProcessId}");
        Assert.IsTrue(NodeProfileStatus.Read(root.Profile()).IsHeld, "The first node keeps its lock.");
    }

    [TestMethod]
    public async Task Status_ExitsZeroWhileTheProfileIsHeld_AndOneOtherwise()
    {
        using var root = new NodeTestRoot();
        var app = Application(root, out var output, out _);
        Assert.AreEqual(NodeExitCodes.Failure, await app.RunAsync(["status", "--data-root", root.DataRoot]));
        StringAssert.Contains(output.ToString(), "No CodeAlta instance holds this profile.");

        await using (await NodeHost.StartAsync(root.Options(developer: true), TestContext.CancellationToken))
        {
            Assert.AreEqual(NodeExitCodes.Failure, await app.RunAsync(["status", "--data-root", root.DataRoot]), "The developer instance has a lock of its own.");
            Assert.AreEqual(NodeExitCodes.Success, await app.RunAsync(["status", "--dev", "--data-root", root.DataRoot]));
            StringAssert.Contains(output.ToString(), $"PID {Environment.ProcessId}");
        }

        Assert.AreEqual(NodeExitCodes.Failure, await app.RunAsync(["status", "--dev", "--data-root", root.DataRoot]));
    }

    [TestMethod]
    public void Status_CountsAStaleLockAsFree()
    {
        using var root = new NodeTestRoot();
        var profile = root.Profile();
        // A process id no process has: int.MaxValue is above every id Windows, Linux and macOS give.
        File.WriteAllText(profile.LockFilePath, int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var status = NodeProfileStatus.Read(profile);

        Assert.IsFalse(status.IsHeld);
        Assert.AreEqual(int.MaxValue, status.ProcessId);
    }

    [TestMethod]
    [DataRow("join", "https://fleet.example", "--token", "abc")]
    [DataRow("update")]
    public async Task CommandsOfTheSurface_SayTheyAreNotImplementedYet(params string[] args)
    {
        using var root = new NodeTestRoot();
        var app = Application(root, out _, out var error);

        Assert.AreEqual(NodeExitCodes.NotImplemented, await app.RunAsync(args));
        StringAssert.Contains(error.ToString(), $"altanode {args[0]}: not implemented yet.");
    }

    [TestMethod]
    [DataRow]
    [DataRow("unknown")]
    [DataRow("run", "--unknown")]
    [DataRow("status", "--data-root", "relative/path")]
    [DataRow("run", "--no-listen", "--port", "2585")]
    [DataRow("run", "--no-listen", "--listen-all")]
    [DataRow("run", "--bind", "127.0.0.1", "--listen-all")]
    [DataRow("run", "--port", "70000")]
    [DataRow("run", "--bind", "not-an-address")]
    [DataRow("remote", "machine")]
    [DataRow("connect")]
    public async Task InvalidCommandLines_ExitWithTwo(params string[] args)
    {
        using var root = new NodeTestRoot();
        var app = Application(root, out _, out _);

        Assert.AreEqual(NodeExitCodes.InvalidUsage, await app.RunAsync(args));
    }

    [TestMethod]
    public async Task Help_ListsTheCommandsOfTheNode()
    {
        using var root = new NodeTestRoot();
        var app = Application(root, out var output, out _);

        Assert.AreEqual(NodeExitCodes.Success, await app.RunAsync(["--help"]));
        foreach (var command in new[] { "run", "status", "install", "uninstall", "logs", "pair", "devices", "revoke", "permissions", "join", "machines", "connect", "remote", "update" })
        {
            StringAssert.Contains(output.ToString(), "  " + command + " ");
        }
    }

    private static NodeApplication Application(NodeTestRoot root, out StringWriter output, out StringWriter error, Action<NodeHost, Action>? started = null)
    {
        output = new StringWriter();
        error = new StringWriter();
        return new NodeApplication(TextWriter.Synchronized(output), TextWriter.Synchronized(error))
        {
            InitializeLogging = false,
            ConfigureHost = root.Isolate,
            Started = started,
            // Status reads no service manager of this machine.
            ServiceEnvironment = static () => null,
        };
    }
}
