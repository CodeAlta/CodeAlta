using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Compaction;
using CodeAlta.CodexAppServer;
using CodeAlta.CodexAppServer.Protocol;

namespace CodeAlta.Plugin.CodexCli;

/// <summary>A model provider that runs CodeAlta sessions through the Codex app-server.</summary>
internal sealed class CodexCliModelProviderRuntime : IAgentModelProviderRuntime, IModelProviderModelCatalog
{
    /// <summary>The protocol family recorded in the journals of the sessions of this provider.</summary>
    public const string ProtocolFamily = "codex-cli";

    private readonly CodexCliRuntimeOptions _options;
    private readonly CodexCliTurnExecutor _turnExecutor;
    private readonly SemaphoreSlim _probeGate = new(1, 1);

    public CodexCliModelProviderRuntime(ModelProviderDescriptor descriptor, CodexCliRuntimeOptions options)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
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
                PermissionModes = CodexCliSession.PermissionModes,
            },
            // Codex keeps the context of a thread and compacts it itself.
            Compaction = AgentCompactionSettings.Default with { Enabled = false },
        };
        _turnExecutor = new CodexCliTurnExecutor(options);
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
    /// Starts the app-server in a folder of its own, and reads its account and its models. No model is called, and
    /// nothing of a session.
    /// </summary>
    public async Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (!Descriptor.IsEnabled)
        {
            return new ModelProviderProbeResult { ProviderId = Descriptor.ProviderId, Availability = ModelProviderAvailability.Disabled };
        }

        var inspection = await InspectAsync(cancellationToken).ConfigureAwait(false);
        return new ModelProviderProbeResult
        {
            ProviderId = Descriptor.ProviderId,
            Availability = inspection.Error is null ? ModelProviderAvailability.Ready : ModelProviderAvailability.Failed,
            Models = inspection.Models.Count > 0 ? inspection.Models : _options.Profile.Models ?? [],
            SelectedModelId = Descriptor.DefaultModelId ?? inspection.DefaultModel,
            SelectedReasoningEffort = Descriptor.DefaultReasoningEffort,
            StatusMessage = inspection.Error ?? inspection.Summary,
            ErrorCategory = inspection.ErrorCategory,
            Capabilities =
            [
                new("system-prompt", "System prompt", true, "The instructions of CodeAlta are added to the developer instructions of Codex."),
                new("codealta-tools", "CodeAlta tools", true, "Codex calls alta and the tools of plugins through an MCP server of the session."),
                new("images", "Images", true, "Pictures of a prompt are given to Codex."),
                new("resume", "Resume", true, "A session goes on in the same Codex thread after a restart."),
            ],
        };
    }

    public async Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(ModelProviderRuntimeDescriptor provider, CancellationToken cancellationToken = default)
    {
        if (_options.Profile.Models is { Count: > 0 } known)
        {
            return known;
        }

        return (await InspectAsync(cancellationToken).ConfigureAwait(false)).Models;
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

    private async Task<CodexCliInspection> InspectAsync(CancellationToken cancellationToken)
    {
        await _probeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var inspection = await CodexCliInspector.InspectAsync(_options, cancellationToken).ConfigureAwait(false);
            if (inspection.Models.Count > 0)
            {
                _options.Profile.Models = inspection.Models;
            }

            return inspection;
        }
        finally
        {
            _probeGate.Release();
        }
    }
}

/// <summary>What the inspection of an app-server found.</summary>
internal sealed record CodexCliInspection(IReadOnlyList<AgentModelInfo> Models, string? DefaultModel, string? Summary, string? Error, string? ErrorCategory, bool SignedIn);

/// <summary>Starts an app-server to read its account and its models.</summary>
internal static class CodexCliInspector
{
    public static async Task<CodexCliInspection> InspectAsync(CodexCliRuntimeOptions options, CancellationToken cancellationToken)
    {
        CodexAppServerCommand command;
        try
        {
            command = await options.ResolveCommandAsync(progress: null, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentProviderFailureException exception)
        {
            return new CodexCliInspection([], null, null, exception.Message, CodexCliFailures.NotFoundCategory, false);
        }

        CodexAppServerClient? client = null;
        try
        {
            Directory.CreateDirectory(options.ProbeDirectory);
            CodexCliHomeConfig.Write(options.Home);
            client = await options.ConnectionFactory.StartAsync(
                    new CodexCliConnectionRequest(command, options.Home, options.Settings.Environment, options.ProbeDirectory, options.InitializeTimeout),
                    message =>
                    {
                        // The probe answers no request of the server: it starts no turn.
                        if (message.RequestId is { } requestId)
                        {
                            _ = client?.RespondErrorAsync(requestId, CodexErrorCodes.MethodNotFound, "CodeAlta is only reading the account and the models.");
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            var version = CodexAppServerCommand.VersionFromUserAgent(client.InitializeResponse.UserAgent);
            options.Profile.Version = version;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.RequestTimeout);
            var account = await client.AccountReadAsync(new GetAccountParams { RefreshToken = false }, timeout.Token).ConfigureAwait(false);
            var models = await ListAllModelsAsync(client, timeout.Token).ConfigureAwait(false);
            var presented = CodexCliModels.Present(models, options.ModelCatalog);
            var defaultModel = models.FirstOrDefault(static model => model.IsDefault)?.Id;
            var pinned = command.IsPinned || string.Equals(version, CodexPinnedRelease.Version, StringComparison.Ordinal)
                ? string.Empty
                : $" It is not the version CodeAlta is tested with ({CodexPinnedRelease.Version}).";
            var description = $"Codex {version ?? "?"} ({(command.IsPinned ? "downloaded by CodeAlta" : command.FileName)}).{pinned}";
            if (account.Account is null && account.RequiresOpenaiAuth)
            {
                return new CodexCliInspection(presented, defaultModel, null,
                    $"{options.ProviderName} is not signed in. Sign in from the form of the provider, then test it again.", CodexCliFailures.SignedOutCategory, false);
            }

            var who = account.Account switch
            {
                Account.ChatgptAccount chatgpt => $" Signed in to ChatGPT{(chatgpt.PlanType is PlanType.UnrecognizedValue ? string.Empty : $" ({chatgpt.PlanType})")}.",
                Account.ApiKeyAccount => " Signed in with an API key.",
                _ => string.Empty,
            };
            return new CodexCliInspection(presented, defaultModel, description + who, null, null, true);
        }
        catch (Exception ex) when (ex is CodexConnectionClosedException or TimeoutException or IOException or CodexJsonRpcException or System.ComponentModel.Win32Exception
            or InvalidOperationException or UnauthorizedAccessException && !cancellationToken.IsCancellationRequested)
        {
            return new CodexCliInspection([], null, null, $"{options.ProviderName} could not be started. {ex.Message}", CodexCliFailures.UnavailableCategory, false);
        }
        finally
        {
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Lists the models of the account, every page.</summary>
    public static async Task<IReadOnlyList<Model>> ListAllModelsAsync(CodexAppServerClient client, CancellationToken cancellationToken)
    {
        var models = new List<Model>();
        string? cursor = null;
        for (var page = 0; page < 20; page++)
        {
            var response = await client.ModelListAsync(new ModelListParams { Cursor = cursor, IncludeHidden = false }, cancellationToken).ConfigureAwait(false);
            models.AddRange(response.Data);
            cursor = response.NextCursor;
            if (string.IsNullOrEmpty(cursor))
            {
                break;
            }
        }

        return models;
    }
}
