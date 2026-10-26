using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Sandbox.Worker;

namespace CodeAlta.Sandbox.Tests;

// The protocol of the worker, with the worker in this process: what the container adds is tested with a real one.
[TestClass]
public sealed class SandboxWorkerTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    [TestMethod]
    public async Task TheFileTools_RunInTheWorker_AndAskTheirPermissionsToCodeAlta()
    {
        using var temp = new TempTree();
        var project = temp.Folder("project");
        File.WriteAllText(Path.Combine(project, "a.txt"), "hello\nworld\n");
        await using var worker = await WorkerOnPipes.StartAsync(project);
        var asked = new List<AgentPermissionRequest>();
        var allow = Host((request, _) => { lock (asked) asked.Add(request); return Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)); });

        var read = await worker.Client.InvokeAsync(Call("read_file", new { path = "a.txt" }), allow, CancellationToken.None).WaitAsync(Deadline);
        Assert.IsTrue(read.Success, read.Error);
        Assert.Contains("world", Text(read));

        var written = await worker.Client.InvokeAsync(Call("write_file", new { path = "b.txt", content = "from the sandbox" }, "call-write"), allow, CancellationToken.None).WaitAsync(Deadline);
        Assert.IsTrue(written.Success, written.Error);
        Assert.AreEqual("from the sandbox", File.ReadAllText(Path.Combine(project, "b.txt")));
        Assert.IsTrue(asked.Any(static request => request is AgentFileChangePermissionRequest { SessionId: "session-1" }), "the change was asked, for the session");

        var deny = Host((_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)));
        var refused = await worker.Client.InvokeAsync(Call("write_file", new { path = "c.txt", content = "no" }), deny, CancellationToken.None).WaitAsync(Deadline);
        Assert.IsFalse(refused.Success);
        Assert.IsFalse(File.Exists(Path.Combine(project, "c.txt")));

        var guarded = await worker.Client.InvokeAsync(Call("write_file", new { path = ".git/hooks/pre-commit", content = "evil" }), allow, CancellationToken.None).WaitAsync(Deadline);
        Assert.IsFalse(guarded.Success, "the file tools of the worker keep the rules of a session without shell");
    }

    [TestMethod]
    public async Task Commands_RunInTheWorker_StreamTheirOutput_AndGoThroughTheGuardOfCodeAlta()
    {
        using var temp = new TempTree();
        var project = temp.Folder("project");
        await using var worker = await WorkerOnPipes.StartAsync(project, commandGuard: true);
        var progress = new StringBuilder();
        var host = new SandboxToolHost((_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            (command, _, _) => ValueTask.FromResult(command.Contains("refused", StringComparison.Ordinal) ? "This command is refused by the host." : null));

        var invocation = Call("shell_command", new { command = "echo from-the-worker" }) with
        {
            Progress = (update, _) => { lock (progress) progress.Append(update.Delta); return ValueTask.CompletedTask; },
        };
        var result = await worker.Client.InvokeAsync(invocation, host, CancellationToken.None).WaitAsync(Deadline);
        Assert.IsTrue(result.Success, result.Error);
        Assert.Contains("from-the-worker", Text(result));
        Assert.Contains("from-the-worker", progress.ToString(), "what the command writes is streamed while it runs");

        var refused = await worker.Client.InvokeAsync(Call("shell_command", new { command = "echo refused" }), host, CancellationToken.None).WaitAsync(Deadline);
        Assert.IsFalse(refused.Success);
        Assert.Contains("refused by the host", refused.Error + Text(refused));
    }

    [TestMethod]
    public async Task ACanceledCall_StopsItsCommand_AndAWorkerThatEnds_FailsWhatWaits()
    {
        using var temp = new TempTree();
        var project = temp.Folder("project");
        var worker = await WorkerOnPipes.StartAsync(project);
        var allow = Host((_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)));
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var watch = Stopwatch.StartNew();

        var canceled = await worker.Client.InvokeAsync(Call("shell_command", new { command = OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 60" : "sleep 60" }), allow, cancel.Token).WaitAsync(Deadline);

        Assert.IsFalse(canceled.Success);
        Assert.IsLessThan(TimeSpan.FromSeconds(30), watch.Elapsed, "the command was stopped, not waited for");

        var pending = worker.Client.InvokeAsync(Call("shell_command", new { command = OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 60" : "sleep 60" }), allow, CancellationToken.None);
        await Task.Delay(500);
        await worker.StopWorkerAsync();
        var failed = await pending.WaitAsync(Deadline);
        Assert.IsFalse(failed.Success);
        Assert.IsTrue(worker.Client.HasEnded);
        Assert.IsFalse((await worker.Client.InvokeAsync(Call("read_file", new { path = "x" }), allow, CancellationToken.None).WaitAsync(Deadline)).Success);
        await worker.DisposeAsync();
    }

    [TestMethod]
    public async Task AWorkerThatDoesNotStopACanceledCall_IsEnded_AndTheCallFails()
    {
        using var toWorker = new AnonymousPipeServerStream(PipeDirection.Out);
        using var workerInput = new AnonymousPipeClientStream(PipeDirection.In, toWorker.ClientSafePipeHandle);
        using var fromWorker = new AnonymousPipeServerStream(PipeDirection.Out);
        using var hostInput = new AnonymousPipeClientStream(PipeDirection.In, fromWorker.ClientSafePipeHandle);
        // A worker that is ready, then answers nothing.
        var silent = Task.Run(async () =>
        {
            var channel = new SandboxWorkerChannel(workerInput, fromWorker);
            while (await channel.ReadAsync() is { } message)
            {
                if (message.Type == SandboxWorkerMessageTypes.Init) await channel.WriteAsync(new SandboxWorkerMessage { Type = SandboxWorkerMessageTypes.Ready }, CancellationToken.None);
            }
        });
        var client = await SandboxWorkerClient.ConnectAsync(hostInput, toWorker, new SandboxWorkerInit("session-1", "test", Path.GetTempPath(), [], null, null, false), CancellationToken.None)
            .WaitAsync(Deadline);
        client.CancelGrace = TimeSpan.FromMilliseconds(200);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var result = await client.InvokeAsync(Call("read_file", new { path = "x" }), Host((_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce))),
            cancel.Token).WaitAsync(Deadline);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(client.HasEnded, "a worker that does not stop is ended: what it runs does not hold the session");
        await client.DisposeAsync();
        toWorker.Dispose();
        await silent.WaitAsync(Deadline);
    }

    [TestMethod]
    public async Task BackgroundCommands_RunInTheWorker_AndCanBeEnded()
    {
        using var temp = new TempTree();
        var project = temp.Folder("project");
        await using var worker = await WorkerOnPipes.StartAsync(project);
        var output = new StringBuilder();

        var job = worker.Client.StartProcess("echo job-output", project, text => { lock (output) output.Append(text); });
        Assert.AreEqual(0, await job.Completion.WaitAsync(Deadline));
        Assert.Contains("job-output", output.ToString());

        var endless = worker.Client.StartProcess(OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 60" : "sleep 60", project, static _ => { });
        await Task.Delay(1000);
        endless.Kill();
        Assert.AreNotEqual(0, await endless.Completion.WaitAsync(Deadline));
    }

    [TestMethod]
    public void AResultOfTheWorker_GivesTextAndImages_NeverAPathCodeAltaWouldRead()
    {
        var result = SandboxWorkerClient.FromSandbox(new AgentToolResult(true,
        [
            new AgentToolResultItem.Text("text"),
            new AgentToolResultItem.Image("iVBORw0KGgo=", "image/png"),
            new AgentToolResultItem.ImageUrl("data:image/png;base64,iVBORw0KGgo="),
            new AgentToolResultItem.LocalImage(@"C:\Users\someone\.ssh\id_ed25519", "image/png"),
            new AgentToolResultItem.ImageUrl("file:///C:/Users/someone/.ssh/id_ed25519"),
        ], null));

        Assert.IsInstanceOfType<AgentToolResultItem.Text>(result.Items[0]);
        Assert.IsInstanceOfType<AgentToolResultItem.Image>(result.Items[1]);
        Assert.IsInstanceOfType<AgentToolResultItem.ImageUrl>(result.Items[2]);
        Assert.IsFalse(result.Items.Any(static item => item is AgentToolResultItem.LocalImage), "a path the host would read with the rights of the user");
        Assert.IsFalse(result.Items.OfType<AgentToolResultItem.ImageUrl>().Any(static item => item.Url.StartsWith("file:", StringComparison.Ordinal)));
        Assert.HasCount(5, result.Items, "what is left out is said");
    }

    [TestMethod]
    public async Task AResultLongerThanAFrame_IsSentInParts()
    {
        using var temp = new TempTree();
        var project = temp.Folder("project");
        var line = new string('x', 3000);
        File.WriteAllLines(Path.Combine(project, "big.txt"), Enumerable.Repeat(line, 2000));
        await using var worker = await WorkerOnPipes.StartAsync(project);

        var result = await worker.Client.InvokeAsync(Call("read_file", new { path = "big.txt", limit = 2000 }),
            Host((_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce))), CancellationToken.None).WaitAsync(Deadline);

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsGreaterThan(5_000_000, Text(result).Length);
    }

    private static SandboxToolHost Host(Func<AgentPermissionRequest, CancellationToken, Task<AgentPermissionDecision>> permission)
        => new((request, token) => permission(request, token), null);

    private static AgentToolInvocation Call(string tool, object arguments, string? toolCallId = null)
        => new(new ModelProviderId("test"), "session-1", toolCallId ?? Guid.NewGuid().ToString("N"), tool, JsonSerializer.SerializeToElement(arguments));

    private static string Text(AgentToolResult result) => string.Concat(result.Items.OfType<AgentToolResultItem.Text>().Select(static item => item.Value));

    // A worker in this process on two pipes, as the container gives its standard streams.
    private sealed class WorkerOnPipes : IAsyncDisposable
    {
        private readonly AnonymousPipeServerStream _toWorker;
        private readonly AnonymousPipeClientStream _workerInput;
        private readonly AnonymousPipeServerStream _fromWorker;
        private readonly AnonymousPipeClientStream _hostInput;
        private readonly Task _worker;

        private WorkerOnPipes(AnonymousPipeServerStream toWorker, AnonymousPipeClientStream workerInput, AnonymousPipeServerStream fromWorker, AnonymousPipeClientStream hostInput, Task worker)
        {
            _toWorker = toWorker;
            _workerInput = workerInput;
            _fromWorker = fromWorker;
            _hostInput = hostInput;
            _worker = worker;
        }

        public SandboxWorkerClient Client { get; private set; } = null!;

        public static async Task<WorkerOnPipes> StartAsync(string project, bool commandGuard = false)
        {
            var toWorker = new AnonymousPipeServerStream(PipeDirection.Out);
            var workerInput = new AnonymousPipeClientStream(PipeDirection.In, toWorker.ClientSafePipeHandle);
            var fromWorker = new AnonymousPipeServerStream(PipeDirection.Out);
            var hostInput = new AnonymousPipeClientStream(PipeDirection.In, fromWorker.ClientSafePipeHandle);
            var worker = Task.Run(() => SandboxWorker.RunAsync(workerInput, fromWorker, CancellationToken.None));
            var pipes = new WorkerOnPipes(toWorker, workerInput, fromWorker, hostInput, worker);
            pipes.Client = await SandboxWorkerClient.ConnectAsync(hostInput, toWorker,
                new SandboxWorkerInit("session-1", "test", project, [project], null, null, commandGuard), CancellationToken.None);
            return pipes;
        }

        // As the end of the worker's process: its output ends.
        public async Task StopWorkerAsync()
        {
            _toWorker.Dispose();
            await _worker.WaitAsync(Deadline);
            _fromWorker.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            _toWorker.Dispose();
            try
            {
                await _worker.WaitAsync(Deadline);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
            }

            _fromWorker.Dispose();
            await Client.DisposeAsync();
            _workerInput.Dispose();
            _hostInput.Dispose();
        }
    }
}
