using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Claude;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Tests;

/// <summary>
/// A Claude Code session of CodeAlta opened in VS Code or a terminal: the host names the conversation of the CLI from the
/// state the session recorded, and closes the idle process it keeps for the session before another program takes it.
/// </summary>
[TestClass]
public sealed class ClaudeCodeOpenElsewhereTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task Conversation_IsNamedFromTheRecordedState_AndReleaseClosesTheIdleProcess_WhichTheNextPromptResumes()
    {
        using var directory = TestTempDirectory.Create();
        var (cli, projectRoot) = Prepare(directory);
        var next = 7100;
        cli.AssignProcessId = () => Interlocked.Increment(ref next);
        var options = cli.CreateOptions();
        await using var host = await CreateHostAsync(directory, options, projectRoot);
        var session = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, ClaudeCodeModelProviderRuntime.CreateDescriptor(options), "Open elsewhere");
        await SendAsync(host, session.SessionId, "one");

        var process = SessionProcess(cli);
        var conversation = process.SessionId;
        Assert.IsTrue(Guid.TryParseExact(conversation, "D", out _), conversation);
        // The CLI CodeAlta runs writes its entry in the registry of live sessions: it is CodeAlta's own, not another
        // program that holds the conversation, although the registry is read by another runtime than the session's.
        cli.HoldConversation(process.ProcessId!.Value, conversation, entrypoint: "sdk-cli");

        var described = await host.Commands.GetSharedConversationAsync(session.SessionId).WaitAsync(Timeout);
        Assert.AreEqual("ok", described.Status);
        var shared = described.Conversation!;
        Assert.AreEqual(conversation, shared.ConversationId);
        Assert.AreEqual($"claude --resume {conversation}", shared.ResumeCommand);
        Assert.AreEqual($"vscode://anthropic.claude-code/open?session={conversation}", shared.EditorUri, "Built by the host from the recorded identifier.");
        Assert.AreEqual(projectRoot, shared.WorkingDirectory);
        Assert.IsNull(shared.Holder, "CodeAlta's own process does not hold the conversation for another program.");
        Assert.IsNull(shared.ConfigDirectory, "No account folder of its own: the CLI's folder.");

        // VS Code opened it meanwhile: it is named as the holder.
        cli.HoldConversation(9001, conversation);
        var held = (await host.Commands.GetSharedConversationAsync(session.SessionId).WaitAsync(Timeout)).Conversation!.Holder;
        Assert.AreEqual((AgentConversationHolderOrigin.VsCode, 9001), (held!.Origin, held.ProcessId));
        cli.ReleaseConversation(9001);

        Assert.IsFalse(process.IsDisposed);
        var released = await host.Commands.ReleaseSharedConversationAsync(session.SessionId).WaitAsync(Timeout);
        Assert.AreEqual("ok", released.Status);
        Assert.AreEqual(conversation, released.Conversation!.ConversationId);
        Assert.IsTrue(process.IsDisposed, "The idle process of the session is closed: VS Code does not find the conversation held by CodeAlta.");
        Assert.AreEqual(0, options.OwnProcesses.Snapshot().Count);
        Assert.IsNull(await host.RuntimeService.TryGetActiveSessionDescriptorAsync(session.SessionId), "The attachment is retired.");
        cli.ReleaseConversation(process.ProcessId!.Value);

        // Released again: nothing is attached, nothing to close.
        Assert.AreEqual("ok", (await host.Commands.ReleaseSharedConversationAsync(session.SessionId).WaitAsync(Timeout)).Status);

        // The next prompt attaches the session again and resumes the conversation as the other program left it.
        cli.WriteTranscript(conversation);
        await SendAsync(host, session.SessionId, "two");
        var resumed = SessionProcess(cli);
        Assert.AreNotSame(process, resumed);
        Assert.AreEqual(conversation, resumed.ResumedSessionId);
        Assert.AreEqual("two", ClaudeCodeFakeProcess.UserText(resumed.UserMessages.Single()));
    }

    [TestMethod]
    public async Task Conversation_OfAProviderWithAnAccountFolder_NamesThatFolder_WhichTheCommandsThatResumeItSet()
    {
        using var directory = TestTempDirectory.Create();
        var (cli, projectRoot) = Prepare(directory);
        var options = cli.CreateOptions(configDirectory: cli.ConfigRoot);
        await using var host = await CreateHostAsync(directory, options, projectRoot);
        var session = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, ClaudeCodeModelProviderRuntime.CreateDescriptor(options), "Account");
        await SendAsync(host, session.SessionId, "one");

        var described = await host.Commands.GetSharedConversationAsync(session.SessionId).WaitAsync(Timeout);
        Assert.AreEqual("ok", described.Status);
        Assert.AreEqual(cli.ConfigRoot, described.Conversation!.ConfigDirectory, "The CLI runs with CLAUDE_CONFIG_DIR set to it: the conversation is there.");
        Assert.AreEqual($"claude --resume {SessionProcess(cli).SessionId}", described.Conversation.ResumeCommand);
        var released = await host.Commands.ReleaseSharedConversationAsync(session.SessionId).WaitAsync(Timeout);
        Assert.AreEqual(cli.ConfigRoot, released.Conversation!.ConfigDirectory);
    }

    [TestMethod]
    public async Task Release_IsRefused_WhileARunWorks_AndLeavesTheProcess()
    {
        using var directory = TestTempDirectory.Create();
        var (cli, projectRoot) = Prepare(directory);
        var options = cli.CreateOptions();
        await using var host = await CreateHostAsync(directory, options, projectRoot);
        var session = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, ClaudeCodeModelProviderRuntime.CreateDescriptor(options), "Busy");
        await SendAsync(host, session.SessionId, "one");

        var answering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cli.OnUserMessage = async (process, message) =>
        {
            answering.TrySetResult();
            await answer.Task;
            process.EmitTextTurn("msg_2", "ok", message);
        };
        var receipt = host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), session.SessionId, "two")).Receipt;
        Assert.IsNotNull(receipt);
        try
        {
            await answering.Task.WaitAsync(Timeout);
            var process = SessionProcess(cli);
            Assert.AreEqual("running", (await host.Commands.ReleaseSharedConversationAsync(session.SessionId).WaitAsync(Timeout)).Status);
            Assert.AreEqual("running", await host.RuntimeService.ReleaseSharedConversationAsync(session.SessionId).WaitAsync(Timeout), "The runtime refuses it too.");
            Assert.IsFalse(process.IsDisposed);
            Assert.AreEqual("ok", (await host.Commands.GetSharedConversationAsync(session.SessionId).WaitAsync(Timeout)).Status, "Described all the same.");
        }
        finally { answer.TrySetResult(); }

        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await receipt.Completion.WaitAsync(Timeout)).Outcome);
    }

    [TestMethod]
    public async Task Release_IsRefused_WhileRemoteControlIsOn()
    {
        using var directory = TestTempDirectory.Create();
        var (cli, projectRoot) = Prepare(directory);
        var options = cli.CreateOptions();
        await using var host = await CreateHostAsync(directory, options, projectRoot);
        var session = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, ClaudeCodeModelProviderRuntime.CreateDescriptor(options), "Remote");
        await SendAsync(host, session.SessionId, "one");
        Assert.AreEqual(AgentRemoteControlStatus.Connected, (await host.Commands.SetRemoteControlAsync(session.SessionId, true).WaitAsync(Timeout)).RemoteControl!.Status);
        using (var limit = new CancellationTokenSource(Timeout))
        {
            while ((await host.RuntimeService.GetCurrentStateAsync(session.SessionId, limit.Token)).Entry?.RemoteControl.Status != AgentRemoteControlStatus.Connected)
                await Task.Delay(20, limit.Token);
        }

        Assert.AreEqual("remote_control", (await host.Commands.ReleaseSharedConversationAsync(session.SessionId).WaitAsync(Timeout)).Status,
            "Closing the process would end Remote Control.");
        Assert.IsFalse(SessionProcess(cli).IsDisposed);
        Assert.IsTrue(SessionProcess(cli).RemoteControlOn);
    }

    [TestMethod]
    public async Task Conversation_IsRefused_ForUnknownSessions_AndSessionsWithoutAConversation()
    {
        using var directory = TestTempDirectory.Create();
        var (cli, projectRoot) = Prepare(directory);
        var options = cli.CreateOptions();
        await using var host = await CreateHostAsync(directory, options, projectRoot);
        var session = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, ClaudeCodeModelProviderRuntime.CreateDescriptor(options), "Not yet");

        Assert.AreEqual(new OwnedSharedConversationResult("unavailable", null), await host.Commands.GetSharedConversationAsync(Guid.NewGuid().ToString("N")));
        Assert.AreEqual(new OwnedSharedConversationResult("none", null), await host.Commands.GetSharedConversationAsync(session.SessionId),
            "No turn yet: the CLI has no conversation of the session.");
        Assert.AreEqual(new OwnedSharedConversationResult("none", null), await host.Commands.ReleaseSharedConversationAsync(session.SessionId));

        // A recorded identifier that is not one the CLI gives is put in no command and no address.
        await SendAsync(host, session.SessionId, "one");
        var store = host.SessionViewCatalog.JournalStore.CreateSessionStore();
        var state = (await store.GetStateAsync(session.SessionId))!;
        var tampered = JsonSerializer.SerializeToElement(new { kind = "claude-code", sessionId = "x & calc.exe", users = 1, assistants = 1 });
        await host.Commands.ReleaseSharedConversationAsync(session.SessionId);
        await store.UpsertStateAsync(state with { ProviderState = tampered, UpdatedAt = DateTimeOffset.UtcNow });
        Assert.AreEqual(new OwnedSharedConversationResult("none", null), await host.Commands.GetSharedConversationAsync(session.SessionId));
    }

    private static (ClaudeCodeFakeCli Cli, string ProjectRoot) Prepare(TestTempDirectory directory)
    {
        var projectRoot = Directory.CreateDirectory(Path.Combine(directory.Path, "project")).FullName;
        Directory.CreateDirectory(Path.Combine(directory.Path, "global"));
        return (new ClaudeCodeFakeCli { ConfigRoot = Path.Combine(directory.Path, "claude") }, projectRoot);
    }

    private static Task<CodeAltaHost> CreateHostAsync(TestTempDirectory directory, ClaudeCodeModelProviderRuntimeOptions options, string projectRoot)
        => CodeAltaHost.CreateAsync(
            new CodeAltaHostOptions
            {
                GlobalRoot = Path.Combine(directory.Path, "global"),
                CurrentProjectPath = projectRoot,
                IsHeadless = true,
                HasInteractiveUi = false,
                StartPlugins = false,
                AutoApproveOwnedPermissions = true,
                ConfigureModelProviders = registry => registry.RegisterOrReplace(
                    ClaudeCodeModelProviderRuntime.CreateDescriptor(options),
                    () => new ClaudeCodeModelProviderRuntime(options)),
            });

    private static async Task SendAsync(CodeAltaHost host, string sessionId, string prompt)
    {
        var receipt = host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), sessionId, prompt)).Receipt;
        Assert.IsNotNull(receipt);
        var result = await receipt.Completion.WaitAsync(Timeout);
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, result.Outcome, result.Code);
    }

    private static ClaudeCodeFakeProcess SessionProcess(ClaudeCodeFakeCli cli)
        => cli.Processes.Last(static process => process.Launch.Arguments.Contains("--mcp-config"));
}
