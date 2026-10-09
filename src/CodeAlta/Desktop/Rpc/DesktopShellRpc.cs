using System.Runtime.CompilerServices;
using System.Threading.Channels;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The page's side of how the application lives beyond its window: what closing the window does and the
/// question the page asks about it, an explicit exit, the question the page asks before an exit that would stop
/// running sessions, and the operating system's folder dialog.
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

    /// <summary>What closing the window does, and whether the application can keep running without it.</summary>
    [NeoRpcMethod("preferences")]
    public DesktopShellPreferences Preferences(DesktopShellRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Current();
    }

    /// <summary>
    /// Changes what closing the window does: <c>ask</c>, <c>keep</c> (the application keeps running) or
    /// <c>exit</c>. Anything else changes nothing.
    /// </summary>
    [NeoRpcMethod("setOnClose")]
    public DesktopShellPreferences SetOnClose(DesktopShellOnCloseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_shell is not null && DesktopPreferences.TryParse(request.OnClose, out var behavior)) _shell.SetOnClose(behavior);
        return Current();
    }

    /// <summary>
    /// Changes the user's setting for the review of permissions: whether the commands and the file changes of a
    /// session are reviewed instead of being approved automatically. It is read at every send, so it applies to
    /// what the sessions do next, not to what is already running.
    /// </summary>
    [NeoRpcMethod("setReviewPermissions")]
    public DesktopShellPreferences SetReviewPermissions(DesktopShellReviewPermissionsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _shell?.SetReviewPermissions(request.Review);
        return Current();
    }

    /// <summary>
    /// Changes the user's setting for the width of the conversations, in percent of the space of a session (40 to
    /// 100). A width out of range changes nothing. The session the user resized follows the setting again. Every
    /// page is told with <c>session-width</c> notices of <see cref="Watch"/>.
    /// </summary>
    [NeoRpcMethod("setSessionWidth")]
    public DesktopShellPreferences SetSessionWidth(DesktopShellSessionWidthRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _shell?.SetSessionWidth(request.Percent, request.SessionId);
        return Current();
    }

    /// <summary>
    /// Zooms the window's view one step in (<c>1</c>) or out (<c>-1</c>), or back to 100 percent (<c>0</c>), and
    /// keeps the zoom for the next starts. It runs on the window's thread, which the view requires.
    /// </summary>
    [NeoRpcMethod("zoom", Dispatch = NeoRpcDispatchMode.UiThread)]
    public DesktopShellPreferences Zoom(DesktopShellZoomRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _shell?.StepZoom(Math.Sign(request.Direction));
        return Current();
    }

    /// <summary>
    /// Hides the window and leaves the application running: the answer to the <c>confirm-close</c> notice of
    /// <see cref="Watch"/>.
    /// </summary>
    [NeoRpcMethod("hide")]
    public DesktopShellHideResponse Hide(DesktopShellRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(_shell is not null && _shell.Hide() ? "ok" : "unavailable");
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

    /// <summary>
    /// Lets the user choose a folder with the dialog of the operating system and returns its full path.
    /// The page decides what the folder is for: choosing it opens nothing and trusts nothing.
    /// </summary>
    /// <remarks>The dialog may stay open as long as an invocation may last: ten minutes.</remarks>
    [NeoRpcMethod("pickFolder", TimeoutMilliseconds = 600_000)]
    public async Task<DesktopShellPickFolderResponse> PickFolderAsync(DesktopShellPickFolderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_shell is null) return new("unavailable", null);
        var pick = await _shell.PickFolderAsync(Title(request.Title), request.InitialDirectory, cancellationToken).ConfigureAwait(false);
        return pick is not { } value
            ? new("busy", null)
            : value.Status switch
            {
                DesktopFolderPickStatus.Ok => new("ok", value.Path),
                DesktopFolderPickStatus.Canceled => new("canceled", null),
                DesktopFolderPickStatus.Unavailable => new("unavailable", null),
                _ => new("failed", null),
            };
    }

    // The title the page asks for, as one bounded line; the default when it gives none.
    internal static string Title(string? value)
    {
        var text = new string([.. (value ?? string.Empty).Where(static character => !char.IsControl(character) && !char.IsSurrogate(character)).Take(120)]).Trim();
        return text.Length == 0 ? "Select a folder" : text;
    }

    /// <summary>
    /// Shows the application's entry in the file manager: on macOS <c>CodeAlta.app</c> selected in the Finder,
    /// from where the user drags it to the Dock. <c>unavailable</c> where there is no entry to show.
    /// </summary>
    [NeoRpcMethod("revealEntry")]
    public DesktopShellRevealEntryResponse RevealEntry(DesktopShellRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(_shell is not null && DesktopIntegration.RevealEntry() ? "ok" : "unavailable");
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
        ? new("unavailable", DesktopPreferences.Name(DesktopCloseBehavior.Ask), false, Platform, false)
        : new("ok", DesktopPreferences.Name(_shell.OnClose), _shell.CanHide, Platform, _shell.EntryAdded, _shell.SessionWidth,
            [.. _shell.SessionWidths().Select(static pair => new DesktopShellSessionWidth(pair.Key, pair.Value))], _shell.TrayAvailable, _shell.Zoom,
            _shell.ReviewPermissions);

    private static string Platform => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
}

