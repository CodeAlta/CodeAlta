using System.Text;
using CodeAlta.Fleet.Update.Service;
using CodeAlta.Fleet.Update.Tests;

namespace CodeAlta.Node.Tests;

/// <summary>
/// The commands that manage a node on its machine: <c>install</c>, <c>uninstall</c> and <c>status</c> on a
/// <see cref="FakeServiceMachine"/> (nothing is installed), and <c>logs</c> on a temporary profile.
/// </summary>
[TestClass]
public sealed class NodeManagementCommandTests
{
    private static readonly ServiceExecutable Shim = new(ServiceExecutableKind.ToolShim, "/home/alice/.dotnet/tools/altanode", []);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Install_DryRun_PrintsTheUnitAndTheCommands_AndChangesNothing()
    {
        using var root = new NodeTestRoot();
        using var machine = new FakeServiceMachine(ServicePlatform.Linux);
        var (code, output, error) = await RunAsync(machine, ["install", "--dry-run", "--data-root", root.DataRoot, "--listen-all", "--port", "2600", "--name", "home server"]);

        Assert.AreEqual(NodeExitCodes.Success, code, error);
        StringAssert.Contains(output, "would install a systemd --user unit of alice");
        StringAssert.Contains(output, "ExecStart=/home/alice/.dotnet/tools/altanode run --data-root ");
        StringAssert.Contains(output, "--port 2600 --listen-all --name \"home server\"");
        StringAssert.Contains(output, "systemctl --user enable altanode-");
        StringAssert.Contains(output, "loginctl enable-linger alice (when lingering is off)");
        Assert.IsEmpty(machine.Commands, "A dry run runs no command.");
        Assert.IsFalse(Directory.Exists(Path.Combine(machine.Home, ".config", "systemd")));
    }

    [TestMethod]
    public async Task Install_Status_Uninstall_AnswerWhetherTheNodeWillStillRun()
    {
        using var root = new NodeTestRoot();
        using var machine = new FakeServiceMachine(ServicePlatform.MacOS);
        var (code, output, error) = await RunAsync(machine, ["install", "--data-root", root.DataRoot, "--offline"]);
        Assert.AreEqual(NodeExitCodes.Success, code, error);
        StringAssert.Contains(output, "Installed a LaunchAgent of alice");
        StringAssert.Contains(output, "It starts the .NET tool's shim /home/alice/.dotnet/tools/altanode");
        var plist = Directory.GetFiles(Path.Combine(machine.Home, "Library", "LaunchAgents")).Single();
        StringAssert.Contains(File.ReadAllText(plist), "<string>--offline</string>");

        (code, output, error) = await RunAsync(machine, ["status", "--data-root", root.DataRoot]);
        Assert.AreEqual(NodeExitCodes.Failure, code, "No instance holds the profile.");
        StringAssert.Contains(output, "No CodeAlta instance holds this profile.");
        StringAssert.Contains(output, "Paired devices: 0.");
        StringAssert.Contains(output, "Service: a LaunchAgent of alice");
        StringAssert.Contains(output, "After a restart: it waits until alice logs in");

        (code, output, error) = await RunAsync(machine, ["uninstall", "--data-root", root.DataRoot]);
        Assert.AreEqual(NodeExitCodes.Success, code, error);
        StringAssert.Contains(output, "Removed a LaunchAgent of alice");
        Assert.IsFalse(File.Exists(plist));

        (code, output, _) = await RunAsync(machine, ["status", "--data-root", root.DataRoot]);
        StringAssert.Contains(output, "Service: not installed");
        (code, output, _) = await RunAsync(machine, ["uninstall", "--data-root", root.DataRoot]);
        Assert.AreEqual(NodeExitCodes.Success, code);
        StringAssert.Contains(output, "No service of this profile is installed");
    }

