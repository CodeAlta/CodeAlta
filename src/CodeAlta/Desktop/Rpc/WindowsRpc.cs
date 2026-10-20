using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CodeAlta.Desktop.Windows;
using NeoAstra;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The windows of the application, as the page of each one sees them: which window it is, what it is asked to do
/// (open a tab moved into it, bring a tab to the front, close), the tabs it holds, and the tabs it moves or drags to
/// another window. Settings reads and changes the windows through it too: their names, icons and colors, the display
/// a window is pinned to, and whether they come back at the next start.
/// </summary>
[NeoRpcService("windows", Version = 1)]
internal sealed class WindowsService
{
    private readonly DesktopWindows? _windows;
    private readonly Func<bool>? _canDrag;

    /// <summary>Creates an unavailable service: the page is the only window there is.</summary>
    internal WindowsService()
    {
    }

    /// <param name="windows">The windows of the application.</param>
    /// <param name="canDrag">Whether the platform tells where the windows are, which dragging a tab between them needs.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    internal WindowsService(DesktopWindows windows, Func<bool> canDrag)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(canDrag);
        (_windows, _canDrag) = (windows, canDrag);
    }

    /// <summary>The window of the page that asks, and what its page kept of its tabs.</summary>
    [NeoRpcMethod("current")]
    public WindowsCurrentResponse Current(WindowsRequest request, NeoRpcContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_windows?.Current(context.ViewLabel) is not { } current) return new("unavailable", null, null, false, false, []);
        return new("ok", Wire(current.Window), current.PageState, _windows.RestoreAtStartup, _canDrag!(), [.. _windows.Snapshot().Select(Wire)]);
    }

    /// <summary>The windows: the main one, the open secondary ones, then the saved ones that are closed.</summary>
    [NeoRpcMethod("list", Dispatch = NeoRpcDispatchMode.UiThread)]
    public async Task<WindowsListResponse> ListAsync(WindowsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_windows is null) return new("unavailable", [], false);
        var windows = await _windows.ListAsync().ConfigureAwait(true);
        return new("ok", [.. windows.Select(Wire)], _windows.RestoreAtStartup);
    }

    /// <summary>The displays there are now, the primary one first.</summary>
    [NeoRpcMethod("displays", Dispatch = NeoRpcDispatchMode.UiThread)]
    public async Task<WindowsDisplaysResponse> DisplaysAsync(WindowsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_windows is null) return new("unavailable", []);
        var displays = await _windows.DisplaysAsync().ConfigureAwait(true);
        return new("ok", [.. displays.Select(static display => new WindowsDisplay(display.Id, display.Name, display.Bounds.Width, display.Bounds.Height,
            (int)Math.Round(display.ScaleFactor * 100), display.IsPrimary))]);
    }

    /// <summary>What is asked of the window of the page, until the page goes away.</summary>
    [NeoRpcMethod("watch")]
    public NeoRpcChannel<WindowsEvent> Watch(WindowsRequest request, NeoRpcContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(WatchAsync(context.ViewLabel, cancellationToken), DesktopJsonContext.Default.WindowsEvent);
    }

    internal async IAsyncEnumerable<WindowsEvent> WatchAsync(string label, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_windows is null) yield break;
        var events = Channel.CreateBounded<WindowsEvent>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        using var registration = _windows.Watch(label, value => events.Writer.TryWrite(value));
        if (registration is null) yield break;
        await foreach (var value in events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return value;
    }

    /// <summary>The tabs the window of the page holds, and, for a secondary window, what its page keeps of them.</summary>
    [NeoRpcMethod("report")]
    public WindowsStatusResponse Report(WindowsReportRequest request, NeoRpcContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(_windows is not null && _windows.Report(context.ViewLabel, request.Tabs ?? [], request.Active, request.PageState) ? "ok" : "unavailable");
    }

    /// <summary>
    /// Moves a tab of the window of the page to another window, or to a new one when no window is named. The page closes
    /// the tab once the answer is <c>ok</c>; <c>other_space</c> when the window shows another space, <c>not_found</c>
    /// when there is no such window. A move the host asked for (<c>RequestId</c>) is answered with the same outcome.
    /// </summary>
    [NeoRpcMethod("moveTab", Dispatch = NeoRpcDispatchMode.UiThread)]
    public async Task<WindowsMoveTabResponse> MoveTabAsync(WindowsMoveTabRequest request, NeoRpcContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_windows is null || string.IsNullOrWhiteSpace(request.Tab) || request.Payload is null) return new("unavailable", null);
        var moved = await _windows.MoveTabAsync(context.ViewLabel, request.Tab, request.Payload, request.SpaceBound, request.Target, null, null).ConfigureAwait(true);
        if (request.RequestId is { } requestId) _windows.CompleteMove(requestId, moved);
        return new(moved.Status, moved.WindowId);
    }

    /// <summary>The page could not do a move the host asked of it (<c>not_movable</c>, <c>not_found</c>).</summary>
    [NeoRpcMethod("refuseMove")]
    public WindowsStatusResponse RefuseMove(WindowsRefuseMoveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _windows?.CompleteMove(request.RequestId, new(string.IsNullOrWhiteSpace(request.Status) ? "not_movable" : request.Status, null));
        return new(_windows is null ? "unavailable" : "ok");
    }

    /// <summary>Brings the window that holds a tab to the front, on that tab: <c>ok</c>, or <c>not_found</c> when no window holds it.</summary>
    [NeoRpcMethod("focusTab")]
    public WindowsWindowIdResponse FocusTab(WindowsTabRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_windows is null) return new("unavailable", null);
        return _windows.FocusTab(request.Tab) is { } id ? new("ok", id) : new("not_found", null);
    }

    /// <summary>Opens a new secondary window, or a saved one that is closed.</summary>
    [NeoRpcMethod("open", Dispatch = NeoRpcDispatchMode.UiThread)]
    public async Task<WindowsWindowResponse> OpenAsync(WindowsOpenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_windows is null) return new("unavailable", null);
        var opened = await _windows.OpenAsync(new DesktopWindowOpening { SavedId = request.SavedId, Name = request.Name, Icon = request.Icon, Color = request.Color })
            .ConfigureAwait(true);
        return opened is null ? new("unavailable", null) : new("ok", Wire(opened));
    }

    /// <summary>Brings a window to the front.</summary>
    [NeoRpcMethod("focus")]
    public WindowsStatusResponse Focus(WindowsIdRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(_windows is null ? "unavailable" : _windows.Focus(request.WindowId ?? DesktopWindows.MainId) ? "ok" : "not_found");
    }

    /// <summary>
    /// Closes a secondary window: the page of the window that asks closed its tabs (no window named), or the page of
    /// the window named is asked to close its tabs, then the window.
    /// </summary>
    [NeoRpcMethod("close")]
    public WindowsStatusResponse Close(WindowsIdRequest request, NeoRpcContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_windows is null) return new("unavailable");
        if (request.WindowId is null) return new(_windows.CloseFromPage(context.ViewLabel) ? "ok" : "main");
        return new(_windows.RequestClose(request.WindowId));
    }

    /// <summary>Forgets a saved window that is closed.</summary>
    [NeoRpcMethod("forget")]
    public WindowsStatusResponse Forget(WindowsIdRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_windows is null) return new("unavailable");
        return new(request.WindowId is null ? "not_found" : _windows.Forget(request.WindowId));
    }

    /// <summary>Changes the name, the icon, the color, whether a window is saved, and the display it is pinned to.</summary>
    [NeoRpcMethod("setIdentity", Dispatch = NeoRpcDispatchMode.UiThread)]
    public async Task<WindowsWindowResponse> SetIdentityAsync(WindowsIdentityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_windows is null) return new("unavailable", null);
        if (request.Icon is { Length: > 0 } icon && !CodeAlta.Catalog.SpaceDescriptor.IsValidIcon(icon)) return new("invalid_icon", null);
        if (request.Color is { Length: > 0 } color && !CodeAlta.Catalog.SpaceDescriptor.IsValidColor(color)) return new("invalid_color", null);
        if (request.Name is { Length: > 64 }) return new("invalid_name", null);
        var changed = await _windows.SetIdentityAsync(request.WindowId, new DesktopWindowIdentityChange
        {
            Name = request.Name?.Trim(), Icon = request.Icon, Color = request.Color, Saved = request.Saved, PinnedDisplayId = request.PinnedDisplayId,
        }).ConfigureAwait(true);
        return changed is null ? new("not_found", null) : new("ok", Wire(changed));
    }

    /// <summary>Changes whether the secondary windows that were open come back at the next start.</summary>
    [NeoRpcMethod("setRestoreAtStartup")]
    public WindowsStatusResponse SetRestoreAtStartup(WindowsRestoreRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _windows?.SetRestoreAtStartup(request.Restore);
        return new(_windows is null ? "unavailable" : "ok");
    }

    /// <summary>
    /// A tab of the page is dragged to a point of the page, which can be outside its window: the window under the point,
    /// when it is another one, draws where the tab would go. No point ends what the other window draws.
    /// </summary>
    [NeoRpcMethod("dragOver", Dispatch = NeoRpcDispatchMode.UiThread)]
    public async Task<WindowsWindowIdResponse> DragOverAsync(WindowsDragRequest request, NeoRpcContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_windows is null || string.IsNullOrWhiteSpace(request.Tab)) return new("unavailable", null);
        var over = await _windows.DragOverAsync(context.ViewLabel, request.Tab, request.SpaceBound, request.X, request.Y).ConfigureAwait(true);
        return new("ok", over);
    }

    /// <summary>
    /// A tab of the page was released at a point of the page outside the window: over another window of the application it
    /// moves there, at the place the drop indicator showed; over no window it opens in a new window, there. The answer is
    /// <c>here</c> when the point is over the window of the page, which handles the drop as a drag inside it.
    /// </summary>
    [NeoRpcMethod("drop", Dispatch = NeoRpcDispatchMode.UiThread)]
    public async Task<WindowsMoveTabResponse> DropAsync(WindowsDropRequest request, NeoRpcContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_windows is null || string.IsNullOrWhiteSpace(request.Tab) || request.Payload is null) return new("unavailable", null);
        var (over, local, screen) = await _windows.DragReleaseAsync(context.ViewLabel, request.X, request.Y).ConfigureAwait(true);
        if (over is not null && over == DesktopWindows.IdOf(context.ViewLabel)) return new("here", over);
        if (over is null && screen is null) return new("here", null);
        var moved = await _windows.MoveTabAsync(context.ViewLabel, request.Tab, request.Payload, request.SpaceBound, over, over is null ? screen : null, local)
            .ConfigureAwait(true);
        return new(moved.Status, moved.WindowId);
    }

    internal static WindowsWindow Wire(DesktopWindowInfo window) => new(window.Id, window.Main, window.Name, window.Icon, window.Color, window.Saved, window.Open,
        window.SpaceId, window.DisplayId, window.DisplayName, window.DisplayPrimary, window.PinnedDisplayId, window.PinnedDisplayName, window.PinnedConnected,
        window.State, window.Focused, [.. window.Tabs], window.ActiveTab);
}