internal sealed record DesktopShellRequest;


/// <param name="OnClose"><c>ask</c>, <c>keep</c> or <c>exit</c>.</param>
internal sealed record DesktopShellOnCloseRequest(string? OnClose);

/// <summary><c>ok</c>, or <c>unavailable</c> where the window cannot stay hidden.</summary>
internal sealed record DesktopShellHideResponse(string Status);

/// <param name="Title">The title of the dialog; a default when blank.</param>
/// <param name="InitialDirectory">The folder shown first; ignored unless it is an existing absolute folder.</param>
internal sealed record DesktopShellPickFolderRequest(string? Title, string? InitialDirectory);

/// <param name="Status">
/// <c>ok</c>, <c>canceled</c> (the user chose nothing), <c>busy</c> (a folder dialog is already open),
/// <c>unavailable</c> (no folder dialog on this system) or <c>failed</c>.
/// </param>
/// <param name="Path">The full path of the chosen folder; null unless <c>ok</c>.</param>
internal sealed record DesktopShellPickFolderResponse(string Status, string? Path);

/// <param name="Confirmed">The user already agreed to stop the running sessions.</param>
internal sealed record DesktopShellExitRequest(bool Confirmed);

/// <summary><c>ok</c> or <c>unavailable</c>.</summary>
internal sealed record DesktopShellExitResponse(string Status);

/// <summary><c>ok</c>, or <c>unavailable</c> when the application has no entry or the file manager did not open.</summary>
internal sealed record DesktopShellRevealEntryResponse(string Status);

/// <param name="Status"><c>ok</c> or <c>unavailable</c>.</param>
/// <param name="OnClose">
/// What closing the window does: <c>ask</c> (the page asks, on a <c>confirm-close</c> notice), <c>keep</c> (the
/// application keeps running) or <c>exit</c>.
/// </param>
/// <param name="CanKeepRunning">The platform has somewhere for the application to stay (a tray icon, the Dock).</param>
/// <param name="Platform"><c>windows</c>, <c>macos</c> or <c>linux</c>: what that place is called.</param>
/// <param name="EntryAdded">This start added the application to the desktop's applications.</param>
/// <param name="SessionWidth">The user's setting: the width of the conversations, in percent of the space of a session.</param>
/// <param name="SessionWidths">The sessions that are shown with a width of their own, set by an <c>alta appearance</c> command.</param>
/// <param name="TrayIcon">The application has an icon in the notification area; without one on macOS it stays in the Dock.</param>
/// <param name="Zoom">The zoom of the window's view, in percent.</param>
/// <param name="ReviewPermissions">
/// The user's setting: the commands and the file changes of a session are reviewed instead of being approved
/// automatically.
/// </param>
internal sealed record DesktopShellPreferences(string Status, string OnClose, bool CanKeepRunning, string Platform, bool EntryAdded,
    int SessionWidth = DesktopPreferences.DefaultSessionWidth, DesktopShellSessionWidth[]? SessionWidths = null, bool TrayIcon = false,
    int Zoom = DesktopPreferences.DefaultZoom, bool ReviewPermissions = true);

/// <summary>Asks to review the commands and the file changes of the sessions, or to approve them automatically.</summary>
/// <param name="Review">Whether they are reviewed.</param>
internal sealed record DesktopShellReviewPermissionsRequest(bool Review);

/// <summary>The width one session is shown with instead of the user's setting.</summary>
internal sealed record DesktopShellSessionWidth(string SessionId, int Percent);

/// <param name="Percent">The width of the conversations, from 40 to 100.</param>
/// <param name="SessionId">The session the user resized, when there is one: it follows the setting again.</param>
internal sealed record DesktopShellSessionWidthRequest(int Percent, string? SessionId = null);

/// <param name="Direction"><c>1</c> zooms in a step, <c>-1</c> out a step, and <c>0</c> back to 100 percent.</param>
internal sealed record DesktopShellZoomRequest(int Direction);
