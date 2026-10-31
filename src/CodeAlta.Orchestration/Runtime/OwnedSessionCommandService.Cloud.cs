using CodeAlta.Agent;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

public sealed partial class OwnedSessionCommandService
{
    /// <summary>
    /// Starts a cloud session of a project and records it as a session of the project that runs in the cloud
    /// (<see cref="SessionRuntimeService.CreateCloudSessionAsync"/>).
    /// </summary>
    /// <param name="project">The project.</param>
    /// <param name="provider">The provider, which must support cloud sessions.</param>
    /// <param name="spaceId">The space of the session, or null.</param>
    /// <param name="title">The title given, or null.</param>
    /// <param name="task">What the cloud session is to do.</param>
    /// <param name="terminal">The terminal of the host, or null.</param>
    /// <param name="onShown">Called when the terminal is shown to the user, or null.</param>
    /// <param name="cancellationToken">Ends the start.</param>
    /// <returns>The outcome; <c>closed</c> when the owner is closing.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The task is empty.</exception>
    public async Task<CloudSessionStartResult> CreateCloudSessionAsync(ProjectDescriptor project, ModelProviderDescriptor provider, string? spaceId, string? title,
        string task, IAgentInteractiveTerminal? terminal, Action? onShown, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_closed || _retained) return new("closed");
        }

        try { return await _runtime.CreateCloudSessionAsync(project, provider, spaceId, title, task, terminal, onShown, cancellationToken).ConfigureAwait(false); }
        catch (ObjectDisposedException) { return new("closed"); }
    }

    /// <summary>Reads what a session holds of the cloud session it runs as, or ran as.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Ends the read.</param>
    /// <returns>The details, or null.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    public async Task<CloudSessionDetails?> GetCloudSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        try { return await _runtime.GetCloudSessionAsync(sessionId, cancellationToken).ConfigureAwait(false); }
        catch (ObjectDisposedException) { return null; }
    }

    /// <summary>Sends a message to the cloud session a session runs as (<see cref="SessionRuntimeService.SendToCloudSessionAsync"/>).</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="message">The message.</param>
    /// <returns>The outcome; <c>closed</c> when the owner is closing.</returns>
    /// <exception cref="ArgumentException">An argument is empty.</exception>
    public async Task<CloudSessionSendResult> SendToCloudSessionAsync(string sessionId, string message)
    {
        lock (_gate)
        {
            if (_closed || _retained) return new("closed");
            if (Reserved(sessionId)) return new("busy");
        }

        try { return await _runtime.SendToCloudSessionAsync(sessionId, message).ConfigureAwait(false); }
        catch (ObjectDisposedException) { return new("closed"); }
    }

    /// <summary>Brings the cloud session a session runs as back to this computer (<see cref="SessionRuntimeService.ContinueCloudSessionHereAsync"/>).</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="worktree">A worktree of the project made for it, or null for the folder of the session.</param>
    /// <param name="fromTerminal">Whether the user brings it back in a terminal.</param>
    /// <returns>The outcome; <c>closed</c> when the owner is closing.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    public async Task<CloudSessionContinueResult> ContinueCloudSessionHereAsync(string sessionId, string? worktree, bool fromTerminal)
    {
        lock (_gate)
        {
            if (_closed || _retained) return new("closed");
            if (Reserved(sessionId) || _active.ContainsKey(sessionId)) return new("busy");
        }

        try { return await _runtime.ContinueCloudSessionHereAsync(sessionId, worktree, fromTerminal).ConfigureAwait(false); }
        catch (ObjectDisposedException) { return new("closed"); }
        catch (SessionLeaseUnavailableException) { return new("busy"); }
    }
}