internal sealed record WindowsRequest;

/// <param name="Status"><c>ok</c>, or <c>unavailable</c> when the page is in no window the host knows.</param>
/// <param name="Window">The window of the page.</param>
/// <param name="PageState">What the page of a secondary window kept of its tabs and panes; null for the main window or a new one.</param>
/// <param name="RestoreAtStartup">Whether the secondary windows that were open come back at the next start.</param>
/// <param name="CanDrag">Whether a tab can be dragged out of a window, which needs the platform to tell where the windows are.</param>
/// <param name="Windows">The windows and the tabs they hold, also the ones that come back at this start before their pages exist.</param>
internal sealed record WindowsCurrentResponse(string Status, WindowsWindow? Window, string? PageState, bool RestoreAtStartup, bool CanDrag, WindowsWindow[] Windows);

/// <param name="Id">The id of the window: <c>main</c> for the main one.</param>
/// <param name="Main">Whether it is the main window.</param>
/// <param name="Name">Its name: the one the user gave, or <c>Main</c>, <c>Window 2</c>…</param>
/// <param name="Icon">Its icon, one of the icons of the spaces; null for none.</param>
/// <param name="Color">Its color, <c>#rrggbb</c>; null for none.</param>
/// <param name="Saved">Whether it is kept when it is closed.</param>
/// <param name="Open">Whether it is open.</param>
/// <param name="SpaceId">The space it shows.</param>
/// <param name="DisplayId">The display it is on, while it is open.</param>
/// <param name="DisplayName">The name of that display.</param>
/// <param name="DisplayPrimary">Whether that display is the primary one.</param>
/// <param name="PinnedDisplayId">The display it goes to whenever it is connected; null for where it was.</param>
/// <param name="PinnedDisplayName">The name of that display.</param>
/// <param name="PinnedConnected">Whether that display is connected.</param>
/// <param name="State"><c>normal</c>, <c>maximized</c>, <c>minimized</c> or <c>fullscreen</c>.</param>
/// <param name="Focused">Whether it is the window that was focused last.</param>
/// <param name="Tabs">The tabs it holds.</param>
/// <param name="ActiveTab">The tab in front.</param>
internal sealed record WindowsWindow(string Id, bool Main, string Name, string? Icon, string? Color, bool Saved, bool Open, string? SpaceId,
    string? DisplayId, string? DisplayName, bool DisplayPrimary, string? PinnedDisplayId, string? PinnedDisplayName, bool PinnedConnected,
    string State, bool Focused, string[] Tabs, string? ActiveTab);

