using System.Threading.Channels;
using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime.Actors;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Identifies one question of a run whose conversation another process holds: the runtime and the attachment the run
/// belongs to, and a fresh attempt identity. An answer must name all of it.
/// </summary>
/// <param name="RuntimeInstanceId">The runtime that owns the attachment.</param>
/// <param name="AttachmentGeneration">The ordinal of the attachment of the session the run belongs to.</param>
/// <param name="SessionId">The session.</param>
/// <param name="RunId">The run, as the provider names it; null when it names none.</param>
/// <param name="RequestId">The identifier the provider gave the question.</param>
/// <param name="AttemptId">A fresh identity: a reused provider identifier never reuses it.</param>
public sealed record SessionConversationHeldHandle(Guid RuntimeInstanceId, long AttachmentGeneration, string SessionId, string? RunId,
    string RequestId, Guid AttemptId);

/// <summary>An immutable copy of a pending question, for a host that shows it.</summary>
/// <param name="Handle">The identity an answer names.</param>
/// <param name="ProviderId">The provider of the session.</param>
/// <param name="Timestamp">When the provider asked.</param>
/// <param name="ConversationId">The identifier the provider gives the conversation.</param>
/// <param name="Holder">The process that holds it.</param>
/// <param name="Choices">The choices offered, in their order.</param>
public sealed record SessionConversationHeldSnapshot(SessionConversationHeldHandle Handle, string ProviderId, DateTimeOffset Timestamp,
    string ConversationId, AgentConversationHolder Holder, IReadOnlyList<AgentConversationHeldChoice> Choices);

/// <summary>
/// The questions of runs whose conversation another process holds (a Claude Code conversation open in VS Code), until
/// the user answers. A run waits as long as it takes, as for a permission; its question ends with the run, or with the
/// attachment of its session. The mailbox serializes register, list, resolve and cancel, never the wait of the user.
/// </summary>
public sealed class SessionConversationHeldService : IAsyncDisposable
{
    internal const int PendingLimit = 64;
    internal const int IdentityLimit = 128;

    private readonly OrchestrationMailboxActor _actor = new(128);
    private readonly Dictionary<SessionConversationHeldHandle, Pending> _pending = [];
    private int _disposeStarted;
    private bool _stopped;

    /// <summary>Initializes an owner with no session to ask: every question is refused until the runtime names the attachments.</summary>
    public SessionConversationHeldService()
    {
        Handler = AskAsync;
    }

    /// <summary>
    /// Gets the handler a session that can be asked is created with: it registers the question on the live attachment of
    /// the session and waits for the answer.
    /// </summary>
    public AgentConversationHeldHandler Handler { get; }

    /// <summary>
    /// Gets or sets what a question is asked on: the live attachment of its session, or null when the session has
    /// none. Set by the runtime that owns the attachments.
    /// </summary>
    internal Func<string, Target?>? ResolveTarget { get; set; }

    /// <summary>The attachment a question is asked on.</summary>
    /// <param name="RuntimeId">The runtime that owns it.</param>
    /// <param name="Attachment">The live attachment of the session.</param>
    /// <param name="ProviderId">The provider of the session.</param>
    internal sealed record Target(Guid RuntimeId, OwnedProviderEventForwarding.Attachment Attachment, ModelProviderId ProviderId);

    private async Task<AgentConversationHeldChoice> AskAsync(AgentConversationHeldRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = request is null || ResolveTarget is not { } resolve ? null : resolve(request.SessionId);
        var pending = target is null ? null : await ExecuteAsync(() => Register(request!, target), null).ConfigureAwait(false);
        if (pending is null)
        {
            throw new InvalidOperationException(
                "The conversation of this session is held by another process and the session cannot be asked what to do now. " +
                "Close the conversation there, then send the message again.");
        }

        // The question ends with the run and with the attachment of its session.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, target!.Attachment.Cancellation.Token);
        try
        {
            return await pending.Completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            await ExecuteAsync(() => Complete(pending.Snapshot.Handle, null), false).ConfigureAwait(false);
        }
    }

