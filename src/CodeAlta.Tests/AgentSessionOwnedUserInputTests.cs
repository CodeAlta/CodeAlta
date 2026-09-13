using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Compaction;
using CodeAlta.Agent.Runtime.Tools;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Tests;

/// <summary>Scripted actual Agent sends; requires separate parent transitive acquisition admission.</summary>
[TestClass]
public sealed class AgentSessionOwnedUserInputTests
{
    [TestMethod]
    public void AskComposition_PreservesInputCallbackAndSessionFallbackContract()
    {
        var owner = new OwnedSessionAskService(false, (_, _) => throw new AssertFailedException("No send."));
        var execution = owner.CreateExecution(Guid.NewGuid(), "session", default);
        AgentUserInputRequestHandler callback = static (_, _) => Task.FromResult(new AgentUserInputResponse(new Dictionary<string, string>()));
        var options = new AgentSendOptions { Input = AgentInput.Text("inert"), OnUserInputRequest = callback, EnableUserInputTool = true };
        try
        {
            Assert.AreSame(callback, execution.Compose(options).OnUserInputRequest);
            Assert.IsTrue(execution.Compose(options).EnableUserInputTool);
            var defaults = new AgentSendOptions { Input = options.Input };
            Assert.IsNull(defaults.OnUserInputRequest); Assert.IsFalse(defaults.EnableUserInputTool);
            Assert.IsFalse(execution.Compose(defaults).EnableUserInputTool);
        }
        finally { execution.Close(); }
    }

