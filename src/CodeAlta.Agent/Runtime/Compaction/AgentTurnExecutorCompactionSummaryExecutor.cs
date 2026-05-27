namespace CodeAlta.Agent.Runtime.Compaction;

internal sealed class AgentTurnExecutorCompactionSummaryExecutor(IModelProviderTurnExecutor turnExecutor)
    : IAgentCompactionSummaryExecutor
{
    private readonly IModelProviderTurnExecutor _turnExecutor = turnExecutor ?? throw new ArgumentNullException(nameof(turnExecutor));

    public async Task<AgentCompactionSummaryResponse> ExecuteAsync(
        AgentCompactionSummaryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var response = await _turnExecutor.ExecuteTurnAsync(
                new AgentTurnRequest
                {
                    Provider = request.Provider,
                    ProviderId = request.ProviderId,
                    SessionId = request.SessionId,
                    RunId = new AgentRunId($"compaction-summary:{Guid.CreateVersion7()}"),
                    ModelId = request.ModelId,
                    ModelInfo = request.ModelInfo,
                    WorkingDirectory = request.WorkingDirectory,
                    SystemMessage = request.SystemMessage,
                    DeveloperInstructions = null,
                    ReasoningEffort = null,
                    MaxOutputTokens = request.MaxOutputTokens,
                    Conversation =
                    [
                        new AgentConversationMessage(
                            AgentConversationRole.User,
                            [new AgentMessagePart.Text(request.UserMessage)]),
                    ],
                    Tools = [],
                    State = request.State,
                },
                static (_, _) => ValueTask.CompletedTask,
                cancellationToken)
            .ConfigureAwait(false);

        var summary = response.AssistantMessage.Parts
            .OfType<AgentMessagePart.Text>()
            .Select(static part => part.Value)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
        return new AgentCompactionSummaryResponse(summary?.Trim() ?? string.Empty, response.Usage);
    }
}
