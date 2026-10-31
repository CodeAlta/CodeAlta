using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Catalog.ModelTiers;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// A session on Adaptive: CodeAlta runs the turns of the user on the turn tier, has a turn that changed code recheck its
/// change, and continues a turn that ends in visible trouble on the escalation tier. The real host, runtime and command
/// owner, with a scripted provider whose runs say what they did.
/// </summary>
[TestClass]
public sealed class AdaptiveTurnTests
{
    private static readonly TimeSpan Timeout = TestDeadlines.Hang;

    private const string Config = """
        [models.tiers]
        complex = ["prov-a:model-c@high"]
        quick = ["prov-a:model-a@high"]

        [models.adaptive]
        code = "quick"
        escalate = "complex"
        """;

    [TestMethod]
    public async Task ATurnThatChangedCode_IsRecheckedOnce_OnTheSameModel()
    {
        await using var f = await Fixture.CreateAsync(Config);
        Assert.AreEqual(("ok", (ModelTier?)ModelTier.Quick), await f.Host.Commands.SetSessionAdaptiveAsync(f.Session.SessionId));
        f.Provider.Scripts.Enqueue(static run => run.Edit("src/app.cs"));
        // The recheck changes code too: a recheck is not rechecked.
        f.Provider.Scripts.Enqueue(static run => run.Edit("src/app.cs"));

        f.Send("add the feature");
        var first = await f.Provider.NextSendAsync();
        Assert.AreEqual("add the feature", first.Text);
        var recheck = await f.Provider.NextSendAsync();

        Assert.AreEqual(AgentPromptProvenance.AdaptiveRecheck, recheck.Provenance);
        StringAssert.Contains(recheck.Text, "check your change against each requirement of the request");
        Assert.AreEqual("model-a", recheck.Options.Model, "The recheck runs on the model of the turn.");
        await f.SettleAsync();
        Assert.AreEqual(2, f.Provider.SendCount);
        Assert.IsTrue((await f.StateAsync()).Adaptive);
    }

    [TestMethod]
    public async Task ATurnThatChangedOnlyDocs_OrNothing_IsNotRechecked()
    {
        await using var f = await Fixture.CreateAsync(Config);
        await f.Host.Commands.SetSessionAdaptiveAsync(f.Session.SessionId);
        f.Provider.Scripts.Enqueue(static run => run.Edit("doc/readme.md"));

        f.Send("explain it in the readme");
        await f.Provider.NextSendAsync();
        await f.SettleAsync();
        f.Send("and what does the parser do?", "second");
        await f.Provider.NextSendAsync();
        await f.SettleAsync();

        Assert.AreEqual(2, f.Provider.SendCount, "Neither turn changed code.");
    }

    [TestMethod]
    public async Task ATurnWhoseLastTestRunFailed_ContinuesOnTheEscalationTier_AndTheNextTurnRunsOnTheTurnTierAgain()
    {
        await using var f = await Fixture.CreateAsync(Config);
        await f.Host.Commands.SetSessionAdaptiveAsync(f.Session.SessionId);
        f.Provider.Scripts.Enqueue(static run => run.Test("npm test", "exit_code: 1\n1 failing"));

        f.Send("fix the failing test");
        await f.Provider.NextSendAsync();
        var escalation = await f.Provider.NextSendAsync();

        Assert.AreEqual(AgentPromptProvenance.AdaptiveEscalation, escalation.Provenance);
        StringAssert.Contains(escalation.Text, "the last test run failed.");
        Assert.AreEqual("model-c", escalation.Options.Model, "The turn continues on the model of the escalation tier.");
        StringAssert.Contains(escalation.Text, "CodeAlta moved this session to PROV-A model-c high");
        await f.SettleAsync();
        Assert.AreEqual("quick", (await f.StateAsync()).Tier, "Once the escalation answered, the session is back on the turn tier.");

        // The next turn of the user runs on the turn tier, whatever model the session ran last.
        f.Send("thanks, now rename it", "second");
        var next = await f.Provider.NextSendAsync();
        Assert.AreEqual("model-a", next.Options.Model);
        await f.SettleAsync();
        Assert.AreEqual(3, f.Provider.SendCount);
    }

