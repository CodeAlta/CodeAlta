using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Browser.Discovery;
using CodeAlta.Browser.Drivers;
using CodeAlta.Plugin.Browser;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.CommandLine;

namespace CodeAlta.Browser.Tests;

[TestClass]
public sealed class BrowserPluginTests
{
    [TestMethod]
    public void Sites_AreHostsWithoutWww_AndStandForTheirSubdomains()
    {
        Assert.AreEqual("github.com", BrowserSites.Normalize("https://www.GitHub.com/login?x=1"));
        Assert.AreEqual("github.com", BrowserSites.Normalize("www.github.com"));
        Assert.AreEqual("xn--bcher-kva.example", BrowserSites.Normalize("bücher.example"));
        Assert.IsNull(BrowserSites.Normalize("file:///C:/secret.txt"));
        Assert.IsNull(BrowserSites.Normalize("not a site"));
        Assert.IsTrue(BrowserSites.Matches("gist.github.com", ["github.com"]));
        Assert.IsFalse(BrowserSites.Matches("notgithub.com", ["github.com"]), "a site is never the end of another name");
        CollectionAssert.AreEqual(new[] { "a.com", "b.org" }, BrowserSites.NormalizeAll(["https://b.org/", "A.com", "www.a.com", "", "bad site"]).ToArray());
    }

    [TestMethod]
    public void Profiles_AreIsolatedPersistentOrUser()
    {
        Assert.IsTrue(BrowserProfileSpec.TryParse("persistent", out var persistent));
        Assert.AreEqual(new BrowserProfileSpec(BrowserProfileKind.Persistent, "default"), persistent);
        Assert.IsTrue(BrowserProfileSpec.TryParse("persistent:Work_2", out var named));
        Assert.AreEqual("persistent:work_2", named.ToString());
        Assert.IsTrue(BrowserProfileSpec.TryParse("USER", out var user));
        Assert.AreEqual(BrowserProfileKind.User, user.Kind);
        Assert.IsFalse(BrowserProfileSpec.TryParse("persistent:../x", out _));
        Assert.IsFalse(BrowserProfileSpec.TryParse("mine", out _));
        var settings = new BrowserSettings { Browser = " ", Profile = "nonsense", Engine = "webkit", UserProfile = new() { AllowedSites = ["WWW.Example.com", "example.com"] } }.Normalized();
        Assert.AreEqual("auto", settings.Browser);
        Assert.AreEqual("isolated", settings.Profile);
        Assert.IsNull(settings.Engine);
        CollectionAssert.AreEqual(new[] { "example.com" }, settings.UserProfile.AllowedSites.ToArray());
    }

    [TestMethod]
    public async Task Guards_KeepFilesToTheSessionFolders_AndThePortsOfCodeAltaOut()
    {
        using var folder = new TempFolder();
        var inside = Path.Combine(folder.Path, "work", "page.html");
        Directory.CreateDirectory(Path.GetDirectoryName(inside)!);
        await File.WriteAllTextAsync(inside, "<p>hi</p>");
        var host = Host(folder);
        host.WorkFolder = (_, _) => ValueTask.FromResult<string?>(Path.Combine(folder.Path, "work"));
        host.ReservedPorts = static () => [2583];
        await using var session = Session(host, BrowserProfileKind.Isolated, new BrowserSettings());
        Assert.IsNull(await session.CheckAsync(new Uri(inside), BrowserRequestKind.Page, asking: true, CancellationToken.None));
        StringAssert.Contains(await session.CheckAsync(new Uri(Path.Combine(folder.Path, "outside.html")), BrowserRequestKind.Page, true, CancellationToken.None), "outside the folders");
        StringAssert.Contains(await session.CheckAsync(new Uri(Path.Combine(folder.Path, "work", ".env")), BrowserRequestKind.Page, true, CancellationToken.None), "secrets");
        StringAssert.Contains(await session.CheckAsync(new Uri("http://127.0.0.1:2583/mcp"), BrowserRequestKind.Resource, false, CancellationToken.None), "port of CodeAlta");
        StringAssert.Contains(await session.CheckAsync(new Uri("http://localhost:2583/"), BrowserRequestKind.Page, true, CancellationToken.None), "port of CodeAlta");
        Assert.IsNull(await session.CheckAsync(new Uri("http://127.0.0.1:2584/"), BrowserRequestKind.Page, true, CancellationToken.None));
        StringAssert.Contains(await session.CheckAsync(new Uri("ftp://example.com/"), BrowserRequestKind.Page, true, CancellationToken.None), "not loaded");
        Assert.IsNull(await session.CheckAsync(new Uri("https://example.com/"), BrowserRequestKind.Page, true, CancellationToken.None), "the isolated profile asks about no site");
    }

