using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Mobile.Tests;

/// <summary>The settings page of the Mobile plugin: what it declares, and how a change of the ports moves the server and what Tailscale serves.</summary>
[TestClass]
public sealed class MobileSettingsTests
{
    [TestMethod]
    public void ThePlugin_DeclaresItsTab_AndItsSettingsPage()
    {
        var canvases = new MobilePlugin(new FakeTailscaleCli()).GetCanvases().ToArray();

        Assert.AreEqual(2, canvases.Length);
        var tab = canvases.Single(static canvas => canvas.Id == MobilePlugin.CanvasId);
        Assert.AreEqual(PluginCanvasPlacement.Tab, tab.Placement);
        var settings = canvases.Single(static canvas => canvas.Id == MobilePlugin.SettingsCanvasId);
        Assert.AreEqual(("Mobile", "smartphone", PluginCanvasScope.Application, PluginCanvasPlacement.Settings), (settings.Title, settings.Icon, settings.Scope, settings.Placement));
        Assert.IsFalse(string.IsNullOrWhiteSpace(settings.Description));
    }

    [TestMethod]
    public async Task TheTab_HasTheStatusAndTheRetry_AndTheSettingsPage_TheSettingsAndTheReset()
    {
        await using var fixture = new Fixture(new FakeTailscaleCli(installed: false));
        await fixture.StartAsync(FreePort.Next(), 4443);

        var (tab, tabView) = await fixture.OpenAsync(MobilePlugin.CanvasId);
        var (page, pageView) = await fixture.OpenAsync(MobilePlugin.SettingsCanvasId);

        CollectionAssert.AreEquivalent(new[] { "mobile.status", "mobile.retry" }, tab.Names.ToArray());
        CollectionAssert.AreEquivalent(new[] { "mobile.settings.get", "mobile.settings.set", "mobile.reset" }, page.Names.ToArray());
        Assert.AreEqual(MobilePlugin.AppModule, tabView.Script?.AppModule);
        Assert.AreEqual(MobilePlugin.SettingsModule, pageView.Script?.AppModule);
    }

    [TestMethod]
    public async Task PortsOutOfRange_AreRefused_AndNothingChanges()
    {
        var cli = new FakeTailscaleCli(installed: false);
        await using var fixture = new Fixture(cli);
        var port = FreePort.Next();
        await fixture.StartAsync(port, 4443);
        var saved = fixture.State.Text;
        var (page, _) = await fixture.OpenAsync(MobilePlugin.SettingsCanvasId);

        foreach (var (local, https) in new (int?, int?)[] { (1023, 4443), (65536, 4443), (null, 4443), (port, 80), (port, 70000), (port, null) })
        {
            var answer = await page.InvokeAsync<MobilePorts, MobileSettingsView>("mobile.settings.set", new MobilePorts(local, https));
            Assert.IsNotNull(answer.Error, $"{local} and {https} are refused");
            StringAssert.Contains(answer.Error, "1024 to 65535");
            Assert.AreEqual((port, 4443), (answer.Port, answer.HttpsPort), "the ports of now come back with the refusal");
        }

        Assert.AreEqual(saved, fixture.State.Text, "nothing is saved");
        Assert.AreEqual("listening", fixture.Plugin.Settings().Server);
        Assert.IsTrue(MobilePlugin.IsPort(1024) && MobilePlugin.IsPort(65535) && !MobilePlugin.IsPort(0));
    }

