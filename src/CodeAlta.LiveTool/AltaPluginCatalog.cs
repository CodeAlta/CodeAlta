namespace CodeAlta.LiveTool;

/// <summary>
/// Read-only plugin runtime view used by <c>alta plugin</c> and <c>alta tool</c> discovery commands.
/// </summary>
public interface IAltaPluginCatalog
{
    /// <summary>Lists loaded or discovered plugin summaries.</summary>
    IReadOnlyList<AltaPluginSummary> ListPlugins();

    /// <summary>Gets one plugin summary by runtime key.</summary>
    AltaPluginSummary? GetPlugin(string runtimeKey);

    /// <summary>Lists plugin-contributed command policies.</summary>
    IReadOnlyList<AltaCommandPolicy> ListCommandPolicies();
}

/// <summary>
/// Describes a plugin for JSONL discovery output.
/// </summary>
public sealed record AltaPluginSummary
{
    /// <summary>Gets the runtime key.</summary>
    public required string RuntimeKey { get; init; }

    /// <summary>Gets the display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Gets the plugin version.</summary>
    public string? Version { get; init; }

    /// <summary>Gets the plugin scope.</summary>
    public string? Scope { get; init; }

    /// <summary>Gets the plugin state.</summary>
    public string? State { get; init; }

    /// <summary>Gets diagnostic messages.</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}