    [TestMethod]
    public async Task UserBrowser_AsksEachSiteOnce_RemembersTheAnswer_AndNeverAsksForAFrame()
    {
        using var folder = new TempFolder();
        var host = Host(folder);
        var asked = new ConcurrentQueue<string?>();
        host.RequestPermission = (_, request, _) =>
        {
            var site = ((AgentBrowserPermissionRequest)request).Site;
            asked.Enqueue(site);
            return ValueTask.FromResult(new AgentPermissionDecision(site switch
            {
                "always.com" => AgentPermissionDecisionKind.AllowAlways,
                "once.com" => AgentPermissionDecisionKind.AllowOnce,
                _ => AgentPermissionDecisionKind.Deny,
            }));
        };
        var kept = new List<string>();
        var settings = new BrowserSettings { UserProfile = new() { Enabled = true, AllowedSites = ["github.com"], BlockedSites = ["bank.com"] } };
        await using var session = Session(host, BrowserProfileKind.User, settings, kept);
        Assert.IsNull(await session.CheckAsync(new Uri("https://gist.github.com/"), BrowserRequestKind.Page, true, CancellationToken.None), "allowed in Settings, subdomains included");
        StringAssert.Contains(await session.CheckAsync(new Uri("https://www.bank.com/"), BrowserRequestKind.Page, true, CancellationToken.None), "blocked");
        Assert.IsNull(await session.CheckAsync(new Uri("https://always.com/a"), BrowserRequestKind.Page, true, CancellationToken.None));
        Assert.IsNull(await session.CheckAsync(new Uri("https://always.com/b"), BrowserRequestKind.Page, true, CancellationToken.None));
        Assert.IsNull(await session.CheckAsync(new Uri("https://once.com/"), BrowserRequestKind.Page, true, CancellationToken.None));
        StringAssert.Contains(await session.CheckAsync(new Uri("https://no.com/"), BrowserRequestKind.Page, true, CancellationToken.None), "did not allow");
        StringAssert.Contains(await session.CheckAsync(new Uri("https://no.com/again"), BrowserRequestKind.Page, true, CancellationToken.None), "did not allow");
        StringAssert.Contains(await session.CheckAsync(new Uri("https://ads.example/frame"), BrowserRequestKind.Frame, false, CancellationToken.None), "load it with navigate_page");
        StringAssert.Contains(await session.CheckAsync(new Uri("https://later.com/"), BrowserRequestKind.Page, false, CancellationToken.None), "load it with navigate_page",
            "a page a tab loads by itself while no tool runs is refused without a question");
        Assert.IsNull(await session.CheckAsync(new Uri("https://cdn.anything/x.js"), BrowserRequestKind.Resource, false, CancellationToken.None), "resources are not sites");
        CollectionAssert.AreEqual(new[] { "always.com", "once.com", "no.com" }, asked.ToArray(), "each site is asked once");
        CollectionAssert.AreEqual(new[] { "always.com" }, kept, "only an answer for always is kept in the settings");
        CollectionAssert.AreEqual(new[] { "always.com", "once.com" }, session.AllowedSites.ToArray());
    }

