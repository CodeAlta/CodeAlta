using CodeAlta.Agent;

namespace CodeAlta.Plugin.Browser;

/// <summary>
/// The proxy a sandboxed session's browser goes through: the network of its sandbox profile.
/// </summary>
/// <param name="Address">The address of the proxy, as <c>127.0.0.1:&lt;port&gt;</c>.</param>
/// <param name="Owner">What stops the proxy once the session's browser is closed.</param>
public sealed record BrowserSandboxProxy(string Address, IAsyncDisposable Owner);

/// <summary>What the sandbox of a session lets its browser reach.</summary>
/// <param name="Profile">The name of the sandbox profile, for the answers.</param>
/// <param name="ReadRoots">The folders the session may read: the files a page may upload or open.</param>
/// <param name="WriteRoots">The folders the session may write: screenshots, traces.</param>
/// <param name="StartProxy">
/// Starts the proxy of the network of the profile; null when the profile lets the network reach everything, and the
/// browser then goes without one.
/// </param>
public sealed record BrowserSandbox(
    string Profile,
    IReadOnlyList<string> ReadRoots,
    IReadOnlyList<string> WriteRoots,
    Func<CancellationToken, ValueTask<BrowserSandboxProxy>>? StartProxy)
{
    /// <summary>Gets the folders and files never read or written, also inside the roots.</summary>
    public IReadOnlyList<string> Denied { get; init; } = [];

    /// <summary>Gets the folders and files never written, also inside the write roots: what other programs run later.</summary>
    public IReadOnlyList<string> ReadOnly { get; init; } = [];

    /// <summary>
    /// Gets a text that changes whenever what the sandbox reaches changes, its network included: a session's browser
    /// started with another one is closed. Null to compare the profile and the folders only.
    /// </summary>
    public string? Fingerprint { get; init; }

    /// <summary>Whether a session's browser started in one sandbox may go on in another: the same reach.</summary>
    public static bool SameReach(BrowserSandbox? before, BrowserSandbox? now)
        => before is null || now is null
            ? before is null && now is null
            : before.Profile == now.Profile && before.Fingerprint == now.Fingerprint
              && before.ReadRoots.SequenceEqual(now.ReadRoots) && before.WriteRoots.SequenceEqual(now.WriteRoots)
              && before.Denied.SequenceEqual(now.Denied) && before.ReadOnly.SequenceEqual(now.ReadOnly);
}

/// <summary>
/// What the application that hosts the <c>browser</c> plugin gives it: where its files go, and what it learns of a session
/// once the host that knows the sessions runs (its folder, its sandbox, how to ask its user).
/// </summary>
/// <remarks>
/// The delegates are null until the host is attached: a session is then not known, not sandboxed and cannot be asked.
/// </remarks>
public sealed class BrowserPluginHost
{
    private Func<string, CancellationToken, ValueTask<string?>>? _workFolder;
    private Func<string, CancellationToken, ValueTask<BrowserSandbox?>>? _sandbox;
    private Func<string, AgentPermissionRequest, CancellationToken, ValueTask<AgentPermissionDecision>>? _requestPermission;
    private Action<string>? _activated;
    private Func<IReadOnlyList<int>>? _reservedPorts;
    private Func<string, string, string?, CancellationToken, Task<CodeAlta.Browser.Tools.LoginFillGrant>>? _signIn;

    /// <summary>Creates what a host gives the plugin.</summary>
    /// <param name="stateRoot">The state root of the application: the browser keeps its profiles and files under <c>browser/</c> there.</param>
    /// <exception cref="ArgumentException"><paramref name="stateRoot"/> is blank.</exception>
    public BrowserPluginHost(string stateRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        StateRoot = Path.GetFullPath(stateRoot);
    }

    /// <summary>Gets the state root of the application.</summary>
    public string StateRoot { get; }

    /// <summary>
    /// Gets the folders of CodeAlta that hold its settings, credentials and sessions, which a page never reads and the
    /// server of the files of projects never serves, wherever they are and whatever their names: the global and state
    /// roots, which a separate profile puts anywhere (<see cref="CodeAlta.Browser.Tools.BrowserFiles.SecretRoots"/>).
    /// None when none are given: the folders are then known by their names alone (<c>.alta</c>).
    /// </summary>
    public IReadOnlyList<string> SecretRoots
    {
        get => _secretRoots ?? [];
        init => _secretRoots = [.. (value ?? throw new ArgumentNullException(nameof(value))).Where(static root => !string.IsNullOrWhiteSpace(root)).Select(Path.GetFullPath)];
    }

