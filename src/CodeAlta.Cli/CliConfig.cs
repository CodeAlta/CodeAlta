using CodeAlta.Catalog;

namespace CodeAlta.Cli;

/// <summary>
/// The configuration a run reads: the user's <c>config.toml</c> (unless <c>--ignore-user-config</c>), then the TOML of
/// <c>CODEALTA_CONFIG_CONTENT</c>, then each <c>-c key=value</c> in order, the last one winning. It is read-only: the run
/// never writes a configuration file. <c>--strict-config</c> rejects the settings no part of CodeAlta reads.
/// </summary>
internal static class CliConfig
{
    /// <summary>Builds the configuration options of a run and validates what they read.</summary>
    /// <returns>False with a message naming the part at fault when the configuration is not valid.</returns>
    public static bool TryCreate(CliCommonOptions options, CliEnvironment environment, string globalRoot,
        out CodeAltaConfigOptions? config, out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        config = null;
        var overlays = new List<CodeAltaConfigOverlay>();
        try
        {
            if (CodeAltaConfigOverlay.FromInlineContent(environment.GetVariable(CodeAltaConfigOverlay.InlineContentVariable)) is { } inline)
            {
                overlays.Add(inline);
            }

            foreach (var assignment in options.Config)
            {
                overlays.Add(CodeAltaConfigOverlay.FromAssignment(assignment));
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            error = exception.Message;
            return false;
        }

        var candidate = new CodeAltaConfigOptions
        {
            IgnoreUserConfig = options.IgnoreUserConfig,
            Overlays = overlays,
            Strict = options.StrictConfig,
            ReadOnly = true,
        };
        var validation = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = globalRoot, Config = candidate }).ValidateEffectiveGlobalConfig();
        if (!validation.IsValid)
        {
            error = validation.Line is { } line
                ? $"{validation.Message} (line {line}, column {validation.Column.GetValueOrDefault(1)})"
                : validation.Message ?? "The CodeAlta configuration is not valid.";
            return false;
        }

        config = candidate;
        error = null;
        return true;
    }
}
