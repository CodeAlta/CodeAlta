namespace CodeAlta.Workspaces;

/// <summary>
/// Represents the durable status of a work thread.
/// </summary>
public enum WorkThreadStatus
{
    /// <summary>
    /// The thread exists but has not received the first prompt yet.
    /// </summary>
    Draft,

    /// <summary>
    /// The thread is currently active.
    /// </summary>
    Active,

    /// <summary>
    /// The thread is waiting for more work.
    /// </summary>
    Waiting,

    /// <summary>
    /// The thread is blocked on input or approval.
    /// </summary>
    Blocked,

    /// <summary>
    /// The thread is continuing in the background.
    /// </summary>
    Background,

    /// <summary>
    /// The thread is complete.
    /// </summary>
    Completed,

    /// <summary>
    /// The thread has been archived.
    /// </summary>
    Archived,
}
