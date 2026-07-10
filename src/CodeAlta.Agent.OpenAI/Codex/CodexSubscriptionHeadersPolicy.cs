using System.ClientModel.Primitives;

namespace CodeAlta.Agent.OpenAI.Codex;

internal sealed class CodexSubscriptionHeadersPolicy : PipelinePolicy
{
    private readonly CodexSubscriptionHeaderContext _context;

    public CodexSubscriptionHeadersPolicy(CodexSubscriptionHeaderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        ApplyRequestHeaders(message);
        ProcessNext(message, pipeline, currentIndex);
        CaptureResponseHeaders(message);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        await ApplyRequestHeadersAsync(message).ConfigureAwait(false);
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
        CaptureResponseHeaders(message);
    }

    private void ApplyRequestHeaders(PipelineMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var accountContext = _context.AuthManager is null
            ? null
            : _context.AuthManager
                .GetAccountContextAsync(message.CancellationToken)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        ApplyRequestHeadersCore(message, accountContext);
    }

    private async ValueTask ApplyRequestHeadersAsync(PipelineMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var accountContext = _context.AuthManager is null
            ? null
            : await _context.AuthManager.GetAccountContextAsync(message.CancellationToken).ConfigureAwait(false);
        ApplyRequestHeadersCore(message, accountContext);
    }

    private void ApplyRequestHeadersCore(
        PipelineMessage message,
        OpenAICodexSubscriptionAccountContext? accountContext)
    {
        var headers = message.Request.Headers;
        var accountId = !string.IsNullOrWhiteSpace(_context.AccountId)
            ? _context.AccountId
            : accountContext?.AccountId;
        if (!string.IsNullOrWhiteSpace(accountId))
        {
            headers.Set("ChatGPT-Account-Id", accountId);
        }

        headers.Set("originator", "codealta");
        if (_context.SendResponsesBetaHeader)
        {
            headers.Set("OpenAI-Beta", "responses=experimental");
        }

        if (!string.IsNullOrWhiteSpace(_context.SessionId))
        {
            headers.Set("session-id", _context.SessionId);
            headers.Set("thread-id", _context.SessionId);
            headers.Set("x-client-request-id", _context.SessionId);
        }

        if (_context.RequestContext is { } requestContext)
        {
            foreach (var header in requestContext.CompatibilityHeaders)
            {
                headers.Set(header.Key, header.Value);
            }
        }

        if (_context.IsFedRamp || accountContext?.IsFedRamp == true)
        {
            headers.Set("X-OpenAI-Fedramp", "true");
        }

        if (_context.TurnState.TryGetCapturedState(out var turnState))
        {
            headers.Set("x-codex-turn-state", turnState);
        }
    }

    private void CaptureResponseHeaders(PipelineMessage message)
    {
        if (message.Response?.Headers.TryGetValue("x-codex-turn-state", out var turnState) == true &&
            !string.IsNullOrWhiteSpace(turnState))
        {
            _context.TurnState.Capture(turnState);
        }
    }
}

internal sealed record CodexSubscriptionHeaderContext(
    string? AccountId,
    string? SessionId,
    bool IsFedRamp,
    bool SendResponsesBetaHeader,
    CodexTurnState TurnState,
    OpenAICodexSubscriptionAuthManager? AuthManager = null,
    CodexSubscriptionRequestContext? RequestContext = null);
