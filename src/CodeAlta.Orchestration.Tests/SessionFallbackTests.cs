using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Catalog.ModelTiers;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// A session whose run fails on its provider goes on with the next target of its tier: the real host, runtime and
/// command owner, with two scripted providers and a parent session.
/// </summary>
[TestClass]
public sealed class SessionFallbackTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private const string Tiers = """
        [models.tiers]
        standard = ["prov-a:model-a@high,medium", "prov-b:model-b@high"]

        [models.fallback]
        enabled = true
        """;

    [TestMethod]
    public async Task ExhaustedQuota_GoesOnWithTheNextProvider_AndTheParentIsNotToldOfTheReplacedError()
    {
        await using var f = await Fixture.CreateAsync(Tiers);
        f.A.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted, DateTimeOffset.UtcNow.AddHours(2))));

        f.Send("do the work");
        var first = await f.NextSendAsync(f.A);
        Assert.AreEqual("do the work", first.Text);
        var continuation = await f.NextSendAsync(f.B);

        StringAssert.Contains(continuation.Text, "[CodeAlta fallback]");
        // A run that stopped before any answer is answered, and nothing more; a tool call that was running is checked.
        StringAssert.Contains(continuation.Text, "If it stopped before any answer, answer the prompt before this message, and do nothing more.");
        StringAssert.Contains(continuation.Text, "check its result");
        Assert.AreEqual("model-b", continuation.Options.Model);
        Assert.AreEqual(AgentReasoningEffort.High, continuation.Options.ReasoningEffort);
        var card = await f.WaitForCardAsync(AgentSessionUpdateKind.ProviderFallback);
        StringAssert.Contains(card.Message, "quota reached until");
        StringAssert.Contains(card.Message, "continuing on PROV-B model-b high");
        var state = await f.ChildStateAsync();
        Assert.AreEqual("prov-b", state.ProviderKey);
        Assert.AreEqual("standard", state.Tier, "A model found in a tier makes the session run on that tier.");

        // The run that continues answers: the parent hears the answer, never the error that was replaced.
        var told = await f.WaitForParentAsync(static prompts => prompts.Count > 0);
        Assert.IsFalse(told.Any(static prompt => prompt.Prompt.Contains("failed or was cancelled", StringComparison.Ordinal)), string.Join("\n", told.Select(static p => p.Prompt)));
        Assert.AreEqual(1, f.Host.Commands.LastFallbacks.Count);
    }

    [TestMethod]
    public async Task SessionOfASpace_FallsBackAlongTheTiersOfItsSpace()
    {
        // The global tier has A only; the space lists B after it.
        await using var f = await Fixture.CreateAsync("""
            [models.tiers]
            standard = ["prov-a:model-a@high"]

            [models.fallback]
            enabled = true
            """, childSpace: async spaces =>
        {
            var created = (await spaces.CreateAsync("Business")).Space!;
            var tiers = ModelSettings.Read(new CodeAltaModelSettingsDocument
            {
                Tiers = new CodeAltaModelTiersDocument { Standard = ["prov-a:model-a@high", "prov-b:model-b@high"] },
            }).Tiers;
            Assert.AreEqual(SpaceChangeStatus.Ok, (await spaces.UpdateSettingsAsync(created.Id, _ => new SpaceSettings(new SpaceNewSessions(null, SpaceModelTiers.From(tiers))))).Status);
            return created.Id;
        });
        var scope = await f.Host.RuntimeService.LoadModelTierScopeAsync(f.Child.SpaceId, f.Child.ProjectRef);
        Assert.IsTrue(scope.FromSpace, $"space {f.Child.SpaceId} {scope.SpaceId} {scope.Tiers.Standard.Count}");
        f.A.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted)));

        f.Send("do the work");
        await f.NextSendAsync(f.A);
        var continuation = await f.NextSendAsync(f.B);

        StringAssert.Contains(continuation.Text, "[CodeAlta fallback]");
        Assert.AreEqual("model-b", continuation.Options.Model);
    }

    [TestMethod]
    public async Task SessionOfASpace_NeverFallsBackToAProviderTheSpaceDoesNotHave()
    {
        // B is in the tier, but the space of the session has not got it: the failure goes through.
        await using var f = await Fixture.CreateAsync(Tiers, childSpace: async spaces =>
        {
            var created = (await spaces.CreateAsync("Business")).Space!;
            var tiers = ModelSettings.Read(new CodeAltaModelSettingsDocument
            {
                Tiers = new CodeAltaModelTiersDocument { Standard = ["prov-a:model-a@high", "prov-b:model-b@high"] },
            }).Tiers;
            await spaces.UpdateSettingsAsync(created.Id, _ => new SpaceSettings(new SpaceNewSessions(null, SpaceModelTiers.From(tiers))));
            return created.Id;
        }, providerSpaces: "[providers.prov-b]\ntype = \"openai-chat\"\nenabled = false\nspaces = [\"personal\"]\n");
        var scope = await f.Host.RuntimeService.LoadModelTierScopeAsync(f.Child.SpaceId, f.Child.ProjectRef);
        Assert.IsTrue(scope.FromSpace);
        Assert.IsTrue(scope.IsProviderAvailable("prov-a"));
        Assert.IsFalse(scope.IsProviderAvailable("prov-b"));
        f.A.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted)));

        f.Send("do the work");
        await f.NextSendAsync(f.A);

        var card = await f.WaitForCardAsync(AgentSessionUpdateKind.Warning);
        StringAssert.Contains(card.Message, "no other model of the standard tier can take over");
        Assert.AreEqual(0, f.B.SendCount);
    }

    [TestMethod]
    public async Task UnavailableProvider_TriesALowerReasoningOfTheSameModelFirst()
    {
        await using var f = await Fixture.CreateAsync(Tiers);
        f.A.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.Unavailable)));

        f.Send("do the work");
        await f.NextSendAsync(f.A);
        var continuation = await f.NextSendAsync(f.A);

        StringAssert.Contains(continuation.Text, "[CodeAlta fallback]");
        Assert.AreEqual("model-a", continuation.Options.Model);
        Assert.AreEqual(AgentReasoningEffort.Medium, continuation.Options.ReasoningEffort);
        Assert.AreEqual(0, f.B.SendCount);
    }

    [TestMethod]
    public async Task AuthFailure_DoesNotSwitch_AndTheParentIsTold()
    {
        await using var f = await Fixture.CreateAsync(Tiers);
        f.A.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.Auth)));

        f.Send("do the work");
        await f.NextSendAsync(f.A);

        var told = await f.WaitForParentAsync(static prompts => prompts.Any(static prompt => prompt.Prompt.Contains("failed or was cancelled", StringComparison.Ordinal)));
        Assert.AreEqual(1, told.Count);
        Assert.AreEqual(0, f.B.SendCount);
        Assert.AreEqual("prov-a", (await f.ChildStateAsync()).ProviderKey);
        Assert.AreEqual(0, f.Host.Commands.LastFallbacks.Count);
    }

    [TestMethod]
    public async Task FallbackThatIsOff_LetsAnExhaustedQuotaThrough()
    {
        await using var f = await Fixture.CreateAsync(Tiers.Replace("enabled = true", "enabled = false", StringComparison.Ordinal));
        f.A.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted)));

        f.Send("do the work");
        await f.NextSendAsync(f.A);

        await f.WaitForParentAsync(static prompts => prompts.Any(static prompt => prompt.Prompt.Contains("failed or was cancelled", StringComparison.Ordinal)));
        Assert.AreEqual(0, f.B.SendCount);
    }

    [TestMethod]
    public async Task ExhaustedChain_LetsTheFailureThroughWithACardThatSaysWhatToDo()
    {
        await using var f = await Fixture.CreateAsync(Tiers);
        f.A.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted)));
        f.B.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted)));

        f.Send("do the work");
        await f.NextSendAsync(f.A);
        await f.NextSendAsync(f.B);

        var card = await f.WaitForCardAsync(AgentSessionUpdateKind.Warning);
        StringAssert.Contains(card.Message, "no other model of the standard tier can take over");
        var told = await f.WaitForParentAsync(static prompts => prompts.Any(static prompt => prompt.Prompt.Contains("failed or was cancelled", StringComparison.Ordinal)));
        Assert.AreEqual(1, told.Count(static prompt => prompt.Prompt.Contains("failed or was cancelled", StringComparison.Ordinal)),
            "Only the last failure is told: the first one was replaced.");
    }

    [TestMethod]
    public async Task ProviderOfAnotherSpace_IsNotATargetOfTheFallback()
    {
        // The session is in the default space; the next target of its tier is a provider of Business alone.
        await using var f = await Fixture.CreateAsync(Tiers + """

            [providers.prov-b]
            type = "openai-chat"
            api_url = "http://127.0.0.1:9/v1"
            api_key = "fixture"
            spaces = ["business"]
            """);
        f.A.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted)));

        f.Send("do the work");
        await f.NextSendAsync(f.A);

        var card = await f.WaitForCardAsync(AgentSessionUpdateKind.Warning);
        StringAssert.Contains(card.Message, "no other model of the standard tier can take over");
        Assert.AreEqual(0, f.B.SendCount);
        Assert.AreEqual("prov-a", (await f.ChildStateAsync()).ProviderKey);
    }

    [TestMethod]
    public async Task Switch_KeepsOrNarrowsThePermissionMode_NeverWidensIt()
    {
        await using var f = await Fixture.CreateAsync(Tiers, childPermissionMode: SessionPermissionModes.AcceptEdits,
            bModes: [SessionPermissionModes.Ask, SessionPermissionModes.Bypass]);
        f.A.Outcomes.Enqueue(Outcome.Fail(new AgentFailureInfo(AgentFailureKind.QuotaExhausted)));

        f.Send("do the work");
        await f.NextSendAsync(f.A);
        var continuation = await f.NextSendAsync(f.B);

        // B has no "accept edits": the session gets B's narrower mode, not "bypass permissions".
        Assert.AreEqual(SessionPermissionModes.Ask, continuation.Options.PermissionMode);
        Assert.AreEqual(SessionPermissionModes.Ask, (await f.ChildStateAsync()).PermissionMode);
    }

    [TestMethod]
    public async Task SessionTier_IsRecorded_AndOutlivesAProviderSwitch()
    {
        await using var f = await Fixture.CreateAsync(Tiers);
        Assert.IsNull(await f.Host.RuntimeService.GetSessionTierAsync(f.Child.SessionId));
        Assert.IsTrue(await f.Host.RuntimeService.SetSessionTierAsync(f.Child.SessionId, ModelTier.Quick));
        Assert.AreEqual(ModelTier.Quick, await f.Host.RuntimeService.GetSessionTierAsync(f.Child.SessionId));

        var context = await f.Host.Commands.GetProviderSelectionAsync(f.Child.SessionId);
        Assert.IsNotNull(context);
        Assert.AreEqual("ok", await f.Host.Commands.SelectProviderAsync(context, "prov-b"));
        Assert.AreEqual(ModelTier.Quick, await f.Host.RuntimeService.GetSessionTierAsync(f.Child.SessionId));
        Assert.IsNull((await f.ChildStateAsync()).PermissionMode, "A switch by hand still takes the mode of the provider.");

        Assert.IsTrue(await f.Host.RuntimeService.SetSessionTierAsync(f.Child.SessionId, null));
        Assert.IsNull(await f.Host.RuntimeService.GetSessionTierAsync(f.Child.SessionId));
        Assert.IsFalse(await f.Host.RuntimeService.SetSessionTierAsync("missing-session", ModelTier.Quick));
    }

    [TestMethod]
    public void ShortRateLimit_IsWaitedOut_NotLeft()
    {
        var on = new ModelFallbackSettings(true, OnQuota: true, OnUnavailable: true);
        Assert.IsFalse(SessionFallbackCoordinator.IsTrigger(on, new AgentFailureInfo(AgentFailureKind.RateLimited, RetryAfterSeconds: 20)));
        Assert.IsTrue(SessionFallbackCoordinator.IsTrigger(on, new AgentFailureInfo(AgentFailureKind.RateLimited, RetryAfterSeconds: 600)));
        Assert.IsTrue(SessionFallbackCoordinator.IsTrigger(on, new AgentFailureInfo(AgentFailureKind.RateLimited)));
        Assert.IsFalse(SessionFallbackCoordinator.IsTrigger(on, new AgentFailureInfo(AgentFailureKind.Auth)));
        Assert.IsFalse(SessionFallbackCoordinator.IsTrigger(on, new AgentFailureInfo(AgentFailureKind.Other)));
        Assert.IsFalse(SessionFallbackCoordinator.IsTrigger(on with { OnUnavailable = false }, new AgentFailureInfo(AgentFailureKind.Unavailable)));
        Assert.IsFalse(SessionFallbackCoordinator.IsTrigger(on with { OnQuota = false }, new AgentFailureInfo(AgentFailureKind.QuotaExhausted)));
    }

    [TestMethod]
    public void PermissionRank_OrdersTheModesFromTheNarrowest()
    {
        string?[] modes = ["dontAsk", "default", "acceptEdits", "auto", "bypassPermissions"];
        for (var index = 1; index < modes.Length; index++)
            Assert.IsTrue(SessionFallbackCoordinator.PermissionRank(modes[index - 1]) < SessionFallbackCoordinator.PermissionRank(modes[index]), modes[index]);
        Assert.AreEqual(SessionFallbackCoordinator.PermissionRank("default"), SessionFallbackCoordinator.PermissionRank(null));
    }

    private sealed record Outcome(AgentFailureInfo? Failure)
    {
        internal static Outcome Fail(AgentFailureInfo failure) => new(failure);
    }

    private sealed record SendRecord(string Text, AgentSessionCreateOptions Options);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CodeAlta-fallback-" + Guid.NewGuid().ToString("N"));
        private readonly ConcurrentQueue<AgentSessionUpdateEvent> _cards = new();
        private readonly CancellationTokenSource _closing = new();
        private Task _collector = Task.CompletedTask;
        private readonly List<OwnedSessionCommandReceipt> _receipts = [];

        internal ScriptedProvider A { get; } = new("prov-a", "model-a", [SessionPermissionModes.Ask, SessionPermissionModes.AcceptEdits, SessionPermissionModes.Bypass]);
        internal ScriptedProvider B { get; private set; } = null!;
        internal CodeAltaHost Host { get; private set; } = null!;
        internal SessionViewJournalStore Journal { get; private set; } = null!;
        internal SessionViewDescriptor Parent { get; private set; } = null!;
        internal SessionViewDescriptor Child { get; private set; } = null!;

        internal static async Task<Fixture> CreateAsync(string config, string? childPermissionMode = null, IReadOnlyList<string>? bModes = null,
            Func<SpaceCatalog, Task<string>>? childSpace = null, string? providerSpaces = null)
        {
            var f = new Fixture();
            f.B = new ScriptedProvider("prov-b", "model-b", bModes ?? [SessionPermissionModes.Ask, SessionPermissionModes.AcceptEdits, SessionPermissionModes.Bypass]);
            await f.SetupAsync(providerSpaces is null ? config : config + "\n" + providerSpaces, childPermissionMode, childSpace);
            return f;
        }

        private async Task SetupAsync(string config, string? childPermissionMode, Func<SpaceCatalog, Task<string>>? childSpace)
        {
            var home = Path.Combine(_root, "home");
            var global = Path.Combine(_root, "global");
            var project = Path.Combine(_root, "project");
            var builtin = Path.Combine(_root, "builtin");
            foreach (var directory in new[] { home, global, project, builtin }) Directory.CreateDirectory(directory);
            var git = Path.Combine(builtin, ".git");
            Directory.CreateDirectory(git);
            File.WriteAllText(Path.Combine(git, "config"), "[core]\nignorecase = true\nexcludesfile = fixture.ignore\n");
            File.WriteAllText(Path.Combine(git, "fixture.ignore"), "");
            File.WriteAllText(Path.Combine(global, "config.toml"), config);
            var catalog = new CatalogOptions { GlobalRoot = global };
            var projectId = (await new ProjectCatalog(catalog).UpsertFromPathAsync(project, CancellationToken.None)).Id;
            Journal = new SessionViewJournalStore(catalog);
            Parent = await PersistAsync(projectId, project, "Parent", parentSessionId: null, permissionMode: null);
            // The space of the child, set up before the host reads it.
            var spaceId = childSpace is null ? null : await childSpace(new SpaceCatalog(new ProjectCatalog(catalog)));
            Child = await PersistAsync(projectId, project, "Child", Parent.SessionId, childPermissionMode, spaceId);
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
                ConfigureModelProviders = registry =>
                {
                    registry.RegisterOrReplace(A.Descriptor, A.CreateRuntime);
                    registry.RegisterOrReplace(B.Descriptor, B.CreateRuntime);
                },
            }, CancellationToken.None);
            _collector = CollectAsync();
        }

        private async Task<SessionViewDescriptor> PersistAsync(string projectId, string project, string title, string? parentSessionId, string? permissionMode,
            string? spaceId = null)
        {
            var session = new SessionViewDescriptor
            {
                SessionId = Guid.CreateVersion7().ToString(),
                Kind = SessionViewKind.ProjectSession,
                ProjectRef = projectId,
                ParentSessionId = parentSessionId,
                ProviderId = A.Descriptor.ProviderId.Value,
                ProviderKey = A.Descriptor.ProviderId.Value,
                WorkingDirectory = project,
                Title = title,
                ModelId = "model-a",
                ReasoningEffort = AgentReasoningEffort.High,
                AgentPromptId = "default",
                CreatedAt = DateTimeOffset.UtcNow,
                SpaceId = spaceId,
            };
            await Journal.EnsureHeaderAsync(session, CancellationToken.None);
            await Journal.CreateSessionStore().UpsertSessionAsync(new AgentSessionSummary
            {
                SessionId = session.SessionId,
                ProviderId = A.Descriptor.ProviderId,
                ProviderKey = A.Descriptor.ProviderId.Value,
                WorkingDirectory = session.WorkingDirectory,
                Title = session.Title,
                CreatedAt = session.CreatedAt,
                UpdatedAt = session.CreatedAt,
                ModelId = session.ModelId,
                ReasoningEffort = session.ReasoningEffort,
                AgentPromptId = session.AgentPromptId,
            }, CancellationToken.None);
            // What a session of a provider writes once it ran: a switch of provider needs it.
            await Journal.CreateSessionStore().UpsertStateAsync(new AgentSessionState
            {
                SessionId = session.SessionId,
                ProviderKey = A.Descriptor.ProviderId.Value,
                UpdatedAt = session.CreatedAt,
            }, CancellationToken.None);
            await Journal.AppendStateAsync(session, new SessionViewLocalState
            {
                ProviderKey = session.ProviderKey,
                ModelId = session.ModelId,
                ReasoningEffort = session.ReasoningEffort,
                AgentPromptId = session.AgentPromptId,
                PermissionMode = permissionMode,
                ParentSessionId = parentSessionId,
            }, CancellationToken.None);
            return session;
        }

        private async Task CollectAsync()
        {
            try
            {
                await foreach (var value in Host.RuntimeService.StreamEventsAsync(_closing.Token))
                {
                    if (value is SessionAgentEvent { Event: AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.ProviderFallback or AgentSessionUpdateKind.Warning } update }
                        && update.SessionId == Child.SessionId)
                        _cards.Enqueue(update);
                }
            }
            catch (OperationCanceledException) { }
        }

        internal void Send(string text)
        {
            var admission = Host.Commands.AdmitSend(new OwnedTextSendRequest("first", Child.SessionId, text));
            Assert.AreEqual(OwnedSessionCommandAdmissionKind.Accepted, admission.Kind);
            _receipts.Add(admission.Receipt!);
        }

        internal async Task<AgentSessionUpdateEvent> WaitForCardAsync(AgentSessionUpdateKind kind)
        {
            var deadline = DateTimeOffset.UtcNow + Timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (_cards.FirstOrDefault(card => card.Kind == kind) is { } card) return card;
                await Task.Delay(20);
            }

            Assert.Fail($"No {kind} card.");
            return null!;
        }

        internal async Task<SendRecord> NextSendAsync(ScriptedProvider provider)
        {
            try { return await provider.NextSendAsync(); }
            catch (TimeoutException)
            {
                Assert.Fail("No send on " + provider.Descriptor.ProviderId.Value + ". Cards: " + string.Join(" | ", _cards.Select(static card => $"{card.Kind}: {card.Message}")));
                throw;
            }
        }

        internal async Task<SessionViewLocalState> ChildStateAsync()
            => (await Journal.ReadLatestStateAsync(Child.SessionId, Child.CreatedAt, CancellationToken.None))!;

        internal async Task<IReadOnlyList<SessionViewQueuedPrompt>> WaitForParentAsync(Func<IReadOnlyList<SessionViewQueuedPrompt>, bool> condition)
        {
            var deadline = DateTimeOffset.UtcNow + Timeout;
            while (true)
            {
                var prompts = (await Journal.ReadLatestStateAsync(Parent.SessionId, Parent.CreatedAt, CancellationToken.None))?.QueuedPrompts
                    .Where(static prompt => prompt.Kind == "parent-notify").ToArray() ?? [];
                if (condition(prompts)) return prompts;
                if (DateTimeOffset.UtcNow >= deadline) Assert.Fail("The parent was not told: " + string.Join("\n", prompts.Select(static p => p.Prompt)));
                await Task.Delay(20);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Task.WhenAll(_receipts.Select(static receipt => receipt.Completion)).WaitAsync(Timeout);
            await _closing.CancelAsync();
            await Host.DisposeAsync();
            await _collector;
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }
    }

    private sealed class ScriptedProvider
    {
        private readonly Channel<SendRecord> _sends = Channel.CreateUnbounded<SendRecord>();
        private int _sendCount;

        internal ScriptedProvider(string id, string model, IReadOnlyList<string> permissionModes)
        {
            Model = model;
            Descriptor = new(new ModelProviderId(id), id.ToUpperInvariant()) { DefaultModelId = model, PermissionModes = permissionModes };
        }

        internal string Model { get; }
        internal ModelProviderDescriptor Descriptor { get; }
        internal ConcurrentQueue<Outcome> Outcomes { get; } = new();
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
                    Models = [new AgentModelInfo(owner.Model, SupportedReasoningEfforts: [AgentReasoningEffort.Medium, AgentReasoningEffort.High])],
                });
            public IModelProviderTurnExecutor CreateTurnExecutor() => throw new InvalidOperationException("Unexpected turn-executor route.");
            public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken cancellationToken = default)
                => Task.FromResult<IAgentSession>(new Session(owner, options.SessionId!, options));
            public Task<IAgentSession> ResumeSessionAsync(string sessionId, AgentSessionResumeOptions options, CancellationToken cancellationToken = default)
                => Task.FromResult<IAgentSession>(new Session(owner, sessionId, options));
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class Session(ScriptedProvider owner, string sessionId, AgentSessionCreateOptions options) : IAgentSession
        {
            private Action<AgentEvent>? _handler;

            public ModelProviderId ProviderId => owner.Descriptor.ProviderId;
            public string SessionId => sessionId;
            public string? WorkspacePath => options.WorkingDirectory;

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
                await owner._sends.Writer.WriteAsync(new SendRecord(text, options), cancellationToken);
                if (owner.Outcomes.TryDequeue(out var outcome) && outcome.Failure is { } failure)
                {
                    _handler?.Invoke(new AgentErrorEvent(ProviderId, SessionId, DateTimeOffset.UtcNow, $"{failure.Kind} on {ProviderId.Value}", exception: null, runId: run)
                    {
                        Failure = failure,
                    });
                    throw new InvalidOperationException($"{failure.Kind} on {ProviderId.Value}");
                }

                _handler?.Invoke(new AgentContentCompletedEvent(ProviderId, SessionId, DateTimeOffset.UtcNow, run, AgentContentKind.Assistant, "answer-" + number, null, "Done."));
                _handler?.Invoke(new AgentSessionUpdateEvent(ProviderId, SessionId, DateTimeOffset.UtcNow, run, AgentSessionUpdateKind.Idle, null));
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
