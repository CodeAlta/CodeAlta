using System.Text.Json;
using CodeAlta.Acp;
using CodeAlta.Agent.Runtime;
using XenoAtom.Logging;

namespace CodeAlta.Plugin.Acp;

// The model, the reasoning effort and the options of the model in the session of the agent, set through its
// configuration options.
internal sealed partial class AcpAgentSession
{
    // Sets the model the turn asks for, then the values chosen in CodeAlta for its other options (its context, a fast
    // mode, a toggle of thinking), then its reasoning effort when the model offers it, last as an option may change the
    // efforts: the options of an agent change with its model, so they are read again after each change.
    private async Task ApplyModelAsync(AgentTurnRequest request, CancellationToken cancellationToken)
    {
        var connection = _connection!;
        if (!string.IsNullOrWhiteSpace(request.ModelId) &&
            AcpConfigOptions.Find(connection.ConfigOptions, "model") is { } model &&
            _options.Quirks.ListedModelId(request.ModelId, [.. AcpConfigOptions.Values(model).Select(static value => value.Value)]) is { } listed)
        {
            if (!string.Equals(AcpJson.GetString(model, "currentValue"), listed, StringComparison.Ordinal))
            {
                await SetOptionAsync(connection, AcpJson.GetString(model, "id") ?? "model", listed, cancellationToken).ConfigureAwait(false);
            }

            // Only once the session runs that model: a model the agent does not list leaves it on another one, whose
            // options are not those chosen for the model asked for.
            if (AcpConfigOptions.Find(connection.ConfigOptions, "model") is { } chosenModel &&
                string.Equals(AcpJson.GetString(chosenModel, "currentValue"), listed, StringComparison.Ordinal))
            {
                await ApplyChoicesAsync(connection, listed, cancellationToken).ConfigureAwait(false);
            }
        }

        if (request.ReasoningEffort is { } effort &&
            AcpConfigOptions.EffortOption(connection.ConfigOptions) is { } thought &&
            AcpConfigOptions.EffortValue(connection.ConfigOptions, effort) is { } value &&
            !string.Equals(AcpJson.GetString(thought, "currentValue"), value, StringComparison.Ordinal))
        {
            await SetOptionAsync(connection, AcpJson.GetString(thought, "id") ?? "thought_level", value, cancellationToken).ConfigureAwait(false);
        }
    }

    // Read at each turn: the options are chosen in any runtime of the provider, in any CodeAlta of the profile. A value the
    // agent no longer offers is left out.
    private async Task ApplyChoicesAsync(Connection connection, string modelId, CancellationToken cancellationToken)
    {
        foreach (var (optionId, chosen) in _options.ModelStore?.ChoicesOf(_options.ProviderKey, modelId) ?? new Dictionary<string, string>())
        {
            if (FindOption(connection.ConfigOptions, optionId) is { } option &&
                AcpConfigOptions.Values(option).Any(offered => string.Equals(offered.Value, chosen, StringComparison.Ordinal)) &&
                !string.Equals(AcpJson.GetString(option, "currentValue"), chosen, StringComparison.Ordinal))
            {
                await SetOptionAsync(connection, optionId, chosen, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static JsonElement? FindOption(JsonElement options, string id)
    {
        if (options.ValueKind == JsonValueKind.Array)
        {
            foreach (var option in options.EnumerateArray())
            {
                if (string.Equals(AcpJson.GetString(option, "id"), id, StringComparison.Ordinal))
                {
                    return option;
                }
            }
        }

        return null;
    }

    private async Task SetOptionAsync(Connection connection, string configId, string value, CancellationToken cancellationToken)
    {
        var request = AcpConfigOptions.CreateSetRequest(connection.AgentSessionId, configId, value);
        try
        {
            var response = await connection.Client.SessionSetConfigOptionAsync(request, Timeout(cancellationToken)).ConfigureAwait(false);
            SetConfigOptions(connection, JsonSerializer.SerializeToElement(response.ConfigOptions, AcpClient.CreateJsonSerializerOptions()));
        }
        catch (AcpJsonRpcException ex)
        {
            // The agent refused the value: the turn goes on with the one it has.
            _logger?.Info($"{_options.AgentName} refused {configId} = {value}: {ex.Message}");
        }
    }
}
