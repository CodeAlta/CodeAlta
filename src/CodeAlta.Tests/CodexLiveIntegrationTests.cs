using CodeAlta.Agent;
using CodeAlta.Agent.Codex;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodexLiveIntegrationTests
{
    private const string LiveCodexTestsEnvironmentVariable = "CODEALTA_RUN_LIVE_CODEX_TESTS";

    [TestMethod]
    [TestCategory("LiveCodex")]
    public async Task CodexAgentBackend_LivePrompt_ProducesAssistantContent()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(LiveCodexTestsEnvironmentVariable), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive(
                $"Set {LiveCodexTestsEnvironmentVariable}=1 to run live Codex integration tests.");
        }

        await using var backend = new CodexAgentBackend(new CodexAgentBackendOptions());
        IAgentSession session;
        try
        {
            session = await backend.CreateSessionAsync(
                    new AgentSessionCreateOptions
                    {
                        Streaming = true,
                        WorkingDirectory = Environment.CurrentDirectory,
                        OnPermissionRequest = static (_, _) =>
                            Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
                    })
                .ConfigureAwait(false);
        }
        catch (FileNotFoundException ex)
        {
            Assert.Inconclusive($"Codex executable was not found: {ex.Message}");
            return;
        }

        await using var asyncSession = session;

        var assistantContent = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errorEvent = new TaskCompletionSource<AgentErrorEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = asyncSession.Subscribe(@event =>
        {
            switch (@event)
            {
                case AgentAssistantMessageDeltaEvent delta when !string.IsNullOrWhiteSpace(delta.Delta):
                    assistantContent.TrySetResult(delta.Delta);
                    break;
                case AgentAssistantMessageEvent message when !string.IsNullOrWhiteSpace(message.Content):
                    assistantContent.TrySetResult(message.Content);
                    break;
                case AgentErrorEvent error:
                    errorEvent.TrySetResult(error);
                    break;
            }
        });

        _ = await asyncSession.SendAsync(
                new AgentSendOptions
                {
                    Input = AgentInput.Text("Reply with exactly the word pong.")
                })
            .ConfigureAwait(false);

        var timeoutTask = Task.Delay(TimeSpan.FromMinutes(2));
        var completedTask = await Task.WhenAny(assistantContent.Task, errorEvent.Task, timeoutTask).ConfigureAwait(false);

        if (completedTask == assistantContent.Task)
        {
            var content = await assistantContent.Task.ConfigureAwait(false);
            Assert.IsFalse(string.IsNullOrWhiteSpace(content));
            return;
        }

        if (completedTask == errorEvent.Task)
        {
            var error = await errorEvent.Task.ConfigureAwait(false);
            Assert.Fail($"Codex returned an error event instead of content: {error.Message}");
        }

        Assert.Fail("No assistant content was received from Codex within the timeout.");
    }
}
