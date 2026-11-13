using CodeAlta.Agent;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Jobs;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class ShellCommandsRpcTests
{
    [TestMethod]
    public async Task Run_StreamsTheOutputInTheFolderOfTheSession_ThenTheExitCode()
    {
        var folder = Directory.CreateTempSubdirectory("alta-shell-").FullName;
        try
        {
            string? started = null, ranIn = null;
            AgentProcessEnvironment? given = null;
            ShellCommandTarget? asked = null;
            var environment = new AgentProcessEnvironment([new("ALTA_TEST", "1")]);
            var service = new ShellCommandsService((target, _) => { asked = target; return Task.FromResult<ShellCommandPlace?>(new(folder, environment)); },
                (command, workdir, variables, onOutput) =>
                {
                    started = command; ranIn = workdir; given = variables;
                    onOutput("first\n");
                    onOutput("second\n");
                    return new FakeProcess(Task.FromResult(3));
                }, "epoch");
            var events = await CollectAsync(service.RunAsync(new("epoch", "session", null, "  git status  "), default));
            Assert.AreEqual(new ShellCommandTarget("session", null), asked);
            CollectionAssert.AreEqual(new[] { "started", "output", "output", "exit" }, events.Select(static e => e.Kind).ToArray());
            Assert.AreEqual(folder, events[0].Value);
            Assert.AreEqual("first\nsecond\n", events[1].Value + events[2].Value);
            Assert.AreEqual(3, events[3].ExitCode);
            Assert.AreEqual(0, events[3].Omitted);
            Assert.AreEqual("git status", started);
            Assert.AreEqual(folder, ranIn);
            Assert.AreSame(environment, given, "the command runs with the variables of the commands of the session");

            // The prompt of a session to start runs its commands in the folder of its project.
            await CollectAsync(service.RunAsync(new("epoch", null, "project", "ls"), default));
            Assert.AreEqual(new ShellCommandTarget(null, "project"), asked);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public async Task Run_RefusesInvalidStaleOrUnknownRequests_WithoutStartingAnything()
    {
        var starts = 0;
        var gone = Path.Combine(Path.GetTempPath(), "alta-no-such-folder-" + Guid.NewGuid().ToString("N"));
        var service = new ShellCommandsService((target, _) => Task.FromResult(target.SessionId == "known" ? new ShellCommandPlace(gone, AgentProcessEnvironment.Empty) : null),
            (_, _, _, _) => { starts++; return new FakeProcess(Task.FromResult(0)); }, "epoch");
        foreach (var request in new ShellCommandRequest?[] { null, new("epoch", "", null, "ls"), new("epoch", "known", "project", "ls"), new("epoch", "known", null, " "),
            new("epoch", "known", null, new string('x', ShellCommandsService.MaximumCommandLength + 1)) })
            Assert.AreEqual("invalid_request", (await CollectAsync(service.RunAsync(request, default))).Single().Status);
        Assert.AreEqual("stale_epoch", (await CollectAsync(service.RunAsync(new("old", "known", null, "ls"), default))).Single().Status);
        Assert.AreEqual("missing_session", (await CollectAsync(service.RunAsync(new("epoch", "unknown", null, "ls"), default))).Single().Status);
        Assert.AreEqual("missing_project", (await CollectAsync(service.RunAsync(new("epoch", null, "unknown", "ls"), default))).Single().Status);
        Assert.AreEqual("missing_folder", (await CollectAsync(service.RunAsync(new("epoch", "known", null, "ls"), default))).Single().Status);
        Assert.AreEqual(0, starts);
    }

    [TestMethod]
    public async Task Run_ReportsAShellThatDoesNotStart()
    {
        var service = new ShellCommandsService(TempPlace,
            (_, _, _, _) => throw new InvalidOperationException("no shell"), "epoch");
        var failure = (await CollectAsync(service.RunAsync(new("epoch", "session", null, "ls"), default))).Single();
        Assert.AreEqual("failed", failure.Status);
        Assert.AreEqual("no shell", failure.Value);
    }

    [TestMethod]
    public async Task Run_SendsTheOutputUpToItsLimit_ThenTheEndOfTheRestWithTheExitCode()
    {
        var service = new ShellCommandsService(TempPlace,
            (command, _, _, onOutput) =>
            {
                onOutput(new string('a', ShellCommandsService.MaximumOutputCharacters - 10));
                onOutput(new string('b', 30));
                onOutput("cc");
                if (command == "huge")
                {
                    for (var i = 0; i < 10; i++) onOutput(new string('d', 10_000));
                    onOutput("summary");
                }
                return new FakeProcess(Task.FromResult(0));
            }, "epoch");
        var events = await CollectAsync(service.RunAsync(new("epoch", "session", null, "big"), default));
        Assert.AreEqual(ShellCommandsService.MaximumOutputCharacters, events.Where(static e => e.Kind == "output").Sum(static e => e.Value!.Length));
        Assert.AreEqual(22, events[^1].Omitted);
        Assert.AreEqual(new string('b', 20) + "cc", events[^1].Value, "the end of what was left out comes with the exit code");

        var huge = (await CollectAsync(service.RunAsync(new("epoch", "session", null, "huge"), default)))[^1];
        Assert.AreEqual(22 + 100_000 + 7, huge.Omitted);
        Assert.AreEqual(ShellCommandsService.MaximumTailCharacters, huge.Value!.Length);
        Assert.EndsWith("summary", huge.Value, "the last words of a long output, a summary or an error, reach the page");
    }

    [TestMethod]
    public async Task ClosingTheStream_EndsTheCommand()
    {
        var running = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new FakeProcess(running.Task) { OnKill = () => running.TrySetResult(-1) };
        var service = new ShellCommandsService(TempPlace,
            (_, _, _, onOutput) => { onOutput("partial"); return process; }, "epoch");
        await using (var reader = service.RunAsync(new("epoch", "session", null, "watch"), default).GetAsyncEnumerator())
        {
            Assert.IsTrue(await reader.MoveNextAsync());
            Assert.AreEqual("started", reader.Current.Kind);
            Assert.IsTrue(await reader.MoveNextAsync());
            Assert.AreEqual("partial", reader.Current.Value);
        }
        Assert.IsTrue(process.Killed);
        Assert.IsTrue(process.Disposed);
    }

    [TestMethod]
    public async Task Run_RunsARealCommandInTheShellOfTheSession()
    {
        var folder = Directory.CreateTempSubdirectory("alta-shell-").FullName;
        try
        {
            var service = new ShellCommandsService((_, _) => Task.FromResult<ShellCommandPlace?>(new(folder, AgentProcessEnvironment.Empty)), "epoch");
            var events = await CollectAsync(service.RunAsync(new("epoch", "session", null, "echo hello-from-shell"), default)).WithinAsync(TestTime.Seconds(60));
            StringAssert.Contains(string.Concat(events.Where(static e => e.Kind == "output").Select(static e => e.Value)), "hello-from-shell");
            Assert.AreEqual(0, events[^1].ExitCode);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static Task<ShellCommandPlace?> TempPlace(ShellCommandTarget target, CancellationToken cancellationToken)
        => Task.FromResult<ShellCommandPlace?>(new(Path.GetTempPath(), AgentProcessEnvironment.Empty));

    private static async Task<List<ShellCommandEvent>> CollectAsync(IAsyncEnumerable<ShellCommandEvent> events)
    {
        var result = new List<ShellCommandEvent>();
        await foreach (var item in events) result.Add(item);
        return result;
    }

    private sealed class FakeProcess(Task<int> completion) : IShellCommandProcess
    {
        public Action? OnKill { get; init; }
        public bool Killed { get; private set; }
        public bool Disposed { get; private set; }
        public int? ProcessId => 42;
        public Task<int> Completion { get; } = completion;
        public void Kill() { Killed = true; OnKill?.Invoke(); }
        public void Dispose() => Disposed = true;
    }
}
