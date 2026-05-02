namespace CodeAlta.Plugins.Abstractions;

/// <summary>Identifies plugin diagnostic severity.</summary>
public enum PluginDiagnosticSeverity
{
    /// <summary>Trace diagnostic.</summary>
    Trace,
    /// <summary>Debug diagnostic.</summary>
    Debug,
    /// <summary>Informational diagnostic.</summary>
    Info,
    /// <summary>Warning diagnostic.</summary>
    Warning,
    /// <summary>Error diagnostic.</summary>
    Error,
    /// <summary>Critical diagnostic.</summary>
    Critical,
}

/// <summary>Identifies the source of a plugin diagnostic.</summary>
public enum PluginDiagnosticSource
{
    /// <summary>Discovery or metadata processing.</summary>
    Discovery,
    /// <summary>Initialization or activation.</summary>
    Initialization,
    /// <summary>Contribution enumeration or registration.</summary>
    Contribution,
    /// <summary>Callback invocation.</summary>
    Callback,
    /// <summary>UI rendering.</summary>
    UiRenderer,
    /// <summary>Prompt processing.</summary>
    Prompt,
    /// <summary>Tool handling.</summary>
    Tool,
    /// <summary>Compaction handling.</summary>
    Compaction,
    /// <summary>Backend/provider handling.</summary>
    Backend,
}

/// <summary>Describes an exception captured for plugin diagnostics.</summary>
public sealed record PluginExceptionInfo
{
    /// <summary>Gets the exception type name.</summary>
    public required string TypeName { get; init; }

    /// <summary>Gets the exception message.</summary>
    public required string Message { get; init; }

    /// <summary>Gets the stack trace, when captured.</summary>
    public string? StackTrace { get; init; }

    /// <summary>Creates exception info from an exception.</summary>
    /// <param name="exception">The exception.</param>
    /// <returns>The exception info.</returns>
    public static PluginExceptionInfo FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new PluginExceptionInfo
        {
            TypeName = exception.GetType().FullName ?? exception.GetType().Name,
            Message = exception.Message,
            StackTrace = exception.StackTrace,
        };
    }
}

/// <summary>Describes a plugin diagnostic attributed to a plugin and contribution point.</summary>
public sealed record PluginDiagnostic
{
    /// <summary>Gets the diagnostic severity.</summary>
    public required PluginDiagnosticSeverity Severity { get; init; }

    /// <summary>Gets the diagnostic source.</summary>
    public required PluginDiagnosticSource Source { get; init; }

    /// <summary>Gets the plugin runtime key.</summary>
    public required string PluginRuntimeKey { get; init; }

    /// <summary>Gets the plugin display name or type name.</summary>
    public string? PluginName { get; init; }

    /// <summary>Gets the contribution point, when known.</summary>
    public PluginPoint? Point { get; init; }

    /// <summary>Gets the natural contribution name, when known.</summary>
    public string? NaturalName { get; init; }

    /// <summary>Gets the diagnostic message.</summary>
    public required string Message { get; init; }

    /// <summary>Gets captured exception information.</summary>
    public PluginExceptionInfo? Exception { get; init; }

