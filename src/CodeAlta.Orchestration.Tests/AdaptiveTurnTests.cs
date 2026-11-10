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
/// A session on Adaptive: CodeAlta runs the turns of the user on the model of the level of the session, has the critics
/// of the level review a turn that changed code and the session revise it with what they found, or has it rechecked on a
/// level without critics. The real host, runtime and command owner, with a scripted provider whose runs say what they did.
/// </summary>
[TestClass]
public sealed class AdaptiveTurnTests
{
    private static readonly TimeSpan Timeout = TestDeadlines.Hang;

    // Low has no critic: its changes are rechecked. Medium is reviewed by model-c, High runs on model-c.
    private const string Config = """
        [models.adaptive]
        level = "medium"

        [models.adaptive.low]
        model = "prov-a:model-a@high"

        [models.adaptive.medium]
        model = "prov-a:model-a@medium"
        critics = ["prov-a:model-c@high"]

        [models.adaptive.high]
        model = "prov-a:model-c@high"
        critics = ["prov-a:model-a@high"]
        """;

    private const string WithoutAdaptive = """
        [models.tiers]
        quick = ["prov-a:model-a@high"]
        """;

    [TestMethod]
    public async Task ALevelWithoutCritics_RechecksATurnThatChangedCode_OnceOnTheModelOfTheLevel()
    {
        await using var f = await Fixture.CreateAsync(Config);
        var (status, level) = await f.Host.Commands.SetSessionAdaptiveAsync(f.Session.SessionId, AdaptiveLevel.Low);
        Assert.AreEqual("ok", status);
        Assert.AreEqual("prov-a:model-a@high", level!.Model.Format());
        f.Provider.Scripts.Enqueue(static run => run.Edit("src/app.cs"));
        // The recheck changes code too: a recheck is not rechecked.
        f.Provider.Scripts.Enqueue(static run => run.Edit("src/app.cs"));

        f.Send("add the feature");
        var first = await f.Provider.NextSendAsync();
        Assert.AreEqual("add the feature", first.Text);
        Assert.AreEqual("model-a", first.Options.Model, "The turn runs on the model of the level.");
        var recheck = await f.Provider.NextSendAsync();

        Assert.AreEqual(AgentPromptProvenance.AdaptiveRecheck, recheck.Provenance);
        StringAssert.Contains(recheck.Text, "check your change against each requirement of the request");
        Assert.AreEqual("model-a", recheck.Options.Model, "The recheck runs on the model of the turn.");
        await f.SettleAsync();
        Assert.AreEqual(2, f.Provider.SendCount);
        var state = await f.StateAsync();
        Assert.IsTrue(state.Adaptive);
        Assert.AreEqual("low", state.AdaptiveLevel);
        Assert.IsNull(state.Tier, "A session on Adaptive has no tier: its level names its models.");
    }

