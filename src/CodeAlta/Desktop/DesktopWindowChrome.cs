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
    internal static NeoWindowOptions WindowOptions(bool developer) => WindowOptions(developer, DesktopAppearance.Default);

    /// <summary>
    /// Options of the main window in the theme it last had: the window is painted in that background from
    /// the moment it is shown, before its view and the page exist.
    /// </summary>
    internal static NeoWindowOptions WindowOptions(bool developer, DesktopAppearance appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        return DesktopWindowPlacement.Apply(new NeoWindowOptions
        {
            Label = "main", Title = developer ? "CodeAlta (dev)" : "CodeAlta", IsVisible = false,
            BackgroundColor = appearance.Background,
            TitleBar = TitleBar(appearance),
        });
    }

    /// <summary>
    /// The title bar the page draws over, with window controls whose symbols suit the page's theme: light
    /// on a dark theme and dark on a light one. Left to the platform they follow the system's theme, which
    /// makes them invisible when the page's theme is the other one.
    /// </summary>
    internal static NeoWindowTitleBar TitleBar(DesktopAppearance appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        return new NeoWindowTitleBar(NeoWindowTitleBarStyle.Overlay)
        {
            Height = TitleBarHeight,
            SymbolColor = appearance.Dark ? new NeoColor(0xf6, 0xf7, 0xf9, 0xff) : new NeoColor(0x1c, 0x21, 0x27, 0xff),
        };
    }

    /// <summary>
    /// Options of the main view. The application owns its shortcuts and menus, so the browser's find, print,
    /// reload and zoom keys, its context menu and its status bubble are off; editing keys keep working.
    /// So is the navigation of its history: the back and forward buttons of a mouse or a keyboard, a swipe
    /// and <c>history.back()</c> leave the view on the document it shows, where they would bring the start-up
    /// screen again. The Tab key stops on links on every platform.
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
    /// The icon file beside the application for a platform. On macOS it is a picture whose tile keeps the
    /// margin of the system's icon grid: the Dock draws a picture that the application sets as large as its
    /// canvas, so the full-bleed tile of the other desktops would look bigger than its neighbours.
    /// </summary>
    internal static string WindowIconFile(bool windows, bool macOS) => windows ? "alta.ico" : macOS ? "alta-dock.png" : "alta.png";

    /// <summary>
    /// Whether the running application gives the system its own picture as icon. Not on macOS when it was
    /// started as an application bundle: the Dock already draws the icon of the bundle, to which macOS 26
    /// gives the shape and the edge of its neighbours, and a picture that the application sets replaces it.
    /// A process started by its executable has the icon of a plain executable without one. And the Dock
    /// keeps the picture it has of an application that runs: a bundle whose icon this start replaced (the
    /// first start after an update) shows the old one until the next start, unless the application sets it.
    /// </summary>
    /// <param name="macOS">Whether the platform is macOS.</param>
    /// <param name="bundleIdentifier">The identifier of the bundle the process was started as; null for none.</param>
    /// <param name="bundleIconChanged">This start gave the bundle another icon than it had.</param>
    internal static bool AppliesWindowIcon(bool macOS, string? bundleIdentifier, bool bundleIconChanged)
        => !macOS || bundleIdentifier is null || bundleIconChanged;

    /// <summary>
    /// Gives the window the application's icon, which the task switcher (Alt+Tab) and the Dock show. A native
    /// window does not take the icon of its executable by itself. A missing icon file or an unsupported
    /// platform leaves the default icon, and so does <c>CodeAlta.app</c> on macOS, which has its own.
    /// </summary>
    internal ValueTask ApplyWindowIconAsync(NeoWindow window) => ApplyWindowIconAsync(window, bundleIconChanged: false);

    /// <inheritdoc cref="ApplyWindowIconAsync(NeoWindow)"/>
    /// <param name="window">The window of the application.</param>
    /// <param name="bundleIconChanged">This start gave the macOS bundle another icon than it had.</param>
    internal async ValueTask ApplyWindowIconAsync(NeoWindow window, bool bundleIconChanged)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (OperatingSystem.IsMacOS() && !AppliesWindowIcon(macOS: true, DesktopIntegration.MacRunningBundleIdentifier(), bundleIconChanged)) return;
        var icon = Path.Combine(AppContext.BaseDirectory, WindowIconFile(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS()));
        if (!File.Exists(icon)) return;
        try { await _services.WindowPolish.SetIconAsync(window, icon).ConfigureAwait(true); }
        catch (Exception exception) when (exception is not OperationCanceledException) { /* The default icon stays. */ }
    }

    /// <summary>The desktop services behind the handlers: the host itself uses their tray and menu commands.</summary>
    internal NeoDesktopServices Services => _services;

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