internal sealed record WindowsListResponse(string Status, WindowsWindow[] Windows, bool RestoreAtStartup);

/// <param name="Id">The id of the display, which names it after a restart where the platform tells its monitor.</param>
/// <param name="Name">The name of the display.</param>
/// <param name="Width">Its width, in the units of the windows.</param>
/// <param name="Height">Its height.</param>
/// <param name="Scale">Its scale, in percent.</param>
/// <param name="Primary">Whether it is the primary display.</param>
internal sealed record WindowsDisplay(string Id, string Name, int Width, int Height, int Scale, bool Primary);

internal sealed record WindowsDisplaysResponse(string Status, WindowsDisplay[] Displays);

/// <param name="Tabs">The tabs the window holds.</param>
/// <param name="Active">The tab in front.</param>
/// <param name="PageState">What the page of a secondary window keeps of its tabs and panes.</param>
internal sealed record WindowsReportRequest(string[]? Tabs, string? Active, string? PageState);

internal sealed record WindowsStatusResponse(string Status);

/// <param name="Tab">The tab, as <c>session:&lt;id&gt;</c>, <c>editor:&lt;project&gt;</c> and so on.</param>
/// <param name="Payload">What the window that receives it opens.</param>
/// <param name="SpaceBound">Whether the tab goes only to a window of the same space.</param>
/// <param name="Target">The window it goes to; null for a new one.</param>
/// <param name="RequestId">The move the host asked for, when it did.</param>
internal sealed record WindowsMoveTabRequest(string Tab, string Payload, bool SpaceBound, string? Target, string? RequestId);

