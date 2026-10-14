namespace CodeAlta.Agent.Runtime;

/// <summary>The one disagreement on the protocol family of a session that is read as consistent.</summary>
internal static class AgentSessionProtocolFamily
{
    /// <summary>
    /// Tells whether a summary names the provider key of the session as its protocol family while the provider state
    /// names the family of the runtime of that provider: CodeAlta 1.8 wrote that summary when it attached a session
    /// of a provider whose key is not its type (a custom OpenAI-compatible provider, a second Claude Code account).
    /// Nothing else is taken as consistent.
    /// </summary>
    /// <param name="summary">The summary of the session.</param>
    /// <param name="state">The provider state of the session.</param>
    /// <param name="runtimeFamily">The protocol family of the runtime of the provider of the session; null when unknown.</param>
    /// <returns><see langword="true"/> for that pattern; the family of the state is then the one of the session.</returns>
    public static bool IsKeyWrittenAsFamily(AgentSessionSummary summary, AgentSessionState state, string? runtimeFamily)
        => !string.IsNullOrWhiteSpace(runtimeFamily)
           && !string.IsNullOrWhiteSpace(summary.ProviderKey)
           && string.Equals(summary.ProviderKey, state.ProviderKey, StringComparison.OrdinalIgnoreCase)
           && string.Equals(summary.ProtocolFamily, summary.ProviderKey, StringComparison.OrdinalIgnoreCase)
           && string.Equals(state.ProtocolFamily, runtimeFamily, StringComparison.OrdinalIgnoreCase);
}
