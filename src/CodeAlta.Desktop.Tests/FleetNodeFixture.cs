using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Net;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Fleet.Channels;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Orchestration.Hosting;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// A node in this process: a real <see cref="CodeAltaHost"/> with a scripted provider, its node API on a loopback port,
/// and the device key and paired machines of a viewer of another profile.
/// </summary>
internal sealed class FleetNodeFixture : IAsyncDisposable
{
    private readonly string _root;
    private CodeAltaHost _host;
    private FleetDirectListener _listener;
    // What the node API was started with, to start it again on a fresh host (RestartHostAsync).
    private Func<CodeAltaHost, FleetNodeService>? _restart;

    private FleetNodeFixture(string root, CodeAltaHost host, FleetNodeService service, FleetDirectListener listener, DeviceIdentity nodeIdentity,
        ScriptedFleetProvider provider, IReadOnlyList<string> addresses)
    {
        _root = root;
        _host = host;
        Service = service;
        _listener = listener;
        NodeIdentity = nodeIdentity;
        Provider = provider;
        Addresses = addresses;
        var viewerFleet = Path.Combine(root, "viewer", "fleet");
        ViewerFleetRoot = viewerFleet;
        ViewerIdentity = DeviceIdentity.LoadOrCreate(viewerFleet);
        ViewerMachines = new PairedMachineStore(viewerFleet);
    }

    /// <summary>Gets the runtime of the node: its catalog of projects, its providers.</summary>
    public CodeAltaHost Host => _host;

    /// <summary>Gets a folder of the fixture that is deleted with it.</summary>
    public string Root => _root;

    public FleetNodeService Service { get; private set; }

    public DeviceIdentity NodeIdentity { get; }

    public ScriptedFleetProvider Provider { get; }

    public IReadOnlyList<string> Addresses { get; private set; }

    public string ViewerFleetRoot { get; }

    public DeviceIdentity ViewerIdentity { get; }

    public PairedMachineStore ViewerMachines { get; }

    public static Task<FleetNodeFixture> StartAsync(CancellationToken cancellationToken) => StartAsync(cancellationToken, null);

    /// <summary>Starts the node, with the automations it runs for its devices; null for none.</summary>
    public static Task<FleetNodeFixture> StartAsync(CancellationToken cancellationToken, IFleetNodeAutomations? automations) => StartAsync(cancellationToken, automations, null);

    /// <summary>Starts the node, with the automations it runs for its devices and what admits its runs to budgets; null for none.</summary>
    public static Task<FleetNodeFixture> StartAsync(CancellationToken cancellationToken, IFleetNodeAutomations? automations,
        Func<FleetNodeBudgetRun, Action, CancellationToken, Task<CodeAlta.Fleet.Server.FleetBudgetAdmission>>? admitBudget)
        => StartAsync(cancellationToken, automations, admitBudget, null);

    /// <summary>Starts the node as above, with the clock of its node API; null for the system's.</summary>
    public static Task<FleetNodeFixture> StartAsync(CancellationToken cancellationToken, IFleetNodeAutomations? automations,
        Func<FleetNodeBudgetRun, Action, CancellationToken, Task<CodeAlta.Fleet.Server.FleetBudgetAdmission>>? admitBudget, TimeProvider? time)
        => StartAsync(cancellationToken, automations, admitBudget, time, appliesSpaceIdentities: false);

    /// <summary>Starts the node as above, its sessions taking the identity of their space (CLI profiles and their secrets) when asked.</summary>
    public static Task<FleetNodeFixture> StartAsync(CancellationToken cancellationToken, IFleetNodeAutomations? automations,
        Func<FleetNodeBudgetRun, Action, CancellationToken, Task<CodeAlta.Fleet.Server.FleetBudgetAdmission>>? admitBudget, TimeProvider? time, bool appliesSpaceIdentities)
        => StartAsync(cancellationToken, automations, admitBudget, time, appliesSpaceIdentities, null);

