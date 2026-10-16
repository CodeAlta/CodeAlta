using CodeAlta.Agent.Runtime;
using XenoAtom.Logging;

namespace CodeAlta.Agent.Claude;

// A conversation of the CLI open in another Claude process (VS Code, a terminal): the CLI resumes it all the same and
// both processes then write it, so the user is asked first.
internal sealed partial class ClaudeCodeSession
{
    private static readonly Logger HeldLogger = LogManager.GetLogger("CodeAlta.ClaudeCode");
    private static readonly AgentConversationHeldChoice[] AllHeldChoices =
        [AgentConversationHeldChoice.Wait, AgentConversationHeldChoice.ContinueAsCopy, AgentConversationHeldChoice.OpenAnyway, AgentConversationHeldChoice.Cancel];
    // A background session of Claude Code refuses to give the conversation to another process: it cannot be opened anyway.
    private static readonly AgentConversationHeldChoice[] BackgroundHeldChoices =
        [AgentConversationHeldChoice.Wait, AgentConversationHeldChoice.ContinueAsCopy, AgentConversationHeldChoice.Cancel];

    /// <summary>Who is asked when the conversation is held, and where the wait is shown.</summary>
    /// <param name="Check">Whether the registry is looked at: not in a turn the CLI started by itself.</param>
    /// <param name="Handler">Asks the user; null when nobody can be asked: the start then fails with a message.</param>
    /// <param name="RunId">The run that asks.</param>
    /// <param name="OnSessionUpdate">Shows the wait.</param>
    private sealed record HeldConversationAsk(
        bool Check,
        AgentConversationHeldHandler? Handler,
        AgentRunId? RunId,
        Func<AgentTurnSessionUpdate, CancellationToken, ValueTask> OnSessionUpdate)
    {
        public static HeldConversationAsk Nobody { get; } = new(true, null, null, static (_, _) => ValueTask.CompletedTask);
    }

    // What a turn asks with: the handler of its run.
    private HeldConversationAsk CreateHeldAsk(Func<AgentTurnSessionUpdate, CancellationToken, ValueTask> onSessionUpdate)
    {
        AgentProviderRunContext? run;
        lock (_gate)
        {
            run = _run;
        }

        return new HeldConversationAsk(run?.ProviderInitiated != true, run?.OnConversationHeld, run?.RunId, onSessionUpdate);
    }

    /// <summary>
    /// Looks whether another live process holds the conversation about to be resumed and, when one does, asks what to
    /// do. Returns when the conversation can be started: free, released after a wait, opened anyway, or to be continued
    /// as a copy (the branch fields then fork it). A conversation that is not started this time is bound again from the
    /// recorded state by the next turn, which sends the prompts of this one again.
    /// </summary>
    /// <returns>Whether the user chose to wait: a refusal of the CLI is then waited out without asking again.</returns>
    private async Task<bool> ResolveHeldConversationAsync(string cliSessionId, HeldConversationAsk ask, CancellationToken cancellationToken)
    {
        if (!ask.Check)
        {
            return false;
        }

        var hold = _liveSessions.FindHolder(cliSessionId);
        if (hold.Status == ClaudeCodeSessionHoldStatus.Unknown)
        {
            // A registry this side cannot read never blocks a session.
            HeldLogger.Warn($"Whether another Claude process holds the conversation {cliSessionId} is not known; it is resumed. {hold.Reason ?? string.Empty}");
            return false;
        }

        if (hold is not { Status: ClaudeCodeSessionHoldStatus.Held, Holder: { } holder })
        {
            return false;
        }

        var request = CreateHeldRequest(cliSessionId, ToAgentHolder(holder), ask.RunId);
        return await AskHeldAsync(ask, request, cancellationToken).ConfigureAwait(false) switch
        {
            AgentConversationHeldChoice.Wait => await WaitForReleaseAsync(ask, request, cancellationToken).ConfigureAwait(false),
            AgentConversationHeldChoice.ContinueAsCopy => ContinueAsCopy(cliSessionId),
            _ => false,
        };
    }

    /// <summary>
    /// The refusals of the CLI to resume a conversation during one start: the poll intervals to wait before the next try,
    /// and the wait shown for them, whose end is shown once the CLI resumes the conversation.
    /// </summary>
    private sealed class BackgroundRefusals
    {
        public int Backoff { get; set; } = 1;

        public AgentConversationHeldRequest? Shown { get; set; }
    }

    // The waits between two starts the CLI refused grow up to this many poll intervals.
    private const int MaxRefusalBackoff = 16;

