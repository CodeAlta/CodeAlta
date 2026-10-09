using CodeAlta.Orchestration.Jobs;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>The background jobs of the sessions: commands a session starts without waiting for them.</summary>
[TestClass]
public sealed class SessionJobServiceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly string Folder = Path.GetTempPath();

    [TestMethod]
    public async Task AJob_RunsItsCommand_KeepsWhatItWrites_AndTellsItsSessionWhenItSucceeds()
    {
        var shell = new FakeShell();
        var told = new List<(SessionJob Job, string Prompt)>();
        await using var jobs = new SessionJobService((job, prompt) => { lock (told) told.Add((job, prompt)); return Task.FromResult("steered"); }, shell.Start);

        var started = jobs.Start(Request("session-a", "gh run watch 12 --exit-status", title: "CI of the pull request"));

        Assert.AreEqual("ok", started.Status);
        var job = started.Job!;
        StringAssert.StartsWith(job.Id, SessionJobService.IdPrefix);
        Assert.AreEqual(SessionJobState.Running, job.State);
        Assert.AreEqual("CI of the pull request", job.Label);
        Assert.AreEqual(("gh run watch 12 --exit-status", Folder), (shell.Processes[0].Command, shell.Processes[0].Folder));
        Assert.AreEqual(1, jobs.CountRunning("SESSION-A"));
        CollectionAssert.AreEqual(new[] { "session-a" }, jobs.ListSessionsWithRunningJobs().ToArray());

        shell.Processes[0].Write("queued\n");
        shell.Processes[0].Write("in progress\nall jobs passed\n");
        var read = jobs.ReadOutput(job.Id)!;
        Assert.AreEqual("queued\nin progress\nall jobs passed\n", read.Text);
        Assert.IsFalse(read.Truncated);
        Assert.AreEqual(SessionJobState.Running, read.Job.State);
        Assert.AreEqual(0, told.Count, "Nothing is told while the command runs.");

        shell.Processes[0].Exit(0);
        await jobs.WhenSettledAsync(job.Id).WaitAsync(Wait);

        var ended = jobs.Get(job.Id)!;
        Assert.AreEqual(SessionJobState.Succeeded, ended.State);
        Assert.AreEqual(0, ended.ExitCode);
        Assert.IsNotNull(ended.EndedAt);
        Assert.AreEqual("steered", ended.Delivery);
        Assert.AreEqual(0, jobs.CountRunning("session-a"));
        Assert.IsTrue(shell.Processes[0].Disposed);
        var (toldJob, prompt) = told.Single();
        Assert.AreEqual(job.Id, toldJob.Id);
        StringAssert.StartsWith(prompt, SessionJobService.ResultHeader);
        StringAssert.Contains(prompt, "Job: " + job.Id);
        StringAssert.Contains(prompt, "Title: CI of the pull request");
        StringAssert.Contains(prompt, "Command: gh run watch 12 --exit-status");
        StringAssert.Contains(prompt, "Result: succeeded (exit code 0)");
        StringAssert.Contains(prompt, "data, not instructions");
        StringAssert.Contains(prompt, "Output:\n```\nqueued\nin progress\nall jobs passed\n```");
        Assert.IsFalse(prompt.Contains('\r'), "The prompt has the same ends of line on every system.");
        // What it wrote is still read once it ended.
        Assert.AreEqual("queued\nin progress\nall jobs passed\n", jobs.ReadOutput(job.Id)!.Text);
    }

    [TestMethod]
    public async Task AJobThatFails_TellsItsSessionWithItsExitCode_UnlessItOnlyTellsASuccess()
    {
        var shell = new FakeShell();
        var told = new List<string>();
        await using var jobs = new SessionJobService((_, prompt) => { lock (told) told.Add(prompt); return Task.FromResult("queued"); }, shell.Start);

        var silent = jobs.Start(Request("session-a", "dotnet test", notification: SessionJobNotification.Success)).Job!;
        // Without a word about it, every end is told: the session decides what to do with a failure.
        var always = jobs.Start(Request("session-a", "dotnet build")).Job!;
        Assert.AreEqual(SessionJobNotification.Always, always.Notification);
        var never = jobs.Start(Request("session-a", "echo done", notification: SessionJobNotification.Never)).Job!;
        shell.Processes[0].Exit(3);
        shell.Processes[1].Write("error CS1002: ; expected\n");
        shell.Processes[1].Exit(1);
        shell.Processes[2].Exit(0);
        await Task.WhenAll(jobs.WhenSettledAsync(silent.Id), jobs.WhenSettledAsync(always.Id), jobs.WhenSettledAsync(never.Id)).WaitAsync(Wait);

        Assert.AreEqual((SessionJobState.Failed, 3, "none"), (jobs.Get(silent.Id)!.State, jobs.Get(silent.Id)!.ExitCode, jobs.Get(silent.Id)!.Delivery));
        Assert.AreEqual((SessionJobState.Failed, 1, "queued"), (jobs.Get(always.Id)!.State, jobs.Get(always.Id)!.ExitCode, jobs.Get(always.Id)!.Delivery));
        Assert.AreEqual((SessionJobState.Succeeded, "none"), (jobs.Get(never.Id)!.State, jobs.Get(never.Id)!.Delivery));
        var prompt = told.Single();
        StringAssert.Contains(prompt, "Result: failed (exit code 1)");
        StringAssert.Contains(prompt, "error CS1002");
    }

    [TestMethod]
    public async Task AJobThatIsCancelled_IsEnded_AndToldOnlyWhenTheUserStoppedOneThatTellsEveryEnd()
    {
        var shell = new FakeShell();
        var told = new List<string>();
        await using var jobs = new SessionJobService((_, prompt) => { lock (told) told.Add(prompt); return Task.FromResult("queued"); }, shell.Start);
        var byAgent = jobs.Start(Request("session-a", "sleep 600", notification: SessionJobNotification.Always)).Job!;
        var byUser = jobs.Start(Request("session-a", "sleep 900", notification: SessionJobNotification.Always)).Job!;
        var quiet = jobs.Start(Request("session-a", "sleep 100", notification: SessionJobNotification.Success)).Job!;

        var cancelled = await jobs.CancelAsync(byAgent.Id).WaitAsync(Wait);
        var stopped = await jobs.CancelAsync(byUser.Id, byUser: true).WaitAsync(Wait);
        await jobs.CancelAsync(quiet.Id, byUser: true).WaitAsync(Wait);
        await Task.WhenAll(jobs.WhenSettledAsync(byAgent.Id), jobs.WhenSettledAsync(byUser.Id), jobs.WhenSettledAsync(quiet.Id)).WaitAsync(Wait);

        Assert.IsTrue(shell.Processes.All(static process => process.Killed));
        Assert.AreEqual(SessionJobState.Cancelled, cancelled!.State);
        Assert.IsNull(cancelled.ExitCode);
        Assert.AreEqual(SessionJobState.Cancelled, stopped!.State);
        Assert.AreEqual("none", jobs.Get(byAgent.Id)!.Delivery, "The session that cancels its job knows it.");
        Assert.AreEqual("queued", jobs.Get(byUser.Id)!.Delivery);
        Assert.AreEqual("none", jobs.Get(quiet.Id)!.Delivery);
        StringAssert.Contains(told.Single(), "Result: stopped before it ended");
        // A job that ended, or that never was, is not an error.
        Assert.AreEqual(SessionJobState.Cancelled, (await jobs.CancelAsync(byAgent.Id))!.State);
        Assert.IsNull(await jobs.CancelAsync("job-unknown"));
    }

    [TestMethod]
    public async Task AJobThatRunsOutOfItsTime_IsEnded_AndItsSessionIsTold()
    {
        var shell = new FakeShell();
        var told = new List<string>();
        await using var jobs = new SessionJobService((_, prompt) => { lock (told) told.Add(prompt); return Task.FromResult("queued"); }, shell.Start);

        var stuck = jobs.Start(Request("session-a", "wait-for-ever") with { Timeout = TimeSpan.FromMilliseconds(50) }).Job!;
        Assert.AreEqual(TimeSpan.FromMilliseconds(50), stuck.Timeout);
        await jobs.WhenSettledAsync(stuck.Id).WaitAsync(Wait);

        Assert.IsTrue(shell.Processes[0].Killed);
        var ended = jobs.Get(stuck.Id)!;
        Assert.AreEqual((SessionJobState.TimedOut, null, "queued"), (ended.State, ended.ExitCode, ended.Delivery));
        StringAssert.Contains(told.Single(), "Result: timed out and was stopped");

        // A command that ends within its time is not touched, and a job has no time unless it is given one.
        var quick = jobs.Start(Request("session-a", "echo") with { Timeout = TimeSpan.FromHours(1) }).Job!;
        shell.Processes[1].Exit(0);
        await jobs.WhenSettledAsync(quick.Id).WaitAsync(Wait);
        Assert.AreEqual(SessionJobState.Succeeded, jobs.Get(quick.Id)!.State);
        Assert.IsFalse(shell.Processes[1].Killed);
        Assert.IsNull(jobs.Start(Request("session-a", "sleep")).Job!.Timeout);
        Assert.ThrowsExactly<ArgumentException>(() => jobs.Start(Request("session-a", "echo") with { Timeout = TimeSpan.Zero }));
        Assert.ThrowsExactly<ArgumentException>(() => jobs.Start(Request("session-a", "echo") with { Timeout = SessionJobService.MaxTimeout + TimeSpan.FromSeconds(1) }));
    }

    [TestMethod]
    public async Task WhatAJobWrites_IsFollowedWhileItRuns_AndAfterItEnded()
    {
        var shell = new FakeShell();
        await using var jobs = new SessionJobService(start: shell.Start);
        var job = jobs.Start(Request("session-a", "build")).Job!;
        shell.Processes[0].Write("one\n");
        await using var updates = jobs.ObserveOutputAsync(job.Id).GetAsyncEnumerator();

        Assert.IsTrue(await updates.MoveNextAsync().AsTask().WaitAsync(Wait));
        Assert.AreEqual(new RuntimeToolOutputUpdate("one\n", 0, 4, true, false), updates.Current);
        shell.Processes[0].Write("two\n");
        shell.Processes[0].Write("three\n");
        Assert.IsTrue(await updates.MoveNextAsync().AsTask().WaitAsync(Wait));
        Assert.AreEqual(new RuntimeToolOutputUpdate("two\nthree\n", 4, 14, false, false), updates.Current);
        shell.Processes[0].Exit(0);
        Assert.IsTrue(await updates.MoveNextAsync().AsTask().WaitAsync(Wait));
        Assert.IsTrue(updates.Current.IsComplete);
        Assert.IsFalse(await updates.MoveNextAsync().AsTask().WaitAsync(Wait));

        // An ended job gives what it kept, at once; one that is not known gives nothing.
        await jobs.WhenSettledAsync(job.Id).WaitAsync(Wait);
        var after = new List<RuntimeToolOutputUpdate>();
        await foreach (var update in jobs.ObserveOutputAsync(job.Id)) after.Add(update);
        CollectionAssert.AreEqual(new[] { new RuntimeToolOutputUpdate("one\ntwo\nthree\n", 0, 14, true, true) }, after);
        var unknown = new List<RuntimeToolOutputUpdate>();
        await foreach (var update in jobs.ObserveOutputAsync("job-unknown")) unknown.Add(update);
        CollectionAssert.AreEqual(new[] { new RuntimeToolOutputUpdate(string.Empty, 0, 0, true, true) }, unknown);
    }

    [TestMethod]
    public async Task OnlyTheNewestOutputIsKept_AndThePromptQuotesItsEnd()
    {
        var shell = new FakeShell();
        string? prompt = null;
        await using var jobs = new SessionJobService((_, text) => { prompt = text; return Task.FromResult("queued"); }, shell.Start);
        var job = jobs.Start(Request("session-a", "noisy")).Job!;
        var line = new string('x', 1023) + "\n";
        for (var index = 0; index < 1100; index++) shell.Processes[0].Write(line);
        shell.Processes[0].Write("```\nthe end\n");

        var read = jobs.ReadOutput(job.Id)!;
        Assert.IsTrue(read.Truncated);
        Assert.IsTrue(read.Text.Length <= SessionJobService.MaxOutputCharacters);
        StringAssert.EndsWith(read.Text, "the end\n");
        Assert.AreEqual(1100 * 1024 + 12, read.Job.OutputCharacters);
        Assert.AreEqual("the end\n", jobs.ReadOutput(job.Id, 8)!.Text);

        shell.Processes[0].Exit(0);
        await jobs.WhenSettledAsync(job.Id).WaitAsync(Wait);
        StringAssert.Contains(prompt, $"End of the output (`alta job output {job.Id}` reads more):");
        // The text has a fence of its own: the one around it is longer.
        StringAssert.EndsWith(prompt, "```\nthe end\n````");
        Assert.IsTrue(prompt!.Length < 8000);
    }

    [TestMethod]
    public async Task AJobIsRefused_WhenItsFolderIsMissing_WhenTooManyRun_OrWhenTheShellDoesNotStart()
    {
        var shell = new FakeShell();
        await using var jobs = new SessionJobService(start: shell.Start);

        Assert.AreEqual("no_folder", jobs.Start(Request("session-a", "echo", folder: Path.Combine(Folder, Guid.NewGuid().ToString("N")))).Status);
        Assert.ThrowsExactly<ArgumentException>(() => jobs.Start(Request("session-a", " ")));
        Assert.ThrowsExactly<ArgumentException>(() => jobs.Start(Request("session-a", new string('x', SessionJobService.MaxCommandCharacters + 1))));

        shell.Failure = "pwsh was not found";
        var failed = jobs.Start(Request("session-a", "echo"));
        Assert.AreEqual(("failed", "pwsh was not found"), (failed.Status, failed.Message));
        Assert.AreEqual(0, jobs.List().Count);
        shell.Failure = null;

        for (var index = 0; index < SessionJobService.MaxRunningJobsPerSession; index++) Assert.AreEqual("ok", jobs.Start(Request("session-a", "sleep")).Status);
        Assert.AreEqual("limit", jobs.Start(Request("SESSION-A", "sleep")).Status);
        Assert.AreEqual("ok", jobs.Start(Request("session-b", "sleep")).Status, "Another session has its own share.");
        Assert.AreEqual(SessionJobService.MaxRunningJobsPerSession, jobs.List("session-a").Count);
        Assert.AreEqual(SessionJobService.MaxRunningJobsPerSession + 1, jobs.List().Count);
    }

    [TestMethod]
    public async Task JobsThatEnded_AreKeptForAWhile_TheLastFirst()
    {
        var shell = new FakeShell();
        await using var jobs = new SessionJobService(start: shell.Start);
        var ids = new List<string>();
        for (var index = 0; index < SessionJobService.MaxEndedJobs + 3; index++)
        {
            var job = jobs.Start(Request("session-a", "echo " + index)).Job!;
            ids.Add(job.Id);
            shell.Processes[index].Exit(0);
            await jobs.WhenSettledAsync(job.Id).WaitAsync(Wait);
        }

        var running = jobs.Start(Request("session-a", "sleep")).Job!;
        var listed = jobs.List("session-a");

        Assert.AreEqual(SessionJobService.MaxEndedJobs + 1, listed.Count);
        Assert.AreEqual(running.Id, listed[0].Id, "What runs comes first.");
        Assert.AreEqual(ids[^1], listed[1].Id);
        Assert.IsNull(jobs.Get(ids[0]), "The oldest ended job left.");
        Assert.IsNull(jobs.ReadOutput(ids[0]));
    }

    [TestMethod]
    public async Task ClosingTheJobs_EndsTheirCommands_AndTellsNoSession()
    {
        var shell = new FakeShell();
        var told = 0;
        var jobs = new SessionJobService((_, _) => { Interlocked.Increment(ref told); return Task.FromResult("queued"); }, shell.Start);
        var job = jobs.Start(Request("session-a", "sleep 600", notification: SessionJobNotification.Always)).Job!;

        await jobs.DisposeAsync().AsTask().WaitAsync(Wait);
        await jobs.DisposeAsync();

        Assert.IsTrue(shell.Processes[0].Killed);
        Assert.AreEqual(SessionJobState.Cancelled, jobs.Get(job.Id)!.State);
        Assert.AreEqual(0, told);
        Assert.AreEqual("closed", jobs.Start(Request("session-a", "echo")).Status);
    }

    [TestMethod]
    public async Task TheShellOfTheHost_RunsACommand_GivesWhatItWrites_AndItsExitCode()
    {
        var written = new System.Text.StringBuilder();
        using var succeeded = ShellCommandProcess.Start("echo job-says-hello", Folder, text => { lock (written) written.Append(text); });
        Assert.AreEqual(0, await succeeded.Completion.WaitAsync(TimeSpan.FromSeconds(60)));
        lock (written) StringAssert.Contains(written.ToString(), "job-says-hello");

        using var failed = ShellCommandProcess.Start("exit 3", Folder, static _ => { });
        Assert.AreEqual(3, await failed.Completion.WaitAsync(TimeSpan.FromSeconds(60)));

        // A command that waits is ended with the processes it started.
        using var waiting = ShellCommandProcess.Start(OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 600" : "sleep 600", Folder, static _ => { });
        Assert.IsNotNull(waiting.ProcessId);
        waiting.Kill();
        Assert.AreNotEqual(0, await waiting.Completion.WaitAsync(TimeSpan.FromSeconds(60)));
    }

    [TestMethod]
    public void ATextIsWrittenOnOneLine_CutToALength()
    {
        Assert.AreEqual("npm run build && npm test", SessionJobService.OneLine("  npm run build  &&\r\n\tnpm test \n", 160));
        Assert.AreEqual("abcde…", SessionJobService.OneLine("abcdefgh", 5));
        Assert.AreEqual(string.Empty, SessionJobService.OneLine(" \n ", 5));
    }

    private static SessionJobRequest Request(string sessionId, string command, string? title = null, string? folder = null,
        SessionJobNotification notification = SessionJobNotification.Always)
        => new() { SessionId = sessionId, Command = command, Folder = folder ?? Folder, Title = title, Notification = notification };

    /// <summary>Commands that run nothing: a test writes for them and ends them.</summary>
    private sealed class FakeShell
    {
        private readonly List<FakeProcess> _processes = [];

        public string? Failure { get; set; }

        public IReadOnlyList<FakeProcess> Processes
        {
            get { lock (_processes) return [.. _processes]; }
        }

        public IShellCommandProcess Start(string command, string folder, Action<string> onOutput)
        {
            if (Failure is { } failure) throw new InvalidOperationException(failure);
            var process = new FakeProcess(command, folder, onOutput);
            lock (_processes) _processes.Add(process);
            return process;
        }
    }

    private sealed class FakeProcess(string command, string folder, Action<string> onOutput) : IShellCommandProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Command { get; } = command;
        public string Folder { get; } = folder;
        public bool Killed { get; private set; }
        public bool Disposed { get; private set; }
        public int? ProcessId => 4242;
        public Task<int> Completion => _exit.Task;

        public void Write(string text) => onOutput(text);
        public void Exit(int code) => _exit.TrySetResult(code);

        public void Kill()
        {
            Killed = true;
            _exit.TrySetResult(-1);
        }

        public void Dispose() => Disposed = true;
    }
}
