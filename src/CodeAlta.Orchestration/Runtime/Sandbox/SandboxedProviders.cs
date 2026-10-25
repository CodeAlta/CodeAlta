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
        "openai-chat", "openai-responses", "azure-openai", "codex", "copilot", "xai", "anthropic", "google-genai", "vertex-ai", "mistral",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets whether a provider type runs the tools of CodeAlta, which a sandbox holds.</summary>
    /// <param name="providerType">The type of the provider, or null when it is not known.</param>
    /// <returns>True for the types whose model CodeAlta drives; false for the others and for an unknown provider.</returns>
    public static bool RunsCodeAltaTools(string? providerType)
        => providerType is not null && (CodeAltaDrivenTypes.Contains(providerType) || providerType.StartsWith("openai-", StringComparison.OrdinalIgnoreCase));
}