    [TestMethod]
    public async Task ATestRunWhoseOutputSaysItFailed_IsEscalated_ThoughAPipeHidItsExitCode()
    {
        // As Claude Code runs it: `npm test | grep`, no exit code in the output, the summary of the runner.
        await using var f = await Fixture.CreateAsync(Config);
        await f.Host.Commands.SetSessionAdaptiveAsync(f.Session.SessionId);
        f.Provider.Scripts.Enqueue(static run => run.Test("npm test 2>&1 | grep -E \"^ℹ (tests|pass|fail)\"", "ℹ tests 3\nℹ pass 2\nℹ fail 1"));

        f.Send("run the tests");
        await f.Provider.NextSendAsync();
        var escalation = await f.Provider.NextSendAsync();

        Assert.AreEqual(AgentPromptProvenance.AdaptiveEscalation, escalation.Provenance);
        StringAssert.Contains(escalation.Text, "the last test run failed");
    }

    [TestMethod]
    public async Task ATestRunThatPassedLast_OrATurnWithoutTests_IsNotEscalated()
    {
        await using var f = await Fixture.CreateAsync(Config);
        await f.Host.Commands.SetSessionAdaptiveAsync(f.Session.SessionId);
        f.Provider.Scripts.Enqueue(static run =>
        {
            run.Test("dotnet test", "exit_code: 1\nfailed");
            run.Test("dotnet test", "exit_code: 0\npassed");
        });

        f.Send("fix it");
        await f.Provider.NextSendAsync();
        await f.SettleAsync();

        Assert.AreEqual(1, f.Provider.SendCount);
    }

    [TestMethod]
    public async Task ATurnThatEndsWithoutAnAnswer_IsEscalated()
    {
        await using var f = await Fixture.CreateAsync(Config);
        await f.Host.Commands.SetSessionAdaptiveAsync(f.Session.SessionId);
        f.Provider.Scripts.Enqueue(static run => run.Silent = true);

        f.Send("do the work");
        await f.Provider.NextSendAsync();
        var escalation = await f.Provider.NextSendAsync();

        StringAssert.Contains(escalation.Text, "the turn ended without an answer");
    }

    [TestMethod]
    public async Task ASessionThatIsNotOnAdaptive_IsNeitherRecheckedNorEscalated()
    {
        await using var f = await Fixture.CreateAsync(Config);
        f.Provider.Scripts.Enqueue(static run => { run.Edit("src/app.cs"); run.Test("npm test", "exit_code: 2"); });

        f.Send("add the feature");
        await f.Provider.NextSendAsync();
        await f.SettleAsync();

        Assert.AreEqual(1, f.Provider.SendCount);
    }

    [TestMethod]
    public async Task ChoosingATierByHand_TakesTheSessionOffAdaptive()
    {
        await using var f = await Fixture.CreateAsync(Config);
        await f.Host.Commands.SetSessionAdaptiveAsync(f.Session.SessionId);
        Assert.IsTrue((await f.Host.Commands.GetSelectionChoicesAsync(f.Session.SessionId))!.Adaptive);

        Assert.AreEqual("ok", await f.Host.Commands.SetSessionTierAsync(f.Session.SessionId, ModelTier.Complex));

        var state = await f.StateAsync();
        Assert.AreEqual("complex", state.Tier);
        Assert.IsNull(state.Adaptive);
        Assert.IsFalse((await f.Host.Commands.GetSelectionChoicesAsync(f.Session.SessionId))!.Adaptive);
    }

