using System.Text.Json;
using CodeAlta.Acp;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Compaction;
using XenoAtom.Logging;

namespace CodeAlta.Plugin.Acp;

/// <summary>A model provider that runs CodeAlta sessions through an ACP agent.</summary>
internal sealed class AcpModelProviderRuntime : IAgentModelProviderRuntime, IModelProviderModelCatalog, IModelProviderModelOptions
{
    /// <summary>The protocol family recorded in the journals of the sessions of this provider.</summary>
    public const string ProtocolFamily = "acp";

    private readonly AcpRuntimeOptions _options;
    private readonly AcpTurnExecutor _turnExecutor;
    private readonly AcpModelCacheStore _modelCache;
    private readonly SemaphoreSlim _probeGate = new(1, 1);

    public AcpModelProviderRuntime(ModelProviderDescriptor descriptor, AcpRuntimeOptions options, AcpModelCacheStore modelCache)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _modelCache = modelCache ?? throw new ArgumentNullException(nameof(modelCache));
        RuntimeDescriptor = new ModelProviderRuntimeDescriptor
        {
            ProtocolFamily = ProtocolFamily,
            ProviderKey = descriptor.ProviderId.Value,
            DisplayName = descriptor.DisplayName,
            TransportKind = AgentTransportKind.AgentClientProtocol,
            IsDefault = descriptor.IsDefault,
            Profile = new AgentProviderProfile
            {
                SupportsReasoningEffort = true,
                PermissionModes = AcpAgentSession.PermissionModes,
            },
            // The agent keeps the context of a session and compacts it itself.
            Compaction = AgentCompactionSettings.Default with { Enabled = false },
        };
        _options.ModelStore = modelCache;
        _turnExecutor = new AcpTurnExecutor(options);
    }

    public ModelProviderDescriptor Descriptor { get; }

    public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; }

    public IModelProviderModelCatalog? ModelCatalog => this;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Starts the agent in a folder of its own, asks for its models in a session it closes, and tells whether it is
    /// signed in. No model is called.
    /// </summary>
    public async Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (!Descriptor.IsEnabled)
        {
            return new ModelProviderProbeResult { ProviderId = Descriptor.ProviderId, Availability = ModelProviderAvailability.Disabled };
        }

        var inspection = await InspectAsync(cancellationToken).ConfigureAwait(false);
        var models = inspection.Models.Count > 0 ? inspection.Models : _modelCache.Read(Descriptor.ProviderId.Value)?.ToModels() ?? [];
        return new ModelProviderProbeResult
        {
            ProviderId = Descriptor.ProviderId,
            Availability = inspection.Error is null ? ModelProviderAvailability.Ready : ModelProviderAvailability.Failed,
            Models = Present(models),
            SelectedModelId = Descriptor.DefaultModelId ?? inspection.CurrentModel,
            SelectedReasoningEffort = Descriptor.DefaultReasoningEffort,
            StatusMessage = inspection.Error ?? inspection.Summary,
            ErrorCategory = inspection.ErrorCategory,
            Capabilities = inspection.Capabilities is { } capabilities ? DescribeCapabilities(capabilities) : [],
        };
    }

    public async Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(ModelProviderRuntimeDescriptor provider, CancellationToken cancellationToken = default)
    {
        if (_options.Profile.Models is { Count: > 0 } known)
        {
            return Present(known);
        }

        if (_modelCache.Read(Descriptor.ProviderId.Value) is { } cached)
        {
            return Present(cached.ToModels());
        }

        var inspection = await InspectAsync(cancellationToken).ConfigureAwait(false);
        return Present(inspection.Models);
    }

    private IReadOnlyList<AgentModelInfo> Present(IReadOnlyList<AcpListedModel> models)
        => AcpModels.Present(models, _options.Quirks, _options.ModelCatalog, _modelCache.Choices(Descriptor.ProviderId.Value));

    /// <summary>
    /// Records the value of an option of a model, which each turn of the model sends to the agent before its prompt,
    /// and keeps it with the models of the provider.
    /// </summary>
    /// <exception cref="ArgumentException">The model has no such option, or the option no such value.</exception>
    /// <exception cref="IOException">The choice could not be kept.</exception>
    public Task<AgentModelInfo?> SetModelOptionAsync(string modelId, string optionId, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(optionId);
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();
        var models = _options.Profile.Models is { Count: > 0 } known ? known : _modelCache.Read(Descriptor.ProviderId.Value)?.ToModels() ?? [];
        if (models.FirstOrDefault(model => string.Equals(model.Id, modelId, StringComparison.Ordinal)) is not { } listed)
        {
            return Task.FromResult<AgentModelInfo?>(null);
        }

        var option = listed.Options?.FirstOrDefault(option => string.Equals(option.Id, optionId, StringComparison.Ordinal))
            ?? throw new ArgumentException($"The model {modelId} has no option {optionId}.", nameof(optionId));
        if (!option.Values.Any(offered => string.Equals(offered.Value, value, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"The option {optionId} of the model {modelId} has no value {value}.", nameof(value));
        }

        var key = Descriptor.ProviderId.Value;
        _modelCache.Choose(key, modelId, optionId, value, () => AcpModelCache.From(models, _options.Profile.CurrentModel));
        return Task.FromResult<AgentModelInfo?>(AcpModels.Present(listed, _options.Quirks, _options.ModelCatalog, _modelCache.ChoicesOf(key, modelId)));
    }

    public IModelProviderTurnExecutor CreateTurnExecutor() => _turnExecutor;

    public AgentRuntimeProviderRegistration CreateProviderRegistration()
        => new()
        {
            Provider = RuntimeDescriptor,
            TurnExecutor = _turnExecutor,
            ModelCatalog = this,
        };

    public ValueTask DisposeAsync() => _turnExecutor.DisposeAsync();

    /// <summary>Says what the agent can and cannot do, as the window shows it with the provider.</summary>
    public static IReadOnlyList<ModelProviderCapability> DescribeCapabilities(AcpAgentCapabilities capabilities)
        =>
        [
            new("system-prompt", "System prompt", capabilities.SystemPrompt,
                capabilities.SystemPrompt
                    ? "The instructions of CodeAlta go in the system prompt of the agent."
                    : "The agent takes no system prompt from CodeAlta: the instructions are given with the first prompt of a session, and again after a compaction."),
            new("codealta-tools", "CodeAlta tools", capabilities.McpHttp,
                capabilities.McpHttp
                    ? "The agent calls alta and the tools of plugins through an MCP server of the session."
                    : "The agent connects to no MCP server over HTTP: it runs without alta and the tools of plugins."),
            new("images", "Images", capabilities.Images, capabilities.Images ? "Pictures of a prompt are given to the agent." : "Pictures of a prompt are not given to the agent."),
            new("resume", "Resume", capabilities.ResumeSession || capabilities.LoadSession,
                capabilities.ResumeSession || capabilities.LoadSession
                    ? "A session goes on in the same session of the agent after a restart."
                    : "After a restart, a session starts a new session of the agent, which is told the conversation."),
        ];

    private async Task<AcpInspection> InspectAsync(CancellationToken cancellationToken)
    {
        await _probeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // What was learned before: the probe learns first the models it knows nothing of, and the others keep what
            // they had when its budget runs out (choosing each of Cursor's models takes most of a second).
            var known = _options.Profile.Models ?? _modelCache.Read(Descriptor.ProviderId.Value)?.ToModels();
            var inspection = await AcpAgentInspector.InspectAsync(_options, known, cancellationToken).ConfigureAwait(false);
            if (inspection.Models.Count > 0)
            {
                inspection = inspection with { Models = AcpListedModel.Merge(inspection.Models, known) };
                _options.Profile.SetModels(inspection.Models, inspection.CurrentModel);
                _modelCache.Write(Descriptor.ProviderId.Value, AcpModelCache.From(inspection.Models, inspection.CurrentModel));
            }

            return inspection;
        }
        finally
        {
            _probeGate.Release();
        }
    }
}

