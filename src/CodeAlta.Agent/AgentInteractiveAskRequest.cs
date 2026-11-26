namespace CodeAlta.Agent;

/// <summary>
/// A structured interactive question request sent by a provider while a turn is running (such as an ACP
/// <c>elicitation/create</c> form request): the host presents the questions immediately and returns the answers to the
/// waiting provider so the turn can continue.
/// </summary>
/// <param name="ProviderId">The model provider identifier.</param>
/// <param name="SessionId">The session identifier.</param>
/// <param name="RunId">The active run identifier, when known.</param>
/// <param name="RequestId">A fresh identifier for this interactive question.</param>
/// <param name="Timestamp">When the question was asked.</param>
/// <param name="Message">Optional introductory message supplied by the provider.</param>
/// <param name="Questions">The questions to present, in order.</param>
public sealed record AgentInteractiveAskRequest(
    ModelProviderId ProviderId,
    string SessionId,
    AgentRunId? RunId,
    string RequestId,
    DateTimeOffset Timestamp,
    string? Message,
    IReadOnlyList<AgentInteractiveAskQuestion> Questions);

/// <summary>
/// One question in an <see cref="AgentInteractiveAskRequest"/>.
/// </summary>
/// <param name="Title">Short display/tab title.</param>
/// <param name="Question">The question text.</param>
/// <param name="Description">Optional explanatory text.</param>
/// <param name="Choices">Optional selectable choices.</param>
/// <param name="AllowMultiSelect">Whether the user may select more than one choice.</param>
/// <param name="Freeform">Optional freeform text input field.</param>
public sealed record AgentInteractiveAskQuestion(
    string Title,
    string Question,
    string? Description,
    IReadOnlyList<AgentInteractiveAskChoice> Choices,
    bool AllowMultiSelect = false,
    AgentInteractiveAskFreeform? Freeform = null);

/// <summary>
/// A selectable choice for an <see cref="AgentInteractiveAskQuestion"/>.
/// </summary>
/// <param name="Title">Choice label shown to the user.</param>
/// <param name="Description">Optional explanatory text.</param>
public sealed record AgentInteractiveAskChoice(
    string Title,
    string? Description = null);

/// <summary>
/// A freeform text input field for an <see cref="AgentInteractiveAskQuestion"/>.
/// </summary>
/// <param name="Title">Optional label for the text field.</param>
/// <param name="Placeholder">Optional placeholder text.</param>
public sealed record AgentInteractiveAskFreeform(
    string? Title = null,
    string? Placeholder = null);

/// <summary>
/// The user's answer to one question in an <see cref="AgentInteractiveAskRequest"/>.
/// </summary>
/// <param name="QuestionIndex">Zero-based index of the answered question.</param>
/// <param name="SelectedChoiceIndexes">Zero-based indexes of selected choices.</param>
/// <param name="FreeformText">Optional freeform answer text.</param>
public sealed record AgentInteractiveAskAnswer(
    int QuestionIndex,
    IReadOnlyList<int> SelectedChoiceIndexes,
    string? FreeformText = null);

/// <summary>
/// The user's response to an <see cref="AgentInteractiveAskRequest"/>.
/// </summary>
/// <param name="Cancelled">Whether the user dismissed or cancelled the form without submitting.</param>
/// <param name="Answers">Answers by question; empty when <paramref name="Cancelled"/> is <see langword="true"/>.</param>
public sealed record AgentInteractiveAskResponse(
    bool Cancelled,
    IReadOnlyList<AgentInteractiveAskAnswer> Answers);

/// <summary>
/// Presents an interactive question from a running provider and returns the user's response.
/// </summary>
/// <param name="request">The question request.</param>
/// <param name="cancellationToken">Cancels and withdraws the question when the turn or request is cancelled.</param>
/// <returns>The user's response.</returns>
public delegate Task<AgentInteractiveAskResponse> AgentInteractiveAskHandler(
    AgentInteractiveAskRequest request,
    CancellationToken cancellationToken);
