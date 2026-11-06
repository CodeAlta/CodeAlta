namespace CodeAlta.Browser.Drivers;

/// <summary>
/// A tab that shows the user when an agent acts in it: a glow around the page and a label while the agent acts, and a
/// cursor that moves to each point of its pointer input before the input. The driver moves the cursor with the input
/// itself; the tools say when an action starts and ends. A tab of a browser without a window shows nothing.
/// </summary>
internal interface IAgentActivityPage
{
    /// <summary>Gets whether the tab shows the agent at all: its browser has a window the user can look at.</summary>
    bool ShowsAgent { get; }

    /// <summary>
    /// Shows that the agent acts in the tab, in the document it has now, until it has not acted for a while. Never fails:
    /// a document that cannot show it, or that does not answer soon, is left as it is.
    /// </summary>
    Task ShowAgentActivityAsync(CancellationToken cancellationToken);
}
