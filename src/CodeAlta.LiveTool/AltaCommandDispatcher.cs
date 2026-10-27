using System.Globalization;
using CodeAlta.Agent;

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
        AgentToolAppResult? app = null;
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
            // The last view a command attached is the one of the call.
            AttachApp = attached => app = attached,
        };
        return AwaitAndFlattenAsync(_registry.InvokeAsync(args, context), () => app);

        static async ValueTask<AltaCommandResult> AwaitAndFlattenAsync(ValueTask<AltaCommandResult> resultTask, Func<AgentToolAppResult?> app)
            => AltaTranscriptFormatter.FlattenForLiveTool(await resultTask.ConfigureAwait(false)) with { App = app() };
    }

    /// <summary>
    /// Invokes <c>alta</c> for a script of Code Mode: the records as the command wrote them, without the caps of the
    /// transcript the model reads, since the script, not the model, reads them.
    /// </summary>
    /// <param name="args">Command arguments excluding the executable name.</param>
    /// <param name="stdin">The text the command reads with <c>--stdin</c>.</param>
    /// <param name="caller">The caller: the session of the script, with its run and step.</param>
    /// <param name="cwd">The folder of the call.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>The result, with the raw JSONL of its output and its diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> or <paramref name="caller"/> is null.</exception>
    public ValueTask<AltaCommandResult> InvokeRawAsync(
        IReadOnlyList<string> args,
        string? stdin,
        AltaCallerIdentity caller,
        string? cwd,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(caller);
        var context = new AltaCommandContext
        {
            Caller = caller,
            Services = _services,
            Stdin = new StringReader(stdin ?? string.Empty),
            Stdout = new StringWriter(CultureInfo.InvariantCulture),
            Stderr = new StringWriter(CultureInfo.InvariantCulture),
            Cwd = cwd,
            CorrelationId = CreateCorrelationId(),
            CancellationToken = cancellationToken,
        };
        return _registry.InvokeAsync(args, context);
    }

    /// <summary>Describes the commands a caller gets, for the declarations of scripts.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="cwd">The folder of the caller.</param>
    /// <returns>The commands.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="caller"/> is null.</exception>
    public IReadOnlyList<Scripts.AltaCommandDescriptor> Describe(AltaCallerIdentity caller, string? cwd)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return _registry.Describe(new AltaCommandContext
        {
            Caller = caller,
            Services = _services,
            Stdin = TextReader.Null,
            Stdout = TextWriter.Null,
            Stderr = TextWriter.Null,
            Cwd = cwd,
            CorrelationId = CreateCorrelationId(),
        });
    }

    /// <summary>Creates a compact correlation id.</summary>
    public static string CreateCorrelationId()
        => Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture);
}
