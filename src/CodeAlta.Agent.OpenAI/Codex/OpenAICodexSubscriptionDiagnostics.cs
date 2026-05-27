using System.Net;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.OpenAI;

namespace CodeAlta.Agent.OpenAI.Codex;

internal static class OpenAICodexSubscriptionDiagnostics
{
    public static string CreateRequestShape(
        OpenAIProviderOptions provider,
        AgentTurnRequest request,
        int retryCount,
        string eventName,
        string? responseId = null,
        HttpStatusCode? httpStatus = null,
        string? errorType = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(request);

        var endpoint = SanitizeEndpoint(provider.BaseUri);
        var sanitizedErrorType = OpenAICodexSubscriptionSecretRedactor.Redact(errorType);
        return string.Join(
            " ",
            new[]
            {
                "codexSubscription",
                $"event={SanitizeToken(eventName)}",
                $"provider={SanitizeToken(provider.ProviderKey)}",
                $"endpoint={endpoint}",
                $"model={SanitizeToken(request.ModelId)}",
                $"session={SanitizeToken(request.SessionId)}",
                $"run={SanitizeToken(request.RunId.Value)}",
                $"retry={retryCount}",
                responseId is null ? null : $"response={SanitizeToken(responseId)}",
                httpStatus is null ? null : $"http={(int)httpStatus.Value}",
                string.IsNullOrWhiteSpace(sanitizedErrorType) ? null : $"errorType={SanitizeToken(sanitizedErrorType)}",
            }.Where(static part => part is not null));
    }

    public static string SanitizeEndpoint(Uri? endpoint)
    {
        if (endpoint is null)
        {
            return "(default)";
        }

        var path = string.IsNullOrWhiteSpace(endpoint.AbsolutePath) ? "/" : endpoint.AbsolutePath;
        return endpoint.Host + path;
    }

    private static string SanitizeToken(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "(none)"
            : OpenAICodexSubscriptionSecretRedactor.Redact(value.Trim()).Replace(' ', '_');
}