    /// <summary>
    /// The CLI refused to resume the conversation because a background session of Claude Code runs it. Asks, or waits
    /// again when the user already chose to wait, and returns to try the start again; throws when it is not tried again.
    /// </summary>
    /// <remarks>
    /// The registry may not list the background session: only a refusal of the CLI tells that it still runs. The wait is
    /// shown once, its end only when the CLI resumes the conversation (<see cref="ShowRefusalsEndedAsync"/>), and the start
    /// is tried again later and later while it is refused, until the user stops the run.
    /// </remarks>
    private async Task<bool> ResolveBackgroundRefusalAsync(string cliSessionId, HeldConversationAsk ask, bool waiting, BackgroundRefusals refusals, string detail,
        Exception refusal, CancellationToken cancellationToken)
    {
        if (ask.Handler is null)
        {
            ForgetBinding();
            throw new InvalidOperationException(
                $"Claude Code refused to resume the conversation {cliSessionId}: a background session of Claude Code is running it. " +
                $"Continue it there, or stop it, then send the message again. {detail}", refusal);
        }

        var hold = _liveSessions.FindHolder(cliSessionId);
        var holder = hold is { Status: ClaudeCodeSessionHoldStatus.Held, Holder: { } found } ? ToAgentHolder(found) : null;
        var request = CreateHeldRequest(cliSessionId,
            holder is { Origin: AgentConversationHolderOrigin.BackgroundSession }
                ? holder
                : new AgentConversationHolder(AgentConversationHolderOrigin.BackgroundSession, BackgroundOriginText),
            ask.RunId);
        if (!waiting && await AskHeldAsync(ask, request, cancellationToken).ConfigureAwait(false) == AgentConversationHeldChoice.ContinueAsCopy)
        {
            return ContinueAsCopy(cliSessionId);
        }

        // The user waits: the background session still runs the conversation.
        try
        {
            if (refusals.Shown is null)
            {
                refusals.Shown = request;
                await ShowWaitAsync(ask, request, AgentConversationHeldUpdates.Waiting, cancellationToken).ConfigureAwait(false);
            }

            if (hold.Status == ClaudeCodeSessionHoldStatus.Held)
            {
                // The registry lists it: the start is tried again once it is gone from there.
                while (_liveSessions.FindHolder(cliSessionId).Status == ClaudeCodeSessionHoldStatus.Held)
                {
                    await Task.Delay(_options.HeldConversationPollInterval, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await Task.Delay(_options.HeldConversationPollInterval * refusals.Backoff, cancellationToken).ConfigureAwait(false);
                refusals.Backoff = Math.Min(refusals.Backoff * 2, MaxRefusalBackoff);
            }
        }
        catch
        {
            ForgetBinding();
            throw;
        }

        return true;
    }

    // The CLI resumed the conversation it refused before: the end of the wait shown for those refusals is shown.
    private async Task ShowRefusalsEndedAsync(HeldConversationAsk ask, BackgroundRefusals refusals, CancellationToken cancellationToken)
    {
        if (refusals.Shown is { } shown)
        {
            refusals.Shown = null;
            await ShowWaitAsync(ask, shown, AgentConversationHeldUpdates.Released, cancellationToken).ConfigureAwait(false);
        }
    }

    private const string BackgroundOriginText = "a background session of Claude Code";

    internal static AgentConversationHolder ToAgentHolder(ClaudeCodeSessionHolder holder)
    {
        var background = holder.Kind is "bg" or "daemon" or "daemon-worker";
        var origin = background
            ? AgentConversationHolderOrigin.BackgroundSession
            : holder.Origin switch
            {
                ClaudeCodeHolderOrigin.VsCode => AgentConversationHolderOrigin.VsCode,
                ClaudeCodeHolderOrigin.Terminal => AgentConversationHolderOrigin.Terminal,
                ClaudeCodeHolderOrigin.OtherApplication => AgentConversationHolderOrigin.OtherApplication,
                _ => AgentConversationHolderOrigin.Unknown,
            };
        return new AgentConversationHolder(origin, background ? BackgroundOriginText : holder.OriginText, holder.Pid, holder.Entrypoint, holder.Kind,
            holder.Status, holder.WorkingDirectory, holder.Name);
    }

    private AgentConversationHeldRequest CreateHeldRequest(string cliSessionId, AgentConversationHolder holder, AgentRunId? runId)
        => new(_providerId, _sessionId, runId, Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow, cliSessionId, holder,
            holder.Origin == AgentConversationHolderOrigin.BackgroundSession ? BackgroundHeldChoices : AllHeldChoices);

    // Asks the user. A run with nobody to ask fails with what the user can do; an answer the question did not offer is
    // taken for Cancel. A conversation that is not started is bound again by the next turn, with the prompts of this one.
    private async Task<AgentConversationHeldChoice> AskHeldAsync(HeldConversationAsk ask, AgentConversationHeldRequest request, CancellationToken cancellationToken)
    {
        if (ask.Handler is not { } handler)
        {
            ForgetBinding();
            throw new InvalidOperationException(DescribeHeldWithoutAsking(request));
        }

        AgentConversationHeldChoice choice;
        try
        {
            choice = await handler(request, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ForgetBinding();
            throw;
        }

        if (choice == AgentConversationHeldChoice.Cancel || !request.Choices.Contains(choice))
        {
            ForgetBinding();
            throw new OperationCanceledException($"The user cancelled the run: the Claude Code conversation {request.ConversationId} is open in {request.Holder.OriginText}.");
        }

        return choice;
    }

    internal static string DescribeHeldWithoutAsking(AgentConversationHeldRequest request)
    {
        var process = request.Holder.ProcessId is { } pid ? $" (process {pid})" : string.Empty;
        var anyway = request.Choices.Contains(AgentConversationHeldChoice.OpenAnyway) ? ", or open it here anyway" : string.Empty;
        return $"The Claude Code conversation {request.ConversationId} is open in {request.Holder.OriginText}{process}: continuing it here too " +
               "would make two processes write it, and this run has nobody to ask what to do. " +
               $"Close it there or wait until it is released, then send the message again; or send it from CodeAlta Desktop to continue it as a copy{anyway}.";
    }

    // Polls the registry under the token of the run until no other process holds the conversation. The wait is shown
    // with a session update, and its end with another.
    private async Task<bool> WaitForReleaseAsync(HeldConversationAsk ask, AgentConversationHeldRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await ShowWaitAsync(ask, request, AgentConversationHeldUpdates.Waiting, cancellationToken).ConfigureAwait(false);
            while (_liveSessions.FindHolder(request.ConversationId).Status == ClaudeCodeSessionHoldStatus.Held)
            {
                await Task.Delay(_options.HeldConversationPollInterval, cancellationToken).ConfigureAwait(false);
            }

            await ShowWaitAsync(ask, request, AgentConversationHeldUpdates.Released, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ForgetBinding();
            throw;
        }

        return true;
    }

    // Shows the start (Waiting) or the end (Released) of a wait for another process to release the conversation.
    private static ValueTask ShowWaitAsync(HeldConversationAsk ask, AgentConversationHeldRequest request, string state, CancellationToken cancellationToken)
        => ask.OnSessionUpdate(new AgentTurnSessionUpdate
        {
            Kind = AgentSessionUpdateKind.Info,
            Message = state == AgentConversationHeldUpdates.Waiting
                ? $"Waiting for {request.Holder.OriginText} to release the Claude Code conversation."
                : $"{Capitalize(request.Holder.OriginText)} released the Claude Code conversation: it is continued here.",
            Details = AgentConversationHeldUpdates.CreateDetails(request, state),
        }, cancellationToken);

    /// <summary>
    /// After a wait for another process to release the conversation, ends the run when that process added turns to it.
    /// The prompt of the run was written in the journal of the session before them: resumed now, the run would answer
    /// after turns the session does not show, and those turns, before its prompt in the conversation, could no longer be
    /// added to the session. Ended, the session is idle: the next message first adds those turns to it (its catch-up),
    /// then is sent. A session whose next turn rewinds or forks the conversation resumes it at its recorded leaf, before
    /// whatever was written after it: nothing is looked at, as for the catch-up.
    /// </summary>
    /// <exception cref="InvalidOperationException">Turns were added while the run waited.</exception>
    private void RefuseTurnsMadeWhileWaiting(string? workingDirectory, string cliSessionId)
    {
        if (_branchPending)
        {
            return;
        }

        AgentExternalTurns read;
        try
        {
            read = ClaudeCodeExternalTurns.Read(
                _options.ResolveConfigRoot(),
                CurrentProviderState(cliSessionId),
                workingDirectory ?? Environment.CurrentDirectory,
                _positions,
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A transcript that cannot be read now is looked at again by the next catch-up: the run goes on.
            HeldLogger.Warn($"Whether turns were added to the Claude Code conversation {cliSessionId} while CodeAlta waited is not known: {ex.Message}");
            return;
        }

        if (read is not { Status: AgentExternalTurnsStatus.Read, Turns: [var first, ..] })
        {
            return;
        }

        ForgetBinding();
        var where = first.Origin switch
        {
            AgentExternalTurnOrigin.VsCode => " in VS Code",
            AgentExternalTurnOrigin.Terminal => " in a terminal",
            AgentExternalTurnOrigin.OtherApplication => $" in another application ({first.Entrypoint})",
            _ => string.Empty,
        };
        throw new AgentConversationChangedException(
            $"Turns were added to the Claude Code conversation {cliSessionId}{where} while this run waited for it. " +
            "CodeAlta adds them to this session before the next message: send the message again.");
    }

    // The run continues a copy of the conversation, as a fork: of the whole of it, or at the point a pending rewind of
    // the session resumes it at. The CLI names the copy when it starts, and the session records that name with the
    // first answer.
    private bool ContinueAsCopy(string cliSessionId)
    {
        _branchAt = _branchPending ? _branchAt : null;
        _branchPending = true;
        _branchFork = true;
        _branchSource = cliSessionId;
        return false;
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
