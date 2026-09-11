using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Compaction;

namespace CodeAlta.Tests;

/// <summary>Actual in-process sends with scripted turns and exclusively denied/canceled shell requests.</summary>
[TestClass]
public sealed class AgentSessionPermissionTests
{
    [TestMethod]
    public Task SendPermission_OverrideTakesPrecedence()
        => Fixture.RunAsync(async fixture =>
        {
            var send = new PermissionRecorder(AgentPermissionDecisionKind.Cancel);
            await fixture.SendAsync(send.HandleAsync, "shell_command was canceled by the host.");
            Assert.AreEqual(1, send.Requests.Count);
            Assert.AreEqual(0, fixture.Fallback.Requests.Count);
            fixture.AssertShellRequest(send.Requests.Single());
        });

    [TestMethod]
    public Task SendPermission_NullUsesSessionFallback()
        => Fixture.RunAsync(async fixture =>
        {
            await fixture.SendAsync(null, "shell_command was denied by the host.");
            Assert.AreEqual(1, fixture.Fallback.Requests.Count);
            fixture.AssertShellRequest(fixture.Fallback.Requests.Single());
        });

    [TestMethod]
    public Task SendPermission_SequentialSendsDoNotReuseOverride()
        => Fixture.RunAsync(async fixture =>
        {
            var first = new PermissionRecorder(AgentPermissionDecisionKind.Cancel);
            var second = new PermissionRecorder(AgentPermissionDecisionKind.Deny);
            await fixture.SendAsync(first.HandleAsync, "shell_command was canceled by the host.");
            await fixture.SendAsync(second.HandleAsync, "shell_command was denied by the host.");
            await fixture.SendAsync(null, "shell_command was denied by the host.");
            Assert.AreEqual(1, first.Requests.Count);
            Assert.AreEqual(1, second.Requests.Count);
            Assert.AreEqual(1, fixture.Fallback.Requests.Count);
            Assert.AreNotSame(fixture.ShellTools[0], fixture.ShellTools[1]);
            Assert.AreNotSame(fixture.ShellTools[1], fixture.ShellTools[2]);
        });

    [TestMethod]
    public Task SendPermission_RetainedToolKeepsOriginalCallback()
        => Fixture.RunAsync(async fixture =>
        {
            var first = new PermissionRecorder(AgentPermissionDecisionKind.Cancel);
            var second = new PermissionRecorder(AgentPermissionDecisionKind.Deny);
            await fixture.SendAsync(first.HandleAsync, "shell_command was canceled by the host.");
            var retained = fixture.ShellTools.Single();
            await fixture.SendAsync(second.HandleAsync, "shell_command was denied by the host.");

            // The API selects a callback; it does not add a lifetime/cancellation lease.
            var result = await retained.Handler(new AgentToolInvocation(
                ModelProviderIds.OpenAIResponses, fixture.SessionId, "retained", "shell_command", fixture.Arguments),
                fixture.CancellationToken);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("shell_command was canceled by the host.", result.Error);
            Assert.AreEqual(2, first.Requests.Count);
            Assert.AreEqual(1, second.Requests.Count);
            Assert.AreEqual(0, fixture.Fallback.Requests.Count);
        });

    [TestMethod]
    public Task SendPermission_LeavesSessionCustomToolsAndUserInputUnchanged()
        => Fixture.RunAsync(async fixture =>
        {
            var permission = fixture.Options.OnPermissionRequest;
            var userInput = fixture.Options.OnUserInputRequest;
            var customTools = fixture.Options.Tools;
            var customHandler = fixture.CustomTool.Handler;
            var send = new PermissionRecorder(AgentPermissionDecisionKind.Cancel);
            await fixture.SendAsync(send.HandleAsync, "shell_command was canceled by the host.");
            await fixture.SendAsync(null, "shell_command was denied by the host.");

            Assert.AreSame(permission, fixture.Options.OnPermissionRequest);
            Assert.AreSame(userInput, fixture.Options.OnUserInputRequest);
            Assert.AreSame(customTools, fixture.Options.Tools);
            Assert.AreSame(customHandler, fixture.CustomTool.Handler);
            Assert.AreEqual(2, fixture.CustomTools.Count);
            foreach (var tool in fixture.CustomTools) Assert.AreSame(fixture.CustomTool, tool);
            Assert.AreEqual(1, send.Requests.Count);
            Assert.AreEqual(1, fixture.Fallback.Requests.Count);
        });

