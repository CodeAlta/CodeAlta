namespace CodeAlta.LiveTool;

/// <summary>
/// The windows of a host that has several: in the desktop app, the main window, which has the activity rail, the
/// projects and Settings, and the secondary windows that hold tabs moved out of it. A host without windows registers
/// no such service, and <c>alta window</c> is then not part of its commands.
/// </summary>
/// <remarks>
/// A tab is named by its reference, as <see cref="AltaWindowTab.Tab"/> gives it: <c>session:&lt;id&gt;</c>,
/// <c>editor:&lt;project&gt;</c>, <c>changes:&lt;project&gt;</c>, <c>terminal:&lt;id&gt;</c>, <c>canvas:…</c>,
/// <c>documentation</c>, and so on. A tab is in one window at a time. Nothing here changes what a session does: the
/// windows only show it.
/// </remarks>
public interface IAltaWindows
{
    /// <summary>Lists the windows: the main one first, then the open secondary ones, then the saved ones that are closed.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The windows.</returns>
    Task<IReadOnlyList<AltaWindow>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Lists the monitors that are connected, the primary one first.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The monitors; none when the platform does not tell them.</returns>
    Task<IReadOnlyList<AltaMonitor>> ListMonitorsAsync(CancellationToken cancellationToken);

    /// <summary>Opens a new secondary window, or a saved one that is closed, and moves tabs into it.</summary>
    /// <param name="request">What the window is.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns><c>ok</c> with the window; <c>not_found</c> for a saved window that is not one; <c>unavailable</c> when no window can be opened now.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    Task<AltaWindowResult> OpenAsync(AltaWindowOpenRequest request, CancellationToken cancellationToken);

    /// <summary>Brings a window to the front.</summary>
    /// <param name="windowId">The id of the window.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns><c>ok</c>, or <c>not_found</c> when no such window is open.</returns>
    Task<AltaWindowResult> FocusAsync(string windowId, CancellationToken cancellationToken);

    /// <summary>Closes a secondary window: its tabs close, as each would; sessions keep running.</summary>
    /// <param name="windowId">The id of the window.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns><c>ok</c>; <c>not_found</c> when no such window is open; <c>main</c> for the main window, which is not closed this way.</returns>
    Task<AltaWindowResult> CloseAsync(string windowId, CancellationToken cancellationToken);

    /// <summary>Moves a tab to another window, or to a new one.</summary>
    /// <param name="tab">The reference of the tab.</param>
    /// <param name="targetWindowId">The window it goes to; null for a new one.</param>
    /// <param name="monitorId">For a new window, the monitor it opens on; null for the default place.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// <c>ok</c> with the window that has the tab now; <c>not_found</c> when no window holds the tab or there is no such
    /// window; <c>other_space</c> when that window shows another space; <c>not_movable</c> when the tab cannot move now
    /// (a prompt is being sent in it); <c>timeout</c> or <c>unavailable</c> when the window that holds it did not move it.
    /// </returns>
    Task<AltaTabMoveResult> MoveTabAsync(string tab, string? targetWindowId, string? monitorId, CancellationToken cancellationToken);

    /// <summary>Places a window: on a monitor, in a state, at bounds in the work area of its monitor, on the current virtual desktop.</summary>
    /// <param name="windowId">The id of the window.</param>
    /// <param name="placement">Where it goes.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns><c>ok</c>; <c>not_found</c>; <c>monitor_not_found</c> when the monitor is not connected; <c>not_supported</c> where the platform cannot place windows.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="placement"/> is null.</exception>
    Task<AltaWindowResult> PlaceAsync(string windowId, AltaWindowPlacement placement, CancellationToken cancellationToken);

    /// <summary>Changes the name, the icon, the color of a window, whether it is kept when it is closed, and the monitor it is pinned to.</summary>
    /// <param name="windowId">The id of the window, open or saved.</param>
    /// <param name="update">What changes.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns><c>ok</c> with the window; <c>not_found</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="update"/> is null.</exception>
    Task<AltaWindowResult> UpdateAsync(string windowId, AltaWindowUpdate update, CancellationToken cancellationToken);
}

/// <summary>A window of the host.</summary>
public sealed record AltaWindow
{
    /// <summary>Gets the id of the window: <c>main</c> for the main one.</summary>
    public required string Id { get; init; }

    /// <summary>Gets a value indicating whether it is the main window.</summary>
    public bool Main { get; init; }

    /// <summary>Gets its name: the one the user gave, or <c>Main</c>, <c>Window 2</c>…</summary>
    public required string Name { get; init; }

    /// <summary>Gets its icon, one of the icons of the spaces; null for none.</summary>
    public string? Icon { get; init; }

    /// <summary>Gets its color, <c>#rrggbb</c>; null for none.</summary>
    public string? Color { get; init; }

    /// <summary>Gets a value indicating whether it is kept when it is closed, as a window the user named is.</summary>
    public bool Saved { get; init; }

    /// <summary>Gets a value indicating whether it is open.</summary>
    public bool Open { get; init; }

    /// <summary>Gets the space it shows.</summary>
    public string? SpaceId { get; init; }

    /// <summary>Gets the monitor it is on, while it is open.</summary>
    public AltaMonitorReference? Monitor { get; init; }

    /// <summary>Gets the monitor it goes to whenever that monitor is connected; null for where it was.</summary>
    public AltaMonitorReference? PinnedMonitor { get; init; }

    /// <summary>Gets a value indicating whether the pinned monitor is connected.</summary>
    public bool PinnedConnected { get; init; }

