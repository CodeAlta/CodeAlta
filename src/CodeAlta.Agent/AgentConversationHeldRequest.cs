using System.Text.Json;

namespace CodeAlta.Agent;

/// <summary>
/// Where the process that holds a conversation of a provider runs, as the user knows it.
/// </summary>
public enum AgentConversationHolderOrigin
{
    /// <summary>The origin is not known: only a live process is.</summary>
    Unknown,

    /// <summary>An editor extension of the provider (VS Code).</summary>
    VsCode,

    /// <summary>The command line of the provider, in a terminal.</summary>
    Terminal,

    /// <summary>Another application that runs the provider.</summary>
    OtherApplication,

    /// <summary>A background session of the provider, which refuses to give the conversation to another process.</summary>
    BackgroundSession,
}

/// <summary>
/// What the user chooses when the conversation a run is to continue is held by another process.
/// </summary>
public enum AgentConversationHeldChoice
{
    /// <summary>The run waits until the other process releases the conversation, then continues it.</summary>
    Wait,

    /// <summary>The run continues a copy of the conversation, which the session follows from then on.</summary>
    ContinueAsCopy,

    /// <summary>The run continues the conversation all the same: both processes then write it.</summary>
    OpenAnyway,

    /// <summary>The run ends, cancelled by the user.</summary>
    Cancel,
}

/// <summary>
/// The process that holds a conversation of a provider, as far as the provider can tell.
/// </summary>
/// <param name="Origin">Where it runs.</param>
/// <param name="OriginText">Where it runs, in words (<c>VS Code</c>, <c>a terminal</c>, ...), for messages.</param>
/// <param name="ProcessId">The identifier of the process, when known.</param>
/// <param name="Entrypoint">How the provider says the process was started, when known.</param>
/// <param name="Kind">The kind of session the provider says the process runs, when known.</param>
/// <param name="Status">What the provider says the process does (busy, idle, ...), when known.</param>
/// <param name="WorkingDirectory">The working directory of the process, when known.</param>
/// <param name="Name">The name of its session, when known.</param>
public sealed record AgentConversationHolder(
    AgentConversationHolderOrigin Origin,
    string OriginText,
    int? ProcessId = null,
    string? Entrypoint = null,
    string? Kind = null,
    string? Status = null,
    string? WorkingDirectory = null,
    string? Name = null);

/// <summary>
/// Asks the host what a run does when the conversation it is to continue is held by another process (the same
/// conversation open in an editor or a terminal). Continuing it there and here at the same time makes two processes
/// write one conversation.
/// </summary>
/// <param name="ProviderId">The model provider.</param>
/// <param name="SessionId">The session of the run.</param>
/// <param name="RunId">The run, when known.</param>
/// <param name="RequestId">A fresh identifier of this question.</param>
/// <param name="Timestamp">When the question was asked.</param>
/// <param name="ConversationId">The identifier the provider gives the conversation.</param>
/// <param name="Holder">The process that holds it.</param>
/// <param name="Choices">The choices offered, in the order to show them. <see cref="AgentConversationHeldChoice.Cancel"/> is always one.</param>
public sealed record AgentConversationHeldRequest(
    ModelProviderId ProviderId,
    string SessionId,
    AgentRunId? RunId,
    string RequestId,
    DateTimeOffset Timestamp,
    string ConversationId,
    AgentConversationHolder Holder,
    IReadOnlyList<AgentConversationHeldChoice> Choices);

/// <summary>
/// Answers a question of a run whose conversation is held by another process. The run waits for the answer as long
/// as it takes; it is cancelled with the run.
/// </summary>
/// <param name="request">The question.</param>
/// <param name="cancellationToken">Cancelled when the run ends.</param>
/// <returns>One of <see cref="AgentConversationHeldRequest.Choices"/>.</returns>
public delegate Task<AgentConversationHeldChoice> AgentConversationHeldHandler(
    AgentConversationHeldRequest request,
    CancellationToken cancellationToken);

/// <summary>
/// The session updates (<see cref="AgentSessionUpdateKind.Info"/>) a provider reports while a run waits for another
/// process to release a conversation (<see cref="AgentConversationHeldChoice.Wait"/>), and when it stops waiting.
/// They are transient: the run ends the wait in any case.
/// </summary>
public static class AgentConversationHeldUpdates
{
    /// <summary>The property of the details of the update that describes the wait.</summary>
    public const string DetailsProperty = "conversationHeld";

    /// <summary>The state of an update sent when the run starts to wait.</summary>
    public const string Waiting = "waiting";

    /// <summary>The state of an update sent when the other process released the conversation: the run continues.</summary>
    public const string Released = "released";

    /// <summary>Builds the details of an update.</summary>
    /// <param name="request">The question the user answered with <see cref="AgentConversationHeldChoice.Wait"/>.</param>
    /// <param name="state"><see cref="Waiting"/> or <see cref="Released"/>.</param>
    /// <returns>The details: <c>{"conversationHeld": {"state", "requestId", "conversationId", "origin", "originText", "processId"}}</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="state"/> is not one of the states.</exception>
    public static JsonElement CreateDetails(AgentConversationHeldRequest request, string state)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (state is not (Waiting or Released)) throw new ArgumentException("Unknown state of a wait.", nameof(state));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartObject(DetailsProperty);
            writer.WriteString("state", state);
            writer.WriteString("requestId", request.RequestId);
            writer.WriteString("conversationId", request.ConversationId);
            writer.WriteString("origin", request.Holder.Origin.ToString());
            writer.WriteString("originText", request.Holder.OriginText);
            if (request.Holder.ProcessId is { } processId) writer.WriteNumber("processId", processId);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    /// <summary>Reads the state of a wait from the details of a session update.</summary>
    /// <param name="details">The details of the update, or null.</param>
    /// <param name="state">The state, <see cref="Waiting"/> or <see cref="Released"/>.</param>
    /// <param name="originText">Where the process that holds the conversation runs, in words.</param>
    /// <returns>Whether the update describes a wait.</returns>
    public static bool TryRead(JsonElement? details, out string state, out string originText)
    {
        state = originText = string.Empty;
        if (details is not { ValueKind: JsonValueKind.Object } element ||
            !element.TryGetProperty(DetailsProperty, out var wait) || wait.ValueKind != JsonValueKind.Object ||
            !wait.TryGetProperty("state", out var stateValue) || stateValue.ValueKind != JsonValueKind.String ||
            stateValue.GetString() is not { } read || read is not (Waiting or Released))
        {
            return false;
        }

        state = read;
        originText = wait.TryGetProperty("originText", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() ?? string.Empty : string.Empty;
        return true;
    }
}