internal sealed record WindowsMoveTabResponse(string Status, string? WindowId);

internal sealed record WindowsRefuseMoveRequest(string RequestId, string? Status);

internal sealed record WindowsTabRequest(string Tab);

internal sealed record WindowsWindowIdResponse(string Status, string? WindowId);

/// <param name="WindowId">The window; null for the window of the page that asks.</param>
internal sealed record WindowsIdRequest(string? WindowId);

internal sealed record WindowsOpenRequest(string? Name, string? Icon, string? Color, string? SavedId);

internal sealed record WindowsWindowResponse(string Status, WindowsWindow? Window);

/// <param name="WindowId">The window.</param>
/// <param name="Name">Its name; empty to take it away, null to leave it.</param>
/// <param name="Icon">Its icon; empty to take it away, null to leave it.</param>
/// <param name="Color">Its color; empty to take it away, null to leave it.</param>
/// <param name="Saved">Whether it is kept when it is closed; null to follow the name.</param>
/// <param name="PinnedDisplayId">The display it goes to whenever it is connected; empty for where it was, null to leave it.</param>
internal sealed record WindowsIdentityRequest(string WindowId, string? Name, string? Icon, string? Color, bool? Saved, string? PinnedDisplayId);

internal sealed record WindowsRestoreRequest(bool Restore);

/// <param name="Tab">The tab that is dragged.</param>
/// <param name="SpaceBound">Whether it goes only to a window of the same space.</param>
/// <param name="X">The point, in the CSS pixels of the page; null when the drag ended.</param>
/// <param name="Y">See <paramref name="X"/>.</param>
internal sealed record WindowsDragRequest(string Tab, bool SpaceBound, int? X, int? Y);

/// <param name="Tab">The tab that was dragged.</param>
/// <param name="Payload">What the window that receives it opens.</param>
/// <param name="SpaceBound">Whether it goes only to a window of the same space.</param>
/// <param name="X">The point where it was released, in the CSS pixels of the page.</param>
/// <param name="Y">See <paramref name="X"/>.</param>
internal sealed record WindowsDropRequest(string Tab, string Payload, bool SpaceBound, int X, int Y);