    [TestMethod]
    public async Task Adaptive_IsRefused_UntilItIsSetUpWithModelsInItsTiers()
    {
        await using (var none = await Fixture.CreateAsync(Config.Replace("[models.adaptive]", "[models.unused]", StringComparison.Ordinal)))
        {
            Assert.AreEqual(("not_ready", (ModelTier?)null), await none.Host.Commands.SetSessionAdaptiveAsync(none.Session.SessionId));
            Assert.IsNull((await none.StateAsync()).Adaptive);
        }

        // The escalation tier has no model.
        await using var empty = await Fixture.CreateAsync(Config.Replace("complex = [\"prov-a:model-c@high\"]", string.Empty, StringComparison.Ordinal));
        Assert.AreEqual("not_ready", (await empty.Host.Commands.SetSessionAdaptiveAsync(empty.Session.SessionId)).Status);
    }

    [TestMethod]
    public async Task InAGitFolder_ATurnThatChangedCode_IsToldByGit_AndOneThatCommittedItsWork_IsNot()
    {
        await using var f = await Fixture.CreateAsync(Config, git: true);
        if (!f.HasGit) Assert.Inconclusive("git is not installed.");
        await f.Host.Commands.SetSessionAdaptiveAsync(f.Session.SessionId);
        // The tools say nothing of the files: git tells the change.
        f.Provider.Scripts.Enqueue(run => File.WriteAllText(Path.Combine(run.Folder, "app.js"), "export const value = 2;\n"));

        f.Send("change the value");
        await f.Provider.NextSendAsync();
        var recheck = await f.Provider.NextSendAsync();
        Assert.AreEqual(AgentPromptProvenance.AdaptiveRecheck, recheck.Provenance);
        await f.SettleAsync();

        // A turn that commits what is there changes nothing in the files.
        f.Provider.Scripts.Enqueue(run => Git(run.Folder, "commit", "-qam", "Change the value"));
        f.Send("commit it", "second");
        await f.Provider.NextSendAsync();
        await f.SettleAsync();
        Assert.AreEqual(3, f.Provider.SendCount);
    }

    [TestMethod]
    public async Task AHeadlessSessionOnAdaptive_StartsOnTheTurnTier_AndIsOnAdaptive()
    {
        await using var f = await Fixture.CreateAsync(Config);
        await using var driver = new HeadlessSessionDriver(f.Host);
        Assert.AreEqual(HeadlessOpenStatus.InvalidRequest,
            (await driver.OpenAsync(new HeadlessSessionRequest { ProjectFolder = f.Project, Adaptive = true, Tier = ModelTier.Complex })).Status);
        var opened = await driver.OpenAsync(new HeadlessSessionRequest { ProjectFolder = f.Project, Adaptive = true });
        Assert.AreEqual(HeadlessOpenStatus.Created, opened.Status, opened.Message);
        Assert.AreEqual("model-a", opened.Session!.ModelId, "A session on Adaptive starts with the model of the tier of its turns.");
        Assert.AreEqual((ModelTier.Quick, true), await f.Host.RuntimeService.GetSessionModelChoiceAsync(opened.Session.SessionId));
    }

    [TestMethod]
    public async Task AHeadlessSession_IsNotPutOnAdaptive_WhileItIsNotSetUp()
    {
        await using var f = await Fixture.CreateAsync(Config.Replace("[models.adaptive]", "[models.unused]", StringComparison.Ordinal));
        await using var driver = new HeadlessSessionDriver(f.Host);
        var opened = await driver.OpenAsync(new HeadlessSessionRequest { ProjectFolder = f.Project, Adaptive = true });
        Assert.AreEqual(HeadlessOpenStatus.ProviderNotReady, opened.Status);
        StringAssert.Contains(opened.Message, "Adaptive is not set up");
    }

