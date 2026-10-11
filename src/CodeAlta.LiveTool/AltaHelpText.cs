using System.Globalization;

namespace CodeAlta.LiveTool;

/// <summary>
/// Renders the model-visible <c>alta</c> help text from the live command registry.
/// </summary>
public static class AltaHelpText
{
    /// <summary>
    /// Renders root <c>alta --help</c> text using built-in command contributors and optional host services.
    /// </summary>
    /// <param name="services">Host services used to discover runtime/plugin command contributions.</param>
    /// <returns>The root help text with normalized line endings.</returns>
    public static string RenderRootHelp(IServiceProvider? services = null)
        => RenderRootHelp(new AltaCommandRegistry(), services ?? new AltaServiceCollection());

    /// <summary>
    /// Renders root <c>alta --help</c> text using the supplied registry and host services.
    /// </summary>
    /// <param name="registry">The command registry to render.</param>
    /// <param name="services">Host services used to discover runtime/plugin command contributions.</param>
    /// <returns>The root help text with normalized line endings.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="registry" /> or <paramref name="services" /> is <see langword="null" />.</exception>
    public static string RenderRootHelp(AltaCommandRegistry registry, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(services);

        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var context = new AltaCommandContext
        {
            Caller = AltaCallerIdentity.Host,
            Services = services,
            Stdin = TextReader.Null,
            Stdout = stdout,
            Stderr = stderr,
            CorrelationId = AltaCommandDispatcher.CreateCorrelationId(),
        };
        var result = registry.InvokeAsync(["--help"], context)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
        if (result.ExitCode != AltaExitCodes.Success)
        {
            throw new InvalidOperationException($"Rendering alta root help failed with exit code {result.ExitCode}: {stderr}");
        }

        return NormalizeLineEndings(stdout.ToString());
    }

    private static string NormalizeLineEndings(string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd('\n');
}
