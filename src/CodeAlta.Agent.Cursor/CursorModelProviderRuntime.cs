using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent.Cursor;

/// <summary>
/// Options for a Cursor agent CLI provider. Construction does not start a process.
/// </summary>
public sealed class CursorModelProviderRuntimeOptions
{
    /// <summary>Gets the provider key.</summary>
    public required string ProviderKey { get; init; }

    /// <summary>Gets the display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Gets the CLI executable. Empty selects <c>agent</c>.</summary>
    public string? Command { get; init; }

    /// <summary>Gets the arguments placed before <c>login</c>, <c>status</c>, and <c>acp</c>.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Gets the profile state root that holds the signed-out marker.</summary>
    public required string StateRootPath { get; init; }

    /// <summary>Gets the default model id.</summary>
    public string? DefaultModelId { get; init; }

    /// <summary>Gets whether this provider is the default.</summary>
    public bool IsDefault { get; init; }

    /// <summary>Gets whether the provider is enabled.</summary>
    public bool IsEnabled { get; init; } = true;
}

/// <summary>
/// Model provider that runs turns through the Cursor agent CLI ACP session the user is signed in to.
/// </summary>
public sealed class CursorModelProviderRuntime : IAgentModelProviderRuntime
{
    /// <summary>The provider type of the configuration (<c>type = "cursor"</c>).</summary>
    public const string ProviderType = "cursor";

    /// <summary>The protocol family recorded for sessions of this provider.</summary>
    public const string ProtocolFamily = "cursor";

    private readonly CursorModelProviderRuntimeOptions _options;
    private readonly CursorModelCatalog _modelCatalog;
    private readonly CursorTurnExecutor _turnExecutor;