    [TestMethod]
    public void Trouble_IsWhatTheHostSees_WithoutKnowingTheTask()
    {
        Assert.AreEqual("the turn ended without an answer", AdaptiveTurnCoordinator.Trouble(null, 3, 0));
        Assert.AreEqual("the turn ended without an answer", AdaptiveTurnCoordinator.Trouble("  ", 3, null));
        Assert.AreEqual("the answer says the tools cannot be used, though it called none",
            AdaptiveTurnCoordinator.Trouble("I cannot run commands in this environment.", 0, null));
        Assert.AreEqual("the last test run failed (exit code 3)", AdaptiveTurnCoordinator.Trouble("Done.", 5, 3));
        Assert.AreEqual("the last test run failed", AdaptiveTurnCoordinator.Trouble("Done.", 5, 1));
        // A plain "cannot" of an answer, a refusal from a turn that used its tools, and a passing last run are no trouble.
        Assert.IsNull(AdaptiveTurnCoordinator.Trouble("The value cannot be null here, so the check is right.", 0, null));
        Assert.IsNull(AdaptiveTurnCoordinator.Trouble("I do not have access to the network.", 2, null));
        Assert.IsNull(AdaptiveTurnCoordinator.Trouble("Done.", 5, 0));
    }

    [TestMethod]
    public void ACodeChange_IsAnyFileButDocumentationAndWhatCodeAltaKeeps()
    {
        Assert.IsTrue(AdaptiveTurnCoordinator.IsCode("src/Parser.cs"));
        Assert.IsTrue(AdaptiveTurnCoordinator.IsCode("package.json"));
        Assert.IsFalse(AdaptiveTurnCoordinator.IsCode("README.md"));
        Assert.IsFalse(AdaptiveTurnCoordinator.IsCode(@"docs\guide.rst"));
        Assert.IsFalse(AdaptiveTurnCoordinator.IsCode(".alta/plans/plan.md"));
        Assert.IsFalse(AdaptiveTurnCoordinator.IsCode(".alta/tasks/x.json"));
        Assert.AreEqual(1, AdaptiveTurnCoordinator.ExitCode("exit_code: 1\nboom"));
        // The summary of the runner tells how the tests ended, whatever the exit code of a pipe.
        foreach (var failed in new[] { "ℹ tests 3\nℹ pass 2\nℹ fail 1", "# fail 2", "=== 1 failed, 3 passed in 0.2s ===", "  2 passing\n  1 failing",
            "Tests:       1 failed, 3 passed, 4 total", "Failed!  - Failed:     1, Passed:    41", "--- FAIL: TestParse (0.00s)\nFAIL", "test result: FAILED. 3 passed; 1 failed" })
            Assert.AreEqual(1, AdaptiveTurnCoordinator.TestOutcome(failed), failed);
        foreach (var passed in new[] { "ℹ tests 3\nℹ pass 3\nℹ fail 0", "=== 4 passed in 0.1s ===", "Passed!  - Failed:     0, Passed:    42", "ok  \tgithub.com/a/b\t0.01s",
            "test result: ok. 4 passed; 0 failed", "  4 passing" })
            Assert.AreEqual(0, AdaptiveTurnCoordinator.TestOutcome(passed), passed);
        Assert.IsNull(AdaptiveTurnCoordinator.TestOutcome("Compiling..."));
        Assert.IsNull(AdaptiveTurnCoordinator.ExitCode("no code"));
        Assert.IsTrue(AdaptiveTurnCoordinator.IsTestCommand("shell_command", Arguments("cd src && dotnet test -c Release")));
        Assert.IsTrue(AdaptiveTurnCoordinator.IsTestCommand("Bash", Arguments("python -m pytest tests/")));
        Assert.IsFalse(AdaptiveTurnCoordinator.IsTestCommand("shell_command", Arguments("git status")));
    }

