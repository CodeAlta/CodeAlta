using System.Collections.Concurrent;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Pseudonymizer;

public sealed partial class PseudonymizerPlugin
{
    // The sessions whose request waits for the ONNX Runtime: their status line says so.
    private readonly ConcurrentDictionary<string, byte> _waitingForRuntime = new(StringComparer.OrdinalIgnoreCase);

    // Whether a model is there and not the ONNX Runtime that runs it: what a version of the application that runs on
    // another ONNX Runtime finds. The runtime is then downloaded without asking, as a part of the model the user chose
    // to download.
    private static bool IsRuntimeMissing(InvalidOperationException? failure) => failure is NerModelMissingException { Missing: NerModelPart.Runtime };

    // Downloads the ONNX Runtime and waits for it, while the status line of the session says so.
    private async Task WaitForRuntimeAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_waitingForRuntime.TryAdd(sessionId, 0)) Services.Ui.InvalidateContent();
        string? failure;
        try
        {
            failure = await _models!.InstallRuntimeAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (_waitingForRuntime.TryRemove(sessionId, out _)) Services.Ui.InvalidateContent();
        }

        if (failure is not null)
            throw new AgentProviderBoundaryException($"This project is pseudonymized, and the engine that runs its model that finds names could not be downloaded ({failure}). Nothing was sent: send again to try again.");
    }

    // The boundary of a request whose model is there and not the ONNX Runtime that runs it: the request waits for the
    // download, a few megabytes, rather than fail. Nothing of it is sent before its texts were analyzed.
    private sealed class RuntimeWaitingBoundary(PseudonymizerPlugin plugin, PluginProviderBoundaryContext context, string language) : IAgentProviderBoundary
    {
        private IAgentProviderBoundary? _ready;

        public async ValueTask<AgentTurnRequest> PrepareRequestAsync(AgentTurnRequest request, CancellationToken cancellationToken)
        {
            await plugin.WaitForRuntimeAsync(context.SessionId, cancellationToken).ConfigureAwait(false);
            var recognizers = plugin.RecognizersOf(language, out var failure);
            _ready = plugin.Boundary(context, language, recognizers, failure);
            return await _ready.PrepareRequestAsync(request, cancellationToken).ConfigureAwait(false);
        }

        public IAgentBoundaryTextStream CreateResponseStream(AgentContentKind kind, string contentId) => Ready.CreateResponseStream(kind, contentId);

        public AgentTurnResponse RestoreResponse(AgentTurnRequest request, AgentTurnResponse response) => Ready.RestoreResponse(request, response);

        // An answer comes after its request was prepared.
        private IAgentProviderBoundary Ready => _ready ?? throw new InvalidOperationException("The request was not prepared.");
    }
}
