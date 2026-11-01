using System.Text.Json;
using CodeAlta.Browser.Discovery;
using CodeAlta.Browser.Tools;

namespace CodeAlta.Browser.Drivers;

/// <summary>A frame of a page: the main one or one of its frames, named by the protocol of the browser.</summary>
/// <param name="Key">The id of the frame (CDP) or of the browsing context (BiDi).</param>
internal sealed record BrowserFrame(string Key);

/// <summary>
/// Where the pointer goes: in the viewport of the page, which CDP takes, and in the viewport of the frame that holds
/// the element, which WebDriver BiDi takes for a frame of another process.
/// </summary>
/// <param name="X">The x coordinate in the viewport of the page.</param>
/// <param name="Y">The y coordinate in the viewport of the page.</param>
/// <param name="Frame">The frame the local coordinates are in.</param>
/// <param name="LocalX">The x coordinate in the viewport of that frame.</param>
/// <param name="LocalY">The y coordinate in the viewport of that frame.</param>
internal sealed record BrowserPoint(double X, double Y, BrowserFrame Frame, double LocalX, double LocalY)
{
    /// <summary>A point of the page itself.</summary>
    public static BrowserPoint OfPage(BrowserFrame main, double x, double y) => new(x, y, main, x, y);
}

/// <summary>A JavaScript dialog a page opened, which stops its scripts until it is answered.</summary>
/// <param name="Type">The kind: <c>alert</c>, <c>confirm</c>, <c>prompt</c> or <c>beforeunload</c>.</param>
/// <param name="Message">The message.</param>
/// <param name="DefaultValue">The default text of a prompt, or null.</param>
internal sealed record PageDialog(string Type, string Message, string? DefaultValue);

/// <summary>What to capture.</summary>
internal sealed record ScreenshotRequest
{
    /// <summary>Gets <c>png</c> or <c>jpeg</c>.</summary>
    public string Format { get; init; } = "png";

    /// <summary>Gets the quality of a JPEG, from 1 to 100, or null.</summary>
    public int? Quality { get; init; }

    /// <summary>Gets the box to capture in CSS pixels of the document, or null for the viewport.</summary>
    public (double X, double Y, double Width, double Height)? Clip { get; init; }

    /// <summary>Gets whether to capture the whole document beyond the viewport.</summary>
    public bool FullPage { get; init; }
}

/// <summary>The emulation of a page, which a call of <c>emulate</c> replaces whole.</summary>
internal sealed record EmulationSettings
{
    /// <summary>Gets the viewport, or null for none.</summary>
    public (int Width, int Height, double DeviceScaleFactor, bool Mobile, bool Touch, bool Landscape)? Viewport { get; init; }

    /// <summary>Gets <c>dark</c> or <c>light</c>, or null for the system's.</summary>
    public string? ColorScheme { get; init; }

    /// <summary>Gets the user agent, or null for the browser's.</summary>
    public string? UserAgent { get; init; }

    /// <summary>Gets the location, or null for none.</summary>
    public (double Latitude, double Longitude)? Geolocation { get; init; }

    /// <summary>Gets the network preset (<c>Offline</c>, <c>Slow 3G</c>…), or null for none.</summary>
    public string? NetworkConditions { get; init; }

    /// <summary>Gets the CPU slowdown, 1 for none.</summary>
    public double CpuThrottlingRate { get; init; } = 1;

    /// <summary>Gets the <c>forced-colors</c> media feature (<c>active</c>, <c>none</c>), or null for the system's.</summary>
    public string? ForcedColors { get; init; }

    /// <summary>Gets the <c>prefers-contrast</c> media feature, or null for the system's.</summary>
    public string? PrefersContrast { get; init; }

    /// <summary>Gets the <c>prefers-reduced-motion</c> media feature, or null for the system's.</summary>
    public string? PrefersReducedMotion { get; init; }

    /// <summary>Gets the headers added to every request, which a call keeps unless it gives others; null to keep them.</summary>
    public IReadOnlyDictionary<string, string>? ExtraHeaders { get; init; }
}

/// <summary>What a browser can do beyond what every browser does.</summary>
/// <param name="CpuThrottling">CPU throttling.</param>
/// <param name="NetworkThrottling">Network presets beyond offline.</param>
/// <param name="Tracing">Performance traces.</param>
/// <param name="HeapSnapshots">Heap snapshots.</param>
internal sealed record BrowserCapabilities(bool CpuThrottling, bool NetworkThrottling, bool Tracing, bool HeapSnapshots);

