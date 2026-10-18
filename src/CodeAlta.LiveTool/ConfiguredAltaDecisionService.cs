using System.Text.Json.Nodes;
using CodeAlta.Agent.TypeSafe;
using CodeAlta.Catalog;

namespace CodeAlta.LiveTool;

/// <summary>
/// Consults the decision providers of the global configuration. The configuration is read again on each request,
/// so a provider added, changed or disabled in Settings is used from the next request on.
/// </summary>
public sealed class ConfiguredAltaDecisionService : IAltaDecisionService
{
    private readonly Func<IReadOnlyList<CodeAltaProviderDocument>> _loadDefinitions;
    private readonly Func<string, string?> _readEnvironment;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Creates the service over a configuration store.
    /// </summary>
    /// <param name="configStore">The store of the global configuration, borrowed.</param>
    /// <param name="httpClient">The HTTP client of the requests, borrowed.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public ConfiguredAltaDecisionService(CodeAltaConfigStore configStore, HttpClient httpClient)
        : this(() => configStore.LoadGlobalProviderDefinitions(), Environment.GetEnvironmentVariable, httpClient)
    {
        ArgumentNullException.ThrowIfNull(configStore);
    }

    internal ConfiguredAltaDecisionService(Func<IReadOnlyList<CodeAltaProviderDocument>> loadDefinitions, Func<string, string?> readEnvironment, HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(loadDefinitions);
        ArgumentNullException.ThrowIfNull(readEnvironment);
        ArgumentNullException.ThrowIfNull(httpClient);
        _loadDefinitions = loadDefinitions;
        _readEnvironment = readEnvironment;
        _httpClient = httpClient;
    }

    /// <inheritdoc />
    public IReadOnlyList<AltaDecisionProvider> ListProviders()
        => [.. Definitions().Select(definition => Describe(definition, ApiKey(definition) is not null))];

    /// <inheritdoc />
    public async Task<AltaDecisionResult> DecideAsync(string? providerKey, string? model, JsonNode state, JsonObject questions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(questions);
        if (!TypeSafeQuestions.TryValidate(state, questions, out var error))
        {
            throw new ArgumentException(error);
        }

        var (provider, client) = Resolve(providerKey);
        try
        {
            var evaluation = await client.EvaluateAsync(string.IsNullOrWhiteSpace(model) ? provider.Model : model.Trim(), state, questions, cancellationToken).ConfigureAwait(false);
            return new AltaDecisionResult(provider, evaluation);
        }
        catch (TypeSafeApiException exception)
        {
            throw Translate(provider, exception);
        }
    }

    /// <inheritdoc />
    public async Task<(AltaDecisionProvider Provider, IReadOnlyList<AltaDecisionModel> Models)> ListModelsAsync(string? providerKey, CancellationToken cancellationToken)
    {
        var (provider, client) = Resolve(providerKey);
        try
        {
            var models = await client.ListModelsAsync(cancellationToken).ConfigureAwait(false);
            return (provider, [.. models.Select(static model => new AltaDecisionModel(model.Name, model.Description, model.ReleaseDate))]);
        }
        catch (TypeSafeApiException exception)
        {
            throw Translate(provider, exception);
        }
    }

    private IEnumerable<CodeAltaProviderDocument> Definitions()
        => _loadDefinitions().Where(static definition => definition.Enabled != false && CodeAltaConfigStore.IsDecisionProviderType(definition.ProviderType));

    private (AltaDecisionProvider Provider, TypeSafeClient Client) Resolve(string? providerKey)
    {
        var definitions = Definitions().ToArray();
        if (definitions.Length == 0)
        {
            throw new AltaDecisionException("decide.noProvider",
                "No decision provider is configured: add the TypeSafe Jev provider in Settings > Providers, give it an API key and enable it.");
        }

        var key = string.IsNullOrWhiteSpace(providerKey) ? null : providerKey.Trim();
        var definition = key is null ? definitions[0]
            : definitions.FirstOrDefault(value => string.Equals(value.ProviderKey, key, StringComparison.OrdinalIgnoreCase))
              ?? throw new AltaDecisionException("decide.providerNotFound",
                  $"No enabled decision provider is '{key}'. The decision providers are: {string.Join(", ", definitions.Select(static value => value.ProviderKey))}.");
        var apiKey = ApiKey(definition);
        var provider = Describe(definition, apiKey is not null);
        if (apiKey is null)
        {
            throw new AltaDecisionException("decide.noCredential",
                $"The decision provider '{provider.Key}' has no API key: set it in Settings > Providers, or set the variable {definition.ApiKeyEnv ?? "its api_key_env names"}.");
        }

        var baseUri = string.IsNullOrWhiteSpace(definition.ApiUrl) ? null : new Uri(definition.ApiUrl, UriKind.Absolute);
        return (provider, new TypeSafeClient(_httpClient, apiKey, baseUri));
    }

    private string? ApiKey(CodeAltaProviderDocument definition)
    {
        if (!string.IsNullOrWhiteSpace(definition.ApiKey))
        {
            return definition.ApiKey.Trim();
        }

        return string.IsNullOrWhiteSpace(definition.ApiKeyEnv) || _readEnvironment(definition.ApiKeyEnv.Trim()) is not { } value || string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private static AltaDecisionProvider Describe(CodeAltaProviderDocument definition, bool hasCredential)
        => new(definition.ProviderKey,
            string.IsNullOrWhiteSpace(definition.DisplayName) ? definition.ProviderKey : definition.DisplayName.Trim(),
            string.IsNullOrWhiteSpace(definition.Model) ? TypeSafeDefaults.Model : definition.Model.Trim(),
            hasCredential);

    private static AltaDecisionException Translate(AltaDecisionProvider provider, TypeSafeApiException exception)
    {
        var code = exception.IsAuthenticationFailure ? "decide.unauthorized"
            : exception.IsInvalidRequest ? "decide.invalidRequest"
            : exception.IsBusy ? "decide.busy"
            : "decide.failed";
        return new AltaDecisionException(code, $"{provider.DisplayName} ({provider.Key}): {exception.Message}", exception);
    }
}
