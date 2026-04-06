using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Models;
using CodeAlta.Agent.LocalRuntime;
using Microsoft.Extensions.AI;

namespace CodeAlta.Agent.Anthropic;

/// <summary>
/// Local-runtime backend for Anthropic Messages providers.
/// </summary>
public sealed class AnthropicAgentBackend : IAgentBackend
{
    private readonly IAgentBackend _inner;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnthropicAgentBackend"/> class.
    /// </summary>
    /// <param name="options">The backend options.</param>
    public AnthropicAgentBackend(AnthropicAgentBackendOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Providers.Count == 0)
        {
            throw new ArgumentException("At least one provider registration is required.", nameof(options));
        }

        _inner = new LocalAgentBackend(
            AgentBackendIds.AnthropicMessages,
            "Anthropic Messages",
            new LocalAgentBackendOptions
            {
                StateRootPath = options.StateRootPath,
                Providers =
                [
                    .. options.Providers.Select(provider => new LocalAgentBackendProviderRegistration
                    {
                        Provider = new LocalAgentProviderDescriptor
                        {
                            ProtocolFamily = "anthropic-messages",
                            ProviderKey = provider.ProviderKey.Trim(),
                            DisplayName = string.IsNullOrWhiteSpace(provider.DisplayName) ? provider.ProviderKey.Trim() : provider.DisplayName.Trim(),
                            BackendId = AgentBackendIds.AnthropicMessages,
                            TransportKind = LocalAgentTransportKind.AnthropicMessages,
                            BaseUri = provider.BaseUri,
                            IsDefault = provider.IsDefault,
                            Profile = provider.Profile ?? new LocalAgentProviderProfile
                            {
                                SupportsDeveloperRole = false,
                                StreamsUsage = true,
                                SupportsThoughtSignatures = true,
                            },
                        },
                        TurnExecutor = CreateTurnExecutor(provider),
                    }),
                ],
            });
    }

    /// <inheritdoc />
    public AgentBackendId BackendId => _inner.BackendId;

    /// <inheritdoc />
    public string DisplayName => _inner.DisplayName;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default) => _inner.StartAsync(cancellationToken);

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default) => _inner.StopAsync(cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
        => _inner.ListModelsAsync(cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentSessionMetadata>> ListSessionsAsync(
        AgentSessionListFilter? filter = null,
        CancellationToken cancellationToken = default)
        => _inner.ListSessionsAsync(filter, cancellationToken);

    /// <inheritdoc />
    public Task<bool> DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        => _inner.DeleteSessionAsync(sessionId, cancellationToken);

    /// <inheritdoc />
    public Task<IAgentSession> CreateSessionAsync(
        AgentSessionCreateOptions options,
        CancellationToken cancellationToken = default)
        => _inner.CreateSessionAsync(options, cancellationToken);

    /// <inheritdoc />
    public Task<IAgentSession> ResumeSessionAsync(
        string sessionId,
        AgentSessionResumeOptions options,
        CancellationToken cancellationToken = default)
        => _inner.ResumeSessionAsync(sessionId, options, cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    private static ILocalAgentTurnExecutor CreateTurnExecutor(AnthropicProviderOptions provider)
    {
        return new LocalAgentChatClientTurnExecutor(
            (providerDescriptor, cancellationToken) => CreateChatClientAsync(provider, providerDescriptor, cancellationToken),
            (providerDescriptor, cancellationToken) => ListModelsAsync(provider, providerDescriptor, cancellationToken));
    }

    private static ValueTask<IChatClient> CreateChatClientAsync(
        AnthropicProviderOptions provider,
        LocalAgentProviderDescriptor providerDescriptor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (provider.ChatClientFactory is not null)
        {
            return ValueTask.FromResult(provider.ChatClientFactory());
        }

        var client = CreateSdkClient(provider, providerDescriptor);
        return ValueTask.FromResult<IChatClient>(new OwnedChatClient(client.AsIChatClient(), client));
    }

    private static async Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(
        AnthropicProviderOptions provider,
        LocalAgentProviderDescriptor providerDescriptor,
        CancellationToken cancellationToken)
    {
        if (provider.ModelListAsync is not null)
        {
            return await provider.ModelListAsync(cancellationToken).ConfigureAwait(false);
        }

        using var client = CreateSdkClient(provider, providerDescriptor);
        var results = new List<AgentModelInfo>();
        var page = await client.Models.List(cancellationToken: cancellationToken).ConfigureAwait(false);
        while (true)
        {
            results.AddRange(page.Items.Select(model => ToAgentModelInfo(providerDescriptor, model)));
            if (!page.HasNext())
            {
                break;
            }

            page = await page.Next(cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    private static AnthropicClient CreateSdkClient(
        AnthropicProviderOptions provider,
        LocalAgentProviderDescriptor providerDescriptor)
    {
        var options = new ClientOptions
        {
            ApiKey = provider.ApiKey,
        };
        if (provider.BaseUri is not null)
        {
            options.BaseUrl = provider.BaseUri.ToString();
        }

        return new AnthropicClient(options);
    }

    private static AgentModelInfo ToAgentModelInfo(
        LocalAgentProviderDescriptor provider,
        ModelInfo model)
    {
        var capabilities = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["createdAt"] = model.CreatedAt,
            ["maxInputTokens"] = model.MaxInputTokens,
            ["maxTokens"] = model.MaxTokens,
        };

        return new AgentModelInfo(
            model.ID,
            DisplayName: model.DisplayName,
            Description: null,
            Provider: provider.ProviderKey,
            Capabilities: capabilities);
    }
}
