using System.Collections.Concurrent;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Node.Tests;

/// <summary>
/// A provider whose turns a test scripts, run by the real agent loop of the node: each turn takes the next step of
/// <see cref="Turns"/>, or answers <c>Done.</c> when none is left.
/// </summary>
internal sealed class ScriptedNodeProvider
{
    internal const string Model = "scripted-model";

    internal ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId("scripted"), "Scripted") { DefaultModelId = Model, IsDefault = true };

    /// <summary>Gets the steps of the next turns, in order.</summary>
    internal ConcurrentQueue<Func<AgentTurnRequest, CancellationToken, Task<AgentTurnResponse>>> Turns { get; } = new();

    /// <summary>Gets the working folder of the last turn.</summary>
    internal string? LastWorkingDirectory { get; private set; }

    /// <summary>Gets the text of the last prompt of the last turn.</summary>
    internal string LastUserText { get; private set; } = "";

    private void Record(AgentTurnRequest request)
    {
        LastWorkingDirectory = request.WorkingDirectory;
        if (request.Conversation.LastOrDefault(static message => message.Role == AgentConversationRole.User) is { } user)
            LastUserText = string.Join("\n", user.Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value));
    }

    internal void Register(ModelProviderRegistry registry) => registry.RegisterOrReplace(Descriptor, () => new Runtime(this));

    /// <summary>A step that waits for a gate, then answers.</summary>
    internal static Func<AgentTurnRequest, CancellationToken, Task<AgentTurnResponse>> WaitThenReply(Task gate, string text, TaskCompletionSource? started = null)
        => async (_, cancellationToken) =>
        {
            started?.TrySetResult();
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return Answer(text);
        };

    internal static AgentTurnResponse Answer(string text)
        => new() { AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text(text)]) };

    private sealed class Runtime(ScriptedNodeProvider owner) : IAgentModelProviderRuntime
    {
        public ModelProviderDescriptor Descriptor => owner.Descriptor;

        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = "test",
            ProviderKey = owner.Descriptor.ProviderId.Value,
            DisplayName = owner.Descriptor.DisplayName,
            TransportKind = AgentTransportKind.OpenAIResponses,
        };

        public IModelProviderModelCatalog? ModelCatalog => null;

        public AgentRuntimeProviderRegistration CreateProviderRegistration() => new() { Provider = RuntimeDescriptor, TurnExecutor = new Executor(owner) };

        public IModelProviderTurnExecutor CreateTurnExecutor() => new Executor(owner);

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelProviderProbeResult
            {
                ProviderId = owner.Descriptor.ProviderId,
                Availability = ModelProviderAvailability.Ready,
                Models = [new AgentModelInfo(Model, DisplayName: Model)],
                SelectedModelId = Model,
            });

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Executor(ScriptedNodeProvider owner) : IModelProviderTurnExecutor
    {
        public Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
            CancellationToken cancellationToken = default)
        {
            owner.Record(request);
            return owner.Turns.TryDequeue(out var step) ? step(request, cancellationToken) : Task.FromResult(Answer("Done."));
        }
    }
}
