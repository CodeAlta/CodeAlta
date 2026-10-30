using System.Diagnostics;
using System.Reflection;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent.Telemetry;

/// <summary>
/// The traces of the runs of sessions, in the OpenTelemetry semantic conventions of generative AI: an
/// <c>invoke_agent</c> span for each run, a <c>chat</c> span for each request to a model whose usage the provider reports,
/// and an <c>execute_tool</c> span for each tool call. Nothing is recorded while nothing listens to <see cref="SourceName"/>:
/// a host starts an OpenTelemetry exporter for it only when the user asks for one (an OTLP endpoint in the environment).
/// </summary>
/// <remarks>
/// The spans carry names, ids, models, durations, token counts and failures; never the content of prompts, answers,
/// tool arguments or tool results.
/// </remarks>
public static class AgentTelemetry
{
    /// <summary>The name of the <see cref="ActivitySource"/> of CodeAlta, which a tracer provider listens to.</summary>
    public const string SourceName = "CodeAlta";

    /// <summary>The name of the agent in the spans (<c>gen_ai.agent.name</c>).</summary>
    public const string AgentName = "CodeAlta";

    /// <summary>
    /// Gets the source of the spans. Static as the .NET guidance for an <see cref="ActivitySource"/> asks: it is created
    /// once, is safe on every thread, and holds no state of a session; its listeners are the process-wide registry of
    /// <c>System.Diagnostics</c>, which a source owned by a host would share anyway.
    /// </summary>
    internal static ActivitySource Source { get; } = new(SourceName,
        typeof(AgentTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]);

    /// <summary>
    /// Gives the value of <c>gen_ai.provider.name</c> for a provider: one of the well-known values of the conventions when
    /// the provider is one of them, else the key of the provider.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <returns>The name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> is <see langword="null"/>.</exception>
    public static string ProviderName(ModelProviderRuntimeDescriptor provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var host = provider.BaseUri?.Host;
        return provider.ProtocolFamily switch
        {
            "anthropic-messages" or "claude-code" => "anthropic",
            "google-genai" => "gcp.gemini",
            "vertex-ai" => "gcp.vertex_ai",
            "mistral-chat" or "mistral-responses" => "mistral",
            "xai" => "x_ai",
            "codex" or "codex-cli" => "openai",
            // The OpenAI protocols serve many endpoints: only those of OpenAI and of Azure OpenAI are named after them.
            "openai-responses" or "openai-chat" when host is null || host.EndsWith("openai.com", StringComparison.OrdinalIgnoreCase) => "openai",
            "openai-responses" or "openai-chat" when host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase) => "azure.ai.openai",
            _ => provider.ProviderKey,
        };
    }
}