/// <summary>What the inspection of an agent found.</summary>
internal sealed record AcpInspection(
    AcpAgentCapabilities? Capabilities,
    IReadOnlyList<AcpListedModel> Models,
    string? CurrentModel,
    string? Summary,
    string? Error,
    string? ErrorCategory);

/// <summary>Starts an agent to read its capabilities and its models, in a session it closes.</summary>
internal static class AcpAgentInspector
{
    public const string NotInstalledCategory = "acp-not-installed";
    public const string SignedOutCategory = "acp-signed-out";
    public const string UnavailableCategory = "acp-unavailable";

    public static Task<AcpInspection> InspectAsync(AcpRuntimeOptions options, CancellationToken cancellationToken)
        => InspectAsync(options, known: null, cancellationToken);

    /// <summary>Inspects the agent; <paramref name="known"/> are the models learned before, whose efforts are learned last.</summary>
    public static async Task<AcpInspection> InspectAsync(AcpRuntimeOptions options, IReadOnlyList<AcpListedModel>? known, CancellationToken cancellationToken)
    {
        var executable = options.Agent.ResolveExecutable();
        if (executable is null)
        {
            return new AcpInspection(null, [], null, null,
                $"{options.Agent.Command} was not found: {options.AgentName} cannot start." +
                (options.Agent.UsesPackageRunner ? $" Install the tool that provides `{options.Agent.CommandName}` (Node.js for npx, uv for uvx)." : " Install the agent, or set its command."),
                NotInstalledCategory);
        }

        Directory.CreateDirectory(options.ProbeDirectory);
        AcpClient? client = null;
        try
        {
            client = await options.ConnectionFactory.StartAsync(
                    new AcpConnectionRequest(executable, options.Agent.Arguments, options.Agent.Environment, options.ProbeDirectory, options.InitializeTimeout),
                    message =>
                    {
                        // The probe answers no request of the agent: it sends no prompt.
                        if (message.RequestId is { } requestId)
                        {
                            _ = client?.RespondWithErrorAsync(requestId, AcpErrorCodes.MethodNotFound, "CodeAlta is only reading the models.");
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            var capabilities = AcpAgentCapabilities.Read(JsonSerializer.SerializeToElement(client.InitializeResponse, AcpClient.CreateJsonSerializerOptions()));
            options.Profile.Capabilities = capabilities;
            var name = capabilities.AgentName ?? options.AgentName;
            var version = capabilities.AgentVersion is { } agentVersion ? $" {agentVersion}" : string.Empty;
            NewSessionResponse session;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(options.RequestTimeout);
                session = await client.SessionNewAsync(new NewSessionRequest { Cwd = options.ProbeDirectory, McpServers = [] }, timeout.Token).ConfigureAwait(false);
            }
            catch (AcpJsonRpcException ex) when (ex.Code == AcpErrorCodes.AuthRequired)
            {
                return new AcpInspection(capabilities, [], null, null,
                    $"{options.AgentName} is not signed in. Sign in from the form of the provider, then test it again.", SignedOutCategory);
            }

            var configOptions = session.ConfigOptions is null
                ? default
                : JsonSerializer.SerializeToElement(session.ConfigOptions, AcpClient.CreateJsonSerializerOptions());
            var current = AcpConfigOptions.Find(configOptions, "model") is { } model ? AcpJson.GetString(model, "currentValue") : null;
            var models = AcpConfigOptions.ReadModels(configOptions);
            if (options.Quirks.LearnsEffortsByModel(models))
            {
                models = await LearnEffortsAsync(client, session.SessionId, configOptions, models, known, options, cancellationToken).ConfigureAwait(false);
            }

            if (capabilities.CloseSession)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    await client.SessionCloseAsync(new CloseSessionRequest { SessionId = session.SessionId }, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is AcpJsonRpcException or OperationCanceledException or TimeoutException)
                {
                    // The session stays in the list of the agent.
                }
            }

            return new AcpInspection(capabilities, models, current, $"{name}{version} ({options.Agent.CommandLine}).", null, null);
        }
        catch (Exception ex) when (ex is AcpConnectionClosedException or TimeoutException or IOException or AcpJsonRpcException or System.ComponentModel.Win32Exception or InvalidOperationException && !cancellationToken.IsCancellationRequested)
        {
            return new AcpInspection(options.Profile.Capabilities, [], null, null, $"{options.AgentName} could not be started. {ex.Message}", UnavailableCategory);
        }
        finally
        {
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    // The options of a session change with its model (the efforts and the context of a model are not those of another):
    // each model is chosen in turn to read them, within a budget, then the model and the effort the session had are
    // chosen again, as an agent may keep the last ones chosen for its next sessions. A model the agent refuses keeps
    // unknown efforts.
    private static async Task<IReadOnlyList<AcpListedModel>> LearnEffortsAsync(AcpClient client, string sessionId, JsonElement configOptions,
        IReadOnlyList<AcpListedModel> models, IReadOnlyList<AcpListedModel>? known, AcpRuntimeOptions options, CancellationToken cancellationToken)
    {
        if (AcpConfigOptions.Find(configOptions, "model") is not { } modelOption || models.Count < 2)
        {
            return models;
        }

        var modelConfigId = AcpJson.GetString(modelOption, "id") ?? "model";
        var currentModel = AcpJson.GetString(modelOption, "currentValue");
        var thought = AcpConfigOptions.EffortOption(configOptions);
        var currentEffort = thought is { } option ? AcpJson.GetString(option, "currentValue") : null;
        var learned = models.ToArray();
        var learnedBefore = known?.Where(static model => model.Efforts is not null).Select(static model => model.Id).ToHashSet(StringComparer.Ordinal) ?? [];
        // The models learned before come last: what they had is kept when the budget does not reach them.
        var order = Enumerable.Range(0, learned.Length).OrderBy(index => learnedBefore.Contains(learned[index].Id) ? 1 : 0).ToArray();
        var changed = false;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.EffortsBudget);
        try
        {
            foreach (var index in order)
            {
                if (learned[index].Efforts is not null)
                {
                    continue;
                }

                JsonElement after;
                try
                {
                    var response = await client.SessionSetConfigOptionAsync(AcpConfigOptions.CreateSetRequest(sessionId, modelConfigId, learned[index].Id), budget.Token).ConfigureAwait(false);
                    after = JsonSerializer.SerializeToElement(response.ConfigOptions, AcpClient.CreateJsonSerializerOptions());
                    changed = true;
                }
                catch (AcpJsonRpcException)
                {
                    continue;
                }

                if (AcpConfigOptions.Find(after, "model") is { } chosen && string.Equals(AcpJson.GetString(chosen, "currentValue"), learned[index].Id, StringComparison.Ordinal))
                {
                    learned[index] = learned[index] with
                    {
                        Efforts = AcpConfigOptions.ReadEfforts(after),
                        DefaultEffort = AcpConfigOptions.CurrentEffort(after),
                        Options = AcpConfigOptions.ReadModelOptions(after),
                    };
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException && !cancellationToken.IsCancellationRequested)
        {
            // The budget is spent: the efforts of the models left stay unknown.
        }

        if (changed && currentModel is not null)
        {
            using var restore = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            restore.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await client.SessionSetConfigOptionAsync(AcpConfigOptions.CreateSetRequest(sessionId, modelConfigId, currentModel), restore.Token).ConfigureAwait(false);
                if (thought is { } effortOption && currentEffort is not null)
                {
                    await client.SessionSetConfigOptionAsync(AcpConfigOptions.CreateSetRequest(sessionId, AcpJson.GetString(effortOption, "id") ?? "thought_level", currentEffort), restore.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is AcpJsonRpcException or OperationCanceledException or TimeoutException && !cancellationToken.IsCancellationRequested)
            {
                options.Logger?.Info($"{options.AgentName} did not take back its model {currentModel} after the probe: {ex.Message}");
            }
        }

        return learned;
    }
}
