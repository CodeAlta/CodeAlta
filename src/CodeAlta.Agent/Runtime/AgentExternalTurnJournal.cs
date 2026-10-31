using System.Text.Json;

namespace CodeAlta.Agent.Runtime;

/// <summary>
/// Writes the turns made outside CodeAlta as the records a session writes for its own turns, so that the timeline shows
/// them and the conversation read back from the journal holds them. No filesystem access here, apart from what the
/// callbacks given do.
/// </summary>
internal static class AgentExternalTurnJournal
{
    /// <summary>The notice of a conversation changed outside CodeAlta in a way that cannot be followed.</summary>
    internal const string CannotFollowNotice =
        "This conversation was changed outside CodeAlta in a way it cannot follow; the model has the full conversation.";

    /// <summary>What was prepared to be appended.</summary>
    /// <param name="Events">The records, in order, the state snapshots included.</param>
    /// <param name="Turns">The turns written (a compaction counts as one).</param>
    /// <param name="Users">The user messages written.</param>
    /// <param name="Assistants">The assistant messages written.</param>
    /// <param name="Preview">The text of the last assistant message written, for the preview of the session.</param>
    /// <param name="State">The state of the session after the records.</param>
    internal sealed record Prepared(IReadOnlyList<AgentEvent> Events, int Turns, int Users, int Assistants, string? Preview, AgentSessionState State);

    /// <summary>Prepares the records of what a read of the turns made outside CodeAlta found.</summary>
    /// <param name="providerId">The provider of the session.</param>
    /// <param name="state">The state the session recorded last: the read followed from it.</param>
    /// <param name="read">What the read found: <see cref="AgentExternalTurnsStatus.Read"/> or <see cref="AgentExternalTurnsStatus.CannotFollow"/>.</param>
    /// <param name="workingDirectory">The folder the session works in, for the files the tool calls name.</param>
    /// <param name="saveImage">Saves an image of a prompt beside the journal and returns the input item that names it.</param>
    /// <param name="prepareToolResult">Saves the images of a tool result beside the journal and returns the result that names them.</param>
    /// <param name="now">The time of the records whose turn has none, and of the states.</param>
    /// <param name="cancellationToken">Stops the preparation.</param>
    /// <returns>The records to append.</returns>
    /// <remarks>
    /// Each turn has a run of its own, whose identifier starts with <see cref="AgentSessionTurn.ExternalRunPrefix"/>,
    /// and ends with an <see cref="AgentSessionUpdateKind.Idle"/> record without usage: the provider reported no usage
    /// for it, so nothing of it is counted as usage of CodeAlta. Its prompt is named with
    /// <see cref="AgentSessionTurn.ExternalPromptPrefix"/> and the place it was made; a turn the provider started by
    /// itself has a prompt of the provider (<see cref="AgentSessionTurn.ProviderPromptPrefix"/>). A state follows each
    /// turn, with the counts of the messages written so far, so that a fork or a rewind at a turn resumes the
    /// conversation of the provider at it.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="read"/> is not one that writes anything.</exception>
    /// <exception cref="OperationCanceledException">The preparation was stopped.</exception>
    internal static async Task<Prepared> PrepareAsync(
        ModelProviderId providerId,
        AgentSessionState state,
        AgentExternalTurns read,
        string? workingDirectory,
        Func<AgentMessagePart.Data, CancellationToken, Task<AgentInputItem>> saveImage,
        Func<AgentToolResult, CancellationToken, Task<AgentToolResult>> prepareToolResult,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(saveImage);
        ArgumentNullException.ThrowIfNull(prepareToolResult);
        if (read.Status is not (AgentExternalTurnsStatus.Read or AgentExternalTurnsStatus.CannotFollow) || read.ProviderStateAfter is not { } stateAfter)
        {
            throw new ArgumentException($"A read that found {read.Status} writes nothing.", nameof(read));
        }

        var events = new List<AgentEvent>();
        var sessionId = state.SessionId;
        if (read.Status == AgentExternalTurnsStatus.CannotFollow)
        {
            events.Add(new AgentSessionUpdateEvent(providerId, sessionId, now, null, AgentSessionUpdateKind.Warning, CannotFollowNotice));
            var followed = state with { ProviderState = stateAfter(0, 0, 0), UpdatedAt = now };
            events.Add(FileSystemAgentSessionStore.CreateStateSnapshotEvent(providerId, followed));
            return new Prepared(events, 0, 0, 0, null, followed);
        }

        var users = 0;
        var assistants = 0;
        var written = 0;
        string? preview = null;
        var current = state;
        // The calls a result answers: those of this read, and a placeholder for one made before it.
        var calls = new Dictionary<string, AgentMessagePart.ToolCall>(StringComparer.Ordinal);
        foreach (var turn in read.Turns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var at = turn.Timestamp ?? now;
            written++;
            if (turn.IsCompaction)
            {
                events.Add(new AgentSessionUpdateEvent(providerId, sessionId, at, null, AgentSessionUpdateKind.CompactionCompleted,
                    $"The conversation was compacted{Where(turn)}."));
                continue;
            }

            var runId = new AgentRunId($"{AgentSessionTurn.ExternalRunPrefix}{Guid.CreateVersion7()}");
            var messages = turn.Messages;
            var index = 0;
            if (messages.Count > 0 && messages[0].Role is AgentConversationRole.User)
            {
                events.AddRange(await CreatePromptAsync(providerId, sessionId, runId, at, messages[0].Parts, PromptContentId(turn), saveImage, cancellationToken)
                    .ConfigureAwait(false));
                users++;
                index = 1;
            }
            else if (turn.TriggerText is { } trigger)
            {
                events.AddRange(await CreatePromptAsync(providerId, sessionId, runId, at, [new AgentMessagePart.Text(trigger)],
                    $"{AgentSessionTurn.ProviderPromptPrefix}{Guid.CreateVersion7()}", saveImage, cancellationToken).ConfigureAwait(false));
                users++;
            }

            for (; index < messages.Count; index++)
            {
                var message = messages[index];
                switch (message.Role)
                {
                    case AgentConversationRole.User:
                        // A message sent while the turn ran, after its prompt.
                        events.AddRange(await CreatePromptAsync(providerId, sessionId, runId, at, message.Parts, PromptContentId(turn), saveImage, cancellationToken)
                            .ConfigureAwait(false));
                        users++;
                        break;
                    case AgentConversationRole.Assistant:
                        events.AddRange(CreateAssistant(providerId, sessionId, runId, at, message, workingDirectory, calls));
                        assistants++;
                        preview = message.Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value)
                            .LastOrDefault(static text => !string.IsNullOrWhiteSpace(text)) ?? preview;
                        break;
                    case AgentConversationRole.Tool:
                        events.AddRange(await CreateToolResultsAsync(providerId, sessionId, runId, at, message, workingDirectory, calls, prepareToolResult, cancellationToken)
                            .ConfigureAwait(false));
                        break;
                }
            }

            events.Add(new AgentSessionUpdateEvent(providerId, sessionId, at, runId, AgentSessionUpdateKind.Idle, null));
            current = current with { ProviderState = stateAfter(written, users, assistants), UpdatedAt = now };
            events.Add(FileSystemAgentSessionStore.CreateStateSnapshotEvent(providerId, current));
        }

