namespace CodeAlta.Acp;

/// <summary>The JSON-RPC error codes ACP uses: those of JSON-RPC 2.0 and those ACP defines.</summary>
public static class AcpErrorCodes
{
    /// <summary>The message is not valid JSON (<c>-32700</c>).</summary>
    public const int ParseError = -32700;

    /// <summary>The message is not a valid request (<c>-32600</c>).</summary>
    public const int InvalidRequest = -32600;

    /// <summary>The method does not exist or is not supported (<c>-32601</c>).</summary>
    public const int MethodNotFound = -32601;

    /// <summary>The parameters of the request are not valid (<c>-32602</c>).</summary>
    public const int InvalidParams = -32602;

    /// <summary>The request failed for a reason of the one that answers it (<c>-32603</c>).</summary>
    public const int InternalError = -32603;

    /// <summary>The agent needs the client to authenticate first (<c>-32000</c>, <c>auth_required</c>).</summary>
    public const int AuthRequired = -32000;

    /// <summary>A resource, such as a file or a session, was not found (<c>-32002</c>).</summary>
    public const int ResourceNotFound = -32002;

    /// <summary>The request was cancelled with <c>$/cancel_request</c> before it completed (<c>-32800</c>).</summary>
    public const int RequestCancelled = -32800;
}
