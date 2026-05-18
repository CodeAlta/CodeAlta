using System.Net.Http.Headers;

namespace CodeAlta.Agent.Copilot;

internal static class CopilotDirectHeaders
{
    public static void ApplyStaticHeaders(HttpRequestHeaders headers)
    {
        headers.TryAddWithoutValidation("User-Agent", "CodeAlta/1.0");
        headers.TryAddWithoutValidation("Editor-Version", "CodeAlta/1.0");
        headers.TryAddWithoutValidation("Editor-Plugin-Version", "codealta/1.0");
        headers.TryAddWithoutValidation("Copilot-Integration-Id", "vscode-chat");
    }

}
