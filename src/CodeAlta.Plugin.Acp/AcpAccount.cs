using System.Text.Json;
using CodeAlta.Acp;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Acp;

/// <summary>
/// The sign-in of an ACP agent, through the ways it lists when it starts (<c>authMethods</c>): one it runs itself
/// (<c>authenticate</c>, which may open a browser), or a command the user runs in a terminal.
/// </summary>
internal sealed class AcpAccount(string probeDirectory, IAcpConnectionFactory connectionFactory) : IPluginProviderAccount
{
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    public async Task<IReadOnlyList<PluginProviderSignInMethod>> GetSignInMethodsAsync(PluginProviderSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var agent = AcpAgentSettings.Read(settings);
        await using var client = await StartAsync(agent, cancellationToken).ConfigureAwait(false);
        var initialize = JsonSerializer.SerializeToElement(client.InitializeResponse, AcpClient.CreateJsonSerializerOptions());
        return CreateMethods(agent, AcpAgentCapabilities.Read(initialize).AuthMethods);
    }

    public async Task SignInAsync(PluginProviderSettings settings, string methodId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodId);
        var agent = AcpAgentSettings.Read(settings);
        await using var client = await StartAsync(agent, cancellationToken).ConfigureAwait(false);
        try
        {
            await client.SendRequestAsync<AuthenticateRequest, AuthenticateResponse>(
                    "authenticate",
                    new AuthenticateRequest { MethodId = new AuthMethodId { Value = methodId } },
                    SignInTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AcpJsonRpcException or TimeoutException or AcpConnectionClosedException)
        {
            throw new InvalidOperationException($"{settings.DisplayName} could not sign in: {ex.Message}", ex);
        }
    }

    /// <summary>Maps the ways to sign in an agent lists.</summary>
    internal static IReadOnlyList<PluginProviderSignInMethod> CreateMethods(AcpAgentSettings agent, IReadOnlyList<JsonElement> methods)
    {
        var result = new List<PluginProviderSignInMethod>();
        foreach (var method in methods)
        {
            if (AcpJson.GetString(method, "id") is not { Length: > 0 } id)
            {
                continue;
            }

            var label = AcpJson.GetString(method, "name") ?? id;
            var description = AcpJson.GetString(method, "description");
            IReadOnlyList<string>? command = null;
            IReadOnlyDictionary<string, string>? environment = null;
            if (AcpJson.TryGetObject(method, "_meta", out var meta) && AcpJson.TryGetObject(meta, "terminal-auth", out var terminal) &&
                AcpJson.GetString(terminal, "command") is { Length: > 0 } metaCommand)
            {
                // An agent that names the command of its sign-in.
                command = [metaCommand, .. ReadStrings(terminal, "args")];
                label = AcpJson.GetString(terminal, "label") ?? label;
            }
            else if (string.Equals(AcpJson.GetString(method, "type"), "terminal", StringComparison.Ordinal))
            {
                // The agent signs in when it is run in a terminal with the arguments of the method, in the place of
                // the ones that start the protocol.
                command = [agent.Command, .. PackageArguments(agent), .. ReadStrings(method, "args")];
                environment = ReadMap(method, "env");
            }

            result.Add(new PluginProviderSignInMethod(id, label, description) { TerminalCommand = command, TerminalEnvironment = environment });
        }

        return result;
    }

    private async Task<AcpClient> StartAsync(AcpAgentSettings agent, CancellationToken cancellationToken)
    {
        var executable = agent.ResolveExecutable() ?? throw new InvalidOperationException($"{agent.Command} was not found.");
        Directory.CreateDirectory(probeDirectory);
        AcpClient? client = null;
        try
        {
            client = await connectionFactory.StartAsync(
                    new AcpConnectionRequest(executable, agent.Arguments, agent.Environment, probeDirectory,
                        agent.UsesPackageRunner ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(2)),
                    message =>
                    {
                        if (message.RequestId is { } requestId)
                        {
                            _ = client?.RespondWithErrorAsync(requestId, AcpErrorCodes.MethodNotFound, "CodeAlta is only signing in.");
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return client;
        }
        catch (Exception ex) when (ex is AcpConnectionClosedException or TimeoutException or IOException or System.ComponentModel.Win32Exception && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"{agent.CommandName} could not be started: {ex.Message}", ex);
        }
    }

    // The arguments of a package runner up to its package (npx --yes @scope/agent@1.0): the agent itself, without the
    // arguments that start the protocol.
    private static IEnumerable<string> PackageArguments(AcpAgentSettings agent)
    {
        if (!agent.UsesPackageRunner)
        {
            yield break;
        }

        foreach (var argument in agent.Arguments)
        {
            yield return argument;
            if (!argument.StartsWith('-'))
            {
                yield break;
            }
        }
    }

    private static IEnumerable<string> ReadStrings(JsonElement element, string name)
        => AcpJson.TryGetArray(element, name, out var array)
            ? array.EnumerateArray().Where(static item => item.ValueKind == JsonValueKind.String).Select(static item => item.GetString()!)
            : [];

    private static IReadOnlyDictionary<string, string>? ReadMap(JsonElement element, string name)
        => AcpJson.TryGetObject(element, name, out var map)
            ? map.EnumerateObject().Where(static property => property.Value.ValueKind == JsonValueKind.String).ToDictionary(static property => property.Name, static property => property.Value.GetString()!)
            : null;
}
