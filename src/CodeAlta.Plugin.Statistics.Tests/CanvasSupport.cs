using System.Text.Json;
using CodeAlta.Plugin.Statistics.Canvas;
using CodeAlta.Plugin.Statistics.History;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Statistics.Tests;

/// <summary>The services of a plugin that has a window: a real database, the projects of <see cref="FakeAlta"/>, a canvas service and a button service that record what they are asked.</summary>
internal sealed class WindowServices(IPluginDatabase database) : IPluginServices
{
    private readonly NoopPluginServices _inner = NoopPluginServices.Create();

    public RecordingUi RecordingUi { get; } = new();

    public RecordingCanvases RecordingCanvases { get; } = new();

    public XenoAtom.Logging.Logger Logger => _inner.Logger;

    public IPluginUiService Ui => RecordingUi;

    public IPluginStateStore State => _inner.State;

    public IPluginDatabase Database { get; } = database;

    public IPluginWorkspaceService Workspace => _inner.Workspace;

    public IPluginSessionService Sessions => _inner.Sessions;

    public IPluginPromptService Prompts => _inner.Prompts;

    public IPluginAgentService Agents => _inner.Agents;

    public IPluginTaskService Tasks => _inner.Tasks;

    public IPluginAltaService Alta { get; } = new FakeAlta();

    public IPluginCanvasService Canvases => RecordingCanvases;

    /// <summary>Makes the context a host gives a plugin it activates.</summary>
    public PluginRuntimeContext Context(PluginFrontends frontend, Type? type = null) => new()
    {
        Plugin = PluginDescriptorFactory.FromType(type ?? typeof(StatisticsPlugin)),
        Host = new PluginHostInfo { ApplicationName = "CodeAlta", Version = "test", HostApiVersion = "1", UserDataDirectory = Path.GetTempPath(), Frontend = frontend, HasInteractiveUi = frontend != PluginFrontends.None },
        Logger = Logger,
        Services = this,
        PackageDirectory = Path.GetTempPath(),
    };
}

/// <summary>The ui service of a window: it counts how many times the plugin asked for its buttons to be read again.</summary>
internal sealed class RecordingUi : IPluginUiService
{
    private readonly NoopPluginUiService _inner = new();
    private int _invalidations;

    public int Invalidations => Volatile.Read(ref _invalidations);

    public bool HasInteractiveUi => true;

    public ValueTask NotifyAsync(string message, CancellationToken cancellationToken = default) => _inner.NotifyAsync(message, cancellationToken);

    public ValueTask<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default) => _inner.ConfirmAsync(title, message, cancellationToken);

    public ValueTask<string?> InputAsync(string title, string? initialText = null, CancellationToken cancellationToken = default) => _inner.InputAsync(title, initialText, cancellationToken);

    public ValueTask<string?> EditTextAsync(string title, string text, CancellationToken cancellationToken = default) => _inner.EditTextAsync(title, text, cancellationToken);

    public ValueTask<T?> SelectAsync<T>(string title, IReadOnlyList<PluginSelectItem<T>> items, CancellationToken cancellationToken = default) => _inner.SelectAsync(title, items, cancellationToken);

    public ValueTask ShowDialogAsync(PluginDialogRequest request, CancellationToken cancellationToken = default) => _inner.ShowDialogAsync(request, cancellationToken);

    public ValueTask<PluginDialogResponse?> ShowDialogForResultAsync(PluginDialogRequest request, CancellationToken cancellationToken = default) => _inner.ShowDialogForResultAsync(request, cancellationToken);

    public void InvalidateButtons() => Interlocked.Increment(ref _invalidations);
}

/// <summary>A canvas service that records the requests to open a canvas.</summary>
internal sealed class RecordingCanvases : IPluginCanvasService
{
    public List<(string CanvasId, PluginCanvasOpenOptions? Options)> Opened { get; } = [];

    public PluginCanvasOpenStatus Status { get; set; } = PluginCanvasOpenStatus.Requested;

    public bool HasInteractiveUi => true;

    public ValueTask<PluginCanvasOpenResult> OpenAsync(string canvasId, PluginCanvasOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        Opened.Add((canvasId, options));
        return ValueTask.FromResult(new PluginCanvasOpenResult(Status, "instance", "work", true));
    }