    [TestMethod]
    public async Task NewPorts_AreSaved_TheServerMoves_AndTailscaleServesTheNewPortsAfterTheOldOnesAreTakenOff()
    {
        var (oldPort, newPort) = (FreePort.Next(), FreePort.Next());
        var cli = new FakeTailscaleCli()
            .Answer("status --json", FakeTailscaleCli.Running)
            .Answer("serve status --json", "{}")
            .Answer($"serve --bg --https=4443 http://127.0.0.1:{oldPort}", "");
        await using var fixture = new Fixture(cli);
        await fixture.StartAsync(oldPort, 4443);
        Assert.AreEqual("https://desk.tail1234.ts.net:4443", fixture.Plugin.Settings().Address);
        var access = await fixture.AccessAsync();
        var phone = (await access.SignInAsync(await access.PairAsync(CancellationToken.None), CancellationToken.None))!;
        var (page, _) = await fixture.OpenAsync(MobilePlugin.SettingsCanvasId);

        // Tailscale now serves the old ports: they are what is taken off.
        cli.Answer("serve status --json", Served(4443, oldPort))
            .Answer("serve --https=4443 off", "")
            .Answer($"serve --bg --https=5443 http://127.0.0.1:{newPort}", "");
        var answer = await page.InvokeAsync<MobilePorts, MobileSettingsView>("mobile.settings.set", new MobilePorts(newPort, 5443));

        Assert.IsNull(answer.Error);
        Assert.AreEqual((newPort, 5443), (answer.Port, answer.HttpsPort));
        Assert.AreEqual("listening", answer.Server, "the server answers on its new port at once");
        Assert.IsNull(answer.Address, "the view is not served on the tailnet until Tailscale says so");
        Assert.AreEqual("pending", answer.Tailscale.State, "the answer is the change itself, whatever Tailscale answers meanwhile");
        await WaitAsync(() => fixture.Plugin.Settings().Tailscale.State == "serving", "Tailscale serves the new ports");
        Assert.AreEqual("https://desk.tail1234.ts.net:5443", fixture.Plugin.Settings().Address);

        var calls = cli.Calls.ToList();
        var off = calls.IndexOf("serve --https=4443 off");
        var on = calls.IndexOf($"serve --bg --https=5443 http://127.0.0.1:{newPort}");
        Assert.IsTrue(off >= 0 && on > off, "the old handler is taken off before the new one is published: " + string.Join(" | ", calls));
        Assert.IsTrue(await ListensAsync(newPort), "the server listens on the new port");
        Assert.IsFalse(await ListensAsync(oldPort), "and no longer on the old one");
        var saved = fixture.State.Read();
        Assert.AreEqual((newPort, 5443), (saved.Port, saved.HttpsPort), "the ports are saved");
        Assert.IsTrue((await fixture.AccessAsync()).IsPhone(phone), "the phones stay signed in");
        Assert.IsTrue(page.Events.Any(static change => change.Name == "mobile.settings.changed"), "the page hears the change");
    }

    [TestMethod]
    public async Task ANewTailnetPort_KeepsTheServer_AndMovesWhatTailscaleServes()
    {
        var port = FreePort.Next();
        var cli = new FakeTailscaleCli()
            .Answer("status --json", FakeTailscaleCli.Running)
            .Answer("serve status --json", "{}")
            .Answer($"serve --bg --https=4443 http://127.0.0.1:{port}", "");
        await using var fixture = new Fixture(cli);
        await fixture.StartAsync(port, 4443);
        var (page, _) = await fixture.OpenAsync(MobilePlugin.SettingsCanvasId);

        cli.Answer("serve status --json", Served(4443, port))
            .Answer("serve --https=4443 off", "")
            .Answer($"serve --bg --https=6443 http://127.0.0.1:{port}", "");
        await page.InvokeAsync<MobilePorts, MobileSettingsView>("mobile.settings.set", new MobilePorts(port, 6443));
        await WaitAsync(() => fixture.Plugin.Settings().Address == "https://desk.tail1234.ts.net:6443", "Tailscale serves the new port");

        Assert.IsTrue(cli.Calls.Contains("serve --https=4443 off"));
        Assert.IsTrue(await ListensAsync(port));
    }