    [TestMethod]
    public async Task ATurnThatChangedOnlyDocs_OrNothing_IsNotChecked()
    {
        await using var f = await Fixture.CreateAsync(Config);
        await f.Host.Commands.SetSessionAdaptiveAsync(f.Session.SessionId, AdaptiveLevel.Low);
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
    public async Task TheCriticsOfTheLevel_ReviewTheChange_AndTheSessionRevisesItOnceWithWhatTheyFound()
    {
        await using var f = await Fixture.CreateAsync(Config);
        var asked = new ConcurrentQueue<AdaptiveCriticRequest>();
        f.Host.Commands.Adaptive.Critic = (request, _) =>
        {
            asked.Enqueue(request);
            return Task.FromResult(new AdaptiveCriticAnswer("1. `parse` drops the last field (src/app.cs:42).\n\nVERDICT: REVISE", null));
        };
        Assert.AreEqual("ok", (await f.Host.Commands.SetSessionAdaptiveAsync(f.Session.SessionId)).Status);
        Assert.AreEqual("medium", (await f.StateAsync()).AdaptiveLevel, "Without a level asked, the default level of the settings.");
        f.Provider.Scripts.Enqueue(static run => run.Edit("src/app.cs"));
        f.Provider.Scripts.Enqueue(static run => run.Edit("src/app.cs"));

        f.Send("parse the CSV export");
        Assert.AreEqual("model-a", (await f.Provider.NextSendAsync()).Options.Model);
        var revision = await f.Provider.NextSendAsync();
        await f.SettleAsync();

        var review = asked.Single();
        Assert.AreEqual("prov-a:model-c@high", review.Critic.Format());
        StringAssert.Contains(review.Prompt, "parse the CSV export", "The critic checks the change against the request.");
        StringAssert.Contains(review.Prompt, "src/app.cs");
        StringAssert.Contains(review.Prompt, "VERDICT: APPROVE");
        Assert.AreEqual(AgentPromptProvenance.AdaptiveReview, revision.Provenance);
        StringAssert.Contains(revision.Text, "`parse` drops the last field");
        StringAssert.Contains(revision.Text, "prov-a:model-c@high");
        Assert.DoesNotContain("VERDICT", revision.Text);
        Assert.AreEqual("model-a", revision.Options.Model, "The revision runs on the model of the turn, which keeps its cache.");
        Assert.AreEqual(2, f.Provider.SendCount, "A revision is not reviewed again.");
    }

    [TestMethod]
    public async Task AnApprovedChange_IsNotRevised_AndACardSaysWhoApprovedIt()
    {
        await using var f = await Fixture.CreateAsync(Config);
        f.Host.Commands.Adaptive.Critic = static (_, _) => Task.FromResult(new AdaptiveCriticAnswer("All requirements are met.\n**VERDICT: APPROVE**", null));
        await f.Host.Commands.SetSessionAdaptiveAsync(f.Session.SessionId, AdaptiveLevel.Medium);
        f.Provider.Scripts.Enqueue(static run => run.Edit("src/app.cs"));

        f.Send("add the feature");
        await f.Provider.NextSendAsync();
        await f.SettleAsync();

        Assert.AreEqual(1, f.Provider.SendCount);
        var card = await f.WaitForCardAsync(static card => card.Kind == AgentSessionUpdateKind.Info, "approval");
        StringAssert.Contains(card.Message, "approved by prov-a:model-c@high");
    }

    [TestMethod]
    public async Task ACriticThatGivesNoVerdict_LeavesTheChangeToARecheck_AndACardSaysSo()
    {
        await using var f = await Fixture.CreateAsync(Config);
        f.Host.Commands.Adaptive.Critic = static (_, _) => Task.FromResult(new AdaptiveCriticAnswer(null, "the provider is not ready"));
        await f.Host.Commands.SetSessionAdaptiveAsync(f.Session.SessionId, AdaptiveLevel.Medium);
        f.Provider.Scripts.Enqueue(static run => run.Edit("src/app.cs"));

        f.Send("add the feature");
        await f.Provider.NextSendAsync();
        var recheck = await f.Provider.NextSendAsync();
        await f.SettleAsync();

        Assert.AreEqual(AgentPromptProvenance.AdaptiveRecheck, recheck.Provenance);
        var card = await f.WaitForCardAsync(static card => card.Kind == AgentSessionUpdateKind.Warning, "review");
        StringAssert.Contains(card.Message, "the provider is not ready");
    }

    [TestMethod]
    public async Task TheCriticOfTheHost_OpensAReadOnlyChildSession_OnItsOwnModel_ThoughTheSessionStartsNoSessionOfItsOwn()
    {
        await using var f = await Fixture.CreateAsync(Config);
        // A session without sub-sessions (altacli --no-sub-sessions) is still reviewed: the critic is the host's, not the agent's.
        Assert.AreEqual("ok", (await f.Host.Commands.SetSessionOptionsAsync(f.Session.SessionId, new AgentSessionOptions { SubSessions = false })).Status);
        await f.Host.Commands.SetSessionAdaptiveAsync(f.Session.SessionId, AdaptiveLevel.Medium);
        f.Provider.Scripts.Enqueue(static run => run.Edit("src/app.cs"));
        f.Provider.Scripts.Enqueue(static run => run.Answer = "The change does what the request asks.\nVERDICT: APPROVE");

        f.Send("add the feature");
        await f.Provider.NextSendAsync();
        var review = await f.Provider.NextSendAsync();
        await f.SettleAsync();

        Assert.AreEqual(AgentPromptProvenance.AdaptiveReview, review.Provenance);
        Assert.AreEqual("model-c", review.Options.Model, "The critic runs on its own model.");
        Assert.AreNotEqual(f.Session.SessionId, review.Options.SessionId, "The critic has a session of its own.");
        StringAssert.Contains(review.Text, "You are read-only");
        var disabled = review.Options.DisabledToolPatterns?.ToArray() ?? [];
        foreach (var tool in AdaptiveCritics.ReadOnlyTools) CollectionAssert.Contains(disabled, tool, $"A critic does without {tool}.");
        // How the answer of a critic decides the turn is told by the tests that give the coordinator a critic of their own.
    }

    [TestMethod]
    public async Task ASessionThatIsNotOnAdaptive_IsNeitherRecheckedNorReviewed()
    {
        await using var f = await Fixture.CreateAsync(Config);
        f.Provider.Scripts.Enqueue(static run => { run.Edit("src/app.cs"); run.Test("npm test", "exit_code: 2"); });

        f.Send("add the feature");
        await f.Provider.NextSendAsync();
        await f.SettleAsync();

        Assert.AreEqual(1, f.Provider.SendCount);
    }

    [TestMethod]
    public async Task ChoosingALevel_RunsTheNextTurnOnItsModel_AndChoosingATierByHand_TakesTheSessionOffAdaptive()
    {
        await using var f = await Fixture.CreateAsync(Config);
        Assert.AreEqual("prov-a:model-c@high", (await f.Host.Commands.SetSessionAdaptiveAsync(f.Session.SessionId, AdaptiveLevel.High)).Level!.Model.Format());
        // Max is not set: the nearest level.
        Assert.AreEqual(AdaptiveLevel.High, (await f.Host.Commands.SetSessionAdaptiveAsync(f.Session.SessionId, AdaptiveLevel.Max)).Level!.Level);
        f.Send("explain it");
        Assert.AreEqual("model-c", (await f.Provider.NextSendAsync()).Options.Model);
        await f.SettleAsync();
        Assert.IsTrue((await f.Host.Commands.GetSelectionChoicesAsync(f.Session.SessionId))!.Adaptive);

        Assert.AreEqual("ok", await f.Host.Commands.SetSessionTierAsync(f.Session.SessionId, ModelTier.Complex));

        var state = await f.StateAsync();
        Assert.AreEqual("complex", state.Tier);
        Assert.IsNull(state.Adaptive);
        Assert.IsNull(state.AdaptiveLevel);
        Assert.IsFalse((await f.Host.Commands.GetSelectionChoicesAsync(f.Session.SessionId))!.Adaptive);
    }

    [TestMethod]
    public async Task Adaptive_IsRefused_UntilALevelIsSet_AndTheKeysOfItsFirstVersionSetNone()
    {
        await using (var none = await Fixture.CreateAsync(WithoutAdaptive))
        {
            Assert.AreEqual(("not_ready", (AdaptiveLevelSettings?)null), await none.Host.Commands.SetSessionAdaptiveAsync(none.Session.SessionId));
            Assert.IsNull((await none.StateAsync()).Adaptive);
        }

        await using var older = await Fixture.CreateAsync(WithoutAdaptive + "\n\n[models.adaptive]\ncode = \"quick\"\nescalate = \"complex\"\n");
        Assert.AreEqual("not_ready", (await older.Host.Commands.SetSessionAdaptiveAsync(older.Session.SessionId)).Status);
    }

    [TestMethod]
    public async Task InAGitFolder_ATurnThatChangedCode_IsToldByGit_WithItsDiff_AndOneThatCommittedItsWork_IsNot()
    {
        await using var f = await Fixture.CreateAsync(Config, git: true);
        if (!f.HasGit) Assert.Inconclusive("git is not installed.");
        var asked = new ConcurrentQueue<AdaptiveCriticRequest>();
        f.Host.Commands.Adaptive.Critic = (request, _) => { asked.Enqueue(request); return Task.FromResult(new AdaptiveCriticAnswer("VERDICT: APPROVE", null)); };
        await f.Host.Commands.SetSessionAdaptiveAsync(f.Session.SessionId, AdaptiveLevel.Medium);
        // The tools say nothing of the files: git tells the change, and gives its diff to the critic.
        f.Provider.Scripts.Enqueue(run => File.WriteAllText(Path.Combine(run.Folder, "app.js"), "export const value = 2;\n"));

        f.Send("change the value");
        await f.Provider.NextSendAsync();
        await f.SettleAsync();
        StringAssert.Contains(asked.Single().Prompt, "+export const value = 2;");

        // A turn that commits what is there changes nothing in the files.
        f.Provider.Scripts.Enqueue(run => Git(run.Folder, "commit", "-qam", "Change the value"));
        f.Send("commit it", "second");
        await f.Provider.NextSendAsync();
        await f.SettleAsync();
        Assert.AreEqual(1, asked.Count);
        Assert.AreEqual(2, f.Provider.SendCount);
    }

    [TestMethod]
    public async Task AHeadlessSessionOnAdaptive_StartsOnTheModelOfItsLevel_AndIsOnAdaptive()
    {
        await using var f = await Fixture.CreateAsync(Config);
        await using var driver = new HeadlessSessionDriver(f.Host);
        Assert.AreEqual(HeadlessOpenStatus.InvalidRequest,
            (await driver.OpenAsync(new HeadlessSessionRequest { ProjectFolder = f.Project, Adaptive = true, Tier = ModelTier.Complex })).Status);
        Assert.AreEqual(HeadlessOpenStatus.InvalidRequest,
            (await driver.OpenAsync(new HeadlessSessionRequest { ProjectFolder = f.Project, AdaptiveLevel = AdaptiveLevel.High })).Status);
        var opened = await driver.OpenAsync(new HeadlessSessionRequest { ProjectFolder = f.Project, Adaptive = true, AdaptiveLevel = AdaptiveLevel.High });
        Assert.AreEqual(HeadlessOpenStatus.Created, opened.Status, opened.Message);
        Assert.AreEqual("model-c", opened.Session!.ModelId, "A session on Adaptive starts with the model of its level.");
        Assert.AreEqual(((ModelTier?)null, true), await f.Host.RuntimeService.GetSessionModelChoiceAsync(opened.Session.SessionId));
        Assert.AreEqual(AdaptiveLevel.High, await f.Host.RuntimeService.GetSessionAdaptiveLevelAsync(opened.Session.SessionId));
    }

    [TestMethod]
    public async Task AHeadlessSession_IsNotPutOnAdaptive_WhileItIsNotSetUp()
    {
        await using var f = await Fixture.CreateAsync(WithoutAdaptive);
        await using var driver = new HeadlessSessionDriver(f.Host);
        var opened = await driver.OpenAsync(new HeadlessSessionRequest { ProjectFolder = f.Project, Adaptive = true });
        Assert.AreEqual(HeadlessOpenStatus.ProviderNotReady, opened.Status);
        StringAssert.Contains(opened.Message, "Adaptive is not set up");
    }

    [TestMethod]
    public void AVerdict_IsTheLastVerdictLine_AndTheFindingsAreWhatSurroundsIt()
    {
        Assert.AreEqual(("REVISE", "1. A bug."), AdaptiveTurnCoordinator.ParseVerdict("1. A bug.\n\nVERDICT: REVISE"));
        Assert.AreEqual("APPROVE", AdaptiveTurnCoordinator.ParseVerdict("VERDICT: REVISE\nwas considered, then:\n**Verdict: approve**").Verdict);
        Assert.AreEqual(((string?)null, "Looks fine."), AdaptiveTurnCoordinator.ParseVerdict("Looks fine."));
        Assert.AreEqual(((string?)null, string.Empty), AdaptiveTurnCoordinator.ParseVerdict(null));
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

        // The answer of the run; "Done." unless a script says otherwise.
        public string Answer { get; set; } = "Done.";

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
            await Task.WhenAll(_receipts.Select(static receipt => receipt.Completion)).WithinAsync(Timeout);
            await Host.RuntimeService.WhenSettledAsync(Session.SessionId).WithinAsync(Timeout);
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
            await Task.WhenAll(_receipts.Select(static receipt => receipt.Completion)).WithinAsync(Timeout);
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

        internal async Task<SendRecord> NextSendAsync() => await _sends.Reader.ReadAsync().AsTask().WithinAsync(Timeout);

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
                    Emit(new AgentContentCompletedEvent(ProviderId, SessionId, DateTimeOffset.UtcNow, run, AgentContentKind.Assistant, "answer-" + number, null, script.Answer));
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