    public ValueTask<bool> CloseAsync(string instanceId, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

    public ValueTask InvalidateAsync(string canvasId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public IReadOnlyList<PluginCanvasInstanceInfo> GetOpen() => [];
}

/// <summary>The registry of the calls of a canvas, as the plugin sees it: the tests call its handlers the way the host does, with the record the script sent.</summary>
internal sealed class RecordingRpc : IPluginCanvasRpc
{
    private readonly Dictionary<string, Delegate> _handlers = new(StringComparer.Ordinal);

    public List<(string Name, JsonElement Value)> Events { get; } = [];

    public bool IsAvailable => true;

    public JsonSerializerOptions JsonOptions { get; set; } = new(JsonSerializerDefaults.Web);

    public IReadOnlyCollection<string> Names => _handlers.Keys;

    public void Handle<TRequest, TResult>(string name, PluginRpcHandler<TRequest, TResult> handler) => Add(name, handler);

    public void Handle<TRequest>(string name, PluginRpcVoidHandler<TRequest> handler) => Add(name, handler);

    public void Stream<TRequest, TItem>(string name, PluginRpcStreamHandler<TRequest, TItem> handler) => Add(name, handler);

    public ValueTask PublishAsync<T>(string name, T value, CancellationToken cancellationToken = default)
    {
        lock (Events)
        {
            Events.Add((name, JsonSerializer.SerializeToElement(value, JsonOptions)));
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Calls a handler with the JSON a script sent, read as the host reads it: with the options the plugin set.</summary>
    public async Task<JsonElement> InvokeAsync(string name, string json = "{}", CancellationToken cancellationToken = default)
    {
        var handler = (PluginRpcHandler<StatisticsCall, JsonElement>)_handlers[name];
        var call = (StatisticsCall)JsonSerializer.Deserialize(json, JsonOptions.GetTypeInfo(typeof(StatisticsCall)))!;
        return await handler(call, cancellationToken);
    }

    /// <summary>Gets the code of the error a call ends with, or null when it succeeds.</summary>
    public async Task<PluginRpcException?> FailureAsync(string name, string json = "{}", CancellationToken cancellationToken = default)
    {
        try
        {
            await InvokeAsync(name, json, cancellationToken);
            return null;
        }
        catch (PluginRpcException exception)
        {
            return exception;
        }
    }

    public List<(string Name, JsonElement Value)> TakeEvents()
    {
        lock (Events)
        {
            var taken = Events.ToList();
            Events.Clear();
            return taken;
        }
    }

    private void Add(string name, Delegate handler)
    {
        Assert.IsTrue(PluginRpc.IsValidName(name), name);
        Assert.IsTrue(_handlers.TryAdd(name, handler), $"{name} is registered twice");
    }
}

/// <summary>An instance of a canvas that is open, with the registry of its calls.</summary>
internal sealed class TestCanvas(RecordingRpc rpc, string? key = null) : PluginCanvasContext
{
    private readonly CancellationTokenSource _closed = new();
    private bool _visible = true;

    public override string InstanceId => "instance-1";

    public override string CanvasId => StatisticsPlugin.CanvasId;

    public override string? SpaceId => "work";

    public override string? ProjectId => null;

    public override string? SessionId => null;

    public override string? Key { get; } = key;

    public override JsonElement? Input => null;

    public override bool IsVisible => _visible;

    public override bool IsOpen => !_closed.IsCancellationRequested;

    public override CancellationToken Closed => _closed.Token;

    public override event Action<bool>? VisibilityChanged;

    public override IPluginCanvasRpc Rpc { get; } = rpc;

    public void SetVisible(bool visible)
    {
        _visible = visible;
        VisibilityChanged?.Invoke(visible);
    }

    public void Close() => _closed.Cancel();

    public override ValueTask SetTitleAsync(string? title, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public override ValueTask SetStatusAsync(string? status, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public override ValueTask UpdateAsync(string html, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public override ValueTask InvalidateAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}

/// <summary>A statistics plugin of the window over the database of a harness, activated, with its calls registered as a canvas registers them.</summary>
internal sealed class CanvasPluginHarness : IAsyncDisposable
{
    private readonly bool _ownsStore;

    private CanvasPluginHarness(StatisticsPlugin plugin, WindowServices services, StoreHarness store, FakeJournalCatalog journals, bool ownsStore)
    {
        _ownsStore = ownsStore;
        Plugin = plugin;
        Services = services;
        Store = store;
        Journals = journals;
    }

    public StatisticsPlugin Plugin { get; }

    public WindowServices Services { get; }

    public StoreHarness Store { get; }

    public FakeJournalCatalog Journals { get; }

    public RecordingRpc Rpc { get; private set; } = new();

    public TestCanvas Canvas { get; private set; } = null!;

    /// <summary>Makes the plugin of a window over an empty store, activated. The engine waits an hour before it reads: only what a test asks for happens.</summary>
    public static async Task<CanvasPluginHarness> CreateAsync(StoreHarness? store = null, TimeSpan? startDelay = null, PluginFrontends frontend = PluginFrontends.Desktop)
    {
        var owns = store is null;
        store ??= await StoreHarness.CreateAsync();
        var journals = new FakeJournalCatalog();
        var plugin = new StatisticsPlugin(journals, startDelay ?? TimeSpan.FromHours(1), TimeSpan.Zero);
        var services = new WindowServices(store.Database);
        plugin.AttachRuntimeContext(services.Context(frontend));
        await plugin.OnActivatedAsync();
        return new CanvasPluginHarness(plugin, services, store, journals, owns);
    }

    /// <summary>Opens the canvas the way the host does: the calls are registered, then the view comes back.</summary>
    public async Task<PluginCanvasView> OpenAsync(string? key = null)
    {
        Rpc = new RecordingRpc();
        Canvas = new TestCanvas(Rpc, key);
        var declaration = Plugin.GetCanvases().Single();
        return await declaration.Open(Canvas, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        Canvas?.Close();
        await Plugin.DisposeAsync();
        if (_ownsStore)
        {
            await Store.DisposeAsync();
        }
    }
}