    /// <summary>
    /// Initializes a new instance of the <see cref="CursorModelProviderRuntime"/> class.
    /// </summary>
    /// <param name="options">The provider runtime options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The provider key or state root is empty.</exception>
    public CursorModelProviderRuntime(CursorModelProviderRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProviderKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.StateRootPath);
        _options = options;
        Descriptor = CreateDescriptor(options);
        RuntimeDescriptor = new ModelProviderRuntimeDescriptor
        {
            ProtocolFamily = ProtocolFamily,
            ProviderKey = Descriptor.ProviderId.Value,
            DisplayName = Descriptor.DisplayName,
            TransportKind = AgentTransportKind.CursorAgentCli,
            IsDefault = options.IsDefault,
            Profile = new AgentProviderProfile
            {
                SupportsReasoningEffort = false,
                SupportsToolResultImages = false,
            },
        };
        _modelCatalog = new CursorModelCatalog(options);
        _turnExecutor = new CursorTurnExecutor(options);
    }

    /// <inheritdoc />
    public ModelProviderDescriptor Descriptor { get; }

    /// <inheritdoc />
    public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; }

    /// <inheritdoc />
    public IModelProviderModelCatalog? ModelCatalog => _modelCatalog;

    /// <summary>
    /// Creates the descriptor without starting the CLI.
    /// </summary>
    /// <param name="options">The provider runtime options.</param>
    /// <returns>The provider descriptor.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public static ModelProviderDescriptor CreateDescriptor(CursorModelProviderRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var providerKey = options.ProviderKey.Trim();
        var displayName = string.IsNullOrWhiteSpace(options.DisplayName) ? "Cursor" : options.DisplayName.Trim();
        return new ModelProviderDescriptor(new ModelProviderId(providerKey), displayName, ProviderType)
        {
            IsDefault = options.IsDefault,
            IsEnabled = options.IsEnabled,
            DefaultModelId = string.IsNullOrWhiteSpace(options.DefaultModelId) ? "auto" : options.DefaultModelId.Trim(),
        };
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (!Descriptor.IsEnabled)
        {
            return new ModelProviderProbeResult { ProviderId = Descriptor.ProviderId, Availability = ModelProviderAvailability.Disabled };
        }

        if (CursorSignInView.IsDismissed(_options.StateRootPath, _options.ProviderKey))
        {
            return SignedOut();
        }

        try
        {
            var signedIn = await CursorAgentLogin.IsAuthenticatedAsync(Launch(null), cancellationToken).ConfigureAwait(false);
            if (!signedIn)
            {
                return SignedOut();
            }
        }
        catch (InvalidOperationException ex)
        {
            return Unavailable(ex.Message);
        }

        IReadOnlyList<AgentModelInfo> models;
        try
        {
            models = await _modelCatalog.ListModelsAsync(RuntimeDescriptor, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return Unavailable(ex.Message);
        }

        return new ModelProviderProbeResult
        {
            ProviderId = Descriptor.ProviderId,
            Availability = ModelProviderAvailability.Ready,
            Models = models,
            SelectedModelId = Descriptor.DefaultModelId,
            StatusMessage = models.Count == 0
                ? "Cursor agent CLI is signed in."
                : $"Cursor agent CLI is signed in. {models.Count} models.",
        };
    }

    /// <inheritdoc />
    public IModelProviderTurnExecutor CreateTurnExecutor() => _turnExecutor;

    /// <inheritdoc />
    public AgentRuntimeProviderRegistration CreateProviderRegistration()
        => new()
        {
            Provider = RuntimeDescriptor,
            TurnExecutor = _turnExecutor,
            ModelCatalog = _modelCatalog,
        };

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private ModelProviderProbeResult SignedOut()
        => new()
        {
            ProviderId = Descriptor.ProviderId,
            Availability = ModelProviderAvailability.Failed,
            StatusMessage = "Cursor is not signed in.",
            ErrorCategory = "cursor-signed-out",
        };

    private ModelProviderProbeResult Unavailable(string message)
        => new()
        {
            ProviderId = Descriptor.ProviderId,
            Availability = ModelProviderAvailability.Failed,
            StatusMessage = message,
            ErrorCategory = "cursor-unavailable",
        };

    private CursorCliLaunch Launch(string? workingDirectory)
        => CursorCliLaunch.From(_options.Command, _options.Arguments, workingDirectory);

    private sealed class CursorModelCatalog(CursorModelProviderRuntimeOptions options) : IModelProviderModelCatalog
    {
        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(ModelProviderRuntimeDescriptor provider, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(provider);
            return CursorAgentLogin.ListModelsAsync(CursorCliLaunch.From(options.Command, options.Arguments, null), cancellationToken);
        }
    }

    private sealed class CursorTurnExecutor(CursorModelProviderRuntimeOptions options) : IModelProviderTurnExecutor
    {
        public async Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(onUpdate);
            if (CursorSignInView.IsDismissed(options.StateRootPath, options.ProviderKey))
            {
                throw new InvalidOperationException("Cursor is signed out in CodeAlta.");
            }

            var prompt = LastUserText(request.Conversation);
            var sessionId = request.CanUseProviderContinuation ? request.State.ProviderSessionId : null;
            var arguments = new List<string>(options.Arguments);
            if (!string.IsNullOrWhiteSpace(request.ModelId))
            {
                arguments.Add("--model");
                arguments.Add(request.ModelId.Trim());
            }

            var launch = CursorCliLaunch.From(options.Command, arguments, request.WorkingDirectory);
            var (created, text) = await CursorAcpTurn.RunAsync(launch, prompt, sessionId, (chunk, token) => onUpdate(new AgentTurnDelta
            {
                Kind = AgentContentKind.Assistant,
                ContentId = "cursor-assistant",
                Text = chunk,
                IsDraft = false,
            }, token), cancellationToken).ConfigureAwait(false);

            using var state = JsonDocument.Parse($"{{\"cursorSessionId\":\"{JsonEncodedText.Encode(created)}\"}}");
            return new AgentTurnResponse
            {
                AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text(text)]),
                AssistantPartContentIds = ["cursor-assistant"],
                ProviderSessionId = created,
                ProviderState = state.RootElement.Clone(),
            };
        }

        private static string LastUserText(IReadOnlyList<AgentConversationMessage> conversation)
        {
            for (var index = conversation.Count - 1; index >= 0; index--)
            {
                if (conversation[index].Role != AgentConversationRole.User)
                {
                    continue;
                }

                var text = string.Concat(conversation[index].Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value));
                if (text.Length > 0)
                {
                    return text;
                }
            }

            throw new InvalidOperationException("Cursor ACP needs a user message.");
        }
    }
}