/// <summary>
/// One tab of a session in a browser. What a tool does to a page goes through here: the CDP and the BiDi drivers
/// implement it for their browsers. Coordinates are CSS pixels of the viewport of the main frame.
/// </summary>
internal interface IBrowserPage
{
    /// <summary>Gets the address of the main frame.</summary>
    string Url { get; }

    /// <summary>Gets the title.</summary>
    string Title { get; }

    /// <summary>Gets whether the tab is gone.</summary>
    bool IsClosed { get; }

    /// <summary>Gets the main frame.</summary>
    BrowserFrame MainFrame { get; }

    /// <summary>Gets the console messages and requests of the page.</summary>
    PageLog Log { get; }

    /// <summary>Gets the dialog the page has open, or null.</summary>
    PageDialog? Dialog { get; }

    /// <summary>Gets the number of documents the main frame started to load, which tells an action that navigated.</summary>
    long Navigations { get; }

    /// <summary>Loads an address and waits for its load event.</summary>
    Task NavigateAsync(string url, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Goes back (-1) or forward (+1) in the history and waits for the load.</summary>
    /// <exception cref="BrowserToolException">There is no such entry.</exception>
    Task TraverseAsync(int delta, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Loads the document again and waits for the load.</summary>
    Task ReloadAsync(bool ignoreCache, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Waits for a navigation that started after <paramref name="navigationsBefore"/>, or starts within
    /// <paramref name="startWindow"/>, to load.
    /// </summary>
    /// <returns>Whether a navigation happened.</returns>
    Task<bool> WaitForNavigationAsync(long navigationsBefore, TimeSpan startWindow, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Runs an operation of the in-page agent in the isolated world of a frame, and gives its value.</summary>
    /// <exception cref="BrowserToolException">The agent failed the operation; its message says why.</exception>
    Task<JsonElement> CallAgentAsync(BrowserFrame frame, string operation, string optionsJson, CancellationToken cancellationToken);

    /// <summary>The frame a frame element of a snapshot holds, or null when it holds none the browser can reach.</summary>
    Task<BrowserFrame?> ChildFrameAsync(BrowserFrame parent, string uid, CancellationToken cancellationToken);

    /// <summary>
    /// Runs a script of the caller in the page itself (its main world), with elements of the latest snapshot as the
    /// arguments of a function, and gives its result as JSON text, or null for <c>undefined</c>.
    /// </summary>
    Task<string?> EvaluateAsync(string source, bool isFunction, IReadOnlyList<(BrowserFrame Frame, string Uid)> arguments, CancellationToken cancellationToken);

    /// <summary>Adds a script that runs before the scripts of each new document, until removed with the result.</summary>
    Task<IAsyncDisposable> AddInitScriptAsync(string source, CancellationToken cancellationToken);

    /// <summary>Sets the files of a file input of the latest snapshot.</summary>
    Task SetFilesAsync(BrowserFrame frame, string uid, IReadOnlyList<string> paths, CancellationToken cancellationToken);

    /// <summary>Moves the pointer.</summary>
    Task MouseMoveAsync(BrowserPoint point, CancellationToken cancellationToken);

    /// <summary>Clicks, or double clicks, with the main button.</summary>
    Task ClickAsync(BrowserPoint point, int clickCount, CancellationToken cancellationToken);

    /// <summary>Presses the main button at a point, moves to another, and releases it there.</summary>
    Task DragAsync(BrowserPoint from, BrowserPoint to, CancellationToken cancellationToken);

    /// <summary>Presses or releases a key, with the modifiers held (CDP bits: Alt 1, Control 2, Meta 4, Shift 8).</summary>
    Task KeyAsync(BrowserKey key, bool down, int modifiers, CancellationToken cancellationToken);

    /// <summary>Types text into what has the focus, as an input method would.</summary>
    Task InsertTextAsync(string text, CancellationToken cancellationToken);

    /// <summary>Captures the page.</summary>
    Task<byte[]> ScreenshotAsync(ScreenshotRequest request, CancellationToken cancellationToken);

    /// <summary>Answers the open dialog.</summary>
    Task HandleDialogAsync(bool accept, string? promptText, CancellationToken cancellationToken);

    /// <summary>Answers each dialog the page opens with what a policy says, until the result is disposed.</summary>
    /// <param name="policy">Gives the answer to a dialog, or null to leave it open.</param>
    IDisposable AnswerDialogs(Func<PageDialog, (bool Accept, string? Text)?> policy);

    /// <summary>Reads the bodies of a request, as far as the browser still has them.</summary>
    Task<NetworkBodies> ReadBodiesAsync(NetworkEntry entry, CancellationToken cancellationToken);

    /// <summary>Gives the page a size, resizing its window; null when it cannot be read back.</summary>
    Task<(int Width, int Height)?> ResizeAsync(int width, int height, CancellationToken cancellationToken);

    /// <summary>Applies an emulation, replacing the one before.</summary>
    Task EmulateAsync(EmulationSettings settings, CancellationToken cancellationToken);

    /// <summary>Brings the tab to the front of its window.</summary>
    Task BringToFrontAsync(CancellationToken cancellationToken);

    /// <summary>Closes the tab.</summary>
    Task CloseAsync(CancellationToken cancellationToken);
}

/// <summary>The extra tools of a Chromium page.</summary>
internal interface IChromiumPage : IBrowserPage
{
    /// <summary>Starts a performance trace of the browser with the categories of DevTools.</summary>
    Task StartTraceAsync(CancellationToken cancellationToken);

    /// <summary>Stops the trace and writes it, as JSON, to a stream.</summary>
    Task StopTraceAsync(Stream destination, CancellationToken cancellationToken);

    /// <summary>Takes a heap snapshot of the page and writes it to a stream.</summary>
    Task TakeHeapSnapshotAsync(Stream destination, CancellationToken cancellationToken);
}

/// <summary>The tabs of one session in a browser: a context of its own, or a window of a shared profile.</summary>
internal interface IBrowserContext : IAsyncDisposable
{
    /// <summary>Gets the browser.</summary>
    InstalledBrowser Browser { get; }

    /// <summary>Gets what the browser can do beyond what every browser does.</summary>
    BrowserCapabilities Capabilities { get; }

    /// <summary>Gets the open tabs of the session.</summary>
    IReadOnlyList<IBrowserPage> Pages { get; }

    /// <summary>Gets whether the browser or the context is gone.</summary>
    bool IsClosed { get; }

    /// <summary>Raised when a tab of the session opens, including a popup of one of its tabs.</summary>
    event Action<IBrowserPage>? PageOpened;

    /// <summary>Opens a tab on <c>about:blank</c>.</summary>
    Task<IBrowserPage> NewPageAsync(bool background, CancellationToken cancellationToken);
}

/// <summary>
/// Decides whether a request of a tab of a session may go: the document of a site the user did not allow in their
/// own browser, a request to a port of CodeAlta. Called by the driver while the request waits.
/// </summary>
/// <param name="url">The address.</param>
/// <param name="kind">What the request loads: the page of the tab, the document of a frame in it, or a resource.</param>
/// <param name="cancellationToken">Ends the wait.</param>
/// <returns>Null to let it go, else why it is refused.</returns>
internal delegate ValueTask<string?> BrowserRequestGate(Uri url, BrowserRequestKind kind, CancellationToken cancellationToken);

/// <summary>What a request of a tab loads.</summary>
internal enum BrowserRequestKind
{
    /// <summary>A resource of a document: a script, a picture, a call of a script.</summary>
    Resource,

    /// <summary>The document of a frame inside the page.</summary>
    Frame,

    /// <summary>The page of the tab itself.</summary>
    Page,
}

/// <summary>How the tabs of a session are opened in a browser.</summary>
internal sealed record BrowserContextOptions
{
    /// <summary>Gets whether the session gets a context of its own, with no cookie or storage of anyone else.</summary>
    public bool Isolated { get; init; } = true;

    /// <summary>Gets the proxy of the requests of the context, as <c>host:port</c>, or null.</summary>
    public string? Proxy { get; init; }

    /// <summary>Gets the folder downloads go to, or null to refuse them.</summary>
    public string? DownloadDirectory { get; init; }

    /// <summary>Gets the gate of requests, or null.</summary>
    public BrowserRequestGate? Gate { get; init; }

    /// <summary>Gets whether the gate sees the documents of the tabs, not only the requests to the ports of CodeAlta.</summary>
    public bool GateDocuments { get; init; }

    /// <summary>Gets the ports of the loopback no request of the tabs may reach.</summary>
    public IReadOnlyList<int> BlockedPorts { get; init; } = [];
}

/// <summary>A browser CodeAlta drives: one it started, or the user's own it attached to.</summary>
internal interface IBrowserConnection : IAsyncDisposable
{
    /// <summary>Gets the browser.</summary>
    InstalledBrowser Browser { get; }

    /// <summary>Gets whether the browser or the connection is gone.</summary>
    bool IsClosed { get; }

    /// <summary>Opens the tabs of a session.</summary>
    Task<IBrowserContext> CreateContextAsync(BrowserContextOptions options, CancellationToken cancellationToken);
}
