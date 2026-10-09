using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// One tool call as the window asks for it: its whole record over a literal record read, the row summaries the
/// tiles show, and the live output of a running call over the runtime projection.
/// </summary>
[TestClass]
public sealed class DesktopToolCallsTests
{
    private const string Epoch = "epoch-1";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task Read_GivesTheWholeRecordOfACall_AsTheRuntimeWritesIt()
    {
        var output = "exit_code: 0\r\nworking_directory: C:\\code\r\nstdout:\r\n" + new string('x', 40_000) + "\r\nstderr:\r\n(empty)";
        var details = JsonSerializer.SerializeToElement(new
        {
            toolCallId = "call", toolName = "shell_command", arguments = new { command = "dotnet build\n  -c Release", workdir = "C:\\code", timeoutMs = (int?)null },
            readFiles = new[] { "a.cs" }, modifiedFiles = Array.Empty<string>(),
            result = new { success = true, items = new object[] { new Dictionary<string, object> { ["$type"] = "text", ["value"] = output } } },
        });
        var service = Service(new() { [40] = Activity(AgentActivityPhase.Completed, "shell_command", details) });

        var response = await service.ReadAsync(new(Epoch, "session", "40", null, null, 0), CancellationToken.None);

        Assert.AreEqual("ok", response.Status);
        var call = response.Call!;
        Assert.AreEqual(("ToolCall", "Completed", "shell_command"), (call.Kind, call.Phase, call.Name));
        Assert.AreEqual("dotnet build\n  -c Release", call.Command, "The whole command, not the first line a row previews.");
        Assert.AreEqual("C:\\code", call.WorkingDirectory);
        Assert.IsNull(call.ExitCode, "The exit code of a shell command is in the text of its result.");
        Assert.AreEqual(new ToolCallText(output, output.Length, false), call.Output, "A history row cuts this text; the record has all of it.");
        using var given = JsonDocument.Parse(call.Arguments!.Text);
        Assert.AreEqual("C:\\code", given.RootElement.GetProperty("workdir").GetString());
        CollectionAssert.AreEqual(new[] { "a.cs" }, call.ReadFiles);
        Assert.AreEqual(0, call.ModifiedFiles.Length);
        Assert.IsNull(call.Diff);
        Assert.IsNull(call.Error);
        // The wire form has no absolute path the record does not hold and serializes with the generated context.
        Assert.IsTrue(JsonSerializer.Serialize(response, DesktopJsonContext.Default.ToolCallResponse).Contains("\"more\":false", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Read_ContinuesALongTextByPosition()
    {
        var output = string.Concat(Enumerable.Range(0, ToolCallsService.ChunkCharacters / 2 + 50).Select(static index => "😀"));
        var service = Service(new()
        {
            [1] = Activity(AgentActivityPhase.Completed, "tool", JsonSerializer.SerializeToElement(new { arguments = "{}", result = new { content = output }, diff = "@@ -1 +1 @@\n-a\n+b\n" })),
        });

        var first = (await service.ReadAsync(new(Epoch, "session", "1", null, null, 0), CancellationToken.None)).Call!;
        Assert.IsTrue(first.Output!.More);
        Assert.AreEqual(output.Length, first.Output.Length);
        Assert.AreEqual(ToolCallsService.ChunkCharacters, first.Output.Text.Length);
        Assert.AreEqual(new ToolCallText("@@ -1 +1 @@\n-a\n+b\n", 18, false), first.Diff);

        var next = await service.ReadAsync(new(Epoch, "session", "1", null, "output", first.Output.Text.Length), CancellationToken.None);
        Assert.AreEqual("ok", next.Status);
        Assert.IsNull(next.Call);
        Assert.AreEqual(output, first.Output.Text + next.Text!.Text);
        Assert.IsFalse(next.Text.More);
        // A part never ends between the two halves of a pair.
        var odd = (await service.ReadAsync(new(Epoch, "session", "1", null, "output", 1), CancellationToken.None)).Text!;
        Assert.AreEqual(ToolCallsService.ChunkCharacters - 1, odd.Text.Length);
        Assert.IsTrue(char.IsLowSurrogate(odd.Text[^1]));
        Assert.AreEqual(new ToolCallText(string.Empty, output.Length, false), (await service.ReadAsync(new(Epoch, "session", "1", null, "output", output.Length), CancellationToken.None)).Text);
        Assert.AreEqual("invalid", (await service.ReadAsync(new(Epoch, "session", "1", null, "output", output.Length + 1), CancellationToken.None)).Status);
        Assert.AreEqual("{}", (await service.ReadAsync(new(Epoch, "session", "1", null, "arguments", 0), CancellationToken.None)).Text!.Text);
    }

    [TestMethod]
    public async Task Read_TakesTheOutputRecordOfTheCall_AndReadsTheShapesOfOtherProviders()
    {
        var completed = new AgentContentCompletedEvent(new("provider"), "session", DateTimeOffset.UnixEpoch, new("run"), AgentContentKind.CommandOutput, "output", "activity", "from the output record",
            JsonSerializer.SerializeToElement(new { diff = "@@ -1 +1 @@\n-x\n+y\n" }));
        var foreign = completed with { ParentActivityId = "another call", Content = "of another call" };
        var command = new AgentActivityEvent(new("provider"), "session", DateTimeOffset.UnixEpoch, new("run"), AgentActivityKind.CommandExecution, AgentActivityPhase.Failed,
            "activity", null, "bash -lc 'make'", "make: *** [all] Error 2", JsonSerializer.SerializeToElement(new { cwd = "/src", exitCode = 2, aggregatedOutput = "aggregated" }));
        var service = Service(new() { [10] = command, [20] = completed, [30] = foreign, [40] = Activity(AgentActivityPhase.Started, null, JsonSerializer.SerializeToElement(new { mcpToolName = "search", input = new { query = "q" } })) });

        var call = (await service.ReadAsync(new(Epoch, "session", "10", "20", null, 0), CancellationToken.None)).Call!;
        Assert.AreEqual(("CommandExecution", "Failed", "bash -lc 'make'", "bash -lc 'make'", "/src", 2), (call.Kind, call.Phase, call.Name, call.Command, call.WorkingDirectory, call.ExitCode));
        Assert.AreEqual("from the output record", call.Output!.Text);
        Assert.AreEqual("@@ -1 +1 @@\n-x\n+y\n", call.Diff!.Text, "The diff an edit left can be on its output record.");
        Assert.AreEqual("make: *** [all] Error 2", call.Error);

        // The output record of another call is not shown as the output of this one.
        Assert.AreEqual("aggregated", (await service.ReadAsync(new(Epoch, "session", "10", "30", null, 0), CancellationToken.None)).Call!.Output!.Text);
        // A call that still runs has no output and no error, and its name can be in its details.
        var running = (await service.ReadAsync(new(Epoch, "session", "40", null, null, 0), CancellationToken.None)).Call!;
        Assert.AreEqual(("search", null, null, "{\"query\":\"q\"}"), (running.Name, running.Output, running.Error, running.Arguments!.Text));
    }

    [TestMethod]
    public async Task Read_RefusesWhatItDoesNotServe()
    {
        var service = Service(new() { [40] = Activity(AgentActivityPhase.Completed, "tool", null), [50] = new AgentContentCompletedEvent(new("provider"), "session", DateTimeOffset.UnixEpoch, null, AgentContentKind.Assistant, "c", null, "text") });
        async Task<string> Status(ToolCallRequest request) => (await service.ReadAsync(request, CancellationToken.None)).Status;

        Assert.AreEqual("unavailable", (await new ToolCallsService().ReadAsync(new(Epoch, "session", "40", null, null, 0), CancellationToken.None)).Status);
        Assert.AreEqual("stale_epoch", await Status(new("other", "session", "40", null, null, 0)));
        foreach (var invalid in new ToolCallRequest[] { new(Epoch, " ", "40", null, null, 0), new(Epoch, "session", "4x", null, null, 0), new(Epoch, "session", "", null, null, 0),
            new(Epoch, "session", "40", "-1", null, 0), new(Epoch, "session", "40", null, "details", 0), new(Epoch, "session", "40", null, null, -1),
            new(Epoch, "session", "99999999999999999999", null, null, 0), new(Epoch, "session", "40", null, "diff", 0) })
            Assert.AreEqual("invalid", await Status(invalid), invalid.ToString());
        Assert.AreEqual("missing_record", await Status(new(Epoch, "session", "50", null, null, 0)), "A record that is no activity is no tool call.");
        Assert.AreEqual("missing_record", await Status(new(Epoch, "session", "41", null, null, 0)));
        Assert.AreEqual("missing_session", await Status(new(Epoch, "unknown", "40", null, null, 0)));

        // The shared read gate refuses synchronously when it is full or closed; a read that fails says nothing of its cause.
        static ToolCallsService Failing(Func<Task<AgentEvent?>> read) => new((_, _, _) => read(), null, Epoch);
        Assert.AreEqual("capacity", (await Failing(() => throw new InvalidOperationException()).ReadAsync(new(Epoch, "session", "40", null, null, 0), CancellationToken.None)).Status);
        Assert.AreEqual("closed", (await Failing(() => throw new ObjectDisposedException("reads")).ReadAsync(new(Epoch, "session", "40", null, null, 0), CancellationToken.None)).Status);
        Assert.AreEqual("read_failed", (await Failing(() => Task.FromException<AgentEvent?>(new IOException(@"C:\Users\someone\secret"))).ReadAsync(new(Epoch, "session", "40", null, null, 0), CancellationToken.None)).Status);
        Assert.AreEqual("too_large", (await Failing(() => Task.FromException<AgentEvent?>(new AgentSessionHistoryException("record_too_large"))).ReadAsync(new(Epoch, "session", "40", null, null, 0), CancellationToken.None)).Status);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ReadAsync(new(Epoch, "session", "40", null, null, 0), canceled.Token));
    }

    [TestMethod]
    public async Task Observe_StreamsWhatARunningCallWrites_InItemsOfBoundedSize()
    {
        var runtime = new SessionRuntimeEventPublisher();
        var service = new ToolCallsService((_, _, _) => Task.FromResult<AgentEvent?>(null), runtime.ToolOutput, Epoch, TimeSpan.Zero);
        runtime.TryPublish(new SessionAgentEvent("session", Activity(AgentActivityPhase.Started, "shell_command", null)));
        var first = "a" + string.Concat(Enumerable.Repeat("😀", ToolCallsService.ItemCharacters / 2));
        runtime.TryPublish(Delta(first));

        await using var channel = service.Observe(new(Epoch, "session", "activity"), CancellationToken.None);
        await using var items = channel.Items.GetAsyncEnumerator();
        Assert.IsTrue(await items.MoveNextAsync().AsTask().WaitAsync(Wait));
        var head = items.Current;
        Assert.AreEqual(("ok", "0", true, false), (head.Status, head.Start, head.IsReset, head.IsComplete));
        Assert.AreEqual(ToolCallsService.ItemCharacters - 1, head.Text.Length, "An item never ends between the two halves of a pair.");
        Assert.IsTrue(await items.MoveNextAsync().AsTask().WaitAsync(Wait));
        var tail = items.Current;
        Assert.AreEqual((head.Text.Length.ToString(), first.Length.ToString(), false, false), (tail.Start, tail.Total, tail.IsReset, tail.IsComplete));
        Assert.AreEqual(first, head.Text + tail.Text);
        Assert.IsTrue(JsonSerializer.Serialize(head, DesktopJsonContext.Default.ToolCallOutputItem).Length < 6 * ToolCallsService.ItemCharacters + 512);

        runtime.TryPublish(Delta("more\n"));
        Assert.IsTrue(await items.MoveNextAsync().AsTask().WaitAsync(Wait));
        Assert.AreEqual(("more\n", first.Length.ToString(), false), (items.Current.Text, items.Current.Start, items.Current.IsComplete));
        runtime.TryPublish(new SessionAgentEvent("session", Activity(AgentActivityPhase.Completed, "shell_command", null)));
        Assert.IsTrue(await items.MoveNextAsync().AsTask().WaitAsync(Wait));
        Assert.AreEqual((string.Empty, true), (items.Current.Text, items.Current.IsComplete));
        Assert.IsFalse(await items.MoveNextAsync().AsTask().WaitAsync(Wait));
    }

    [TestMethod]
    public async Task Observe_EndsAtOnce_ForACallThatIsNotRunningOrARequestItRefuses()
    {
        var runtime = new SessionRuntimeEventPublisher();
        var service = new ToolCallsService((_, _, _) => Task.FromResult<AgentEvent?>(null), runtime.ToolOutput, Epoch, TimeSpan.Zero);
        async Task<ToolCallOutputItem> Only(ToolCallsService target, ToolCallObserveRequest request)
        {
            var items = new List<ToolCallOutputItem>();
            await using var channel = target.Observe(request, CancellationToken.None);
            await foreach (var item in channel.Items.WaitAsync(Wait)) items.Add(item);
            return items.Single();
        }

        Assert.AreEqual(new ToolCallOutputItem("ok", string.Empty, "0", "0", true, true), await Only(service, new(Epoch, "session", "not running")));
        Assert.AreEqual("stale_epoch", (await Only(service, new("other", "session", "activity"))).Status);
        Assert.AreEqual("invalid", (await Only(service, new(Epoch, "session", " "))).Status);
        Assert.AreEqual("invalid", (await Only(service, new(Epoch, "", "activity"))).Status);
        var unavailable = await Only(new ToolCallsService(), new(Epoch, "session", "activity"));
        Assert.AreEqual(("unavailable", true), (unavailable.Status, unavailable.IsComplete));
        Assert.AreEqual("unavailable", (await Only(new ToolCallsService((_, _, _) => Task.FromResult<AgentEvent?>(null), null, Epoch), new(Epoch, "session", "activity"))).Status);
    }

    [TestMethod]
    public async Task Observe_StreamsWhatABackgroundJobOfTheSessionWrites_UnderTheIdentityOfTheJob()
    {
        var runtime = new SessionRuntimeEventPublisher();
        Action<string>? write = null;
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var jobs = new CodeAlta.Orchestration.Jobs.SessionJobService(start: (_, _, onOutput) => { write = onOutput; return new JobProcess(exit.Task); });
        var service = new ToolCallsService((_, _, _) => Task.FromResult<AgentEvent?>(null), runtime.ToolOutput, Epoch, TimeSpan.Zero) { Jobs = jobs };
        var job = jobs.Start(new() { SessionId = "session", Command = "npm test", Folder = Path.GetTempPath() }).Job!;
        write!("12 passing\n");

        await using var channel = service.Observe(new(Epoch, "session", job.Id), CancellationToken.None);
        await using var items = channel.Items.GetAsyncEnumerator();
        Assert.IsTrue(await items.MoveNextAsync().AsTask().WaitAsync(Wait));
        Assert.AreEqual(("ok", "12 passing\n", "0", true, false), (items.Current.Status, items.Current.Text, items.Current.Start, items.Current.IsReset, items.Current.IsComplete));
        write("done\n");
        Assert.IsTrue(await items.MoveNextAsync().AsTask().WaitAsync(Wait));
        Assert.AreEqual(("done\n", "11", false), (items.Current.Text, items.Current.Start, items.Current.IsComplete));
        exit.SetResult(0);
        Assert.IsTrue(await items.MoveNextAsync().AsTask().WaitAsync(Wait));
        Assert.IsTrue(items.Current.IsComplete);

        // The job of another session is not served: it reads as a call that does not run.
        await using var other = service.Observe(new(Epoch, "another", job.Id), CancellationToken.None);
        var foreign = new List<ToolCallOutputItem>();
        await foreach (var item in other.Items.WaitAsync(Wait)) foreign.Add(item);
        Assert.AreEqual(new ToolCallOutputItem("ok", string.Empty, "0", "0", true, true), foreign.Single());
    }

    private sealed class JobProcess(Task<int> exit) : CodeAlta.Orchestration.Jobs.IShellCommandProcess
    {
        public int? ProcessId => null;
        public Task<int> Completion => exit;
        public void Kill() { }
        public void Dispose() { }
    }

    [TestMethod]
    public void RowSummary_ShowsAnAltaCommandAsItsCommandLine_AndNamesFilesAndArguments()
    {
        static HistoryToolSummary Row(string? name, object details)
            => HistoryToolProjection.Project(Activity(AgentActivityPhase.Completed, name, JsonSerializer.SerializeToElement(details)));

        var alta = Row("alta", new { arguments = new { args = new[] { "plugin", "create", "git-prompt", "--description", "Shows the \"current\" branch.", "" }, stdin = (string?)null } });
        Assert.AreEqual(("alta plugin create git-prompt --description \"Shows the \\\"current\\\" branch.\" \"\"", true), (alta.Primary, alta.IsCommand));
        // Another tool with the same argument is not an alta command.
        Assert.IsFalse(Row("other", new { arguments = new { args = new[] { "x" } } }).IsCommand);

        Assert.AreEqual("https://example.org/a", Row("webget", new { arguments = new { url = "https://example.org/a", rawHtml = false } }).Primary);
        Assert.AreEqual("src/a.cs", Row("apply_patch", new { arguments = new { input = "*** Begin Patch" }, modifiedFiles = new[] { "src/a.cs" } }).Primary);
        Assert.AreEqual("a.cs (+2)", Row("apply_patch", new { arguments = new { input = "*** Begin Patch" }, modifiedFiles = new[] { @"C:\code\src\a.cs", "b.cs", "c.cs" } }).Primary);
        var click = Row("click", new { arguments = new { pageId = (string?)null, uid = "1_25", dblClick = false, nested = new { ignored = true }, text = new string('x', 200) } });
        Assert.AreEqual("uid: 1_25, dblClick: false, text: " + new string('x', 96) + "…", click.Primary);
        Assert.IsFalse(click.IsCommand);
        Assert.IsNull(Row("take_snapshot", new { arguments = new { pageId = (string?)null } }).Primary);
    }

    [TestMethod]
    public void RowSummary_OfAShellResult_SaysItsExitCodeAndPreviewsWhatTheCommandWrote()
    {
        var summary = HistoryToolProjection.ProjectOutput("exit_code: 3\r\nworking_directory: C:\\code\r\nstdout:\r\nabout to fail\r\nsecond\r\nstderr:\r\nboom", 4096, out _)!;
        Assert.AreEqual((3, "about to fail", 3), (summary.ExitCode, summary.Output, summary.OutputLines));
        Assert.AreEqual("about to fail\nsecond\nboom".Length, summary.OutputBytes);
        var silent = HistoryToolProjection.ProjectOutput("exit_code: 0\nworking_directory: /tmp\nstdout:\n(empty)\nstderr:\n(empty)", 4096, out _)!;
        Assert.AreEqual((0, null, 0, 0), (silent.ExitCode, silent.Output, silent.OutputLines, silent.OutputBytes));
        var errors = HistoryToolProjection.ProjectOutput("exit_code: 1\nworking_directory: /tmp\nstdout:\n(empty)\nstderr:\nonly errors", 4096, out _)!;
        Assert.AreEqual((1, "only errors", 1), (errors.ExitCode, errors.Output, errors.OutputLines));
        // Any other result is previewed as it is, without an exit code.
        var plain = HistoryToolProjection.ProjectOutput("shell_command was denied by the host.", 4096, out _)!;
        Assert.AreEqual((null, "shell_command was denied by the host."), (plain.ExitCode, plain.Output));

        Assert.AreEqual(new ShellCommandResult(-1, "C:\\a b", "one\nstderr:\nnot the last", "last"),
            ShellCommandResult.Parse("exit_code: -1\nworking_directory: C:\\a b\nstdout:\none\nstderr:\nnot the last\nstderr:\nlast"));
        foreach (var other in new[] { null, "", "exit_code: x\nworking_directory: /\nstdout:\na\nstderr:\nb", "exit_code: 0\nstdout:\na\nstderr:\nb", "exit_code: 0\nworking_directory: /\nstdout:\nno error section", "exit_code: 0" })
            Assert.IsNull(ShellCommandResult.Parse(other));
        // The row of the history carries the exit code to the page.
        var completed = new AgentContentCompletedEvent(new("provider"), "session", DateTimeOffset.UnixEpoch, new("run"), AgentContentKind.ToolOutput, "c", "activity",
            "exit_code: 3\nworking_directory: /tmp\nstdout:\n(empty)\nstderr:\nboom");
        Assert.AreEqual(3, WorkspaceService.ProjectHistory(new([new AgentSessionHistoryEntry(0, completed)], null, false)).Entries.Single().Tool!.ExitCode);
    }

    private static ToolCallsService Service(Dictionary<long, AgentEvent> records)
        => new((sessionId, offset, _) => sessionId != "session" ? Task.FromException<AgentEvent?>(new AgentSessionHistoryException("missing_session"))
            : records.TryGetValue(offset, out var record) ? Task.FromResult<AgentEvent?>(record) : Task.FromException<AgentEvent?>(new AgentSessionHistoryException("invalid_cursor")), null, Epoch);

    private static AgentActivityEvent Activity(AgentActivityPhase phase, string? name, JsonElement? details)
        => new(new("provider"), "session", DateTimeOffset.UnixEpoch, new("run"), AgentActivityKind.ToolCall, phase, "activity", null, name, null, details);

    private static SessionAgentEvent Delta(string text)
        => new("session", new AgentContentDeltaEvent(new("provider"), "session", DateTimeOffset.UnixEpoch, new("run"), AgentContentKind.ToolOutput, "activity:output", "activity", text));
}

internal static class ToolCallTestWaits
{
    /// <summary>Fails a test whose enumeration does not end in time, instead of hanging it.</summary>
    internal static async IAsyncEnumerable<T> WaitAsync<T>(this IAsyncEnumerable<T> source, TimeSpan timeout)
    {
        await using var enumerator = source.GetAsyncEnumerator();
        while (await enumerator.MoveNextAsync().AsTask().WaitAsync(timeout)) yield return enumerator.Current;
    }
}
