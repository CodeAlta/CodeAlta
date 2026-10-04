using System.Runtime.CompilerServices;
using System.Threading.Channels;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The page's side of how the application lives beyond its window: whether closing the window leaves it
/// running, an explicit exit, and the question the page asks before an exit that would stop running sessions.
/// </summary>
[NeoRpcService("desktopShell", Version = 1)]
internal sealed class DesktopShellService
{
    private readonly DesktopShell? _shell;

    /// <summary>Creates an unavailable service for a window without a shell.</summary>
    internal DesktopShellService()
    {
    }

    /// <exception cref="ArgumentNullException"><paramref name="shell"/> is null.</exception>
    internal DesktopShellService(DesktopShell shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        _shell = shell;
    }

    /// <summary>Whether closing the window leaves the application running, and whether it can.</summary>
    [NeoRpcMethod("preferences")]
    public DesktopShellPreferences Preferences(DesktopShellRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Current();
    }

    /// <summary>Changes whether closing the window leaves the application running.</summary>
    [NeoRpcMethod("setCloseToTray")]
    public DesktopShellPreferences SetCloseToTray(DesktopShellCloseToTrayRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_shell is not null && request.Enabled is { } enabled) _shell.SetCloseToTray(enabled);
        return Current();
    }

    /// <summary>
    /// Exits the application. While sessions run and the request is not <c>confirmed</c>, the answer is
    /// <c>confirm</c> and a <c>confirm-exit</c> notice follows on <see cref="Watch"/>.
    /// </summary>
    [NeoRpcMethod("exit")]
    public DesktopShellExitResponse Exit(DesktopShellExitRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_shell is null) return new("unavailable");
        _shell.RequestExit(request.Confirmed);
        return new("ok");
    }

    /// <summary>The shell's notices for this page, until the page goes away.</summary>
    [NeoRpcMethod("watch")]
    public NeoRpcChannel<DesktopShellEvent> Watch(DesktopShellRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(WatchAsync(cancellationToken), DesktopJsonContext.Default.DesktopShellEvent);
    }

    internal async IAsyncEnumerable<DesktopShellEvent> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_shell is null) yield break;
        // A page that does not read loses the oldest notice, never the newest question.
        var notices = Channel.CreateBounded<DesktopShellEvent>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        using var registration = _shell.Watch(value => notices.Writer.TryWrite(value));
        await foreach (var notice in notices.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return notice;
    }

    private DesktopShellPreferences Current() => _shell is null
        ? new("unavailable", false, false, Platform, false)
        : new("ok", _shell.CloseToTray, _shell.CanHide, Platform, _shell.EntryAdded);

    private static string Platform => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
}

internal sealed record DesktopShellRequest;

internal sealed record DesktopShellCloseToTrayRequest(bool? Enabled);

/// <param name="Confirmed">The user already agreed to stop the running sessions.</param>
internal sealed record DesktopShellExitRequest(bool Confirmed);

/// <summary><c>ok</c> or <c>unavailable</c>.</summary>
internal sealed record DesktopShellExitResponse(string Status);

/// <param name="Status"><c>ok</c> or <c>unavailable</c>.</param>
/// <param name="CloseToTray">Closing the window leaves the application running.</param>
/// <param name="CanKeepRunning">The platform has somewhere for the application to stay (a tray icon, the Dock).</param>
/// <param name="Platform"><c>windows</c>, <c>macos</c> or <c>linux</c>: what that place is called.</param>
/// <param name="EntryAdded">This start added the application to the desktop's applications.</param>
internal sealed record DesktopShellPreferences(string Status, bool CloseToTray, bool CanKeepRunning, string Platform, bool EntryAdded);