    /// <summary>Starts the node as above, with what admits its sessions to an environment; null runs none in an environment.</summary>
    public static Task<FleetNodeFixture> StartAsync(CancellationToken cancellationToken, IFleetNodeAutomations? automations,
        Func<FleetNodeBudgetRun, Action, CancellationToken, Task<CodeAlta.Fleet.Server.FleetBudgetAdmission>>? admitBudget, TimeProvider? time, bool appliesSpaceIdentities,
        Func<string, string, string?, CancellationToken, Task<CodeAlta.Orchestration.Runtime.EnvironmentAdmissionResult>>? admitEnvironment)
        => StartAsync(cancellationToken, automations, admitBudget, time, appliesSpaceIdentities, admitEnvironment, null);

    /// <summary>Starts the node as above, with what tells the providers that run with a personal plan; null tells none.</summary>
    public static async Task<FleetNodeFixture> StartAsync(CancellationToken cancellationToken, IFleetNodeAutomations? automations,
        Func<FleetNodeBudgetRun, Action, CancellationToken, Task<CodeAlta.Fleet.Server.FleetBudgetAdmission>>? admitBudget, TimeProvider? time, bool appliesSpaceIdentities,
        Func<string, string, string?, CancellationToken, Task<CodeAlta.Orchestration.Runtime.EnvironmentAdmissionResult>>? admitEnvironment,
        Func<ModelProviderDescriptor, bool>? isPersonalProvider, CodeAlta.ModelServers.ModelHost? modelServers = null, bool showsPromptSuggestions = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-desktop-fleet-tests", Guid.NewGuid().ToString("N"));
        var provider = new ScriptedFleetProvider();
        var host = await CreateHostAsync(root, provider, appliesSpaceIdentities, showsPromptSuggestions, cancellationToken);
        var nodeFleet = Path.Combine(root, "node-fleet");
        var identity = DeviceIdentity.LoadOrCreate(nodeFleet);
        IReadOnlyList<string> addresses = [];
        FleetNodeService Service(CodeAltaHost on) => new(new FleetNodeServiceOptions
        {
            Host = on, Identity = identity, TrustedDevices = new TrustedDeviceStore(nodeFleet, TimeProvider.System), Name = "test-node", Version = "1.0.0",
            PairingAddresses = () => addresses, Automations = automations, AdmitBudget = admitBudget, TimeProvider = time ?? TimeProvider.System,
            AdmitEnvironment = admitEnvironment, IsPersonalProvider = isPersonalProvider,
            ModelServers = modelServers,
        });
        var service = Service(host);
        var listener = FleetDirectListener.Start(new IPEndPoint(IPAddress.Loopback, 0), service.ServeAsync);
        addresses = [FleetDirectConnector.Format(IPAddress.Loopback, listener.LocalEndPoint.Port)];
        return new FleetNodeFixture(root, host, service, listener, identity, provider, addresses)
        {
            _restart = Service,
            _appliesSpaceIdentities = appliesSpaceIdentities,
            _showsPromptSuggestions = showsPromptSuggestions,
        };
    }

    private bool _appliesSpaceIdentities;

    private bool _showsPromptSuggestions;

    /// <summary>
    /// Stops the node API and its host, then starts a fresh host on the same profile and the node API on it, as a node that
    /// restarts: nothing of the first process (its receipts, its caches) remains. The channels to the first are closed.
    /// </summary>
    public async Task RestartHostAsync(CancellationToken cancellationToken)
    {
        await _listener.DisposeAsync();
        await Service.DisposeAsync();
        await _host.DisposeAsync();
        _host = await CreateHostAsync(_root, Provider, _appliesSpaceIdentities, _showsPromptSuggestions, cancellationToken);
        Service = _restart!(_host);
        _listener = FleetDirectListener.Start(new IPEndPoint(IPAddress.Loopback, 0), Service.ServeAsync);
        Addresses = [FleetDirectConnector.Format(IPAddress.Loopback, _listener.LocalEndPoint.Port)];
    }

    /// <summary>Creates a real host on a profile under <paramref name="root"/>, its turns scripted by <paramref name="provider"/>.</summary>
    internal static Task<CodeAltaHost> CreateHostAsync(string root, ScriptedFleetProvider provider, bool appliesSpaceIdentities, CancellationToken cancellationToken)
        => CreateHostAsync(root, provider, appliesSpaceIdentities, showsPromptSuggestions: false, cancellationToken);

