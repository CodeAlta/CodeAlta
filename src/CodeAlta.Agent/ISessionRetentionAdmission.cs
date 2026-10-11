namespace CodeAlta.Agent;

/// <summary>
/// Claims session work before it becomes visible or runnable, mutually exclusively with automatic retirement.
/// Claims must cover the original operation and every outstanding handoff, not only a cancelable waiter.
/// </summary>
public interface ISessionRetentionAdmission
{
    /// <summary>
    /// Claims a canonical session identity until the returned lease is disposed, or returns null if retirement owns it.
    /// A refused caller must not prepare a provider, publish work, or act on the session.
    /// </summary>
    /// <exception cref="ArgumentException">The identity is empty or has surrounding whitespace.</exception>
    IDisposable? TryProtectSession(string sessionId);

    /// <summary>
    /// Claims work whose target is not known yet, protecting every session until the lease is disposed.
    /// Returns null when retirement already owns any session; a refused caller must not start the work.
    /// Transfer to narrower claims must overlap this lease, never leave an admission gap.
    /// </summary>
    IDisposable? TryProtectAll();
}