    private Pending? Register(AgentConversationHeldRequest request, Target target)
    {
        var attachment = target.Attachment;
        if (_stopped || _pending.Count >= PendingLimit || attachment.IsRetiring || attachment.Cancellation.IsCancellationRequested
            || target.RuntimeId == Guid.Empty || attachment.Ordinal is <= 0 or > 9007199254740991
            || !string.Equals(attachment.Identity.SessionId, request.SessionId, StringComparison.Ordinal)
            || !string.Equals(request.ProviderId.Value, target.ProviderId.Value, StringComparison.OrdinalIgnoreCase)
            || !OwnedUserInputValidation.Text(request.SessionId, IdentityLimit, identity: true)
            || !OwnedUserInputValidation.Text(request.RequestId, IdentityLimit, identity: true)
            || (request.RunId is { } run && !OwnedUserInputValidation.Text(run.Value, IdentityLimit, identity: true))
            || !OwnedUserInputValidation.Text(request.ConversationId, IdentityLimit, identity: true)
            || request.Holder is null || request.Choices is not { Count: > 0 and <= 4 } choices
            || !choices.Contains(AgentConversationHeldChoice.Cancel) || choices.Distinct().Count() != choices.Count)
        {
            return null;
        }

        var handle = new SessionConversationHeldHandle(target.RuntimeId, attachment.Ordinal, request.SessionId, request.RunId?.Value,
            request.RequestId, Guid.NewGuid());
        var pending = new Pending(new SessionConversationHeldSnapshot(handle, request.ProviderId.Value, request.Timestamp,
            request.ConversationId, request.Holder, Array.AsReadOnly(choices.ToArray())));
        _pending.Add(handle, pending);
        return pending;
    }

    private bool Complete(SessionConversationHeldHandle handle, AgentConversationHeldChoice? choice)
    {
        if (!_pending.TryGetValue(handle, out var pending)) return false;
        if (choice is { } chosen && !pending.Snapshot.Choices.Contains(chosen)) return false;
        _pending.Remove(handle);
        // Without a choice the question ended with its run: the run sees its own cancellation, not this.
        pending.Completion.TrySetResult(choice ?? AgentConversationHeldChoice.Cancel);
        return choice is not null;
    }

    /// <summary>Lists the pending questions of a session, at most four.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancels the query before the owner reads.</param>
    /// <returns>The questions, oldest first.</returns>
    /// <exception cref="ArgumentException">The session identity is invalid.</exception>
    /// <exception cref="OperationCanceledException">The query was cancelled.</exception>
    public ValueTask<IReadOnlyList<SessionConversationHeldSnapshot>> ListAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!OwnedUserInputValidation.Text(sessionId, IdentityLimit, identity: true)) throw new ArgumentException("Invalid session identity.", nameof(sessionId));
        cancellationToken.ThrowIfCancellationRequested();
        return ExecuteAsync<IReadOnlyList<SessionConversationHeldSnapshot>>(() => Array.AsReadOnly(_pending.Values
                .Where(pending => pending.Snapshot.Handle.SessionId == sessionId)
                .Select(static pending => pending.Snapshot)
                .OrderBy(static snapshot => snapshot.Timestamp)
                .Take(4)
                .ToArray()),
            Array.Empty<SessionConversationHeldSnapshot>());
    }

    /// <summary>Lists the sessions with a question that waits for the user.</summary>
    /// <returns>The identifiers of the sessions.</returns>
    public ValueTask<IReadOnlyList<string>> ListWaitingSessionsAsync()
        => ExecuteAsync<IReadOnlyList<string>>(
            () => Array.AsReadOnly(_pending.Keys.Select(static handle => handle.SessionId).Distinct(StringComparer.Ordinal).ToArray()),
            Array.Empty<string>());

    /// <summary>Answers a pending question once, with one of the choices it offered.</summary>
    /// <param name="handle">The exact identity of the question.</param>
    /// <param name="choice">The choice.</param>
    /// <param name="cancellationToken">Cancels the request before the owner decides.</param>
    /// <returns>Whether the answer was taken: false for a question that ended or was answered, or a choice it did not offer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handle"/> is null.</exception>
    /// <exception cref="OperationCanceledException">The request was cancelled before the owner decided.</exception>
    public ValueTask<bool> ResolveAsync(SessionConversationHeldHandle handle, AgentConversationHeldChoice choice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        cancellationToken.ThrowIfCancellationRequested();
        return ExecuteAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Enum.IsDefined(choice) && Complete(handle, choice);
        }, false);
    }

    /// <summary>Ends the owner: every pending question is answered with Cancel.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        try
        {
            await _actor.AskAsync(_ =>
            {
                _stopped = true;
                foreach (var pending in _pending.Values) pending.Completion.TrySetResult(AgentConversationHeldChoice.Cancel);
                _pending.Clear();
                return ValueTask.FromResult(true);
            }).ConfigureAwait(false);
        }
        finally
        {
            await _actor.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<T> ExecuteAsync<T>(Func<T> command, T stoppedResult)
    {
        if (Volatile.Read(ref _disposeStarted) != 0) return stoppedResult;
        try
        {
            return await _actor.AskAsync(_ => ValueTask.FromResult(command())).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposeStarted) != 0)
        {
            return stoppedResult;
        }
        catch (ChannelClosedException) when (Volatile.Read(ref _disposeStarted) != 0)
        {
            return stoppedResult;
        }
    }

    private sealed class Pending(SessionConversationHeldSnapshot snapshot)
    {
        internal SessionConversationHeldSnapshot Snapshot { get; } = snapshot;
        internal TaskCompletionSource<AgentConversationHeldChoice> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
