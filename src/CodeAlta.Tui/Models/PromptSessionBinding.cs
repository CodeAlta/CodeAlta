using CodeAlta.Agent;
using CodeAlta.Catalog;

namespace CodeAlta.Tui.Models;

internal readonly record struct PromptSessionId
{
    public PromptSessionId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

    public override string ToString() => Value;
}

internal readonly record struct SessionDraftId
{
    public SessionDraftId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

    public override string ToString() => Value;
}

internal abstract record ShellSessionRef
{
    private ShellSessionRef()
    {
    }

    public sealed record Draft : ShellSessionRef
    {
        public Draft(SessionDraftId draftId)
        {
            if (draftId.IsEmpty)
            {
                throw new ArgumentException("Session draft id cannot be empty.", nameof(draftId));
            }

            DraftId = draftId;
        }

        public SessionDraftId DraftId { get; init; }
    }

    public sealed record Running : ShellSessionRef
    {
        public Running(string sessionId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
            SessionId = sessionId;
        }

        public string SessionId { get; init; }
    }
}

internal sealed record PromptSessionBinding
{
    public PromptSessionBinding(
        PromptSessionId promptSessionId,
        ProjectId projectId,
        ShellSessionRef session,
        ModelProviderId modelProviderId,
        string? modelId = null,
        AgentReasoningEffort? reasoningEffort = null)
    {
        if (promptSessionId.IsEmpty)
        {
            throw new ArgumentException("Prompt session id cannot be empty.", nameof(promptSessionId));
        }

        if (projectId == default)
        {
            throw new ArgumentException("Project id cannot be empty.", nameof(projectId));
        }

        ArgumentNullException.ThrowIfNull(session);

        if (modelProviderId.IsEmpty)
        {
            throw new ArgumentException("Model provider id cannot be empty.", nameof(modelProviderId));
        }

        PromptSessionId = promptSessionId;
        ProjectId = projectId;
        Session = session;
        ModelProviderId = modelProviderId;
        ModelId = string.IsNullOrWhiteSpace(modelId) ? null : modelId;
        ReasoningEffort = reasoningEffort;
    }

    public PromptSessionId PromptSessionId { get; init; }

    public ProjectId ProjectId { get; init; }

    public ShellSessionRef Session { get; init; }

    public ModelProviderId ModelProviderId { get; init; }

    public string? ModelId { get; init; }

    public AgentReasoningEffort? ReasoningEffort { get; init; }
}
