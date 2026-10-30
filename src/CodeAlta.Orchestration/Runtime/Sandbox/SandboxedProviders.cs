using System.Collections.Frozen;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>The providers a sandboxed session can run on: those whose model CodeAlta drives with its own tools.</summary>
/// <remarks>
/// A provider that runs its own agent (Claude Code, ACP agents, the types of plugins) runs its tools in its own process,
/// outside the sandbox of the session, until that process runs in the sandbox too.
/// </remarks>
internal static class SandboxedProviders
{
    private static readonly FrozenSet<string> CodeAltaDrivenTypes = new[]
    {
        "openai-chat", "openai-responses", "azure-openai", "codex", "copilot", "xai", "anthropic", "google-genai", "vertex-ai", "mistral", "mistral-vibe", "llama-cpp",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets whether a provider type runs the tools of CodeAlta, which a sandbox holds.</summary>
    /// <param name="providerType">The type of the provider, or null when it is not known.</param>
    /// <returns>True for the types whose model CodeAlta drives; false for the others and for an unknown provider.</returns>
    public static bool RunsCodeAltaTools(string? providerType)
        => providerType is not null && (CodeAltaDrivenTypes.Contains(providerType) || providerType.StartsWith("openai-", StringComparison.OrdinalIgnoreCase));

    /// <summary>Gets whether a provider that runs its own tools starts its process in the sandbox of the session (Claude Code, ACP agents, Codex CLI).</summary>
    /// <param name="providerType">The type of the provider, or null when it is not known.</param>
    /// <returns>True for the types whose process the sandbox starts.</returns>
    public static bool StartsInSandbox(string? providerType)
        => string.Equals(providerType, "claude-code", StringComparison.OrdinalIgnoreCase) || string.Equals(providerType, "acp", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(providerType, "codex-cli", StringComparison.OrdinalIgnoreCase);

    /// <summary>Gets whether a sandboxed session can run on a provider type: its tools run in the sandbox either way.</summary>
    /// <param name="providerType">The type of the provider, or null when it is not known.</param>
    /// <returns>True when the sandbox holds what the provider runs.</returns>
    public static bool RunsInSandbox(string? providerType) => RunsCodeAltaTools(providerType) || StartsInSandbox(providerType);
}
