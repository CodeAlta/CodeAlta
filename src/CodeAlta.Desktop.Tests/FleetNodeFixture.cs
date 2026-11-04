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
    private readonly CodeAltaHost _host;
    private readonly FleetDirectListener _listener;

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

    public FleetNodeService Service { get; }

    public DeviceIdentity NodeIdentity { get; }

    public ScriptedFleetProvider Provider { get; }

    public IReadOnlyList<string> Addresses { get; }

    public string ViewerFleetRoot { get; }

    public DeviceIdentity ViewerIdentity { get; }

    public PairedMachineStore ViewerMachines { get; }

    public static Task<FleetNodeFixture> StartAsync(CancellationToken cancellationToken) => StartAsync(cancellationToken, null);

    /// <summary>Starts the node, with the automations it runs for its devices; null for none.</summary>
    public static Task<FleetNodeFixture> StartAsync(CancellationToken cancellationToken, IFleetNodeAutomations? automations) => StartAsync(cancellationToken, automations, null);

    /// <summary>Starts the node, with the automations it runs for its devices and what admits its runs to budgets; null for none.</summary>
    public static async Task<FleetNodeFixture> StartAsync(CancellationToken cancellationToken, IFleetNodeAutomations? automations,
        Func<FleetNodeBudgetRun, Action, CancellationToken, Task<CodeAlta.Fleet.Server.FleetBudgetAdmission>>? admitBudget)
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-desktop-fleet-tests", Guid.NewGuid().ToString("N"));
        var global = Path.Combine(root, "global");
        var project = Path.Combine(root, "project");
        var home = Path.Combine(root, "home");
        var builtin = Path.Combine(root, "builtin");
        foreach (var path in new[] { global, project, home, builtin }) Directory.CreateDirectory(path);
        var provider = new ScriptedFleetProvider();
        var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
        {
            GlobalRoot = global, CurrentProjectPath = project, DiscoveryScope = new(home, root), BuiltInSkillRoot = builtin,
            PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, OwnsLogging = false, IsHeadless = true,
            ConfigureModelProviders = provider.Register,
        }, cancellationToken);
        var nodeFleet = Path.Combine(root, "node-fleet");
        var identity = DeviceIdentity.LoadOrCreate(nodeFleet);
        IReadOnlyList<string> addresses = [];
        var service = new FleetNodeService(new FleetNodeServiceOptions
        {
            Host = host, Identity = identity, TrustedDevices = new TrustedDeviceStore(nodeFleet, TimeProvider.System), Name = "test-node", Version = "1.0.0",
            PairingAddresses = () => addresses, Automations = automations, AdmitBudget = admitBudget,
        });
        var listener = FleetDirectListener.Start(new IPEndPoint(IPAddress.Loopback, 0), service.ServeAsync);
        addresses = [FleetDirectConnector.Format(IPAddress.Loopback, listener.LocalEndPoint.Port)];
        return new FleetNodeFixture(root, host, service, listener, identity, provider, addresses);
    }

    /// <summary>Pairs the viewer with the node, as a person does with a pairing link.</summary>
    public async Task<PairedMachine> PairViewerAsync(CancellationToken cancellationToken)
    {
        var (offer, _) = Service.Pairing.CreateOffer("test-node", Addresses, TimeSpan.FromMinutes(1));
        return await FleetNodeClient.PairAsync(offer, ViewerIdentity, "viewer", ViewerMachines, cancellationToken);
    }

    /// <summary>A pairing link of the node, as <c>altanode pair</c> prints it.</summary>
    public string CreatePairingLink() => Service.Pairing.CreateOffer("test-node", Addresses, TimeSpan.FromMinutes(1)).Offer.ToUri();

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
                Models = [new AgentModelInfo(Model, DisplayName: Model)],
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