    private readonly IReadOnlyList<string>? _secretRoots;

    /// <summary>Gets the folder of the browser's own files: <c>&lt;state root&gt;/browser</c>.</summary>
    public string Root => Path.Combine(StateRoot, "browser");

    /// <summary>
    /// Gets whether a user is there to see the browser and answer its requests. Without one (<c>altacli</c>), the
    /// browser runs headless and the user's own browser is never used.
    /// </summary>
    public bool HasUser { get; init; } = true;

    /// <summary>Gets or sets how the folder a session works in is found: its worktree, else the folder of its project.</summary>
    public Func<string, CancellationToken, ValueTask<string?>>? WorkFolder
    {
        get => Volatile.Read(ref _workFolder);
        set => Volatile.Write(ref _workFolder, value);
    }

    /// <summary>Gets or sets how the sandbox of a session is found: null for a session that is not sandboxed.</summary>
    public Func<string, CancellationToken, ValueTask<BrowserSandbox?>>? Sandbox
    {
        get => Volatile.Read(ref _sandbox);
        set => Volatile.Write(ref _sandbox, value);
    }

    /// <summary>
    /// Gets or sets how the user of a session is asked a request no permission mode answers, on the card of the session,
    /// while a tool of the session runs. The answer is a denial when nobody can be asked.
    /// </summary>
    public Func<string, AgentPermissionRequest, CancellationToken, ValueTask<AgentPermissionDecision>>? RequestPermission
    {
        get => Volatile.Read(ref _requestPermission);
        set => Volatile.Write(ref _requestPermission, value);
    }

    /// <summary>
    /// Gets or sets what is told when a session turns the browser tools on: the window gives back its UI tools, which have
    /// the same names.
    /// </summary>
    public Action<string>? Activated
    {
        get => Volatile.Read(ref _activated);
        set => Volatile.Write(ref _activated, value);
    }

    /// <summary>
    /// Gets or sets the ports of this computer that a page never reaches: the ones of CodeAlta (its MCP server, the mobile
    /// view, the endpoints of agent CLIs).
    /// </summary>
    public Func<IReadOnlyList<int>>? ReservedPorts
    {
        get => Volatile.Read(ref _reservedPorts);
        set => Volatile.Write(ref _reservedPorts, value);
    }

    /// <summary>
    /// Gets or sets what gives the tool <c>fill_login</c> of a session the sign-in the user shared for a site: it gets the
    /// session, the origin of the top frame of the tab (read from the browser, never from the agent) and the sign-in the
    /// agent chose among several (or null); it finds the sign-ins of that origin, asks the user on the card of the session
    /// unless the user allowed that site already, and reads the values from the vault, masked. Null where the host keeps no
    /// sign-ins: the tool then answers <c>unsupported</c>. It is never called for the user's own browser or a sandboxed session.
    /// </summary>
    public Func<string, string, string?, CancellationToken, Task<CodeAlta.Browser.Tools.LoginFillGrant>>? SignIn
    {
        get => Volatile.Read(ref _signIn);
        set => Volatile.Write(ref _signIn, value);
    }

    private BrowserPlugin? _plugin;

    /// <summary>Takes the browser tools back from a session: the window gave it its UI tools, which have the same names.</summary>
    /// <param name="sessionId">The session.</param>
    public void ReleaseTools(string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId)) Volatile.Read(ref _plugin)?.Deactivate(sessionId);
    }

    // The plugin that runs: the one the runtime activated last.
    internal void Attach(BrowserPlugin plugin) => Volatile.Write(ref _plugin, plugin);

    internal void Detach(BrowserPlugin plugin) => Interlocked.CompareExchange(ref _plugin, null, plugin);

    /// <summary>Gets what the plugin reads of this computer to find browsers: tests give a fixture.</summary>
    internal CodeAlta.Browser.Discovery.BrowserEnvironment Environment { get; init; } = CodeAlta.Browser.Discovery.BrowserEnvironment.Current();

    /// <summary>Gets how long a session's browser stays open without a call of its tools.</summary>
    internal TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Gets what finds the program of a port of this computer: tests give a fixture.</summary>
    internal IPortOwners PortOwners { get; init; } = new SystemPortOwners();

    /// <summary>Gets how long a browser the plugin starts has to open its endpoint: tests on a busy machine give more.</summary>
    internal TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets whether the browser the user opens from the window runs without a window: for tests only.</summary>
    internal bool UserBrowserHeadless { get; init; }
}
