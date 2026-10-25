using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// A provider whose turns a test scripts, run by the real agent loop of the runtime (built-in tools, journal, usage):
/// each turn takes the next step of <see cref="Turns"/>, or answers <c>Done.</c> when none is left.
/// </summary>
internal sealed class ScriptedTurnProvider
{
    internal ScriptedTurnProvider(string id = "scripted", string model = "scripted-model",
        ModelProviderAvailability availability = ModelProviderAvailability.Ready)
    {
        Model = model;
        Availability = availability;
        Descriptor = new ModelProviderDescriptor(new ModelProviderId(id), "Scripted " + id) { DefaultModelId = model };
    }

    internal string Model { get; }
    internal ModelProviderAvailability Availability { get; }
    internal ModelProviderDescriptor Descriptor { get; }
    internal ModelProviderId ProviderId => Descriptor.ProviderId;

    /// <summary>Gets the steps of the next turns, in order.</summary>
    internal ConcurrentQueue<Func<AgentTurnRequest, CancellationToken, Task<AgentTurnResponse>>> Turns { get; } = new();

    /// <summary>Gets the steps of the next turns of one session, taken before <see cref="Turns"/>.</summary>
    internal ConcurrentDictionary<string, ConcurrentQueue<Func<AgentTurnRequest, CancellationToken, Task<AgentTurnResponse>>>> SessionTurns { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the requests the provider was sent, in order.</summary>
    internal ConcurrentQueue<AgentTurnRequest> Requests { get; } = new();

    /// <summary>Adds steps for the next turns of one session.</summary>
    internal void Script(string sessionId, params Func<AgentTurnRequest, CancellationToken, Task<AgentTurnResponse>>[] steps)
    {
        var queue = SessionTurns.GetOrAdd(sessionId, static _ => new());
        foreach (var step in steps) queue.Enqueue(step);
    }

    /// <summary>The requests sent for one session, in order.</summary>
    internal AgentTurnRequest[] RequestsOf(string sessionId) => [.. Requests.Where(request => string.Equals(request.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))];

    internal void Register(ModelProviderRegistry registry)
        => registry.RegisterOrReplace(Descriptor, () => new Runtime(this));

    /// <summary>A step that answers with a text, and reports the usage of the request.</summary>
    internal static Func<AgentTurnRequest, CancellationToken, Task<AgentTurnResponse>> Reply(string text)
        => (_, _) => Task.FromResult(Answer(text));

    /// <summary>A step that asks for one tool call.</summary>
    internal static Func<AgentTurnRequest, CancellationToken, Task<AgentTurnResponse>> CallTool(string name, string argumentsJson, string callId = "call-1")
        => (_, _) =>
        {
            using var arguments = JsonDocument.Parse(argumentsJson);
            return Task.FromResult(new AgentTurnResponse
            {
                AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant,
                    [new AgentMessagePart.ToolCall(callId, name, arguments.RootElement.Clone())]),
            });
        };

    /// <summary>A step that fails with a failure the provider classified.</summary>
    internal static Func<AgentTurnRequest, CancellationToken, Task<AgentTurnResponse>> Fail(AgentFailureKind kind, string message = "scripted failure")
        => (_, _) => Task.FromException<AgentTurnResponse>(new AgentProviderFailureException(message, new AgentFailureInfo(kind)));

    /// <summary>A step that tells it started, waits for a gate, then answers.</summary>
    internal static Func<AgentTurnRequest, CancellationToken, Task<AgentTurnResponse>> WaitThenReply(Task gate, string text, TaskCompletionSource? started = null)
        => async (_, cancellationToken) =>
        {
            started?.TrySetResult();
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return Answer(text);
        };

    /// <summary>A step that runs an action of the test, then answers.</summary>
    internal static Func<AgentTurnRequest, CancellationToken, Task<AgentTurnResponse>> DoThenReply(Func<Task> action, string text)
        => async (_, _) =>
        {
            await action().ConfigureAwait(false);
            return Answer(text);
        };