    /// <summary>Gets its state: <c>normal</c>, <c>maximized</c>, <c>minimized</c> or <c>fullscreen</c>.</summary>
    public string State { get; init; } = "normal";

    /// <summary>Gets a value indicating whether it is the window the user used last.</summary>
    public bool Focused { get; init; }

    /// <summary>Gets a value indicating whether it is shown, rather than hidden with the application.</summary>
    public bool Visible { get; init; }

    /// <summary>Gets its position and size, in the units of the monitors; null when it is closed.</summary>
    public AltaRect? Bounds { get; init; }

    /// <summary>Gets whether it is on the current virtual desktop; null where the platform does not tell.</summary>
    public bool? OnCurrentDesktop { get; init; }

    /// <summary>Gets the tabs it holds, in no particular order but the one in front marked.</summary>
    public IReadOnlyList<AltaWindowTab> Tabs { get; init; } = [];
}

/// <summary>A tab of a window.</summary>
/// <param name="Tab">The reference of the tab, which <see cref="IAltaWindows.MoveTabAsync"/> takes.</param>
/// <param name="Kind">What the tab shows: <c>session</c>, <c>editor</c>, <c>changes</c>, <c>terminal</c>, <c>canvas</c>, <c>documentation</c>…</param>
/// <param name="Active">Whether it is the tab in front of its window.</param>
public sealed record AltaWindowTab(string Tab, string Kind, bool Active);

/// <summary>A monitor, as a window names it.</summary>
/// <param name="Id">The id of the monitor.</param>
/// <param name="Name">The name of the monitor.</param>
/// <param name="Primary">Whether it is the primary monitor.</param>
public sealed record AltaMonitorReference(string Id, string? Name, bool Primary);

/// <summary>A monitor that is connected.</summary>
public sealed record AltaMonitor
{
    /// <summary>Gets the id of the monitor, which names it after a restart where <see cref="IdPersistent"/>.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the name the platform shows for it, such as its model.</summary>
    public required string Name { get; init; }

    /// <summary>Gets its number, from 1, in the order of <see cref="IAltaWindows.ListMonitorsAsync"/>.</summary>
    public int Number { get; init; }

    /// <summary>Gets its bounds.</summary>
    public required AltaRect Bounds { get; init; }

    /// <summary>Gets the part of its bounds that the taskbar, the menu bar or the Dock leave to windows.</summary>
    public required AltaRect WorkArea { get; init; }

    /// <summary>Gets its scale, 1 for 100 percent.</summary>
    public double Scale { get; init; } = 1;

    /// <summary>Gets a value indicating whether it is the primary monitor.</summary>
    public bool Primary { get; init; }

    /// <summary>Gets a value indicating whether its id names it after a restart.</summary>
    public bool IdPersistent { get; init; }
}

/// <summary>A rectangle, in the units of the monitors.</summary>
public sealed record AltaRect(int X, int Y, int Width, int Height);

/// <summary>A secondary window to open.</summary>
public sealed record AltaWindowOpenRequest
{
    /// <summary>Gets the id of a saved window that is closed, which opens again; null for a new window.</summary>
    public string? SavedWindowId { get; init; }

    /// <summary>Gets the name of a new window; a named window is saved. Null for none.</summary>
    public string? Name { get; init; }

    /// <summary>Gets the icon of a new window; null for none.</summary>
    public string? Icon { get; init; }

    /// <summary>Gets the color of a new window; null for none.</summary>
    public string? Color { get; init; }

    /// <summary>Gets the monitor it opens on; null for the default place.</summary>
    public string? MonitorId { get; init; }

    /// <summary>Gets the tabs moved into it once it is open.</summary>
    public IReadOnlyList<string> Tabs { get; init; } = [];
}

/// <summary>Where a window goes. What is null does not change.</summary>
public sealed record AltaWindowPlacement
{
    /// <summary>Gets the monitor it goes to.</summary>
    public string? MonitorId { get; init; }

    /// <summary>Gets its state: <c>normal</c>, <c>maximized</c>, <c>minimized</c> or <c>fullscreen</c>.</summary>
    public string? State { get; init; }

    /// <summary>Gets its position, from the top-left corner of the work area of its monitor, and its size.</summary>
    public AltaRect? Bounds { get; init; }

    /// <summary>Gets a value indicating whether it comes to the current virtual desktop.</summary>
    public bool CurrentDesktop { get; init; }
}

/// <summary>What changes of a window. What is null does not change; an empty text takes the value away.</summary>
public sealed record AltaWindowUpdate
{
    /// <summary>Gets its name; a window that is given a name is saved.</summary>
    public string? Name { get; init; }

    /// <summary>Gets its icon.</summary>
    public string? Icon { get; init; }

    /// <summary>Gets its color.</summary>
    public string? Color { get; init; }

    /// <summary>Gets whether it is kept when it is closed.</summary>
    public bool? Saved { get; init; }

    /// <summary>Gets the monitor it goes to whenever it is connected; an empty text for where it was.</summary>
    public string? PinnedMonitorId { get; init; }
}

/// <summary>What became of a request about a window.</summary>
/// <param name="Status">The status: <c>ok</c>, or why not.</param>
/// <param name="Window">The window as it is now, with <c>ok</c> when there is one.</param>
public sealed record AltaWindowResult(string Status, AltaWindow? Window = null);

/// <summary>What became of a move of a tab.</summary>
/// <param name="Status">The status: <c>ok</c>, or why not.</param>
/// <param name="WindowId">The window that has the tab now, with <c>ok</c>.</param>
public sealed record AltaTabMoveResult(string Status, string? WindowId = null);