    private static JsonElement Arguments(string command)
        => JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["arguments"] = new Dictionary<string, string> { ["command"] = command } });

    private static void Git(string folder, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false" }.Concat(arguments))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', arguments)}: {error}");
    }

    private sealed record SendRecord(string Text, AgentSessionCreateOptions Options, string? Provenance);

    /// <summary>What a scripted run does before it answers: the tool calls it makes, and whether it answers.</summary>
    private sealed class RunScript(ScriptedProvider.Session session, AgentRunId run)
    {
        private int _calls;

        public string Folder => session.Folder;

        public bool Silent { get; set; }

        public void Edit(string path)
        {
            var id = $"call-{++_calls}";
            session.Emit(new AgentActivityEvent(session.ProviderId, session.SessionId, DateTimeOffset.UtcNow, run, AgentActivityKind.ToolCall,
                AgentActivityPhase.Started, id, null, "write_file", null, JsonSerializer.SerializeToElement(new { arguments = new { path } })));
            session.Emit(new AgentContentCompletedEvent(session.ProviderId, session.SessionId, DateTimeOffset.UtcNow, run, AgentContentKind.ToolOutput,
                id + "-output", id, "Written.", JsonSerializer.SerializeToElement(new { toolName = "write_file", modifiedFiles = new[] { path } })));
            session.Emit(new AgentActivityEvent(session.ProviderId, session.SessionId, DateTimeOffset.UtcNow, run, AgentActivityKind.ToolCall,
                AgentActivityPhase.Completed, id, null, "write_file", null, null));
        }

        public void Test(string command, string output)
        {
            var id = $"call-{++_calls}";
            session.Emit(new AgentActivityEvent(session.ProviderId, session.SessionId, DateTimeOffset.UtcNow, run, AgentActivityKind.ToolCall,
                AgentActivityPhase.Started, id, null, "shell_command", null, Arguments(command)));
            session.Emit(new AgentContentCompletedEvent(session.ProviderId, session.SessionId, DateTimeOffset.UtcNow, run, AgentContentKind.ToolOutput,
                id + "-output", id, output));
            session.Emit(new AgentActivityEvent(session.ProviderId, session.SessionId, DateTimeOffset.UtcNow, run, AgentActivityKind.ToolCall,
                AgentActivityPhase.Completed, id, null, "shell_command", null, null));
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CodeAlta-adaptive-" + Guid.NewGuid().ToString("N"));
        private readonly ConcurrentQueue<AgentSessionUpdateEvent> _cards = new();
        private readonly CancellationTokenSource _closing = new();
        private readonly List<OwnedSessionCommandReceipt> _receipts = [];
        private Task _collector = Task.CompletedTask;

        internal ScriptedProvider Provider { get; } = new("prov-a", "model-a");
        internal CodeAltaHost Host { get; private set; } = null!;
        internal SessionViewJournalStore Journal { get; private set; } = null!;
        internal SessionViewDescriptor Session { get; private set; } = null!;
        internal bool HasGit { get; private set; }
        internal string Project => Path.Combine(_root, "project");

        internal static async Task<Fixture> CreateAsync(string config, bool git = false)
        {
            var f = new Fixture();
            await f.SetupAsync(config, git);
            return f;
        }

        private async Task SetupAsync(string config, bool git)
        {
            var home = Path.Combine(_root, "home");
            var global = Path.Combine(_root, "global");
            var project = Path.Combine(_root, "project");
            var builtin = Path.Combine(_root, "builtin");
            foreach (var directory in new[] { home, global, project, builtin }) Directory.CreateDirectory(directory);
            var builtinGit = Path.Combine(builtin, ".git");
            Directory.CreateDirectory(builtinGit);
            File.WriteAllText(Path.Combine(builtinGit, "config"), "[core]\nignorecase = true\nexcludesfile = fixture.ignore\n");
            File.WriteAllText(Path.Combine(builtinGit, "fixture.ignore"), "");
            if (git)
            {
                try
                {
                    File.WriteAllText(Path.Combine(project, "app.js"), "export const value = 1;\n");
                    Git(project, "init", "-q");
                    Git(project, "add", "-A");
                    Git(project, "commit", "-qm", "Start");
                    HasGit = true;
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }

            File.WriteAllText(Path.Combine(global, "config.toml"), config);
            var catalog = new CatalogOptions { GlobalRoot = global };
            var projectId = (await new ProjectCatalog(catalog).UpsertFromPathAsync(project, CancellationToken.None)).Id;
            Journal = new SessionViewJournalStore(catalog);
            Session = new SessionViewDescriptor
            {
                SessionId = Guid.CreateVersion7().ToString(),
                Kind = SessionViewKind.ProjectSession,
                ProjectRef = projectId,
                ProviderId = Provider.Descriptor.ProviderId.Value,
                ProviderKey = Provider.Descriptor.ProviderId.Value,
                WorkingDirectory = project,
                Title = "Adaptive",
                ModelId = "model-c",
                ReasoningEffort = AgentReasoningEffort.High,
                AgentPromptId = "default",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            await Journal.EnsureHeaderAsync(Session, CancellationToken.None);
            await Journal.CreateSessionStore().UpsertSessionAsync(new AgentSessionSummary
            {
                SessionId = Session.SessionId,
                ProviderId = Provider.Descriptor.ProviderId,
                ProviderKey = Provider.Descriptor.ProviderId.Value,
                WorkingDirectory = Session.WorkingDirectory,
                Title = Session.Title,
                CreatedAt = Session.CreatedAt,
                UpdatedAt = Session.CreatedAt,
                ModelId = Session.ModelId,
                ReasoningEffort = Session.ReasoningEffort,
                AgentPromptId = Session.AgentPromptId,
            }, CancellationToken.None);
            await Journal.AppendStateAsync(Session, new SessionViewLocalState
            {
                ProviderKey = Session.ProviderKey,
                ModelId = Session.ModelId,
                ReasoningEffort = Session.ReasoningEffort,
                AgentPromptId = Session.AgentPromptId,
            }, CancellationToken.None);
            Host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global,
                CurrentProjectPath = project,
                DiscoveryScope = new SessionDiscoveryScope(home, _root),
                BuiltInSkillRoot = builtin,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty,
                StartPlugins = false,
                OwnsLogging = false,
                IsHeadless = true,
                ConfigureModelProviders = registry => registry.RegisterOrReplace(Provider.Descriptor, Provider.CreateRuntime),
            }, CancellationToken.None);
            _collector = CollectAsync();
        }

        private async Task CollectAsync()
        {
            try
            {
                await foreach (var value in Host.RuntimeService.StreamEventsAsync(_closing.Token))
                {
                    if (value is SessionAgentEvent { Event: AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Warning or AgentSessionUpdateKind.Info } update }
                        && update.SessionId == Session.SessionId)
                        _cards.Enqueue(update);
                }
            }
            catch (OperationCanceledException) { }
        }

        internal void Send(string text, string requestId = "first")
        {
            var admission = Host.Commands.AdmitSend(new OwnedTextSendRequest(requestId, Session.SessionId, text));
            Assert.AreEqual(OwnedSessionCommandAdmissionKind.Accepted, admission.Kind);
            _receipts.Add(admission.Receipt!);
        }

        // Waits until the session and what CodeAlta decides at the end of its runs have settled.
        internal async Task SettleAsync()
        {
            await Task.WhenAll(_receipts.Select(static receipt => receipt.Completion)).WaitAsync(Timeout);
            await Host.RuntimeService.WhenSettledAsync(Session.SessionId).WaitAsync(Timeout);
        }

        internal async Task<AgentSessionUpdateEvent> WaitForCardAsync(Func<AgentSessionUpdateEvent, bool> match, string what)
        {
            var deadline = DateTimeOffset.UtcNow + Timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (_cards.FirstOrDefault(match) is { } card) return card;
                await Task.Delay(20);
            }

            Assert.Fail($"No {what} card. Cards: " + string.Join(" | ", _cards.Select(static card => $"{card.Kind}: {card.Message}")));
            return null!;
        }

        internal async Task<SessionViewLocalState> StateAsync()
            => (await Journal.ReadLatestStateAsync(Session.SessionId, Session.CreatedAt, CancellationToken.None))!;

        public async ValueTask DisposeAsync()
        {
            await Task.WhenAll(_receipts.Select(static receipt => receipt.Completion)).WaitAsync(Timeout);
            await _closing.CancelAsync();
            await Host.DisposeAsync();
            await _collector;
            try { Directory.Delete(_root, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed class ScriptedProvider
    {
        private readonly Channel<SendRecord> _sends = Channel.CreateUnbounded<SendRecord>();
        private int _sendCount;

        internal ScriptedProvider(string id, string model)
        {
            Model = model;
            Descriptor = new(new ModelProviderId(id), id.ToUpperInvariant(), null) { DefaultModelId = model };
        }

        internal string Model { get; }
        internal ModelProviderDescriptor Descriptor { get; }
        internal ConcurrentQueue<Action<RunScript>> Scripts { get; } = new();
        internal int SendCount => Volatile.Read(ref _sendCount);

        internal async Task<SendRecord> NextSendAsync() => await _sends.Reader.ReadAsync().AsTask().WaitAsync(Timeout);

        internal IModelProviderRuntime CreateRuntime() => new Runtime(this);

        private sealed class Runtime(ScriptedProvider owner) : IModelProviderSessionRuntime
        {
            public ModelProviderDescriptor Descriptor => owner.Descriptor;
            public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new ModelProviderProbeResult
                {
                    ProviderId = owner.Descriptor.ProviderId,
                    Models =
                    [
                        new AgentModelInfo(owner.Model, SupportedReasoningEfforts: [AgentReasoningEffort.Medium, AgentReasoningEffort.High]),
                        new AgentModelInfo("model-c", SupportedReasoningEfforts: [AgentReasoningEffort.Medium, AgentReasoningEffort.High]),
                    ],
                });
            public IModelProviderTurnExecutor CreateTurnExecutor() => throw new InvalidOperationException("Unexpected turn-executor route.");
            public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken cancellationToken = default)
                => Task.FromResult<IAgentSession>(new Session(owner, options.SessionId!, options));
            public Task<IAgentSession> ResumeSessionAsync(string sessionId, AgentSessionResumeOptions options, CancellationToken cancellationToken = default)
                => Task.FromResult<IAgentSession>(new Session(owner, sessionId, options));
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        internal sealed class Session(ScriptedProvider owner, string sessionId, AgentSessionCreateOptions options) : IAgentSession
        {
            private Action<AgentEvent>? _handler;

            public ModelProviderId ProviderId => owner.Descriptor.ProviderId;
            public string SessionId => sessionId;
            public string? WorkspacePath => options.WorkingDirectory;
            internal string Folder => options.WorkingDirectory!;

            internal void Emit(AgentEvent agentEvent) => _handler?.Invoke(agentEvent);

            public async IAsyncEnumerable<AgentEvent> StreamEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.CompletedTask;
                yield break;
            }

            public IDisposable Subscribe(Action<AgentEvent> handler)
            {
                _handler = handler;
                return new Subscription(() => _handler = null);
            }

            public async Task<AgentRunId> SendAsync(AgentSendOptions send, CancellationToken cancellationToken = default)
            {
                var number = Interlocked.Increment(ref owner._sendCount);
                var run = new AgentRunId($"{owner.Descriptor.ProviderId.Value}-run-{number}");
                var text = string.Concat(send.Input.Items.OfType<AgentInputItem.Text>().Select(static item => item.Value));
                await owner._sends.Writer.WriteAsync(new SendRecord(text, options, send.Provenance), cancellationToken);
                var script = new RunScript(this, run);
                if (owner.Scripts.TryDequeue(out var steps)) steps(script);
                if (!script.Silent)
                    Emit(new AgentContentCompletedEvent(ProviderId, SessionId, DateTimeOffset.UtcNow, run, AgentContentKind.Assistant, "answer-" + number, null, "Done."));
                Emit(new AgentSessionUpdateEvent(ProviderId, SessionId, DateTimeOffset.UtcNow, run, AgentSessionUpdateKind.Idle, null));
                return run;
            }

            public Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No steer.");
            public Task AbortAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task CompactAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("No compaction.");
            public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentEvent>>([]);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class Subscription(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }
}
