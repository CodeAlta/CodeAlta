using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Cli.Tests;

/// <summary>A step of a turn of <see cref="FakeProvider"/>: it can stream deltas, then answers.</summary>
internal delegate Task<AgentTurnResponse> FakeTurn(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken);

/// <summary>
/// A provider whose turns a test scripts, run by the real agent loop of the runtime (tools, journal, usage): each turn
/// takes the next step of its session, then of <see cref="Turns"/>, or answers <c>Done.</c>.
/// </summary>
internal sealed class FakeProvider
{
    internal FakeProvider(string id = "fake", string model = "fake-model", ModelProviderAvailability availability = ModelProviderAvailability.Ready,
        IReadOnlyList<AgentModelInfo>? models = null)
    {
        Model = model;
        Availability = availability;
        Models = models ?? [new AgentModelInfo(model, DisplayName: "Fake Model")];
        Descriptor = new ModelProviderDescriptor(new ModelProviderId(id), "Fake " + id) { DefaultModelId = model };
    }

    internal string Model { get; }

    /// <summary>Gets the models the provider lists when it is ready, the default one first.</summary>
    internal IReadOnlyList<AgentModelInfo> Models { get; }
    internal ModelProviderAvailability Availability { get; }
    internal ModelProviderDescriptor Descriptor { get; }
    internal ConcurrentQueue<FakeTurn> Turns { get; } = new();
    internal ConcurrentDictionary<string, ConcurrentQueue<FakeTurn>> SessionTurns { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal ConcurrentQueue<AgentTurnRequest> Requests { get; } = new();

    internal void Register(ModelProviderRegistry registry) => registry.RegisterOrReplace(Descriptor, () => new Runtime(this));

    internal void Script(string sessionId, params FakeTurn[] steps)
    {
        var queue = SessionTurns.GetOrAdd(sessionId, static _ => new());
        foreach (var step in steps) queue.Enqueue(step);
    }

    internal static FakeTurn Reply(string text) => (_, _, _) => Task.FromResult(Answer(text));

    /// <summary>Streams the text in a few deltas of the answer (and one of reasoning), then answers with it.</summary>
    internal static FakeTurn Stream(string text, string? reasoning = null) => async (_, onUpdate, cancellationToken) =>
    {
        if (reasoning is not null)
            await onUpdate(new AgentTurnDelta { Kind = AgentContentKind.Reasoning, ContentId = "reasoning-1", Text = reasoning }, cancellationToken);
        var half = text.Length / 2;
        await onUpdate(new AgentTurnDelta { Kind = AgentContentKind.Assistant, ContentId = "answer-1", Text = text[..half] }, cancellationToken);
        await onUpdate(new AgentTurnDelta { Kind = AgentContentKind.Assistant, ContentId = "answer-1", Text = text[half..] }, cancellationToken);
        var answer = Answer(text);
        // The reasoning streamed is part of the answer, as a provider gives it.
        return reasoning is null ? answer : answer with
        {
            AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Reasoning(reasoning), .. answer.AssistantMessage.Parts]),
        };
    };

    internal static FakeTurn CallTool(string name, string argumentsJson, string callId = "call-1") => (_, _, _) =>
    {
        using var arguments = JsonDocument.Parse(argumentsJson);
        return Task.FromResult(new AgentTurnResponse
        {
            AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.ToolCall(callId, name, arguments.RootElement.Clone())]),
        });
    };

    internal static FakeTurn Fail(AgentFailureKind kind, string message = "fake failure")
        => (_, _, _) => Task.FromException<AgentTurnResponse>(new AgentProviderFailureException(message, new AgentFailureInfo(kind)));

    internal static FakeTurn WaitThenReply(Task gate, string text, TaskCompletionSource? started = null) => async (_, _, cancellationToken) =>
    {
        started?.TrySetResult();
        await gate.WaitAsync(cancellationToken);
        return Answer(text);
    };

    /// <summary>Waits for the gate without the token of the turn: a provider that ignores cancellation.</summary>
    internal static FakeTurn IgnoreCancellation(Task gate, string text, TaskCompletionSource? started = null) => async (_, _, _) =>
    {
        started?.TrySetResult();
        await gate;
        return Answer(text);
    };

    internal static FakeTurn DoThenReply(Func<AgentTurnRequest, Task> action, string text) => async (request, _, _) =>
    {
        await action(request);
        return Answer(text);
    };

    internal static AgentTurnResponse Answer(string text) => new()
    {
        AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text(text)]),
        Usage = new AgentSessionUsage(LastOperation: new AgentOperationUsageSnapshot(Model: "fake-model", InputTokens: 100, OutputTokens: 20,
            CachedInputTokens: 40, ReasoningTokens: 5, Cost: 0.25, CostUnit: "USD")),
    };

    private sealed class Runtime(FakeProvider owner) : IAgentModelProviderRuntime
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

        public AgentRuntimeProviderRegistration CreateProviderRegistration() => new() { Provider = RuntimeDescriptor, TurnExecutor = new Executor(owner) };

        public IModelProviderTurnExecutor CreateTurnExecutor() => new Executor(owner);

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelProviderProbeResult
            {
                ProviderId = owner.Descriptor.ProviderId,
                Availability = owner.Availability,
                Models = owner.Availability == ModelProviderAvailability.Ready ? owner.Models : [],
                SelectedModelId = owner.Model,
                StatusMessage = owner.Availability == ModelProviderAvailability.Ready ? null : "Sign in first.",
            });

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Executor(FakeProvider owner) : IModelProviderTurnExecutor
    {
        public async Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
            CancellationToken cancellationToken = default)
        {
            owner.Requests.Enqueue(request);
            var step = owner.SessionTurns.TryGetValue(request.SessionId, out var own) && own.TryDequeue(out var scripted) ? scripted
                : owner.Turns.TryDequeue(out var next) ? next : Reply("Done.");
            return await step(request, onUpdate, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>What a run of <c>altacli</c> in a test gave: its exit code and its two output streams.</summary>
internal sealed record CliRun(int ExitCode, string Stdout, string Stderr)
{
    /// <summary>The lines of standard output, without the empty last one.</summary>
    public string[] Lines => Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
/// The folders of a run of <c>altacli</c> under one temporary root, deleted with it: a home whose <c>.alta</c> is the
/// profile (never the user's), a project folder, a temporary folder, a built-in skill root, and the boundary of instructions.
/// </summary>
internal sealed class CliTestRoot : IDisposable
{
    internal CliTestRoot()
    {
        Root = Path.Combine(Path.GetTempPath(), "CodeAlta-cli-" + Guid.NewGuid().ToString("N"));
        Home = Path.Combine(Root, "home");
        Profile = Path.Combine(Home, ".alta");
        ProjectRoot = Path.Combine(Root, "project");
        TempRoot = Path.Combine(Root, "temp");
        BuiltInSkillRoot = Path.Combine(Root, "builtin");
        foreach (var directory in new[] { Home, ProjectRoot, TempRoot, BuiltInSkillRoot }) Directory.CreateDirectory(directory);
        // The skill root is a git checkout of its own, as the shipped one is: discovery stops there.
        var git = Path.Combine(BuiltInSkillRoot, ".git");
        Directory.CreateDirectory(git);
        File.WriteAllText(Path.Combine(git, "config"), "[core]\nignorecase = true\n");
    }

    internal string Root { get; }
    internal string Home { get; }
    internal string Profile { get; }
    internal string ProjectRoot { get; }
    internal string TempRoot { get; }
    internal string BuiltInSkillRoot { get; }
    internal Dictionary<string, string?> Variables { get; } = new(StringComparer.Ordinal);
    internal CodeAltaHost? LastHost { get; private set; }

    internal CliEnvironment Environment(FakeProvider? provider) => new()
    {
        UserProfile = Home,
        CurrentDirectory = ProjectRoot,
        TempRoot = TempRoot,
        GetVariable = name => Variables.TryGetValue(name, out var value) ? value : null,
        InitializeLogging = false,
        HandleSignals = false,
        ConfigureModelProviders = provider is null ? null : provider.Register,
        DiscoveryScope = new SessionDiscoveryScope(Home, Root),
        BuiltInSkillRoot = BuiltInSkillRoot,
        PluginEnvironment = FrozenDictionary<string, string?>.Empty,
        AbortGrace = TimeSpan.FromSeconds(10),
        HostCreated = host =>
        {
            LastHost = host;
            LastPlugins = [.. host.PluginRuntime.ActivePlugins.Select(static plugin => plugin.Descriptor.DisplayName ?? plugin.Descriptor.RuntimeKey)];
        },
    };

    /// <summary>Gets the plugins active when the last host was created.</summary>
    internal string[] LastPlugins { get; private set; } = [];

    /// <summary>Runs <c>altacli</c> in this process, with a standard input when one is given (as a pipe).</summary>
    internal Task<CliRun> RunAsync(FakeProvider? provider, string[] args, string? stdin = null, CancellationToken cancellationToken = default)
        => RunWithEnvironmentAsync(Environment(provider), args, stdin, cancellationToken);

    /// <summary>Runs <c>altacli</c> in this process with an environment of its own, such as one whose plugins have the variables of the process.</summary>
    internal async Task<CliRun> RunWithEnvironmentAsync(CliEnvironment environment, string[] args, string? stdin = null, CancellationToken cancellationToken = default)
    {
        using var stdout = new MemoryStream();
        var stderr = new StringWriter();
        var streams = new CliStreams
        {
            Stdin = new StringReader(stdin ?? string.Empty),
            IsStdinRedirected = stdin is not null,
            Stdout = stdout,
            Stderr = TextWriter.Synchronized(stderr),
        };
        var code = await CliApplication.RunAsync(args, streams, environment, cancellationToken).WaitAsync(TimeSpan.FromSeconds(60));
        return new CliRun(code, Encoding.UTF8.GetString(stdout.ToArray()), stderr.ToString());
    }

    /// <summary>Lists the files under a folder, relative to it.</summary>
    internal static string[] Files(string folder)
        => Directory.Exists(folder)
            ? [.. Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(folder, path).Replace('\\', '/')).Order(StringComparer.Ordinal)]
            : [];

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
