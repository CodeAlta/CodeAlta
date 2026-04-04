namespace CodeAlta.Search;

/// <summary>
/// Identifies the source of a recent-usage event.
/// </summary>
public enum ProjectFileUsageAccessKind
{
    /// <summary>
    /// The item was accepted from the popup.
    /// </summary>
    PopupAccepted = 0,

    /// <summary>
    /// The item was inserted into a prompt.
    /// </summary>
    PromptInserted = 1,

    /// <summary>
    /// The item was opened in an editor flow.
    /// </summary>
    EditorOpened = 2,

    /// <summary>
    /// The item was opened from a command or picker flow.
    /// </summary>
    CommandOpened = 3,
}
