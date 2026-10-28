using System.Text.Json;

namespace CodeAlta.CodexAppServer;

/// <summary>An error the Codex app-server answered a request with.</summary>
public sealed class CodexJsonRpcException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="CodexJsonRpcException"/> class.</summary>
    /// <param name="method">The method of the request.</param>
    /// <param name="code">The code of the error.</param>
    /// <param name="message">The message of the error.</param>
    /// <param name="data">The data of the error, or <see langword="null"/>.</param>
    public CodexJsonRpcException(string method, int code, string message, JsonElement? data)
        : base(string.IsNullOrWhiteSpace(message) ? $"{method} failed with code {code}." : message)
    {
        Method = method;
        Code = code;
        Data = data;
    }

    /// <summary>Gets the method of the request.</summary>
    public string Method { get; }

    /// <summary>Gets the code of the error.</summary>
    public int Code { get; }

    /// <summary>Gets the data of the error, or <see langword="null"/>.</summary>
    public new JsonElement? Data { get; }
}

/// <summary>The Codex app-server stopped, or closed its output, before it answered.</summary>
public sealed class CodexConnectionClosedException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="CodexConnectionClosedException"/> class.</summary>
    /// <param name="message">What happened, with the last lines of its errors when there are some.</param>
    /// <param name="innerException">The cause, or <see langword="null"/>.</param>
    public CodexConnectionClosedException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>The codes of the errors of the JSON-RPC protocol.</summary>
public static class CodexErrorCodes
{
    /// <summary>The method does not exist.</summary>
    public const int MethodNotFound = -32601;

    /// <summary>The parameters are invalid.</summary>
    public const int InvalidParams = -32602;

    /// <summary>An error of the server.</summary>
    public const int InternalError = -32603;
}
