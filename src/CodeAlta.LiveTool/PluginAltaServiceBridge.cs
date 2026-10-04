using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.LiveTool;

/// <summary>
/// Lets plugins invoke <c>alta</c> commands through their services. A host creates it before its plugin
/// runtime starts and gives it the dispatcher once the <c>alta</c> services exist; an invocation made
/// before that is answered with <see cref="AltaExitCodes.ServiceUnavailable"/>.
/// </summary>
public sealed class PluginAltaServiceBridge : IPluginAltaRuntimeService
{
    private AltaCommandDispatcher? _dispatcher;

    /// <summary>Sets the dispatcher that serves the invocations from now on.</summary>
    /// <param name="dispatcher">The host's <c>alta</c> command dispatcher.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dispatcher"/> is null.</exception>
    public void SetDispatcher(AltaCommandDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
    }

    /// <inheritdoc />
    public ValueTask<PluginAltaCommandResult> InvokeAsync(
        IReadOnlyList<string> args,
        string? stdin = null,
        PluginAltaInvocationOptions? options = null,
        CancellationToken cancellationToken = default)
        => InvokeAsync(pluginRuntimeKey: string.Empty, args, stdin, options, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<PluginAltaCommandResult> InvokeAsync(
        string pluginRuntimeKey,
        IReadOnlyList<string> args,
        string? stdin = null,
        PluginAltaInvocationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        var dispatcher = _dispatcher;
        if (dispatcher is null)
        {
            var correlationId = AltaCommandDispatcher.CreateCorrelationId();
            return new PluginAltaCommandResult
            {
                ExitCode = AltaExitCodes.ServiceUnavailable,
                TranscriptJsonl = AltaJsonlWriter.Serialize(AltaJsonlWriter.CreateResultRecord(
                    correlationId,
                    AltaExitCodes.ServiceUnavailable,
                    truncated: false,
                    recordCount: 0,
                    diagnosticCount: 1,
                    duration: TimeSpan.Zero)) + "\n" +
                    AltaJsonlWriter.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["type"] = "alta.error",
                        ["version"] = 1,
                        ["correlationId"] = correlationId,
                        ["code"] = "service.unavailable",
                        ["exitCode"] = AltaExitCodes.ServiceUnavailable,
                        ["message"] = "The alta dispatcher is not ready for plugin invocation.",
                    }) + "\n",
                Error = "The alta dispatcher is not ready for plugin invocation.",
            };
        }

        options ??= new PluginAltaInvocationOptions();
        using var timeout = options.Timeout is { } timeoutValue && timeoutValue > TimeSpan.Zero
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        if (timeout is not null)
        {
            timeout.CancelAfter(options.Timeout!.Value);
        }

        var effectiveToken = timeout?.Token ?? cancellationToken;
        var result = await dispatcher.InvokeAsync(
                args,
                stdin ?? string.Empty,
                new AltaCallerIdentity
                {
                    Kind = "plugin",
                    SourceSessionId = options.SourceSessionId,
                    SourceProjectId = options.SourceProjectId,
                    SourceAgentId = options.SourceAgentId,
                    PluginRuntimeKey = pluginRuntimeKey,
                },
                options.WorkingDirectory,
                maxOutputRecords: options.MaxOutputRecords,
                maxOutputBytes: options.MaxOutputBytes,
                cancellationToken: effectiveToken);
        return new PluginAltaCommandResult
        {
            ExitCode = result.ExitCode,
            TranscriptJsonl = result.Stdout,
            Truncated = result.Truncated,
            Error = result.Error,
        };
    }
}