    [TestMethod]
    public async Task TheSamePorts_ChangeNothing()
    {
        var port = FreePort.Next();
        var cli = new FakeTailscaleCli(installed: false);
        await using var fixture = new Fixture(cli);
        await fixture.StartAsync(port, 4443);
        var (page, _) = await fixture.OpenAsync(MobilePlugin.SettingsCanvasId);
        var writes = fixture.State.Writes;

        var answer = await page.InvokeAsync<MobilePorts, MobileSettingsView>("mobile.settings.set", new MobilePorts(port, 4443));

        Assert.IsNull(answer.Error);
        Assert.AreEqual(writes, fixture.State.Writes, "nothing is written");
        Assert.AreEqual(0, page.Events.Count);
    }

    [TestMethod]
    public async Task TheReset_GivesANewPairingToken_SignsEveryPhoneOut_AndKeepsThePorts()
    {
        var port = FreePort.Next();
        await using var fixture = new Fixture(new FakeTailscaleCli(installed: false));
        await fixture.StartAsync(port, 4443);
        var (tab, _) = await fixture.OpenAsync(MobilePlugin.CanvasId);
        await tab.InvokeAsync<MobileEmpty, MobileStatus>("mobile.status", new MobileEmpty());
        var access = await fixture.AccessAsync();
        var before = access.PairingToken!;
        var phone = (await access.SignInAsync(before, CancellationToken.None))!;
        var (page, _) = await fixture.OpenAsync(MobilePlugin.SettingsCanvasId);

        var answer = await page.InvokeAsync<MobileEmpty, MobileSettingsView>("mobile.reset", new MobileEmpty());

        access = await fixture.AccessAsync();
        Assert.AreNotEqual(before, fixture.Plugin.PairingToken);
        Assert.IsTrue(MobileToken.IsValid(fixture.Plugin.PairingToken));
        Assert.IsNull(await access.SignInAsync(before, CancellationToken.None), "the code of before no longer signs in");
        Assert.IsFalse(access.IsPhone(phone), "the phones of before are signed out");
        Assert.AreEqual((port, 4443), (answer.Port, answer.HttpsPort));
        Assert.IsFalse(JsonSerializer.Serialize(answer, page.JsonOptions).Contains(fixture.Plugin.PairingToken!, StringComparison.Ordinal), "the page never gets the token");
    }

    [TestMethod]
    public async Task TheFiles_KeepNoSecret_TheTabMakesThePairingToken_AndARestartMakesANewOneForTheSamePhones()
    {
        var port = FreePort.Next();
        var state = new MemoryState();
        string token, phone;
        await using (var fixture = new Fixture(new FakeTailscaleCli(installed: false), state))
        {
            await fixture.StartAsync(port, 4443);
            Assert.IsNull(fixture.Plugin.PairingToken, "no pairing token until the tab shows the code");
            var (tab, _) = await fixture.OpenAsync(MobilePlugin.CanvasId);
            await tab.InvokeAsync<MobileEmpty, MobileStatus>("mobile.status", new MobileEmpty());
            token = fixture.Plugin.PairingToken!;
            Assert.IsTrue(MobileToken.IsValid(token));
            phone = (await (await fixture.AccessAsync()).SignInAsync(token, CancellationToken.None))!;
            Assert.AreNotEqual(token, phone, "a phone gets a key of its own");
        }

        Assert.IsFalse(state.AllText.Contains(token, StringComparison.Ordinal), "the pairing token is not on disk");
        Assert.IsFalse(state.AllText.Contains(phone, StringComparison.Ordinal), "the key of the phone is not on disk");
        StringAssert.Contains(state.AllText, MobileAccess.Hash(phone));

        await using var restarted = new Fixture(new FakeTailscaleCli(installed: false), state);
        await restarted.StartAsync(port, 4443, write: false);
        var access = await restarted.AccessAsync();
        Assert.IsTrue(access.IsPhone(phone), "a phone signed in stays signed in after a restart");
        Assert.IsNull(restarted.Plugin.PairingToken);
        var (again, _) = await restarted.OpenAsync(MobilePlugin.CanvasId);
        await again.InvokeAsync<MobileEmpty, MobileStatus>("mobile.status", new MobileEmpty());
        Assert.AreNotEqual(token, restarted.Plugin.PairingToken, "the tab makes a new pairing token");
        Assert.IsNull(await access.SignInAsync(token, CancellationToken.None), "the pairing token of before no longer signs in");
        Assert.IsNull(await access.SignInAsync("0123456789abcdefghijklmnopqrstuvwxyzABCDEFG", CancellationToken.None), "a wrong token is refused");
        Assert.IsNull(await access.SignInAsync(MobileAccess.Hash(phone), CancellationToken.None), "the hash on disk signs nothing in");
    }

