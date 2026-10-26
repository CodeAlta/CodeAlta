namespace CodeAlta.Cli;

/// <summary>The output formats of <c>altacli</c>.</summary>
internal enum CliFormat
{
    /// <summary>Text: standard output carries the final answer only.</summary>
    Text,

    /// <summary>JSONL: one JSON-RPC notification of ACP per line (<see cref="CliJsonlWriter"/>).</summary>
    Jsonl,
}

/// <summary>The options every command that composes a host takes: the storage, the configuration, plugins and output.</summary>
internal class CliCommonOptions
{
    public string? Project { get; set; }

    public bool Isolated { get; set; }

    public bool Developer { get; set; }

    public string? Profile { get; set; }

    public bool Ephemeral { get; set; }

    public bool IgnoreUserConfig { get; set; }

    public List<string> Config { get; } = [];

    public bool StrictConfig { get; set; }

    public bool Offline { get; set; }

    public bool NoMcp { get; set; }

    public bool NoPlugins { get; set; }

    public bool NoSkills { get; set; }

    // --code-mode[=on|only]: the mode of Code Mode, whatever [code_mode] of the configuration says; null for the configuration.
    public string? CodeMode { get; set; }

    // The sandbox profile of the sessions of run and acp, or `off`; null for what the settings decide.
    public string? Sandbox { get; set; }

    public bool Verbose { get; set; }

    public bool Quiet { get; set; }

    public string? FormatText { get; set; }
}

/// <summary>The options of <c>altacli run</c>.</summary>
internal sealed class CliRunOptions : CliCommonOptions
{
    public List<string> Prompt { get; } = [];

    public string? Model { get; set; }

    public string? Tier { get; set; }

    public string? Reasoning { get; set; }

    public string? PromptId { get; set; }

    public string? Title { get; set; }

    public string? Resume { get; set; }

    public string? SessionId { get; set; }

    public List<string> Files { get; } = [];

    public string? OutputLastMessage { get; set; }

    public string? OutputSchema { get; set; }

    public string? Timeout { get; set; }

    public List<string> DisableTools { get; } = [];
}

/// <summary>The options of <c>altacli acp</c>: the common ones, and what its new sessions start with.</summary>
internal sealed class CliAcpOptions : CliCommonOptions
{
    public string? Model { get; set; }

    public string? Tier { get; set; }

    public string? Reasoning { get; set; }

    public string? PromptId { get; set; }

    public List<string> DisableTools { get; } = [];
}

/// <summary>The options of <c>altacli models</c> and <c>altacli auth check</c>.</summary>
internal sealed class CliProviderOptions : CliCommonOptions
{
    public string? Provider { get; set; }
}

/// <summary>The options of <c>altacli alta</c>: the common ones, then the arguments of the <c>alta</c> command.</summary>
internal sealed class CliAltaOptions : CliCommonOptions
{
    public List<string> Arguments { get; } = [];
}
