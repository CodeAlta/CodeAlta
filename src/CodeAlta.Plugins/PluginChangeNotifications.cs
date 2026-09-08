namespace CodeAlta.Plugins;

/// <summary>
/// Describes plugin change notification options.
/// </summary>
public sealed record PluginChangeNotificationOptions
{
    /// <summary>Gets a value indicating whether interactive toasts may be shown.</summary>
    public bool Interactive { get; init; }

    /// <summary>Gets a value indicating whether headless fallback diagnostics should be collected.</summary>
    public bool HeadlessLogging { get; init; } = true;
}

/// <summary>
/// Describes a coalesced plugin source-change notification.
/// </summary>
public sealed record PluginChangeNotification
{
    /// <summary>Gets changed plugin package ids.</summary>
    public IReadOnlyList<string> PackageIds { get; init; } = [];

    /// <summary>Gets a value indicating whether a root rescan is required.</summary>
    public bool RequiresRescan { get; init; }

    /// <summary>Gets the display message.</summary>
    public required string Message { get; init; }

    /// <summary>Gets the compact footer/status text.</summary>
    public required string FooterStatus { get; init; }
}

/// <summary>
/// Coalesces plugin source changes into toast/status/headless notifications without stealing focus.
/// </summary>
public sealed class PluginChangeNotificationService
{
    private readonly PluginChangeNotificationOptions _options;
    private readonly Action<string>? _toastSink;
    private readonly List<string> _headlessMessages = [];
    private readonly object _gate = new();
    private PluginChangeNotification? _lastNotification;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginChangeNotificationService"/> class.
    /// </summary>
    /// <param name="options">Notification options.</param>
    /// <param name="toastSink">Borrowed host sink, required for interactive notifications; no implicit terminal fallback.</param>
    /// <remarks>The sink still runs under the existing gate after status publication. Its exception
    /// escapes without rolling back status; this service neither dispatches UI work nor owns the sink.</remarks>
    /// <exception cref="ArgumentNullException">Interactive options require a non-null <paramref name="toastSink"/>.</exception>
    public PluginChangeNotificationService(PluginChangeNotificationOptions? options = null, Action<string>? toastSink = null)
    {
        _options = options ?? new PluginChangeNotificationOptions();
        if (_options.Interactive) ArgumentNullException.ThrowIfNull(toastSink);
        _toastSink = toastSink;
    }

    /// <summary>Raised when the user selects the notification action to open plugin management filtered to changed plugins.</summary>
    public event EventHandler<PluginChangeNotification>? OpenManagementRequested;

    /// <summary>Gets the compact footer/status text while pending changes exist.</summary>
    public string? FooterStatus
    {
        get
        {
            lock (_gate)
            {
                return _lastNotification?.FooterStatus;
            }
        }
    }

    /// <summary>Gets headless fallback log messages.</summary>
    public IReadOnlyList<string> HeadlessMessages
    {
        get
        {
            lock (_gate)
            {
                return _headlessMessages.ToArray();
            }
        }
    }

    /// <summary>
    /// Coalesces and reports pending plugin source changes.
    /// </summary>
    /// <param name="changes">Pending changes.</param>
    /// <returns>The coalesced notification, or <see langword="null"/> when there are no changes.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="changes"/> is <see langword="null"/>.</exception>
    public PluginChangeNotification? Notify(IReadOnlyList<PluginSourceChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            Clear();
            return null;
        }

        var packageIds = changes
            .Select(static change => change.PackageId)
            .Where(static packageId => !string.IsNullOrWhiteSpace(packageId))
            .Select(static packageId => packageId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static packageId => packageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var requiresRescan = changes.Any(static change => change.Kind == PluginSourceChangeKind.UnknownRescanRequired || change.PackageId is null);
        var changedCount = packageIds.Length + (requiresRescan ? 1 : 0);
        var message = changedCount == 1
            ? "1 plugin changed"
            : $"{changedCount} plugins changed";
        var notification = new PluginChangeNotification
        {
            PackageIds = packageIds,
            RequiresRescan = requiresRescan,
            Message = message,
            FooterStatus = $"Plugins: {changedCount} changed",
        };

        lock (_gate)
        {
            _lastNotification = notification;
            if (_options.Interactive)
            {
                _toastSink!(message); // Validated for interactive options at construction.
            }
            else if (_options.HeadlessLogging)
            {
                _headlessMessages.Add(message);
            }
        }

        return notification;
    }

    /// <summary>
    /// Dispatches the notification action to open plugin management filtered to changed plugins.
    /// </summary>
    public void OpenManagementForChangedPlugins()
    {
        PluginChangeNotification? notification;
        lock (_gate)
        {
            notification = _lastNotification;
        }

        if (notification is not null)
        {
            OpenManagementRequested?.Invoke(this, notification);
        }
    }

    /// <summary>Clears pending notification status.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _lastNotification = null;
        }
    }
}
