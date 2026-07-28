namespace CodeAlta.Agent.OpenAI;

internal enum OpenAIResponsesProtocolErrorCode
{
    StreamClosedBeforeTerminalResponse,
    StreamCompletedWithoutTerminalPayload,
    TerminalResponseWithoutAssistantOutput,
    UnsupportedTerminalResponseUpdate,
    StreamClosedAfterToolCall,
    UnexpectedBinaryFrame,
}

internal sealed class OpenAIResponsesProtocolException : InvalidOperationException
{
    public OpenAIResponsesProtocolException(
        OpenAIResponsesProtocolErrorCode errorCode,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public OpenAIResponsesProtocolErrorCode ErrorCode { get; }
}

internal enum OpenAIResponsesTransportErrorCode
{
    WebSocketConnectTimeout,
    WebSocketReceiveIdleTimeout,
}

internal sealed class OpenAIResponsesTransportException : TimeoutException
{
    public OpenAIResponsesTransportException(
        OpenAIResponsesTransportErrorCode errorCode,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public OpenAIResponsesTransportErrorCode ErrorCode { get; }
}
