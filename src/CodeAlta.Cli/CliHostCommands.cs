using System.Buffers;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Hosting;
using CodeAlta.LiveTool;

namespace CodeAlta.Cli;

/// <summary>
/// The commands of <c>altacli</c> that use a host without running a prompt: <c>alta</c>, <c>models</c> and
/// <c>auth check</c>. They compose the host of <c>run</c> (<see cref="CliHost"/>), with the same storage and configuration.
/// </summary>
internal sealed class CliHostCommands(CliStreams streams, CliOutput output, CliEnvironment environment, CancellationToken processToken)
{
    /// <summary><c>altacli alta</c>: the <c>alta</c> command of the host, as a caller that belongs to no session.</summary>
    public async ValueTask<int> AltaAsync(CliAltaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Arguments.Count == 0) options.Arguments.Add("--help");
        var stdin = options.Arguments.Contains("--stdin", StringComparer.Ordinal)
            ? await streams.Stdin.ReadToEndAsync(processToken).ConfigureAwait(false)
            : null;
        return await WithHostAsync(options, async (host, folder) =>
        {
            var result = await host.Alta.InvokeAsync(options.Arguments, stdin, AltaCallerIdentity.Cli, folder, cancellationToken: processToken).ConfigureAwait(false);
            if (result.Stdout.Length > 0) output.WriteText(result.Stdout);
            if (result.Stderr.Length > 0) output.Error(result.Stderr.TrimEnd());
            return result.ExitCode;
        }).ConfigureAwait(false);
    }

    /// <summary><c>altacli models</c>: the models of the providers that are ready, or of one provider.</summary>
    public async ValueTask<int> ModelsAsync(CliProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!CliRunCommand.TryParseFormat(options.FormatText, out var format)) return Invalid($"'{options.FormatText}' is not a format: use text or jsonl.");
        return await WithHostAsync(options, async (host, _) =>
        {
            var registry = host.Host.ModelProviderRegistry;
            var providers = registry.ListProviders().Where(static provider => provider.IsEnabled).ToList();
            if (options.Provider is { } named)
            {
                providers = providers.Where(provider => string.Equals(provider.ProviderId.Value, named, StringComparison.OrdinalIgnoreCase)).ToList();
                if (providers.Count == 0) return Invalid($"The provider '{named}' is not configured, or is turned off.");
            }

            var initialization = host.Host.ModelProviderInitializationService;
            var ready = 0;
            foreach (var provider in providers)
            {
                IReadOnlyList<AgentModelInfo> models;
                try
                {
                    models = await initialization.GetModelsAsync(provider.ProviderId, processToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or KeyNotFoundException)
                {
                    output.Error($"altacli: provider {provider.ProviderId.Value}: {exception.Message}");
                    continue;
                }

                var state = initialization.CurrentStates.FirstOrDefault(candidate => candidate.ProviderId.Value == provider.ProviderId.Value);
                if (state is not { Availability: ModelProviderAvailability.Ready })
                {
                    output.Error($"altacli: provider {provider.ProviderId.Value} is not ready ({state?.Availability.ToString().ToLowerInvariant() ?? "unknown"}){Describe(state?.StatusMessage)}");
                    continue;
                }

                ready++;
                foreach (var model in models)
                {
                    if (format == CliFormat.Jsonl)
                    {
                        output.WriteLine(Json(writer =>
                        {
                            writer.WriteString("provider", provider.ProviderId.Value);
                            writer.WriteString("model", model.Id);
                            writer.WriteString("ref", provider.ProviderId.Value + ":" + model.Id);
                            AcpEventProjector.WriteStringOrNull(writer, "name", model.DisplayName);
                            writer.WriteBoolean("default", string.Equals(model.Id, provider.DefaultModelId, StringComparison.OrdinalIgnoreCase));
                        }));
                    }
                    else
                    {
                        output.WriteText(provider.ProviderId.Value + ":" + model.Id + (model.DisplayName is { Length: > 0 } name && name != model.Id ? "  " + name : string.Empty));
                    }
                }
            }

            return options.Provider is not null && ready == 0 ? CliExitCodes.ProviderNotReady : CliExitCodes.Completed;
        }).ConfigureAwait(false);
    }

    /// <summary><c>altacli auth check</c>: 0 when the provider is ready, 6 when it is not (a sign-in, a key), 2 when it is unknown.</summary>
    public async ValueTask<int> AuthCheckAsync(CliProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!CliRunCommand.TryParseFormat(options.FormatText, out var format)) return Invalid($"'{options.FormatText}' is not a format: use text or jsonl.");
        if (string.IsNullOrWhiteSpace(options.Provider)) return Invalid("Name the provider to check: --provider <id>.");
        return await WithHostAsync(options, async (host, _) =>
        {
            var provider = host.Host.ModelProviderRegistry.ListProviders(includeDisabled: true)
                .FirstOrDefault(candidate => string.Equals(candidate.ProviderId.Value, options.Provider, StringComparison.OrdinalIgnoreCase));
            if (provider is null) return Report(options.Provider!, "unknown", $"The provider '{options.Provider}' is not configured.", CliExitCodes.InvalidUsage, 0);
            if (!provider.IsEnabled) return Report(provider.ProviderId.Value, "disabled", "The provider is turned off in the settings.", CliExitCodes.ProviderNotReady, 0);
            var initialization = host.Host.ModelProviderInitializationService;
            try
            {
                await initialization.GetModelsAsync(provider.ProviderId, processToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or KeyNotFoundException)
            {
                return Report(provider.ProviderId.Value, "failed", exception.Message, CliExitCodes.ProviderNotReady, 0);
            }

            var state = initialization.CurrentStates.FirstOrDefault(candidate => candidate.ProviderId.Value == provider.ProviderId.Value);
            var availability = state?.Availability ?? ModelProviderAvailability.Unknown;
            var ready = ConfiguredProviderInspection.TryBuildActiveProviderTestResult(availability, state?.StatusMessage ?? string.Empty, state?.Models ?? [],
                static count => $"Ready: {count} model(s).", out var inspection) && inspection.Success;
            var message = ready ? inspection.Message : string.IsNullOrWhiteSpace(inspection.Message) ? state?.StatusMessage ?? "Not ready." : inspection.Message;
            return Report(provider.ProviderId.Value, availability.ToString().ToLowerInvariant(), message,
                ready ? CliExitCodes.Completed : CliExitCodes.ProviderNotReady, ready ? inspection.ModelCount : 0);
        }).ConfigureAwait(false);

        int Report(string providerId, string availability, string message, int code, int models)
        {
            if (format == CliFormat.Jsonl)
            {
                output.WriteLine(Json(writer =>
                {
                    writer.WriteString("provider", providerId);
                    writer.WriteBoolean("ready", code == CliExitCodes.Completed);
                    writer.WriteString("availability", availability);
                    writer.WriteString("message", message);
                    writer.WriteNumber("models", models);
                    writer.WriteNumber("exitCode", code);
                }));
            }
            else
            {
                output.WriteText($"{providerId}: {(code == CliExitCodes.Completed ? "ready" : "not ready")} - {message}");
            }

            return code;
        }
    }

    // The host of a command: its storage, its configuration, its logging; the command runs once it is created.
    private async ValueTask<int> WithHostAsync(CliCommonOptions options, Func<CliHost, string, Task<int>> command)
    {
        var folder = Path.GetFullPath(options.Project ?? environment.CurrentDirectory, environment.CurrentDirectory);
        if (!Directory.Exists(folder)) return Invalid($"The project folder '{folder}' does not exist.");
        if (!CliStorage.TryResolve(options, environment, out var storage, out var error)) return Invalid(error!);
        if (!CliConfig.TryCreate(options, environment, storage!.GlobalRoot, out var config, out error)) return Invalid(error!);
        var ownsLogging = environment.InitializeLogging && CliLogging.Initialize(storage.StateRoot, output, options.Verbose);
        try
        {
            await using var host = await CliHost.CreateAsync(new CliHostRequest
            {
                Storage = storage,
                Config = config!,
                ProjectFolder = folder,
                StartPlugins = !options.NoPlugins,
                StartMcp = !options.NoMcp,
                ListsSkills = !options.NoSkills,
                Offline = options.Offline,
            }, environment, processToken).ConfigureAwait(false);
            environment.HostCreated?.Invoke(host.Host);
            return await command(host, folder).ConfigureAwait(false);
        }
        finally
        {
            if (ownsLogging) CliLogging.Shutdown();
        }
    }

    private int Invalid(string message)
    {
        output.Error("altacli: " + message);
        return CliExitCodes.InvalidUsage;
    }

    private static string Describe(string? status) => string.IsNullOrWhiteSpace(status) ? "." : ": " + status;

    private static byte[] Json(Action<Utf8JsonWriter> writeFields)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writeFields(writer);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }
}