    [TestMethod]
    public async Task UserBrowser_IsRefusedWhileTheSwitchIsOff_AndASandboxedSessionBrowsesIsolatedOnly()
    {
        using var folder = new TempFolder();
        var host = new BrowserPluginHost(folder.Path) { HasUser = true };
        await using var plugin = await StartAsync(host, new BrowserSettings());
        var (exit, records) = await RunAsync(plugin, folder, "s1", ["browser", "activate", "--profile", "user"]);
        Assert.AreEqual(5, exit);
        StringAssert.Contains(records[^1].GetProperty("message").GetString(), "Settings > Extensions > Browser");
        await using var headless = await StartAsync(Host(folder), new BrowserSettings { UserProfile = new() { Enabled = true } });
        (exit, records) = await RunAsync(headless, folder, "s1", ["browser", "activate", "--profile", "user"]);
        Assert.AreEqual(5, exit);
        StringAssert.Contains(records[^1].GetProperty("message").GetString(), "no user to agree");
        host.Sandbox = (_, _) => ValueTask.FromResult<BrowserSandbox?>(new BrowserSandbox("strict", [folder.Path], [folder.Path], null));
        (exit, records) = await RunAsync(plugin, folder, "s1", ["browser", "activate", "--profile", "persistent"]);
        Assert.AreEqual(5, exit);
        StringAssert.Contains(records[^1].GetProperty("message").GetString(), "isolated profile only");
        Assert.IsFalse(plugin.IsActive("s1"));
    }

    [TestMethod]
    public void TheBrowsersOfCodeAlta_SendWebRtcThroughTheProxyOnly_AndResolveNoNameAhead()
    {
        var options = new CodeAlta.Browser.Launch.BrowserLaunchOptions { Executable = "chrome", ProfileDirectory = "profile" };
        var arguments = CodeAlta.Browser.Launch.ChromiumLauncher.Arguments(options);
        CollectionAssert.Contains(arguments.ToArray(), "--force-webrtc-ip-handling-policy=disable_non_proxied_udp");
        CollectionAssert.Contains(arguments.ToArray(), "--dns-prefetch-disable");
        var userJs = CodeAlta.Browser.Launch.FirefoxLauncher.UserJs(options);
        StringAssert.Contains(userJs, "user_pref(\"media.peerconnection.ice.proxy_only\", true);");
        StringAssert.Contains(userJs, "user_pref(\"network.dns.disablePrefetch\", true);");
    }

    [TestMethod]
    public void ASandboxIsTheSameOnlyWithTheSameReach()
    {
        BrowserSandbox Sandbox(string fingerprint, string folder = "C:/work") => new("strict", [folder], [folder], null) { Fingerprint = fingerprint };
        Assert.IsTrue(BrowserSandbox.SameReach(null, null));
        Assert.IsTrue(BrowserSandbox.SameReach(Sandbox("a"), Sandbox("a")));
        Assert.IsFalse(BrowserSandbox.SameReach(Sandbox("a"), Sandbox("b")), "the same profile with other hosts or ports");
        Assert.IsFalse(BrowserSandbox.SameReach(Sandbox("a"), Sandbox("a", "C:/other")), "the same profile with other folders");
        Assert.IsFalse(BrowserSandbox.SameReach(Sandbox("a"), null));
        Assert.IsFalse(BrowserSandbox.SameReach(null, Sandbox("a")));
    }