    [TestMethod]
    public void ActualFactory_ActivationRequiresFlagAndCallbackAndHonorsOptOut()
    {
        // Supplied client is fixture-owned; no handler or HTTP request is invoked in this matrix.
        using var client = new HttpClient();
        foreach (var enabled in new[] { false, true })
        foreach (var callback in new[] { false, true })
        foreach (var profile in new bool?[] { null, false, true })
        {
            var options = new AgentBuiltInToolOptions
            {
                ProviderId = new("inert"), SessionId = "inert", WorkingDirectory = "inert-unused-path", HttpClient = client,
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)),
                OnUserInputRequest = callback ? static (_, _) => throw new AssertFailedException("No direct invocation.") : null,
                EnableUserInputTool = enabled,
                Provider = new() { ProtocolFamily = "inert", ProviderKey = "inert", DisplayName = "Inert", TransportKind = AgentTransportKind.OpenAIResponses,
                    Profile = new() { BuiltInToolOverrides = profile is null ? null : new Dictionary<string, bool> { ["request_user_input"] = profile.Value } } },
            };
            var tools = AgentBuiltInToolFactory.CreateDefaultTools(options);
            Assert.AreEqual(enabled && callback && profile != false, tools.Any(t => t.Spec.Name == "request_user_input"));
        }
        Assert.IsFalse(new AgentBuiltInToolOptions { ProviderId = new("inert"), SessionId = "inert", WorkingDirectory = "inert-unused-path", HttpClient = client,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)) }.EnableUserInputTool);
    }

    [TestMethod]
    public Task ActualAgent_PrecedenceFallbackSequentialAndRetainedTool() => AgentFixture.Run(async f =>
    {
        var first = new Recorder("first literal\r\n😀"); var second = new Recorder("second");
        var permission = f.Options.OnPermissionRequest; var input = f.Options.OnUserInputRequest; var tools = f.Options.Tools;
        await f.SendWithoutActivation(first.Handle);
        await f.Send(first.Handle); var retained = f.Tools.Single();
        await f.SendWithoutActivation(second.Handle);
        await f.Send(second.Handle); await f.Send(null);
        await f.SendWithoutActivation(null);
        Assert.AreEqual(1, first.Requests.Count); Assert.AreEqual(1, second.Requests.Count); Assert.AreEqual(1, f.Fallback.Requests.Count);
        Assert.AreNotSame(f.Tools[0], f.Tools[1]); Assert.AreNotSame(f.Tools[1], f.Tools[2]);
        var result = await f.Keep(() => retained.Handler(new(ModelProviderIds.OpenAIResponses, "input-session", "retained", "request_user_input", f.Arguments), f.Token));
        Assert.IsTrue(result.Success); Assert.AreEqual(2, first.Requests.Count); Assert.AreEqual(1, second.Requests.Count);
        foreach (var request in first.Requests.Concat(second.Requests).Concat(f.Fallback.Requests))
        { Assert.AreEqual("input-session", request.SessionId); Assert.IsNull(request.RunId); Assert.AreEqual("q", request.Form.Prompts.Single().Id); }
        Assert.AreSame(permission, f.Options.OnPermissionRequest); Assert.AreSame(input, f.Options.OnUserInputRequest); Assert.AreSame(tools, f.Options.Tools);
        Assert.IsTrue(f.Customs.All(t => ReferenceEquals(t, f.Custom)));
    });

    [TestMethod]
    public Task ActualAgent_BorrowedToolClientRemainsCallerOwned() => AgentFixture.Run(async f =>
    {
        await f.Send(null);
        await f.CloseSession();
        f.AssertClientUnreleased();
        // Run owns the sole release, after the original body, session cleanup and observers settle.
    });

    [TestMethod]
    public async Task FixtureCleanup_CancellationFaultDoesNotCountAsSuccess()
    {
        await RequireSuccessfulCleanupAsync(null, [], Task.CompletedTask);
        foreach (var canceled in new[] { false, true })
        foreach (var withPrimary in new[] { false, true })
        {
            var primary = withPrimary ? new InvalidOperationException("primary") : null;
            var cancellation = new TaskCanceledException("cleanup cancellation fault");
            var aggregate = new AggregateException(cancellation);
            var stop = canceled ? Task.FromCanceled(new CancellationToken(true)) : Task.FromException(aggregate);
            AggregateException? failure = null;
            try { await RequireSuccessfulCleanupAsync(primary, primary is null ? [] : [Task.FromException(primary)], stop); }
            catch (AggregateException ex) { failure = ex; }
            Assert.IsNotNull(failure);
            Assert.AreEqual(withPrimary ? 2 : 1, failure.InnerExceptions.Count);
            if (primary is not null) Assert.AreSame(primary, failure.InnerExceptions[0]);
            var cleanup = failure.InnerExceptions[^1];
            if (canceled)
            {
                Assert.IsInstanceOfType<TaskCanceledException>(cleanup);
                Assert.AreSame(stop, ((TaskCanceledException)cleanup).Task);
            }
            else
            {
                Assert.AreSame(aggregate, cleanup);
                Assert.AreSame(cancellation, aggregate.InnerExceptions.Single());
            }
        }
    }

    private static async Task RequireSuccessfulCleanupAsync(Exception? primary, IEnumerable<Task> originals, Task stop)
    {
        var failures = new List<Exception>();
        if (primary is not null) failures.Add(primary);
        // Await the original stop, not its swallowing observer. No cancellation is expected here.
        foreach (var original in originals.Append(stop).Distinct())
        {
            try { await original; }
            catch (Exception ex)
            {
                // Preserve all direct faults (including aggregate identity), not just await's first fault.
                if (original.Exception is { } fault)
                {
                    foreach (var error in fault.InnerExceptions)
                        if (!failures.Contains(error)) failures.Add(error);
                }
                else if (!failures.Contains(ex)) failures.Add(ex);
            }
        }
        if (failures.Count > 0) throw new AggregateException("Agent fixture originals or cleanup failed.", failures);
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        internal int Disposals { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests++; throw new AssertFailedException("No HTTP request is admitted."); }
        protected override void Dispose(bool disposing)
        { if (disposing) Disposals++; base.Dispose(disposing); }
    }

    private sealed class OwnedClient(RejectingHandler handler) : HttpClient(handler, disposeHandler: true)
    {
        internal int Disposals { get; private set; }
        protected override void Dispose(bool disposing)
        { if (disposing) Disposals++; base.Dispose(disposing); }
    }

    private sealed class Recorder(string value)
    {
        internal List<AgentUserInputRequest> Requests { get; } = [];
        internal Task<AgentUserInputResponse> Handle(AgentUserInputRequest request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Requests.Add(request); return Task.FromResult(new AgentUserInputResponse(new Dictionary<string, string>(StringComparer.Ordinal) { ["q"] = value })); }
    }
    private sealed class Script : IModelProviderTurnExecutor
    {
        internal Queue<Func<AgentTurnRequest, AgentTurnResponse>> Steps { get; } = [];
        public Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Steps.Dequeue()(request)); }
    }
    private sealed class AgentFixture
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CodeAlta-owned-input-agent-" + Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Script _script = new();
        private readonly List<Task> _originals = [];
        private readonly TaskCompletionSource<AgentSession?> _acquired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopLaunch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _lifetime, _observer, _stop, _stopObserver;
        private AgentSession? _session;
        private RejectingHandler? _handler;
        private OwnedClient? _client;
        internal Recorder Fallback { get; } = new("fallback");
        internal List<AgentToolDefinition> Tools { get; } = [];
        internal List<AgentToolDefinition> Customs { get; } = [];
        internal AgentToolDefinition Custom { get; private set; } = null!;
        internal AgentSessionCreateOptions Options { get; private set; } = null!;
        internal JsonElement Arguments { get; private set; }
        internal CancellationToken Token => _cancellation.Token;
        internal Task<T> Keep<T>(Func<Task<T>> start)
        {
            var acquisition = new TaskCompletionSource<Task<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var observer = Observe(acquisition.Task); _originals.Add(observer);
            try { acquisition.SetResult(start()); } catch (Exception ex) { acquisition.SetException(ex); }
            return observer;
        }
        internal Task Keep(Func<Task> start) => Keep(async () => { await start(); return true; });
        private static async Task<T> Observe<T>(Task<Task<T>> task) => await await task;
        private static async Task Settle(Task task) { try { await task; } catch { /* Original outcome is retained. */ } }
        internal static async Task Run(Func<AgentFixture, Task> body)
        {
            var f = new AgentFixture(); var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f._stop = f.Stop(); f._stopObserver = Settle(f._stop);
            f._lifetime = f.Core(body, launch.Task); f._observer = Settle(f._lifetime); launch.SetResult();
            try
            {
                await f._lifetime.WaitAsync(TimeSpan.FromSeconds(30)); await f._observer;
                f.AssertClientUnreleased();
                var client = f._client!; var handler = f._handler!;
                client.Dispose();
                Assert.AreEqual(1, client.Disposals); Assert.AreEqual(1, handler.Disposals);
                Assert.AreEqual(0, handler.Requests);
                f._cancellation.Dispose();
            }
            catch (Exception ex) { ex.Data["RetainedFixture"] = f; ex.Data["RetainedRoot"] = f._root; throw; }
            finally { f._stopLaunch.TrySetResult(); Console.WriteLine("Retained Agent input root: " + f._root); }
        }
        private async Task Core(Func<AgentFixture, Task> body, Task launch)
        {
            await launch; Exception? primary = null;
            try { await Keep(Setup); await Keep(() => body(this)); Assert.AreEqual(0, _script.Steps.Count); }
            catch (Exception ex) { primary = ex; throw; }
            finally
            {
                _acquired.TrySetResult(_session); _stopLaunch.TrySetResult();
                await Task.WhenAll(_originals.Select(Settle));
                await _stopObserver!;
                await RequireSuccessfulCleanupAsync(primary, _originals, _stop!);
            }
        }
        private async Task Stop()
        {
            await _stopLaunch.Task;
            var cancellation = _cancellation.CancelAsync();
            var disposal = DisposeAcquired();
            var all = Task.WhenAll(cancellation, disposal);
            try { await all; } catch { throw all.Exception ?? new AggregateException(new TaskCanceledException(all)); }
        }
        private async Task DisposeAcquired() { var session = await _acquired.Task; if (session is not null) await session.DisposeAsync(); }
        internal Task CloseSession() => Keep(() => _session!.DisposeAsync().AsTask());
        internal void AssertClientUnreleased()
        {
            Assert.IsNotNull(_client); Assert.IsNotNull(_handler);
            Assert.AreEqual(0, _client.Disposals); Assert.AreEqual(0, _handler.Disposals);
            Assert.AreEqual(0, _handler.Requests);
        }
        private async Task Setup()
        {
            for (var ancestor = new DirectoryInfo(Path.GetDirectoryName(_root)!); ancestor is not null; ancestor = ancestor.Parent)
                if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse ancestry refused.");
            if (Directory.Exists(_root) || File.Exists(_root)) throw new IOException("Root already exists.");
            Directory.CreateDirectory(_root); var work = Path.Combine(_root, "work"); Directory.CreateDirectory(work);
            var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(_root, "store")));
            var provider = new ModelProviderRuntimeDescriptor { ProtocolFamily = "openai-responses", ProviderKey = "input-fixture", DisplayName = "Inert scripted input",
                TransportKind = AgentTransportKind.OpenAIResponses, BaseUri = new Uri("https://input-fixture.invalid/"), Profile = new AgentProviderProfile(), Compaction = AgentCompactionSettings.Default with { Enabled = false } };
            var summary = new AgentSessionSummary { SessionId = "input-session", ProviderId = ModelProviderIds.OpenAIResponses, ProtocolFamily = provider.ProtocolFamily,
                ProviderKey = provider.ProviderKey, ModelId = "fixture-model", WorkingDirectory = work, CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch };
            var state = new AgentSessionState { SessionId = summary.SessionId, ProtocolFamily = provider.ProtocolFamily, ProviderKey = provider.ProviderKey, UpdatedAt = DateTimeOffset.UnixEpoch };
            await Keep(() => store.UpsertSessionAsync(summary, Token)); await Keep(() => store.UpsertStateAsync(state, Token));
            using (var document = JsonDocument.Parse("{\"prompts\":[{\"id\":\"q\",\"question\":\"Literal question\",\"allowFreeform\":true}]}")) Arguments = document.RootElement.Clone();
            using (var document = JsonDocument.Parse("{\"type\":\"object\"}")) Custom = new(new("fixture_custom", "Never invoked", document.RootElement.Clone()), static (_, _) => throw new AssertFailedException("No custom invocation."));
            Options = new() { ProviderKey = provider.ProviderKey, Model = summary.ModelId, WorkingDirectory = work, ProjectRoots = [work], InstructionsAlreadyComposed = true,
                SystemMessage = "Explicit inert instructions.", DeveloperInstructions = "No discovery.", Tools = [Custom],
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)), OnUserInputRequest = Fallback.Handle };
            // Publish each owner before the next acquisition; failed/uncertain setup retains both.
            _handler = new RejectingHandler();
            _client = new OwnedClient(_handler);
            _session = new(ModelProviderIds.OpenAIResponses, provider, summary, state, [], store, _script, Options, cachedModels: [new AgentModelInfo("fixture-model", "Fixture")])
            { BuiltInToolHttpClient = _client };
            _acquired.TrySetResult(_session);
        }
        internal Task Send(AgentUserInputRequestHandler? callback)
        {
            _script.Steps.Enqueue(request =>
            {
                Tools.Add(request.Tools.Single(t => t.Spec.Name == "request_user_input")); Customs.Add(request.Tools.Single(t => t.Spec.Name == "fixture_custom"));
                return new() { AssistantMessage = new(AgentConversationRole.Assistant, [new AgentMessagePart.ToolCall("input-" + Tools.Count, "request_user_input", Arguments)]) };
            });
            _script.Steps.Enqueue(request =>
            {
                var result = request.Conversation.Last().Parts.OfType<AgentMessagePart.ToolResult>().Single().Result; Assert.IsTrue(result.Success);
                return new() { AssistantMessage = new(AgentConversationRole.Assistant, [new AgentMessagePart.Text("Inert completion")]) };
            });
            return Keep(() => _session!.SendAsync(new() { Input = AgentInput.Text("Inert request"), OnUserInputRequest = callback, EnableUserInputTool = true }, Token));
        }
        internal Task SendWithoutActivation(AgentUserInputRequestHandler? callback)
        {
            _script.Steps.Enqueue(request =>
            {
                Assert.IsFalse(request.Tools.Any(t => t.Spec.Name == "request_user_input"));
                return new() { AssistantMessage = new(AgentConversationRole.Assistant, [new AgentMessagePart.Text("No input tool without explicit activation")]) };
            });
            return Keep(() => _session!.SendAsync(new() { Input = AgentInput.Text("Inert default request"), OnUserInputRequest = callback }, Token));
        }
    }
}