    [TestMethod]
    public async Task AFileOfBefore_WithTheTokenItself_IsMigrated_AndItsPhonesStaySignedIn()
    {
        var port = FreePort.Next();
        const string Legacy = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFG";
        var state = new MemoryState();
        await state.WriteJsonAsync(PluginStateScope.User, "mobile.json", new MobileSettings { Token = Legacy, Port = port, HttpsPort = 4443 });
        await using var fixture = new Fixture(new FakeTailscaleCli(installed: false), state);

        await fixture.StartAsync(port, 4443, write: false);

        Assert.IsFalse(state.AllText.Contains(Legacy, StringComparison.Ordinal), "the token leaves the files");
        Assert.IsNull(state.Read().Token);
        Assert.AreEqual((port, 4443), (state.Read().Port, state.Read().HttpsPort), "the ports stay");
        var access = await fixture.AccessAsync();
        Assert.IsTrue(access.IsPhone(Legacy), "the cookie of a phone of before still signs it in");
        Assert.AreEqual(Legacy, await access.SignInAsync(Legacy, CancellationToken.None), "and so does the token it kept");
        Assert.IsFalse(access.IsPhone("ZYXWVUTSRQPONMLKJIHGFEDCBAzyxwvutsrqponmlkji"), "a wrong key is refused");
    }

    [TestMethod]
    public async Task AHostThatCannotSayTheAsks_StopsNothing_AndTheAsksOfBeforeAreKept()
    {
        var asks = new FailingAsks();
        await using var fixture = new Fixture(new FakeTailscaleCli(installed: false), asks: asks);
        await fixture.StartAsync(FreePort.Next(), 4443);

        Assert.AreEqual("s1", (await fixture.Plugin.ReadAskingAsync(CancellationToken.None))!.Single().SessionId);
        asks.Fail = true;
        Assert.IsNull(await fixture.Plugin.ReadAskingAsync(CancellationToken.None), "a failure keeps the asks of the last flush");
        Assert.IsNull(await fixture.Plugin.ReadAskingAsync(CancellationToken.None), "and does not stop the next reading");
        asks.Fail = false;
        Assert.IsNotNull(await fixture.Plugin.ReadAskingAsync(CancellationToken.None));
    }

    private sealed class FailingAsks : IPluginAskService
    {
        internal bool Fail { get; set; }

        public bool HasAsks => true;

        public ValueTask<IReadOnlyList<PluginAskSummary>> ListAskingAsync(CancellationToken cancellationToken = default)
            => Fail ? throw new InvalidOperationException("The host closes.") : ValueTask.FromResult<IReadOnlyList<PluginAskSummary>>([new("s1", "ask-1", "0", "pending", false)]);

        public ValueTask<PluginAsk?> GetPendingAsync(string sessionId, CancellationToken cancellationToken = default) => ValueTask.FromResult<PluginAsk?>(null);

        public ValueTask<PluginAskOutcome> AnswerAsync(PluginAskReference ask, IReadOnlyList<PluginAskAnswer> answers, PluginAskReviewAnswer? review, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new PluginAskOutcome("unavailable"));

        public ValueTask<PluginAskOutcome> CancelAsync(PluginAskReference ask, CancellationToken cancellationToken = default) => ValueTask.FromResult(new PluginAskOutcome("unavailable"));
    }