    [TestMethod]
    public void Guidance_TellsTheAgentWhenToNameABrowser()
    {
        var text = BrowserPlugin.Guidance(null);
        StringAssert.Contains(text, "alta browser activate");
        StringAssert.Contains(text, "only when the user or the task names a browser");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Activate_GivesTheToolsOfTheBrowser_WhichBrowseUntilTheSessionGivesThemBack(bool firefox)
    {
        var engine = firefox ? BrowserEngine.Firefox : BrowserEngine.Chromium;
        if (BrowserTestHost.Find(engine) is not { } browser)
        {
            Assert.Inconclusive($"No {engine} browser is installed.");
            return;
        }

        using var folder = new TempFolder();
        await using var site = await TestSite.StartAsync();
        var host = Host(folder);
        var activated = new List<string>();
        host.Activated = activated.Add;
        await using var plugin = await StartAsync(host, new BrowserSettings { Browser = browser.Id });
        var (exit, records) = await RunAsync(plugin, folder, "s1", ["browser", "activate"]);
        Assert.AreEqual(0, exit, records[^1].ToString());
        var activation = records[^1];
        Assert.AreEqual(browser.Id, activation.GetProperty("browser").GetString());
        Assert.AreEqual("settings", activation.GetProperty("source").GetString());
        Assert.AreEqual("now", activation.GetProperty("toolsAvailable").GetString());
        Assert.IsTrue(activation.GetProperty("headless").GetBoolean(), "a host without a user browses headless");
        CollectionAssert.AreEqual(new[] { "s1" }, activated, "the window gives back its UI tools");
        var names = activation.GetProperty("tools").EnumerateArray().Select(static name => name.GetString()).ToArray();
        CollectionAssert.Contains(names, "navigate_page");
        Assert.AreEqual(engine == BrowserEngine.Chromium, names.Contains("performance_start_trace"), "the Chromium tools come with Chromium only");

        var tools = plugin.CreateTools(plugin.SessionOf("s1")!);
        var navigated = await CallAsync(tools, "s1", "navigate_page", $$"""{"type":"url","url":"{{site.Origin}}/form"}""");
        Assert.IsTrue(navigated.Success, navigated.Error);
        StringAssert.Contains(Text(navigated), "Sign in · Demo");
        var status = await RunAsync(plugin, folder, "s1", ["browser", "status"]);
        Assert.AreEqual(1, status.Records[^1].GetProperty("pages").GetInt32());
        Assert.AreEqual("Browser", BrowserPlugin.StatusItem(plugin.SessionOf("s1")!).Label);
        StringAssert.Contains(BrowserPlugin.StatusItem(plugin.SessionOf("s1")!).Text, "1 tab");
        Assert.AreEqual(1, plugin.Pool.Running.Count);

        // Another session browses apart: its own context, its own tabs.
        Assert.AreEqual(0, (await RunAsync(plugin, folder, "s2", ["browser", "activate"])).Exit);
        var other = await CallAsync(plugin.CreateTools(plugin.SessionOf("s2")!), "s2", "list_pages", "{}");
        Assert.IsFalse(Text(other).Contains("/form", StringComparison.Ordinal), Text(other));
        Assert.AreEqual(1, plugin.Pool.Running.Count, "the sessions share one browser");

        Assert.AreEqual(0, (await RunAsync(plugin, folder, "s1", ["browser", "deactivate"])).Exit);
        Assert.AreEqual(0, (await RunAsync(plugin, folder, "s2", ["browser", "deactivate"])).Exit);
        var refused = await CallAsync(tools, "s1", "list_pages", "{}");
        Assert.IsFalse(refused.Success);
        StringAssert.Contains(refused.Error, "alta browser activate");
        Assert.AreEqual(0, plugin.Pool.Running.Count, "the browser ends with its last session");
        Assert.IsFalse(Directory.Exists(plugin.Pool.TemporaryRoot) && Directory.EnumerateDirectories(plugin.Pool.TemporaryRoot).Any(), "the isolated profile leaves no folder");
        Assert.IsFalse(Directory.Exists(Path.Combine(host.Root, "downloads", "s1")), "nor an empty folder of downloads");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SandboxedSession_ReachesOnlyTheNetworkAndTheFoldersOfItsSandbox(bool firefox)
    {
        var engine = firefox ? BrowserEngine.Firefox : BrowserEngine.Chromium;
        if (BrowserTestHost.Find(engine) is not { } browser)
        {
            Assert.Inconclusive($"No {engine} browser is installed.");
            return;
        }

        using var folder = new TempFolder();
        await using var allowed = await TestSite.StartAsync();
        await using var other = await TestSite.StartAsync();
        var host = Host(folder);
        var work = Directory.CreateDirectory(Path.Combine(folder.Path, "work")).FullName;
        await File.WriteAllTextAsync(Path.Combine(work, "page.html"), "<title>Inside</title><p>inside</p>");
        await File.WriteAllTextAsync(Path.Combine(folder.Path, "outside.html"), "<title>Outside</title>");
        host.WorkFolder = (_, _) => ValueTask.FromResult<string?>(work);
        host.ReservedPorts = static () => [2583];
        var proxies = 0;
        // The profile of the strict sandbox: no network, but the local port of the session's own server.
        host.Sandbox = (_, _) => ValueTask.FromResult<BrowserSandbox?>(new BrowserSandbox("strict", [work], [work], _ =>
        {
            Interlocked.Increment(ref proxies);
            var proxy = CodeAlta.Sandbox.Network.SandboxNetworkProxy.Start([], [allowed.Port], [2583]);
            return ValueTask.FromResult(new BrowserSandboxProxy(new Uri(proxy.Endpoint).Authority, proxy));
        }));
        await using var plugin = await StartAsync(host, new BrowserSettings { Browser = browser.Id });
        var (exit, records) = await RunAsync(plugin, folder, "s1", ["browser", "activate"]);
        Assert.AreEqual(0, exit, records[^1].ToString());
        Assert.AreEqual("strict", records[^1].GetProperty("sandbox").GetString());
        var tools = plugin.CreateTools(plugin.SessionOf("s1")!);

        var own = await CallAsync(tools, "s1", "navigate_page", $$"""{"type":"url","url":"{{allowed.Origin}}/form"}""");
        Assert.IsTrue(own.Success, own.Error);
        StringAssert.Contains(Text(own), "Sign in · Demo", "the local port of the profile is reached");
        Assert.AreEqual(1, proxies);

        var refused = await CallAsync(tools, "s1", "navigate_page", $$"""{"type":"url","url":"{{other.Origin}}/form"}""");
        Assert.IsFalse(Text(refused).Contains("Sign in · Demo", StringComparison.Ordinal), "another local port is not reached: " + Text(refused));
        var internet = await CallAsync(tools, "s1", "navigate_page", """{"type":"url","url":"http://example.com/"}""");
        Assert.IsFalse(Text(internet).Contains("Example Domain", StringComparison.Ordinal), "a profile without network reaches no host: " + Text(internet));

        var inside = await CallAsync(tools, "s1", "navigate_page", $$"""{"type":"url","url":"{{new Uri(Path.Combine(work, "page.html")).AbsoluteUri}}"}""");
        Assert.IsTrue(inside.Success, inside.Error);
        StringAssert.Contains(Text(inside), "Inside");
        var outside = await CallAsync(tools, "s1", "navigate_page", $$"""{"type":"url","url":"{{new Uri(Path.Combine(folder.Path, "outside.html")).AbsoluteUri}}"}""");
        Assert.IsFalse(outside.Success);
        StringAssert.Contains(outside.Error, "outside the folders");
        var mcp = await CallAsync(tools, "s1", "navigate_page", """{"type":"url","url":"http://127.0.0.1:2583/mcp"}""");
        Assert.IsFalse(mcp.Success);
        StringAssert.Contains(mcp.Error, "port of CodeAlta");

        // A sandbox that changes takes the browser back.
        host.Sandbox = (_, _) => ValueTask.FromResult<BrowserSandbox?>(null);
        var changed = await CallAsync(tools, "s1", "list_pages", "{}");
        Assert.IsFalse(changed.Success);
        StringAssert.Contains(changed.Error, "sandbox of this session changed");
        Assert.IsFalse(plugin.IsActive("s1"));
    }

    [TestMethod]
    public async Task IdleTabs_Close_AndTheNextCallOpensThemAgain()
    {
        if (BrowserTestHost.Find(BrowserEngine.Chromium) is not { } browser)
        {
            Assert.Inconclusive("No Chromium browser is installed.");
            return;
        }

        using var folder = new TempFolder();
        await using var site = await TestSite.StartAsync();
        await using var plugin = await StartAsync(new BrowserPluginHost(folder.Path) { HasUser = false, IdleTimeout = TimeSpan.FromSeconds(2) }, new BrowserSettings { Browser = browser.Id });
        Assert.AreEqual(0, (await RunAsync(plugin, folder, "s1", ["browser", "activate"])).Exit);
        var tools = plugin.CreateTools(plugin.SessionOf("s1")!);
        Assert.IsTrue((await CallAsync(tools, "s1", "navigate_page", $$"""{"type":"url","url":"{{site.Origin}}/form"}""")).Success);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (plugin.SessionOf("s1")!.IsOpen && DateTime.UtcNow < deadline) await Task.Delay(200);
        Assert.IsFalse(plugin.SessionOf("s1")!.IsOpen, "the tabs closed while no tool was called");
        Assert.IsTrue(plugin.IsActive("s1"), "the session keeps its tools");
        var again = await CallAsync(tools, "s1", "navigate_page", $$"""{"type":"url","url":"{{site.Origin}}/second"}""");
        Assert.IsTrue(again.Success, again.Error);
    }

    // -------------------------------------------------------------------------------------------------------------

    private static BrowserPluginHost Host(TempFolder folder) => new(folder.Path) { HasUser = false };

    private static BrowserAgentSession Session(BrowserPluginHost host, BrowserProfileKind kind, BrowserSettings settings, List<string>? kept = null)
    {
        var browser = new InstalledBrowser("chrome", "Google Chrome", BrowserEngine.Chromium, "chrome.exe", "150.0", null, [new BrowserProfile("Person 1", "x", true)]);
        var hosted = kind == BrowserProfileKind.User ? new BrowserPluginHost(host.StateRoot) { HasUser = true, WorkFolder = host.WorkFolder, RequestPermission = host.RequestPermission, ReservedPorts = host.ReservedPorts } : host;
        return new BrowserAgentSession(hosted, new BrowserPool(hosted), "s1", new BrowserTarget(browser, new BrowserProfileSpec(kind, null), true), BrowserChoiceSource.Auto, "test", null,
            () => settings, (site, _) =>
            {
                kept?.Add(site);
                return ValueTask.CompletedTask;
            }, host.StateRoot);
    }

    private static async Task<BrowserPlugin> StartAsync(BrowserPluginHost host, BrowserSettings settings)
    {
        var state = new MemoryState();
        await state.WriteJsonAsync(PluginStateScope.User, "settings.json", settings);
        var services = new Services(state);
        var plugin = new BrowserPlugin(host);
        plugin.AttachRuntimeContext(new PluginRuntimeContext
        {
            Plugin = PluginDescriptorFactory.FromType(typeof(BrowserPlugin)),
            Host = new PluginHostInfo { ApplicationName = "CodeAlta", Version = "test", HostApiVersion = "1", UserDataDirectory = host.StateRoot, Frontend = PluginFrontends.Cli },
            Logger = services.Logger,
            Services = services,
            PackageDirectory = host.StateRoot,
        });
        await plugin.OnActivatedAsync();
        return plugin;
    }

    private static async Task<(int Exit, IReadOnlyList<JsonElement> Records)> RunAsync(BrowserPlugin plugin, TempFolder folder, string sessionId, string[] args)
    {
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var context = new PluginAltaCommandContext
        {
            Plugin = PluginDescriptorFactory.FromType(typeof(BrowserPlugin)),
            Services = new Services(new MemoryState()),
            CorrelationId = "test",
            WorkingDirectory = folder.Path,
            SourceSessionId = sessionId,
            RunTools = new AgentRunTools(),
            Stdin = TextReader.Null,
            Stdout = stdout,
            Stderr = stderr,
        };
        var app = new CommandApp("alta", "test") { plugin.GetAltaCommands().Single().CreateCommandNode(context) };
        var exit = await app.RunAsync(args, new CommandRunConfig { Out = TextWriter.Null, Error = stderr });
        var records = stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        Assert.AreNotEqual(0, records.Length, $"No record: {stderr}");
        return (exit, records);
    }

    private static async Task<AgentToolResult> CallAsync(IReadOnlyList<AgentToolDefinition> tools, string sessionId, string name, string arguments)
    {
        using var document = JsonDocument.Parse(arguments);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var tool = tools.Single(tool => tool.Spec.Name == name);
        return await tool.Handler(new AgentToolInvocation(new ModelProviderId("test"), sessionId, "call", name, document.RootElement.Clone()), timeout.Token);
    }

    private static string Text(AgentToolResult result) => string.Join('\n', result.Items.OfType<AgentToolResultItem.Text>().Select(static item => item.Value));

    /// <summary>A folder of a test, deleted after it.</summary>
    internal sealed class TempFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codealta-browser-plugin", Guid.NewGuid().ToString("N")[..8])).FullName;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The state of the plugin, kept as JSON in memory.</summary>
    private sealed class MemoryState : IPluginStateStore
    {
        private readonly ConcurrentDictionary<string, string> _files = new(StringComparer.Ordinal);

        public string GetDirectory(PluginStateScope scope) => System.IO.Path.GetTempPath();

        public ValueTask<T?> ReadJsonAsync<T>(PluginStateScope scope, string name, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_files.TryGetValue(name, out var text) ? JsonSerializer.Deserialize<T>(text) : default);

        public ValueTask WriteJsonAsync<T>(PluginStateScope scope, string name, T value, CancellationToken cancellationToken = default)
        {
            _files[name] = JsonSerializer.Serialize(value);
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(PluginStateScope scope, string name, CancellationToken cancellationToken = default)
        {
            _files.TryRemove(name, out _);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>The services of a host, with the state in memory.</summary>
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

        public IPluginAltaService Alta => _inner.Alta;
    }
}