    /// <summary>Creates a real host as above that shows the next prompt a session suggests, as a desktop does, when asked.</summary>
    internal static Task<CodeAltaHost> CreateHostAsync(string root, ScriptedFleetProvider provider, bool appliesSpaceIdentities, bool showsPromptSuggestions,
        CancellationToken cancellationToken)
    {
        var global = Path.Combine(root, "global");
        var project = Path.Combine(root, "project");
        var home = Path.Combine(root, "home");
        var builtin = Path.Combine(root, "builtin");
        foreach (var path in new[] { global, project, home, builtin }) Directory.CreateDirectory(path);
        return CodeAltaHost.CreateAsync(new CodeAltaHostOptions
        {
            GlobalRoot = global, CurrentProjectPath = project, DiscoveryScope = new(home, root), BuiltInSkillRoot = builtin,
            PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, OwnsLogging = false, IsHeadless = true,
            ConfigureModelProviders = provider.Register, AppliesSpaceIdentities = appliesSpaceIdentities, ShowsPromptSuggestions = showsPromptSuggestions,
        }, cancellationToken);
    }

    /// <summary>Pairs the viewer with the node, as a person does with a pairing link.</summary>
    public async Task<PairedMachine> PairViewerAsync(CancellationToken cancellationToken)
    {
        var (offer, _) = Service.Pairing.CreateOffer("test-node", Addresses, TimeSpan.FromMinutes(1));
        return await FleetNodeClient.PairAsync(offer, ViewerIdentity, "viewer", ViewerMachines, cancellationToken);
    }

    /// <summary>A pairing link of the node, as <c>altanode pair --manage</c> prints it (<c>altanode pair</c> without <paramref name="manages"/>).</summary>
    /// <param name="manages">Whether the computer that redeems it manages the node, as its owner asked.</param>
    public string CreatePairingLink(bool manages = true) => Service.Pairing.CreateOffer("test-node", Addresses, TimeSpan.FromMinutes(1), manages).Offer.ToUri();

    public async ValueTask DisposeAsync()
    {
        await _listener.DisposeAsync();
        await Service.DisposeAsync();
        await _host.DisposeAsync();
        NodeIdentity.Dispose();
        ViewerIdentity.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>A provider whose turns a test scripts, run by the real agent loop of the node: each turn takes the next step, or answers <c>Done.</c>.</summary>
internal sealed class ScriptedFleetProvider
{
    internal const string Model = "scripted-model";

    // Settable, then registered again, for a test that configures the provider (its default permission mode).
    internal ModelProviderDescriptor Descriptor { get; set; } = new(new ModelProviderId("scripted"), "Scripted") { DefaultModelId = Model, IsDefault = true };

    internal ConcurrentQueue<Func<AgentTurnRequest, CancellationToken, Task<AgentTurnResponse>>> Turns { get; } = new();

    /// <summary>Gets the text of the last prompt of each turn, in order.</summary>
    internal ConcurrentQueue<string> Prompts { get; } = new();

    internal void Register(ModelProviderRegistry registry) => registry.RegisterOrReplace(Descriptor, () => new Runtime(this));

    internal static Func<AgentTurnRequest, CancellationToken, Task<AgentTurnResponse>> WaitThenReply(Task gate, string text, TaskCompletionSource? started = null)
        => async (_, cancellationToken) =>
        {
            started?.TrySetResult();
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return Answer(text);
        };

    internal static AgentTurnResponse Answer(string text)
        => new() { AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text(text)]) };

    private sealed class Runtime(ScriptedFleetProvider owner) : IAgentModelProviderRuntime
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
                Availability = ModelProviderAvailability.Ready,
                Models = [new AgentModelInfo(Model, DisplayName: Model, DefaultReasoningEffort: AgentReasoningEffort.High,
                    SupportedReasoningEfforts: [AgentReasoningEffort.Low, AgentReasoningEffort.High])],
                SelectedModelId = Model,
            });

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Executor(ScriptedFleetProvider owner) : IModelProviderTurnExecutor
    {
        public Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
            CancellationToken cancellationToken = default)
        {
            if (request.Conversation.LastOrDefault(static message => message.Role == AgentConversationRole.User) is { } user)
                owner.Prompts.Enqueue(string.Join("\n", user.Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value)));
            return owner.Turns.TryDequeue(out var step) ? step(request, cancellationToken) : Task.FromResult(Answer("Done."));
        }
    }
}
