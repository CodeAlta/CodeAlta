using System.Net.Http.Headers;

namespace CodeAlta.Agent.CopilotDirect;

internal static class CopilotDirectHeaders
{
    public static void ApplyStaticHeaders(HttpRequestHeaders headers)
    {
        headers.TryAddWithoutValidation("User-Agent", "CodeAlta/1.0");
        headers.TryAddWithoutValidation("Editor-Version", "CodeAlta/1.0");
        headers.TryAddWithoutValidation("Editor-Plugin-Version", "codealta/1.0");
        headers.TryAddWithoutValidation("Copilot-Integration-Id", "vscode-chat");
    }

    public static void ApplyTurnHeaders(HttpRequestMessage request, string token, bool isAgentInitiated, bool hasVisionInput, bool anthropicMessages)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        ApplyStaticHeaders(request.Headers);
        request.Headers.TryAddWithoutValidation("Openai-Intent", "conversation-edits");
        request.Headers.TryAddWithoutValidation("X-Initiator", isAgentInitiated ? "agent" : "user");
        if (hasVisionInput)
        {
            request.Headers.TryAddWithoutValidation("Copilot-Vision-Request", "true");
        }

        if (anthropicMessages)
        {
            request.Headers.TryAddWithoutValidation("anthropic-beta", "interleaved-thinking-2025-05-14");
        }
    }
}