    // What `tailscale serve status --json` says when the HTTPS port serves the server of the plugin on its local port.
    private static string Served(int httpsPort, int localPort)
        => """{"Web":{"desk.tail1234.ts.net:HTTPS":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:LOCAL"}}}}}"""
            .Replace("HTTPS", httpsPort.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("LOCAL", localPort.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static async Task WaitAsync(Func<bool> condition, string what)
    {
        var end = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.IsTrue(DateTime.UtcNow < end, "Waited for " + what);
            await Task.Delay(20);
        }
    }

    private static async Task<bool> ListensAsync(int port)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync("127.0.0.1", port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>The plugin of a window, attached to services of the tests and started on the ports a test gives.</summary>
    private sealed class Fixture(FakeTailscaleCli cli, MemoryState? state = null, IPluginAskService? asks = null) : IAsyncDisposable
    {
        internal MobilePlugin Plugin { get; } = new(cli);

        internal MemoryState State { get; } = state ?? new();

        // The access of the plugin, read from its files when it started.
        internal Task<MobileAccess> AccessAsync() => Task.FromResult(Plugin.Access!);

        internal async Task StartAsync(int port, int httpsPort, bool write = true)
        {
            if (write) await State.WriteJsonAsync(PluginStateScope.User, "mobile.json", new MobileSettings { Port = port, HttpsPort = httpsPort });
            var services = new Services(State) { Asks = asks ?? NoopPluginAskService.Instance };
            Plugin.AttachRuntimeContext(new PluginRuntimeContext
            {
                Plugin = PluginDescriptorFactory.FromType(typeof(MobilePlugin)),
                Host = new PluginHostInfo { ApplicationName = "CodeAlta", Version = "test", HostApiVersion = "1", UserDataDirectory = Path.GetTempPath(), Frontend = PluginFrontends.Desktop, HasInteractiveUi = true },
                Logger = services.Logger,
                Services = services,
                PackageDirectory = Path.GetTempPath(),
            });
            await Plugin.StartAsync(CancellationToken.None);
        }

        internal async Task<(RecordingRpc Rpc, PluginCanvasView View)> OpenAsync(string canvasId)
        {
            var rpc = new RecordingRpc();
            var view = await Plugin.GetCanvases().Single(canvas => canvas.Id == canvasId).Open(new TestCanvas(rpc, canvasId), CancellationToken.None);
            return (rpc, view);
        }

        public async ValueTask DisposeAsync() => await Plugin.DisposeAsync();
    }

    /// <summary>The services a window gives the plugin, with the state in memory.</summary>
    private sealed class Services(MemoryState state) : IPluginServices
    {
        private readonly NoopPluginServices _inner = NoopPluginServices.Create();

        public XenoAtom.Logging.Logger Logger => _inner.Logger;

        public IPluginUiService Ui => _inner.Ui;

        public IPluginStateStore State => state;

        public IPluginWorkspaceService Workspace => _inner.Workspace;

        public IPluginSessionService Sessions => _inner.Sessions;

        public IPluginPromptService Prompts => _inner.Prompts;

        public IPluginAgentService Agents => _inner.Agents;

        public IPluginTaskService Tasks => _inner.Tasks;

        public IPluginAltaService Alta { get; } = new FakeAlta();

        public IPluginSpaceService Spaces { get; } = new FakeSpaces("default");

        public IPluginAppFileService AppFiles { get; } = new FakeFiles().Add("mobile.html", "<!doctype html><p>Phone</p>", "text/html");

        public IPluginAskService Asks { get; init; } = NoopPluginAskService.Instance;
    }

    /// <summary>The state of the plugin, kept as JSON in memory.</summary>
    internal sealed class MemoryState : IPluginStateStore
    {
        private readonly ConcurrentDictionary<string, string> _files = new(StringComparer.Ordinal);
        private int _writes;

        internal int Writes => Volatile.Read(ref _writes);

        internal string? Text => _files.GetValueOrDefault("mobile.json");

        /// <summary>Every file of the plugin, as JSON.</summary>
        internal string AllText => string.Join("\n", _files.Values);

        internal MobileSettings Read() => JsonSerializer.Deserialize<MobileSettings>(Text!)!;

        public string GetDirectory(PluginStateScope scope) => Path.GetTempPath();

        public ValueTask<T?> ReadJsonAsync<T>(PluginStateScope scope, string name, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_files.TryGetValue(name, out var text) ? JsonSerializer.Deserialize<T>(text) : default);

        public ValueTask WriteJsonAsync<T>(PluginStateScope scope, string name, T value, CancellationToken cancellationToken = default)
        {
            _files[name] = JsonSerializer.Serialize(value);
            Interlocked.Increment(ref _writes);
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(PluginStateScope scope, string name, CancellationToken cancellationToken = default)
        {
            _files.TryRemove(name, out _);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>The registry of the calls of a page: a test calls its handlers as the host does, and reads the events it published.</summary>
    internal sealed class RecordingRpc : IPluginCanvasRpc
    {
        private readonly Dictionary<string, Delegate> _handlers = new(StringComparer.Ordinal);

        internal ConcurrentQueue<(string Name, JsonElement Value)> Events { get; } = new();

        public bool IsAvailable => true;

        public JsonSerializerOptions JsonOptions { get; set; } = new(JsonSerializerDefaults.Web);

        public IReadOnlyCollection<string> Names => _handlers.Keys;

        public void Handle<TRequest, TResult>(string name, PluginRpcHandler<TRequest, TResult> handler) => Add(name, handler);

        public void Handle<TRequest>(string name, PluginRpcVoidHandler<TRequest> handler) => Add(name, handler);

        public void Stream<TRequest, TItem>(string name, PluginRpcStreamHandler<TRequest, TItem> handler) => Add(name, handler);

        public ValueTask PublishAsync<T>(string name, T value, CancellationToken cancellationToken = default)
        {
            Events.Enqueue((name, JsonSerializer.SerializeToElement(value, JsonOptions.GetTypeInfo(typeof(T)))));
            return ValueTask.CompletedTask;
        }

        /// <summary>Calls a handler with a request that went through JSON, as the one a script sends does.</summary>
        internal async Task<TResult> InvokeAsync<TRequest, TResult>(string name, TRequest request)
        {
            var json = JsonSerializer.Serialize(request, JsonOptions.GetTypeInfo(typeof(TRequest)));
            var read = (TRequest)JsonSerializer.Deserialize(json, JsonOptions.GetTypeInfo(typeof(TRequest)))!;
            return await ((PluginRpcHandler<TRequest, TResult>)_handlers[name])(read, CancellationToken.None);
        }

        private void Add(string name, Delegate handler)
        {
            Assert.IsTrue(PluginRpc.IsValidName(name), name);
            Assert.IsTrue(_handlers.TryAdd(name, handler), $"{name} is registered twice");
        }
    }

    /// <summary>An instance of a page that is open.</summary>
    private sealed class TestCanvas(RecordingRpc rpc, string canvasId) : PluginCanvasContext
    {
        private readonly CancellationTokenSource _closed = new();

        public override string InstanceId => "instance-" + canvasId;

        public override string CanvasId => canvasId;

        public override string? SpaceId => null;

        public override string? ProjectId => null;

        public override string? SessionId => null;

        public override string? Key => null;

        public override JsonElement? Input => null;

        public override bool IsVisible => true;

        public override bool IsOpen => !_closed.IsCancellationRequested;

        public override CancellationToken Closed => _closed.Token;

        public override event Action<bool>? VisibilityChanged { add { } remove { } }

        public override IPluginCanvasRpc Rpc { get; } = rpc;

        public override ValueTask SetTitleAsync(string? title, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public override ValueTask SetStatusAsync(string? status, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public override ValueTask UpdateAsync(string html, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public override ValueTask InvalidateAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