        // The last state is the one of the whole read, with its position and its cost: the one of the last turn when it
        // is a turn of messages, otherwise (a compaction last, or nothing to show) one more.
        if (read.Turns is [.., { IsCompaction: false }])
        {
            return new Prepared(events, written, users, assistants, preview, current);
        }

        var final = current with { ProviderState = stateAfter(read.Turns.Count, users, assistants), UpdatedAt = now };
        events.Add(FileSystemAgentSessionStore.CreateStateSnapshotEvent(providerId, final));
        return new Prepared(events, written, users, assistants, preview, final);
    }

    private static string PromptContentId(AgentExternalTurn turn)
        => $"{AgentSessionTurn.ExternalPromptPrefix}{turn.Origin switch
        {
            AgentExternalTurnOrigin.VsCode => "vscode",
            AgentExternalTurnOrigin.Terminal => "terminal",
            AgentExternalTurnOrigin.OtherApplication => "other",
            AgentExternalTurnOrigin.Cloud => "cloud",
            _ => "unknown",
        }}:{Guid.CreateVersion7()}";

    private static string Where(AgentExternalTurn turn)
        => turn.Origin switch
        {
            AgentExternalTurnOrigin.VsCode => " in VS Code",
            AgentExternalTurnOrigin.Terminal => " in a terminal",
            AgentExternalTurnOrigin.OtherApplication => " in another application",
            AgentExternalTurnOrigin.Cloud => " in the cloud",
            _ => " outside CodeAlta",
        };

    private static async Task<AgentEvent[]> CreatePromptAsync(
        ModelProviderId providerId,
        string sessionId,
        AgentRunId runId,
        DateTimeOffset at,
        IReadOnlyList<AgentMessagePart> parts,
        string contentId,
        Func<AgentMessagePart.Data, CancellationToken, Task<AgentInputItem>> saveImage,
        CancellationToken cancellationToken)
    {
        var items = new List<AgentInputItem>(parts.Count);
        foreach (var part in parts)
        {
            switch (part)
            {
                case AgentMessagePart.Text text:
                    items.Add(new AgentInputItem.Text(text.Value));
                    break;
                case AgentMessagePart.Data data:
                    items.Add(await saveImage(data, cancellationToken).ConfigureAwait(false));
                    break;
                case AgentMessagePart.Uri uri:
                    items.Add(new AgentInputItem.ImageUrl(uri.Value));
                    break;
            }
        }

        var input = new AgentInput(items);
        var serialized = AgentSession.SerializeAgentInput(input);
        return
        [
            new AgentRawEvent(providerId, sessionId, at, "local.userMessage", serialized, runId),
            new AgentContentCompletedEvent(providerId, sessionId, at, runId, AgentContentKind.User, contentId, null,
                AgentSession.RenderUserInput(items), serialized)
            {
                // A turn the provider started by itself is recorded as such, as when it starts one in a run of CodeAlta.
                Provenance = contentId.StartsWith(AgentSessionTurn.ProviderPromptPrefix, StringComparison.Ordinal) ? AgentPromptProvenance.Provider : null,
            },
        ];
    }

    private static List<AgentEvent> CreateAssistant(
        ModelProviderId providerId,
        string sessionId,
        AgentRunId runId,
        DateTimeOffset at,
        AgentConversationMessage message,
        string? workingDirectory,
        Dictionary<string, AgentMessagePart.ToolCall> calls)
    {
        var events = new List<AgentEvent>
        {
            new AgentRawEvent(providerId, sessionId, at, "local.assistantMessage", AgentSession.SerializeLocalMessage(message), runId),
        };
        foreach (var part in message.Parts)
        {
            switch (part)
            {
                case AgentMessagePart.Text text when !string.IsNullOrWhiteSpace(text.Value):
                    events.Add(new AgentContentCompletedEvent(providerId, sessionId, at, runId, AgentContentKind.Assistant,
                        $"assistant:{Guid.CreateVersion7()}", null, text.Value));
                    break;
                case AgentMessagePart.Reasoning reasoning:
                    events.Add(new AgentContentCompletedEvent(providerId, sessionId, at, runId, AgentContentKind.Reasoning,
                        $"reasoning:{Guid.CreateVersion7()}", null, reasoning.Value ?? string.Empty, AgentSession.CreateReasoningDetails(reasoning)));
                    break;
                case AgentMessagePart.ToolCall call:
                    calls[call.CallId] = call;
                    events.Add(new AgentActivityEvent(providerId, sessionId, at, runId, AgentActivityKind.ToolCall, AgentActivityPhase.Requested,
                        call.CallId, null, call.Name, null, AgentSession.CreateToolCallDetails(call, workingDirectory)));
                    break;
            }
        }

        return events;
    }

    private static async Task<List<AgentEvent>> CreateToolResultsAsync(
        ModelProviderId providerId,
        string sessionId,
        AgentRunId runId,
        DateTimeOffset at,
        AgentConversationMessage message,
        string? workingDirectory,
        Dictionary<string, AgentMessagePart.ToolCall> calls,
        Func<AgentToolResult, CancellationToken, Task<AgentToolResult>> prepareToolResult,
        CancellationToken cancellationToken)
    {
        var events = new List<AgentEvent>();
        var parts = new List<AgentMessagePart>(message.Parts.Count);
        var outcomes = new List<AgentEvent>();
        foreach (var part in message.Parts)
        {
            if (part is not AgentMessagePart.ToolResult toolResult)
            {
                parts.Add(part);
                continue;
            }

            var result = await prepareToolResult(toolResult.Result, cancellationToken).ConfigureAwait(false);
            parts.Add(toolResult with { Result = result });
            var call = calls.GetValueOrDefault(toolResult.CallId)
                       ?? new AgentMessagePart.ToolCall(toolResult.CallId, "tool", JsonDocument.Parse("{}").RootElement.Clone());
            var details = AgentSession.CreateToolResultDetails(call, result, workingDirectory);
            // A session records the start of each call it runs, then its end and its output.
            events.Add(new AgentActivityEvent(providerId, sessionId, at, runId, AgentActivityKind.ToolCall, AgentActivityPhase.Started,
                call.CallId, null, call.Name, null, AgentSession.CreateToolCallDetails(call, workingDirectory)));
            outcomes.Add(new AgentActivityEvent(providerId, sessionId, at, runId, AgentActivityKind.ToolCall,
                result.Success ? AgentActivityPhase.Completed : AgentActivityPhase.Failed, call.CallId, null, call.Name, result.Error, details));
            outcomes.Add(new AgentContentCompletedEvent(providerId, sessionId, at, runId, AgentContentKind.ToolOutput, $"{call.CallId}:output",
                call.CallId, AgentSession.RenderToolResult(result), details));
        }

        events.Add(new AgentRawEvent(providerId, sessionId, at, "local.toolMessage",
            AgentSession.SerializeLocalMessage(message with { Parts = parts }), runId));
        events.AddRange(outcomes);
        return events;
    }
}