    internal static AgentTurnResponse Answer(string text)
        => new()
        {
            AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text(text)]),
            Usage = new AgentSessionUsage(LastOperation: new AgentOperationUsageSnapshot(Model: "scripted-model", InputTokens: 100, OutputTokens: 20,
                CachedInputTokens: 40, ReasoningTokens: 5, Cost: 0.25, CostUnit: "USD")),
        };

    private sealed class Runtime(ScriptedTurnProvider owner) : IAgentModelProviderRuntime
    {
        public ModelProviderDescriptor Descriptor => owner.Descriptor;

        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = "test",
            ProviderKey = owner.Descriptor.ProviderId.Value,
            DisplayName = owner.Descriptor.DisplayName,
            TransportKind = AgentTransportKind.OpenAIResponses,
        };

        public IModelProviderModelCatalog? ModelCatalog => null;

        public AgentRuntimeProviderRegistration CreateProviderRegistration() => new()
        {
            Provider = RuntimeDescriptor,
            TurnExecutor = new Executor(owner),
        };

        public IModelProviderTurnExecutor CreateTurnExecutor() => new Executor(owner);

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelProviderProbeResult
            {
                ProviderId = owner.Descriptor.ProviderId,
                Availability = owner.Availability,
                Models = owner.Availability == ModelProviderAvailability.Ready ? [new AgentModelInfo(owner.Model, DisplayName: owner.Model)] : [],
                SelectedModelId = owner.Model,
            });

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Executor(ScriptedTurnProvider owner) : IModelProviderTurnExecutor
    {
        public async Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
            CancellationToken cancellationToken = default)
        {
            owner.Requests.Enqueue(request);
            var step = owner.SessionTurns.TryGetValue(request.SessionId, out var own) && own.TryDequeue(out var scripted) ? scripted
                : owner.Turns.TryDequeue(out var next) ? next : Reply("Done.");
            return await step(request, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// The folders of a headless host, under one temporary root that is deleted with it: a home and an instruction
/// boundary of their own (nothing of the user's profile is read), a global root, a state root, a project folder that no
/// catalog lists, and a built-in skill root.
/// </summary>
internal sealed class HeadlessTestRoot : IAsyncDisposable
{
    private readonly List<CodeAltaHost> _hosts = [];
    private readonly List<PrivateApplicationDatabase> _databases = [];

    internal HeadlessTestRoot()
    {
        Root = Path.Combine(Path.GetTempPath(), "CodeAlta-headless-" + Guid.NewGuid().ToString("N"));
        Home = Path.Combine(Root, "home");
        GlobalRoot = Path.Combine(Root, "global");
        StateRoot = Path.Combine(Root, "state");
        ProjectRoot = Path.Combine(Root, "project");
        BuiltInSkillRoot = Path.Combine(Root, "builtin");
        DatabasesRoot = Path.Combine(Root, "databases");
        foreach (var directory in new[] { Home, GlobalRoot, StateRoot, ProjectRoot, BuiltInSkillRoot }) Directory.CreateDirectory(directory);
        // The skill root is a git checkout of its own, as the shipped one is: discovery stops there.
        var git = Path.Combine(BuiltInSkillRoot, ".git");
        Directory.CreateDirectory(git);
        File.WriteAllText(Path.Combine(git, "config"), "[core]\nignorecase = true\n");
    }

    internal string Root { get; }
    internal string Home { get; }
    internal string GlobalRoot { get; }
    internal string StateRoot { get; }
    internal string ProjectRoot { get; }
    internal string BuiltInSkillRoot { get; }
    internal string DatabasesRoot { get; }

    /// <summary>Creates a private database under this root, disposed with it.</summary>
    internal PrivateApplicationDatabase CreateDatabase()
    {
        var database = PrivateApplicationDatabase.Create(DatabasesRoot);
        _databases.Add(database);
        return database;
    }

    /// <summary>
    /// Creates a headless host on these folders, disposed with the root unless disposed before. Without a state root it
    /// shares the global root; read-only global state gives it a private database.
    /// </summary>
    internal async Task<CodeAltaHost> CreateHostAsync(ScriptedTurnProvider provider, bool readOnlyGlobalState = false, bool separateStateRoot = false,
        string? scratchpadRoot = null, string? currentProject = null, bool fullTrust = false, bool headlessInstructions = false,
        CodeAltaConfigOptions? config = null, bool listsSkills = true, string? sessionLeaseOwner = null, bool followExternalSessions = false)
    {
        var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
        {
            SessionLeaseOwner = sessionLeaseOwner,
            FollowExternalSessions = followExternalSessions,
            GlobalRoot = GlobalRoot,
            StateRoot = separateStateRoot ? StateRoot : null,
            CurrentProjectPath = currentProject ?? ProjectRoot,
            DiscoveryScope = new SessionDiscoveryScope(Home, Root),
            BuiltInSkillRoot = BuiltInSkillRoot,
            PluginEnvironment = FrozenDictionary<string, string?>.Empty,
            StartPlugins = false,
            OwnsLogging = false,
            IsHeadless = true,
            ReadOnlyGlobalState = readOnlyGlobalState,
            ApplicationDatabase = readOnlyGlobalState ? CreateDatabase().Database : null,
            ScratchpadRoot = scratchpadRoot,
            FullTrust = fullTrust,
            HeadlessInstructions = headlessInstructions,
            Config = config ?? CodeAltaConfigOptions.Default,
            ListsSkills = listsSkills,
            ConfigureModelProviders = provider.Register,
        }, CancellationToken.None);
        _hosts.Add(host);
        return host;
    }

    /// <summary>The options of a send through the runtime, in a folder.</summary>
    internal static SessionExecutionOptions Options(ScriptedTurnProvider provider, string folder, IReadOnlyList<AgentToolDefinition>? tools = null,
        AgentReasoningEffort? reasoningEffort = null) => new()
    {
        ProviderId = provider.ProviderId,
        ProviderKey = provider.ProviderId.Value,
        Model = provider.Model,
        ReasoningEffort = reasoningEffort,
        WorkingDirectory = folder,
        ProjectRoots = [folder],
        Tools = tools,
        OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
    };

    /// <summary>Lists the files under a folder with their length and last write, relative to it.</summary>
    internal static IReadOnlyDictionary<string, (long Length, DateTime LastWrite)> Snapshot(string folder)
        => Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(folder, path), path => (new FileInfo(path).Length, File.GetLastWriteTimeUtc(path)), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, (long, DateTime)>();

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts) await host.DisposeAsync();
        foreach (var database in _databases) await database.DisposeAsync();
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
