namespace CodeAlta.Agent.LocalRuntime.Compaction;

internal sealed class LocalAgentTurnExecutorCompactionSummaryExecutor(ILocalAgentTurnExecutor turnExecutor)
    : ILocalAgentCompactionSummaryExecutor
{
    private readonly ILocalAgentTurnExecutor _turnExecutor = turnExecutor ?? throw new ArgumentNullException(nameof(turnExecutor));

    public async Task<LocalAgentCompactionSummaryResponse> ExecuteAsync(
        LocalAgentCompactionSummaryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var response = await _turnExecutor.ExecuteTurnAsync(
                new LocalAgentTurnRequest
                {
                    Provider = request.Provider,
                    BackendId = request.BackendId,
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
                        new LocalAgentConversationMessage(
                            LocalAgentConversationRole.User,
                            [new LocalAgentMessagePart.Text(request.UserMessage)]),
                    ],
                    Tools = [],
                    State = request.State,
                },
                static (_, _) => ValueTask.CompletedTask,
                cancellationToken)
            .ConfigureAwait(false);

        var summary = response.AssistantMessage.Parts
            .OfType<LocalAgentMessagePart.Text>()
            .Select(static part => part.Value)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new InvalidOperationException("The compaction summarizer did not return a textual summary.");
        }

        return new LocalAgentCompactionSummaryResponse(summary.Trim(), response.Usage);
    }
}