    /// <summary>Gets operation context metadata.</summary>
    public IReadOnlyDictionary<string, string> OperationContext { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets the diagnostic timestamp.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Creates attributed plugin diagnostics.</summary>
public static class PluginDiagnostics
{
    /// <summary>Creates a discovery diagnostic.</summary>
    /// <param name="pluginRuntimeKey">The plugin runtime key.</param>
    /// <param name="message">The diagnostic message.</param>
    /// <param name="exception">The optional exception.</param>
    /// <returns>The diagnostic.</returns>
    public static PluginDiagnostic Discovery(string pluginRuntimeKey, string message, Exception? exception = null)
        => Create(pluginRuntimeKey, PluginDiagnosticSource.Discovery, PluginDiagnosticSeverity.Error, message, null, null, exception);

    /// <summary>Creates an initialization diagnostic.</summary>
    /// <param name="pluginRuntimeKey">The plugin runtime key.</param>
    /// <param name="message">The diagnostic message.</param>
    /// <param name="exception">The optional exception.</param>
    /// <returns>The diagnostic.</returns>
    public static PluginDiagnostic Initialization(string pluginRuntimeKey, string message, Exception? exception = null)
        => Create(pluginRuntimeKey, PluginDiagnosticSource.Initialization, PluginDiagnosticSeverity.Error, message, null, null, exception);

    /// <summary>Creates a contribution diagnostic.</summary>
    /// <param name="pluginRuntimeKey">The plugin runtime key.</param>
    /// <param name="point">The contribution point.</param>
    /// <param name="naturalName">The natural contribution name.</param>
    /// <param name="message">The diagnostic message.</param>
    /// <param name="exception">The optional exception.</param>
    /// <returns>The diagnostic.</returns>
    public static PluginDiagnostic Contribution(string pluginRuntimeKey, PluginPoint point, string? naturalName, string message, Exception? exception = null)
        => Create(pluginRuntimeKey, PluginDiagnosticSource.Contribution, PluginDiagnosticSeverity.Warning, message, point, naturalName, exception);

    /// <summary>Creates a callback diagnostic.</summary>
    /// <param name="pluginRuntimeKey">The plugin runtime key.</param>
    /// <param name="point">The callback contribution point.</param>
    /// <param name="naturalName">The natural contribution name.</param>
    /// <param name="message">The diagnostic message.</param>
    /// <param name="exception">The optional exception.</param>
    /// <returns>The diagnostic.</returns>
    public static PluginDiagnostic Callback(string pluginRuntimeKey, PluginPoint point, string? naturalName, string message, Exception? exception = null)
        => Create(pluginRuntimeKey, PluginDiagnosticSource.Callback, PluginDiagnosticSeverity.Error, message, point, naturalName, exception);

    /// <summary>Creates a UI renderer diagnostic.</summary>
    /// <param name="pluginRuntimeKey">The plugin runtime key.</param>
    /// <param name="naturalName">The renderer name.</param>
    /// <param name="message">The diagnostic message.</param>
    /// <param name="exception">The optional exception.</param>
    /// <returns>The diagnostic.</returns>
    public static PluginDiagnostic UiRenderer(string pluginRuntimeKey, string? naturalName, string message, Exception? exception = null)
        => Create(pluginRuntimeKey, PluginDiagnosticSource.UiRenderer, PluginDiagnosticSeverity.Warning, message, PluginPoint.Ui, naturalName, exception);

    /// <summary>Creates a prompt diagnostic.</summary>
    /// <param name="pluginRuntimeKey">The plugin runtime key.</param>
    /// <param name="message">The diagnostic message.</param>
    /// <param name="exception">The optional exception.</param>
    /// <returns>The diagnostic.</returns>
    public static PluginDiagnostic Prompt(string pluginRuntimeKey, string message, Exception? exception = null)
        => Create(pluginRuntimeKey, PluginDiagnosticSource.Prompt, PluginDiagnosticSeverity.Warning, message, PluginPoint.PromptProcessor, null, exception);

    /// <summary>Creates a tool diagnostic.</summary>
    /// <param name="pluginRuntimeKey">The plugin runtime key.</param>
    /// <param name="toolName">The tool name.</param>
    /// <param name="message">The diagnostic message.</param>
    /// <param name="exception">The optional exception.</param>
    /// <returns>The diagnostic.</returns>
    public static PluginDiagnostic Tool(string pluginRuntimeKey, string? toolName, string message, Exception? exception = null)
        => Create(pluginRuntimeKey, PluginDiagnosticSource.Tool, PluginDiagnosticSeverity.Error, message, PluginPoint.AgentTool, toolName, exception);

    /// <summary>Creates a compaction diagnostic.</summary>
    /// <param name="pluginRuntimeKey">The plugin runtime key.</param>
    /// <param name="message">The diagnostic message.</param>
    /// <param name="exception">The optional exception.</param>
    /// <returns>The diagnostic.</returns>
    public static PluginDiagnostic Compaction(string pluginRuntimeKey, string message, Exception? exception = null)
        => Create(pluginRuntimeKey, PluginDiagnosticSource.Compaction, PluginDiagnosticSeverity.Warning, message, PluginPoint.Compaction, null, exception);

    /// <summary>Creates a backend diagnostic.</summary>
    /// <param name="pluginRuntimeKey">The plugin runtime key.</param>
    /// <param name="backendName">The backend name.</param>
    /// <param name="message">The diagnostic message.</param>
    /// <param name="exception">The optional exception.</param>
    /// <returns>The diagnostic.</returns>
    public static PluginDiagnostic Backend(string pluginRuntimeKey, string? backendName, string message, Exception? exception = null)
        => Create(pluginRuntimeKey, PluginDiagnosticSource.Backend, PluginDiagnosticSeverity.Error, message, PluginPoint.AgentBackend, backendName, exception);

    private static PluginDiagnostic Create(
        string pluginRuntimeKey,
        PluginDiagnosticSource source,
        PluginDiagnosticSeverity severity,
        string message,
        PluginPoint? point,
        string? naturalName,
        Exception? exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginRuntimeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new PluginDiagnostic
        {
            PluginRuntimeKey = pluginRuntimeKey,
            Source = source,
            Severity = severity,
            Message = message,
            Point = point,
            NaturalName = naturalName,
            Exception = exception is null ? null : PluginExceptionInfo.FromException(exception),
        };
    }
}

/// <summary>Identifies plugin failure policy metadata.</summary>
public enum PluginFailurePolicy
{
    /// <summary>Log and continue with other plugins.</summary>
    LogAndContinue,
    /// <summary>Disable only the failing contribution.</summary>
    DisableContribution,
    /// <summary>Deactivate the failing plugin.</summary>
    DeactivatePlugin,
    /// <summary>Fail the current operation closed.</summary>
    FailOperationClosed,
}

/// <summary>Identifies a plugin lifecycle state.</summary>
public enum PluginLifecycleState
{
    /// <summary>Assembly and type were discovered.</summary>
    Discovered,
    /// <summary>Package is known to CodeAlta.</summary>
    Installed,
    /// <summary>Assembly and plugin instance were loaded.</summary>
    Loaded,
    /// <summary>Runtime context was attached and initialization completed.</summary>
    Initialized,
    /// <summary>Plugin is globally active.</summary>
    Active,
    /// <summary>Plugin is deactivating.</summary>
    Deactivating,
    /// <summary>Plugin is deactivated.</summary>
    Deactivated,
    /// <summary>Assembly load context is eligible for unload.</summary>
    Unloaded,
    /// <summary>Plugin failed load, initialization, activation, or policy checks.</summary>
    Failed,
}

/// <summary>Identifies why a lifecycle transition occurred.</summary>
public enum PluginLifecycleReason
{
    /// <summary>Normal startup.</summary>
    Startup,
    /// <summary>Plugin reload.</summary>
    Reload,
    /// <summary>User or configuration disabled the plugin.</summary>
    Disable,
    /// <summary>A failure caused the transition.</summary>
    Failure,
    /// <summary>Application shutdown.</summary>
    Shutdown,
    /// <summary>Assembly unload.</summary>
    Unload,
}
