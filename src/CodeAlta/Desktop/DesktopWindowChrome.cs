using System.Text;
using NeoAstra;
using NeoAstra.Desktop;
using NeoAstra.Desktop.Opener;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop;

/// <summary>
/// The chromeless main window. The page draws the title bar (logo, session tabs) over the whole window while
/// the platform keeps its frame, resize borders and window controls; the page learns the title-bar layout and
/// minimizes, maximizes and closes the window through NeoAstra's desktop handlers.
/// </summary>
internal sealed class DesktopWindowChrome : IAsyncDisposable
{
    /// <summary>Height of the title bar in CSS pixels; the session tab strip uses the same height.</summary>
    internal const int TitleBarHeight = 38;

    // Of the desktop services, the application's page may only drive its own window.
    private const string Capabilities = """{"$schema":"neoastra-capabilities-v1.schema.json","version":1,"capabilities":[{"id":"main-window","views":["main"],"permissions":["window:management","window:close"]}]}""";

    private readonly NeoDesktopServices _services;
    private readonly NeoPluginHost _plugins;
    private readonly NeoCapabilityManifest _capabilities;

    private DesktopWindowChrome(NeoDesktopServices services, NeoPluginHost plugins, NeoCapabilityManifest capabilities)
    {
        _services = services;
        _plugins = plugins;
        _capabilities = capabilities;
    }

    /// <summary>Options of the main window: default placement and a title bar handed to the page.</summary>
    internal static NeoWindowOptions WindowOptions() => WindowOptions(developer: false);

    /// <summary>Options of the main window; the developer instance says so in its title.</summary>
    internal static NeoWindowOptions WindowOptions(bool developer) => DesktopWindowPlacement.Apply(new NeoWindowOptions
    {
        Label = "main", Title = developer ? "CodeAlta (dev)" : "CodeAlta", IsVisible = false,
        TitleBar = new NeoWindowTitleBar(NeoWindowTitleBarStyle.Overlay) { Height = TitleBarHeight },
    });

    /// <summary>
    /// Options of the main view. The application owns its shortcuts and menus, so the browser's find, print,
    /// reload and zoom keys, its context menu and its status bubble are off; editing keys keep working.
    /// </summary>
    internal static NeoAstraOptions ViewOptions() => new()
    {
        ViewLabel = "main",
        BrowserFeatures = NeoBrowserFeatures.ApplicationShell(),
        BridgePolicy = OperatingSystem.IsLinux() ? NeoBridgePolicy.TrustEntireView : NeoBridgePolicy.TrustedOrigins,
        BridgeOrigins = OperatingSystem.IsLinux() ? [] : ["app://codealta"],
    };

    /// <summary>Starts the desktop services behind the window handlers.</summary>
    /// <param name="application">The running application.</param>
    /// <param name="dataRoot">The application data root; desktop services keep their private files below it.</param>
    internal static async ValueTask<DesktopWindowChrome> StartAsync(NeoApplication application, string dataRoot)
    {
        ArgumentNullException.ThrowIfNull(application);
        var capabilities = ResolveCapabilities();
        var services = CreateServices(dataRoot, application.Dispatcher);
        var plugins = new NeoPluginBuilder().AddNeoAstraDesktop(services).Build();
        await plugins.StartAsync(application).ConfigureAwait(true);
        return new DesktopWindowChrome(services, plugins, capabilities);
    }

    /// <summary>
    /// Creates the desktop services. The window handlers are the only ones the page uses; the opener scopes
    /// cannot be empty, so they hold the services' own private directory and the project site, and nothing of
    /// the user's.
    /// </summary>
    internal static NeoDesktopServices CreateServices(string dataRoot, NeoDispatcher? dispatcher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var privateData = Path.Combine(dataRoot, "desktop");
        Directory.CreateDirectory(privateData);
        return NeoDesktopServices.CreateSystem("org.codealta.desktop", "CodeAlta", DesktopCommandLine.Version,
            privateData, ["https://codealta.github.io"], [privateData], [privateData], [NeoOpenFileIntent.TextDocument], dispatcher);
    }

    /// <summary>Resolves the capabilities of the main view for this platform.</summary>
    internal static NeoCapabilityManifest ResolveCapabilities() => NeoCapabilityManifest.Resolve(Encoding.UTF8.GetBytes(Capabilities),
        new NeoPermissionCatalogBuilder().AddNeoAstraDesktopPermissions().Build(),
        new NeoCapabilityResolutionOptions
        {
            Platform = OperatingSystem.IsWindows() ? NeoCapabilityPlatform.Windows
                : OperatingSystem.IsMacOS() ? NeoCapabilityPlatform.MacOS : NeoCapabilityPlatform.Linux,
            Release = true, Profile = NeoSecurityProfile.ProductionLocalApp,
        });

    /// <summary>
    /// Gives the window the application's icon, which the task switcher (Alt+Tab) shows. A native window
    /// does not take the icon of its executable by itself. A missing icon file or an unsupported platform
    /// leaves the default icon.
    /// </summary>
    internal async ValueTask ApplyWindowIconAsync(NeoWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var icon = Path.Combine(AppContext.BaseDirectory, "alta.ico");
        if (!File.Exists(icon)) return;
        try { await _services.WindowPolish.SetIconAsync(window, icon).ConfigureAwait(true); }
        catch (Exception exception) when (exception is not OperationCanceledException) { /* The default icon stays. */ }
    }

    /// <summary>Lets the main view call the desktop handlers, which are denied without an authorization service.</summary>
    internal NeoRpcOptions Authorize(NeoRpcOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.CapabilityManifest = _capabilities;
        options.SecurityProfile = _capabilities.Profile;
        options.AuthorizationService = new NeoCapabilityAuthorizationService(_capabilities, trustUnconfiguredViews: true);
        return options;
    }

    /// <summary>Registers the desktop handlers (window state, title bar, minimize, maximize, close).</summary>
    internal void AddHandlers(NeoRpcBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddNeoAstraDesktopHandlers(_services, new NeoDesktopRendererOptions());
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _plugins.DisposeAsync().ConfigureAwait(false);
        await _services.DisposeAsync().ConfigureAwait(false);
    }
}
