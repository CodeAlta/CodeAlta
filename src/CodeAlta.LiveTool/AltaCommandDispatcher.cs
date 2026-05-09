using System.Globalization;

namespace CodeAlta.LiveTool;

/// <summary>
/// Convenience service that captures stdout/stderr and returns the compact live-tool transcript.
/// </summary>
public sealed class AltaCommandDispatcher
{
    private readonly AltaCommandRegistry _registry;
    private readonly IServiceProvider _services;

    /// <summary>Initializes a new dispatcher.</summary>
    /// <param name="registry">The command registry.</param>
    /// <param name="services">Host services used by command handlers.</param>
    public AltaCommandDispatcher(AltaCommandRegistry registry, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(services);
        _registry = registry;
        _services = services;
    }

    /// <summary>Invokes <c>alta</c> and returns help text or a flat JSONL transcript.</summary>
    public ValueTask<AltaCommandResult> InvokeAsync(
        IReadOnlyList<string> args,
        string? stdin = null,
        AltaCallerIdentity? caller = null,
        string? cwd = null,
        int? maxOutputRecords = null,
        int? maxOutputBytes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        var context = new AltaCommandContext
        {
            Caller = caller ?? AltaCallerIdentity.Host,
            Services = _services,
            Stdin = new StringReader(stdin ?? string.Empty),
            Stdout = new StringWriter(CultureInfo.InvariantCulture),
            Stderr = new StringWriter(CultureInfo.InvariantCulture),
            Cwd = cwd,
            CorrelationId = CreateCorrelationId(),
            MaxOutputRecords = maxOutputRecords,
            MaxOutputBytes = maxOutputBytes,
            CancellationToken = cancellationToken,
        };
        return AwaitAndFlattenAsync(_registry.InvokeAsync(args, context));

        static async ValueTask<AltaCommandResult> AwaitAndFlattenAsync(ValueTask<AltaCommandResult> resultTask)
            => AltaTranscriptFormatter.FlattenForLiveTool(await resultTask.ConfigureAwait(false));
    }

    /// <summary>Creates a compact correlation id.</summary>
    public static string CreateCorrelationId()
        => Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture);
}
