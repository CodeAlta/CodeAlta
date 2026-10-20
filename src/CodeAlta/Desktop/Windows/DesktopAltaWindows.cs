using CodeAlta.LiveTool;
using NeoAstra;

namespace CodeAlta.Desktop.Windows;

/// <summary>The windows of CodeAlta Desktop for <c>alta window</c>: what <see cref="DesktopWindows"/> knows and does.</summary>
/// <param name="windows">The windows of the application.</param>
internal sealed class DesktopAltaWindows(DesktopWindows windows) : IAltaWindows
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<AltaWindow>> ListAsync(CancellationToken cancellationToken)
        => [.. (await windows.ListAsync().WaitAsync(cancellationToken).ConfigureAwait(false)).Select(Window)];

    /// <inheritdoc />
    public async Task<IReadOnlyList<AltaMonitor>> ListMonitorsAsync(CancellationToken cancellationToken)
    {
        var displays = await windows.DisplaysAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        return [.. displays.Select(static (display, index) => new AltaMonitor
        {
            Id = display.Id, Name = display.Name, Number = index + 1, Bounds = Rect(display.Bounds), WorkArea = Rect(display.WorkArea),
            Scale = display.ScaleFactor, Primary = display.IsPrimary, IdPersistent = display.IsIdPersistent,
        })];
    }

    /// <inheritdoc />
    public async Task<AltaWindowResult> OpenAsync(AltaWindowOpenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SavedWindowId is { } saved && !(await windows.ListAsync().ConfigureAwait(false)).Any(window => window.Id == saved && !window.Main))
            return new("not_found");
        var opened = await windows.OpenAsync(new DesktopWindowOpening
        {
            SavedId = request.SavedWindowId, Name = request.Name, Icon = request.Icon, Color = Color(request.Color), DisplayId = request.MonitorId,
        }).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (opened is null) return new("unavailable");
        // The tabs are moved by the windows that hold them, one after the other.
        var moves = new List<AltaWindowTabMove>(request.Tabs.Count);
        foreach (var tab in request.Tabs)
            moves.Add(new(tab, (await windows.RequestMoveAsync(tab, opened.Id).WaitAsync(cancellationToken).ConfigureAwait(false)).Status));
        return new("ok", await FindAsync(opened.Id, cancellationToken).ConfigureAwait(false) ?? Window(opened), moves);
    }

    /// <inheritdoc />
    public async Task<AltaWindowResult> FocusAsync(string windowId, CancellationToken cancellationToken)
        => windows.Focus(windowId) ? new("ok", await FindAsync(windowId, cancellationToken).ConfigureAwait(false)) : new("not_found");

    /// <inheritdoc />
    public async Task<AltaWindowResult> CloseAsync(string windowId, CancellationToken cancellationToken)
    {
        var window = await FindAsync(windowId, cancellationToken).ConfigureAwait(false);
        var status = windows.RequestClose(windowId);
        return new(status, status == "ok" ? window : null);
    }

    /// <inheritdoc />
    public async Task<AltaTabMoveResult> MoveTabAsync(string tab, string? targetWindowId, string? monitorId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tab);
        var opening = targetWindowId is null || !(await windows.ListAsync().ConfigureAwait(false)).Any(window => window.Id == targetWindowId && window.Open);
        var moved = await windows.RequestMoveAsync(tab, targetWindowId).WaitAsync(cancellationToken).ConfigureAwait(false);
        // A window that opens for the tab goes to the monitor that was asked for.
        if (moved.Status == "ok" && moved.WindowId is { } id && opening && monitorId is not null)
            await windows.PlaceAsync(id, new DesktopWindowPlacing { DisplayId = monitorId }).ConfigureAwait(false);
        return new(moved.Status, moved.WindowId);
    }

    /// <inheritdoc />
    public async Task<AltaWindowResult> PlaceAsync(string windowId, AltaWindowPlacement placement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(placement);
        NeoWindowState? state = placement.State?.ToLowerInvariant() switch
        {
            null => null,
            "normal" => NeoWindowState.Normal,
            "maximized" => NeoWindowState.Maximized,
            "minimized" => NeoWindowState.Minimized,
            "fullscreen" => NeoWindowState.Fullscreen,
            _ => null,
        };
        var status = await windows.PlaceAsync(windowId, new DesktopWindowPlacing
        {
            DisplayId = placement.MonitorId, State = state, CurrentDesktop = placement.CurrentDesktop,
            Bounds = placement.Bounds is { } bounds ? new NeoRect(bounds.X, bounds.Y, bounds.Width, bounds.Height) : null,
        }).WaitAsync(cancellationToken).ConfigureAwait(false);
        status = status == "display_not_found" ? "monitor_not_found" : status;
        return new(status, status == "ok" ? await FindAsync(windowId, cancellationToken).ConfigureAwait(false) : null);
    }

    /// <inheritdoc />
    public async Task<AltaWindowResult> UpdateAsync(string windowId, AltaWindowUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.PinnedMonitorId is { Length: > 0 } monitor && !(await windows.DisplaysAsync().ConfigureAwait(false)).Any(display => display.Id == monitor))
            return new("monitor_not_found");
        var changed = await windows.SetIdentityAsync(windowId, new DesktopWindowIdentityChange
        {
            Name = update.Name, Icon = update.Icon, Color = update.Color is null ? null : Color(update.Color) ?? string.Empty, Saved = update.Saved,
            PinnedDisplayId = update.PinnedMonitorId,
        }).WaitAsync(cancellationToken).ConfigureAwait(false);
        return changed is null ? new("not_found") : new("ok", Window(changed));
    }

    private async Task<AltaWindow?> FindAsync(string windowId, CancellationToken cancellationToken)
        => (await windows.ListAsync().WaitAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(window => window.Id == windowId) is { } found ? Window(found) : null;

    internal static AltaWindow Window(DesktopWindowInfo window) => new()
    {
        Id = window.Id, Main = window.Main, Name = window.Name, Icon = window.Icon, Color = window.Color, Saved = window.Saved, Open = window.Open,
        SpaceId = window.SpaceId, State = window.State, Focused = window.Focused, Visible = window.Visible, OnCurrentDesktop = window.OnCurrentDesktop,
        Monitor = window.DisplayId is { } display ? new AltaMonitorReference(display, window.DisplayName, window.DisplayPrimary) : null,
        PinnedMonitor = window.PinnedDisplayId is { } pinned ? new AltaMonitorReference(pinned, window.PinnedDisplayName, false) : null,
        PinnedConnected = window.PinnedConnected,
        Bounds = window.Bounds is { } bounds ? new AltaRect(bounds.X, bounds.Y, bounds.Width, bounds.Height) : null,
        Tabs = [.. window.Tabs.Select(tab => new AltaWindowTab(tab, KindOf(tab), tab == window.ActiveTab))],
    };

    /// <summary>What a tab shows, from its reference: <c>session</c>, <c>editor</c>, <c>changes</c>, <c>terminal</c>, <c>canvas</c>, <c>documentation</c>…</summary>
    internal static string KindOf(string tab)
    {
        var colon = tab.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? tab : tab[..colon];
    }

    // A color as the windows keep it: #rrggbb, also from #rgb.
    private static string? Color(string? color)
    {
        if (string.IsNullOrEmpty(color)) return color;
        return color.Length == 4 && color[0] == '#' ? $"#{color[1]}{color[1]}{color[2]}{color[2]}{color[3]}{color[3]}".ToLowerInvariant() : color.ToLowerInvariant();
    }

    private static AltaRect Rect(NeoRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);
}