    private sealed class PermissionRecorder
    {
        private readonly AgentPermissionDecisionKind _decision;

        internal PermissionRecorder(AgentPermissionDecisionKind decision)
        {
            // Fixture safety is independent of which callback production chooses.
            if (decision is not (AgentPermissionDecisionKind.Deny or AgentPermissionDecisionKind.Cancel))
                throw new ArgumentOutOfRangeException(nameof(decision));
            _decision = decision;
        }

        internal List<AgentPermissionRequest> Requests { get; } = [];

        internal Task<AgentPermissionDecision> HandleAsync(AgentPermissionRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(new AgentPermissionDecision(_decision));
        }
    }

    private sealed class ScriptedTurnExecutor : IModelProviderTurnExecutor
    {
        internal Queue<Func<AgentTurnRequest, AgentTurnResponse>> Steps { get; } = [];

        public Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request,
            Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Steps.TryDequeue(out var step)) throw new InvalidOperationException("No scripted turn remains.");
            return Task.FromResult(step(request));
        }
    }

    private sealed class Fixture
    {
        private const string Command = "codealta-permission-fixture-must-not-run";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CodeAlta-send-permission-" + Guid.NewGuid().ToString("N"));
        private readonly ScriptedTurnExecutor _executor = new();
        private AgentSession? _session;
        private bool _ownsRoot;
        private bool _safeToDelete;

        internal string SessionId { get; } = Guid.CreateVersion7().ToString();
        internal string WorkingDirectory => Path.Combine(_root, "work");
        internal PermissionRecorder Fallback { get; } = new(AgentPermissionDecisionKind.Deny);
        internal List<AgentToolDefinition> ShellTools { get; } = [];
        internal List<AgentToolDefinition> CustomTools { get; } = [];
        internal AgentSessionCreateOptions Options { get; private set; } = null!;
        internal AgentToolDefinition CustomTool { get; private set; } = null!;
        internal JsonElement Arguments { get; private set; }
        internal CancellationToken CancellationToken { get; private set; }

        internal static async Task RunAsync(Func<Fixture, Task> body)
        {
            var fixture = new Fixture();
            // One retained task owns setup, all sends/tool calls, session disposal and the CTS.
            var work = fixture.RunOwnedAsync(body);
            try
            {
                await work.WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (Exception ex)
            {
                // Observe a late fault without disposing/deleting resources still used by the task.
                _ = ObserveAsync(work);
                throw new InvalidOperationException("Permission fixture failed; root: " + fixture._root, ex);
            }
            finally
            {
                if (work.IsCompleted && fixture._ownsRoot && fixture._safeToDelete)
                {
                    RejectReparseAncestors(fixture._root);
                    Directory.Delete(fixture._root, recursive: true);
                }
            }
        }

        private async Task RunOwnedAsync(Func<Fixture, Task> body)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            CancellationToken = cancellation.Token;
            try
            {
                RejectReparseAncestors(Path.GetDirectoryName(_root)!);
                if (Directory.Exists(_root) || File.Exists(_root)) throw new IOException("Fixture root already exists.");
                Directory.CreateDirectory(_root);
                _ownsRoot = true;
                Directory.CreateDirectory(WorkingDirectory);
                var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(_root, "store")));
                var timestamp = new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);
                var provider = new ModelProviderRuntimeDescriptor
                {
                    ProtocolFamily = "openai-responses",
                    ProviderKey = "permission-fixture",
                    DisplayName = "Scripted permission fixture",
                    TransportKind = AgentTransportKind.OpenAIResponses,
                    BaseUri = new Uri("https://permission-fixture.invalid/"),
                    Profile = new AgentProviderProfile(),
                    Compaction = AgentCompactionSettings.Default with { Enabled = false },
                };
                var summary = new AgentSessionSummary
                {
                    SessionId = SessionId,
                    ProviderId = ModelProviderIds.OpenAIResponses,
                    ProtocolFamily = provider.ProtocolFamily,
                    ProviderKey = provider.ProviderKey,
                    ModelId = "fixture-model",
                    WorkingDirectory = WorkingDirectory,
                    CreatedAt = timestamp,
                    UpdatedAt = timestamp,
                };
                var state = new AgentSessionState
                {
                    SessionId = SessionId,
                    ProtocolFamily = provider.ProtocolFamily,
                    ProviderKey = provider.ProviderKey,
                    UpdatedAt = timestamp,
                };
                await store.UpsertSessionAsync(summary, CancellationToken);
                await store.UpsertStateAsync(state, CancellationToken);
                using (var document = JsonDocument.Parse("{\"command\":\"" + Command + "\"}"))
                    Arguments = document.RootElement.Clone();
                using (var document = JsonDocument.Parse("{\"type\":\"object\"}"))
                    CustomTool = new AgentToolDefinition(new AgentToolSpec("fixture_custom", "Never invoked", document.RootElement.Clone()),
                        static (_, _) => throw new InvalidOperationException("Custom tool must not execute."));
                Options = new AgentSessionCreateOptions
                {
                    ProviderKey = provider.ProviderKey,
                    Model = summary.ModelId,
                    WorkingDirectory = WorkingDirectory,
                    ProjectRoots = [WorkingDirectory],
                    InstructionsAlreadyComposed = true,
                    SystemMessage = "Isolated fixture instructions.",
                    DeveloperInstructions = "Do not discover project instructions.",
                    Tools = [CustomTool],
                    OnPermissionRequest = Fallback.HandleAsync,
                    OnUserInputRequest = static (_, _) => throw new InvalidOperationException("User input must not execute."),
                };
                _session = new AgentSession(ModelProviderIds.OpenAIResponses, provider, summary, state, [], store,
                    _executor, Options, cachedModels: [new AgentModelInfo("fixture-model", "Fixture model")]);
                await body(this);
                Assert.AreEqual(0, _executor.Steps.Count);
            }
            finally
            {
                // No body/helper starts detached I/O. Disposal and CTS ownership remain within work,
                // even if the outer finite wait expires; only confirmed disposal permits root deletion.
                if (_session is not null) await _session.DisposeAsync();
                _safeToDelete = true;
            }
        }

        internal async Task SendAsync(AgentPermissionRequestHandler? callback, string expectedError)
        {
            var callId = "shell-" + ShellTools.Count;
            _executor.Steps.Enqueue(request =>
            {
                Assert.AreEqual(WorkingDirectory, request.WorkingDirectory);
                ShellTools.Add(request.Tools.Single(tool => tool.Spec.Name == "shell_command"));
                CustomTools.Add(request.Tools.Single(tool => tool.Spec.Name == "fixture_custom"));
                return new AgentTurnResponse
                {
                    AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant,
                        [new AgentMessagePart.ToolCall(callId, "shell_command", Arguments)]),
                };
            });
            _executor.Steps.Enqueue(request =>
            {
                var result = request.Conversation.Last().Parts.OfType<AgentMessagePart.ToolResult>().Single();
                Assert.AreEqual(callId, result.CallId);
                Assert.IsFalse(result.Result.Success);
                Assert.AreEqual(expectedError, result.Result.Error);
                return new AgentTurnResponse
                {
                    AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant,
                        [new AgentMessagePart.Text("Denied or canceled; nothing executed.")]),
                };
            });
            await _session!.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Fixture request"), OnPermissionRequest = callback },
                CancellationToken);
        }

        internal void AssertShellRequest(AgentPermissionRequest request)
        {
            Assert.IsInstanceOfType<AgentCommandPermissionRequest>(request);
            var command = (AgentCommandPermissionRequest)request;
            Assert.AreEqual(SessionId, command.SessionId);
            Assert.AreEqual(Command, command.Command);
            Assert.AreEqual(WorkingDirectory, command.WorkingDirectory);
            Assert.IsNull(command.RunId);
        }

        private static async Task ObserveAsync(Task work)
        {
            try { await work; }
            catch { /* The finite waiter reports failure; observe any eventual task fault too. */ }
        }

        private static void RejectReparseAncestors(string path)
        {
            for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
                if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Fixture ancestry contains a reparse point.");
        }
    }
}
