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

internal enum McpTransportKind
{
    Stdio,
    Http,
}

internal sealed record McpServerDefinition
{
    public required string Key { get; init; }

    public required McpTransportKind Transport { get; init; }

    public required McpConfigScope SourceScope { get; init; }

    public required string SourcePath { get; init; }

    public required McpConfigFlavor SourceFlavor { get; init; }

    public string? Command { get; init; }

    public IReadOnlyList<string> Args { get; init; } = [];

    public string? Cwd { get; init; }

    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public string? Url { get; init; }

    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
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
}

internal sealed record McpEffectiveServer
{
    public required McpServerDefinition Definition { get; init; }

    public bool OverridesGlobal { get; init; }

    public McpServerDefinition? ShadowedGlobalDefinition { get; init; }
}

internal sealed record McpConfigSnapshot
{
    public IReadOnlyList<McpConfigSource> Sources { get; init; } = [];

    public IReadOnlyList<McpEffectiveServer> EffectiveServers { get; init; } = [];

    public IReadOnlyList<McpServerDefinition> ShadowedGlobalServers { get; init; } = [];

    public McpConfigScope DefaultWriteScope { get; init; }
}

internal sealed record McpConfigPathOptions
{
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
