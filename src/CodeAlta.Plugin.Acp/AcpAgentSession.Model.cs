using System.Text.Json;
using CodeAlta.Acp;
using CodeAlta.Agent.Runtime;
using XenoAtom.Logging;

namespace CodeAlta.Plugin.Acp;

// The model and the reasoning effort of the session of the agent, set through its configuration options.
internal sealed partial class AcpAgentSession
{
    // Sets the model the turn asks for, then its reasoning effort when the model offers it: the options of an agent
    // change with its model, so they are read again after each change.
    private async Task ApplyModelAsync(AgentTurnRequest request, CancellationToken cancellationToken)
    {
        var connection = _connection!;
        if (!string.IsNullOrWhiteSpace(request.ModelId) &&
            AcpConfigOptions.Find(connection.ConfigOptions, "model") is { } model &&
            !string.Equals(AcpJson.GetString(model, "currentValue"), request.ModelId, StringComparison.Ordinal) &&
            AcpConfigOptions.Values(model).Any(value => string.Equals(value.Value, request.ModelId, StringComparison.Ordinal)))
        {
            await SetOptionAsync(connection, AcpJson.GetString(model, "id") ?? "model", request.ModelId, cancellationToken).ConfigureAwait(false);
        }

        if (request.ReasoningEffort is { } effort &&
            AcpConfigOptions.Find(connection.ConfigOptions, "thought_level") is { } thought &&
            AcpConfigOptions.EffortValue(connection.ConfigOptions, effort) is { } value &&
            !string.Equals(AcpJson.GetString(thought, "currentValue"), value, StringComparison.Ordinal))
        {
            await SetOptionAsync(connection, AcpJson.GetString(thought, "id") ?? "thought_level", value, cancellationToken).ConfigureAwait(false);
        }
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
