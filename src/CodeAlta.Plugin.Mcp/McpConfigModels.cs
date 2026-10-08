using System.Text.Json.Nodes;

namespace CodeAlta.Plugin.Mcp;

internal enum McpConfigScope
{
    Global,
    Project,
}

internal enum McpConfigFlavor
{
    CodeAlta,
    Copilot,
    Vscode,
    Claude,
    Intellij,
}

// Whose file a definition comes from. CodeAlta writes only its own; the others are read where another tool keeps them.
internal enum McpConfigOrigin
{
    // <project>/.alta/mcp.json, ~/.alta/mcp.json
    CodeAlta,

    // <project>/.mcp.json, the file several tools share
    Common,

    // <project>/.github/mcp.json, ~/.copilot/mcp-config.json
    Copilot,

    // <project>/.vscode/mcp.json
    Vscode,
}

internal enum McpTransportKind
{
    Stdio,
    Http,
}

// Why a server of a file of another tool is left out.
internal enum McpSkipReason
{
    // The definition is not one CodeAlta can read (no command and no URL, an unknown transport, a field of the wrong type).
    Invalid,

    // It uses ${input:...}, a value Visual Studio Code asks the user for.
    InputVariable,

    // It uses a variable CodeAlta does not know.
    UnknownVariable,

    // It takes its environment from a file (envFile).
    EnvironmentFile,
}

internal sealed record McpServerDefinition
{
    public required string Key { get; init; }

    public required McpTransportKind Transport { get; init; }

    public required McpConfigScope SourceScope { get; init; }

    public required string SourcePath { get; init; }

    public required McpConfigFlavor SourceFlavor { get; init; }

    public McpConfigOrigin SourceOrigin { get; init; }

    public string? Command { get; init; }

    public IReadOnlyList<string> Args { get; init; } = [];

    public string? Cwd { get; init; }

    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public string? Url { get; init; }

    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public McpOAuthOptions? OAuth { get; init; }
}

internal sealed record McpConfigSource
{
    public required McpConfigScope Scope { get; init; }

    public required string Path { get; init; }

    public bool Exists { get; init; }

    public bool DirectoryExists { get; init; }

    public bool IsWritable { get; init; }

    public McpConfigFlavor? Flavor { get; init; }

    public string? RootKey { get; init; }

    public bool IsValid { get; init; } = true;

    public string? Diagnostic { get; init; }

    public IReadOnlyList<McpServerDefinition> Servers { get; init; } = [];

    public McpConfigOrigin Origin { get; init; }

    // The servers of a file of another tool that are left out; a file of CodeAlta is invalid as a whole instead.
    public IReadOnlyList<McpSkippedServer> SkippedServers { get; init; } = [];
}

internal sealed record McpSkippedServer
{
    public required string Key { get; init; }

    public required McpConfigScope Scope { get; init; }

    public required McpConfigOrigin Origin { get; init; }

    public required string Path { get; init; }

    public required McpSkipReason Reason { get; init; }

    // Names the field or the variable; never holds a value of the definition.
    public required string Message { get; init; }
}

internal sealed record McpEffectiveServer
{
    public required McpServerDefinition Definition { get; init; }

    public bool OverridesGlobal { get; init; }

    public McpServerDefinition? ShadowedGlobalDefinition { get; init; }
}

// A definition that is not used because a source that comes first defines the same key.
internal sealed record McpShadowedServer
{
    public required McpServerDefinition Definition { get; init; }

    public required McpServerDefinition OverriddenBy { get; init; }
}

internal sealed record McpConfigSnapshot
{
    // The two files CodeAlta writes: global first, then the project one when a project is given.
    public IReadOnlyList<McpConfigSource> Sources { get; init; } = [];

    // The files of other tools that exist, in the order they are applied. CodeAlta never writes them.
    public IReadOnlyList<McpConfigSource> ExternalSources { get; init; } = [];

    public IReadOnlyList<McpEffectiveServer> EffectiveServers { get; init; } = [];

    public IReadOnlyList<McpShadowedServer> ShadowedServers { get; init; } = [];

    public McpConfigScope DefaultWriteScope { get; init; }
}

internal sealed record McpConfigPathOptions
{
    public bool ProbeWritability { get; init; } = true;
    public string? UserHomeDirectory { get; init; }

    public string? ProjectDirectory { get; init; }
}

internal sealed record McpConfigMutationResult
{
    public required string Path { get; init; }

    public required McpConfigScope Scope { get; init; }

    public required McpConfigFlavor Flavor { get; init; }

    public bool CreatedFile { get; init; }

    public bool Changed { get; init; }
}

internal sealed record McpConfigDocument
{
    public required JsonObject Root { get; init; }

    public required McpConfigFlavor Flavor { get; init; }

    public required string RootKey { get; init; }
}