    [TestMethod]
    public async Task Install_Again_NamesOnlyAnInstanceThatStillHoldsTheProfile()
    {
        using var root = new NodeTestRoot();
        using var machine = new FakeServiceMachine(ServicePlatform.MacOS);
        Assert.AreEqual(NodeExitCodes.Success, (await RunAsync(machine, ["install", "--data-root", root.DataRoot])).Code);
        var lockPath = root.Profile().LockFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        File.WriteAllText(lockPath, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // An instance the service does not stop keeps the profile: the service's node starts once it exits.
        var (code, output, error) = await RunAsync(machine, ["install", "--data-root", root.DataRoot]);
        Assert.AreEqual(NodeExitCodes.Success, code, error);
        StringAssert.Contains(output, "The service's node starts once that instance exits");

        // The service's own node stops on the bootout of install again, and launchd starts the new one: nothing to wait for.
        machine.CommandRan = command =>
        {
            if (command.StartsWith("launchctl bootout", StringComparison.Ordinal)) File.Delete(lockPath);
        };
        (code, output, error) = await RunAsync(machine, ["install", "--data-root", root.DataRoot, "--port", "2601"]);
        Assert.AreEqual(NodeExitCodes.Success, code, error);
        Assert.IsFalse(output.Contains("starts once that instance exits", StringComparison.Ordinal), output);
    }

    [TestMethod]
    public async Task Install_RefusesWhatTheOsCannotDo_WithExitOne()
    {
        using var root = new NodeTestRoot();
        using var machine = new FakeServiceMachine(ServicePlatform.Windows);
        // A Windows service is installed by an administrator: from a terminal that is not elevated, nothing changes.
        var (code, _, error) = await RunAsync(machine, ["install", "--system", "--account", "codealta", "--data-root", root.DataRoot]);

        Assert.AreEqual(NodeExitCodes.Failure, code);
        StringAssert.Contains(error, "elevated terminal");
        Assert.IsEmpty(machine.Commands);
    }

    [TestMethod]
    [DataRow("install", "--password-stdin")]
    [DataRow("install", "--user", "--system", "--account", "codealta")]
    [DataRow("install", "--account", "codealta")]
    [DataRow("install", "--no-listen", "--port", "2600")]
    [DataRow("install", "--port", "nope")]
    [DataRow("install", "--name", " ")]
    [DataRow("install", "--data-root", "relative")]
    [DataRow("uninstall", "--account", "codealta")]
    [DataRow("logs", "--lines", "many")]
    [DataRow("revoke")]
    [DataRow("revoke", "a", "b")]
    [DataRow("permissions", "laptop", "--allow", "fly")]
    [DataRow("devices", "extra")]
    public async Task InvalidManagementCommandLines_ExitWithTwo(params string[] args)
    {
        using var root = new NodeTestRoot();
        using var machine = new FakeServiceMachine(ServicePlatform.Linux);
        var withRoot = args.Contains("--data-root") ? args : [.. args, "--data-root", root.DataRoot];

        var (code, _, error) = await RunAsync(machine, withRoot);

        Assert.AreEqual(NodeExitCodes.InvalidUsage, code, error);
        Assert.IsEmpty(machine.Commands);
    }

    [TestMethod]
    public async Task Logs_PrintsTheLastLines_AndSaysWhenThereIsNoLog()
    {
        using var root = new NodeTestRoot();
        using var machine = new FakeServiceMachine(ServicePlatform.Linux);
        var (code, _, error) = await RunAsync(machine, ["logs", "--data-root", root.DataRoot]);
        Assert.AreEqual(NodeExitCodes.Failure, code);
        StringAssert.Contains(error, "no log yet");

        var log = NodeLogging.GetLogFilePath(root.DataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, string.Concat(Enumerable.Range(1, 80).Select(static line => $"line {line}\n")));
        (code, var output, error) = await RunAsync(machine, ["logs", "--data-root", root.DataRoot, "-n", "3"]);
        Assert.AreEqual(NodeExitCodes.Success, code, error);
        Assert.AreEqual("line 78\nline 79\nline 80\n", output);
        (_, output, _) = await RunAsync(machine, ["logs", "--data-root", root.DataRoot]);
        StringAssert.StartsWith(output, "line 31\n");
    }

    [TestMethod]
    public async Task Logs_Follow_PrintsWhatTheNodeWritesNext_AcrossARoll()
    {
        using var root = new NodeTestRoot();
        var log = NodeLogging.GetLogFilePath(root.DataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, "first\n");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        // The command writes from its own thread while the test reads what it wrote: both go through one lock.
        var output = new LockedWriter();
        var command = new NodeLogCommand(output, TextWriter.Null) { Stop = stop.Token, PollInterval = TimeSpan.FromMilliseconds(20) };
        var options = new NodeLogOptions { Follow = true };
        options.Profile.DataRoot = root.DataRoot;
        var following = command.RunAsync(options).AsTask();

        await WaitForAsync(() => output.ToString().Contains("first", StringComparison.Ordinal));
        await using (var writer = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            await writer.WriteAsync(Encoding.UTF8.GetBytes("second\n"), TestContext.CancellationToken);
        }

        await WaitForAsync(() => output.ToString().Contains("second", StringComparison.Ordinal));
        // The log rolls: the file moves aside and a new one starts.
        File.Move(log, log + ".1");
        File.WriteAllText(log, "third\n");
        await WaitForAsync(() => output.ToString().Contains("third", StringComparison.Ordinal));
        await stop.CancelAsync();

        Assert.AreEqual(NodeExitCodes.Success, await following.WithinAsync(TestTime.Seconds(10), TestContext.CancellationToken));
        Assert.AreEqual("first\nsecond\nthird\n", output.ToString().ReplaceLineEndings("\n"));
    }

    [TestMethod]
    public void Tail_ReadsFromTheEnd_AcrossChunks()
    {
        var text = string.Concat(Enumerable.Range(1, 20_000).Select(static line => $"line {line}\n"));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        Assert.AreEqual("line 19999\nline 20000\n", NodeLogCommand.Tail(stream, 2));
        Assert.AreEqual(text, NodeLogCommand.Tail(stream, 30_000));
        Assert.AreEqual("", NodeLogCommand.Tail(stream, 0));
        using var unterminated = new MemoryStream(Encoding.UTF8.GetBytes("a\nb\nc"));
        Assert.AreEqual("b\nc", NodeLogCommand.Tail(unterminated, 2));
    }

    // A writer whose text can be read while another thread writes to it: TextWriter.Synchronized locks the writes only, and a
    // StringBuilder read during a write can throw.
    private sealed class LockedWriter : TextWriter
    {
        private readonly Lock _gate = new();
        private readonly StringBuilder _text = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_gate) _text.Append(value);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            lock (_gate) _text.Append(buffer, index, count);
        }

        public override void Write(string? value)
        {
            lock (_gate) _text.Append(value);
        }

        public override string ToString()
        {
            lock (_gate) return _text.ToString();
        }
    }

    private async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TestTime.Seconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("The command never printed what was expected.");
            await Task.Delay(20, TestContext.CancellationToken);
        }
    }

    private async Task<(int Code, string Output, string Error)> RunAsync(FakeServiceMachine machine, string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var app = new NodeApplication(TextWriter.Synchronized(output), TextWriter.Synchronized(error))
        {
            InitializeLogging = false,
            ClientStop = TestContext.CancellationToken,
            ServiceEnvironment = () => machine.Environment,
            ServiceExecutable = () => Shim,
        };
        var code = await app.RunAsync(args).WithinAsync(TestTime.Seconds(60), TestContext.CancellationToken);
        return (code, output.ToString().ReplaceLineEndings("\n"), error.ToString());
    }
}
