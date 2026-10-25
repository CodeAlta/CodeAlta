using System.Text.Json;

namespace CodeAlta.Acp;

/// <summary>
/// Represents a JSON-RPC error returned by an ACP agent.
/// </summary>
public sealed class AcpJsonRpcException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AcpJsonRpcException"/> class.
    /// </summary>
    /// <param name="code">The JSON-RPC error code.</param>
    /// <param name="message">The JSON-RPC error message.</param>
    /// <param name="errorPayload">Optional raw error payload.</param>
    public AcpJsonRpcException(int code, string message, JsonElement? errorPayload = null)
        : base(message)
    {
        Code = code;
        ErrorPayload = errorPayload;
    }

    /// <summary>
    /// Gets the JSON-RPC error code.
    /// </summary>
    public int Code { get; }

    /// <summary>
    /// Gets the optional raw error payload.
    /// </summary>
    public JsonElement? ErrorPayload { get; }

    /// <summary>
    /// Gets the <c>data</c> of the error, or <see langword="null"/> when it has none.
    /// </summary>
    public JsonElement? ErrorData
        => ErrorPayload is { ValueKind: JsonValueKind.Object } payload && payload.TryGetProperty("data", out var data) && data.ValueKind != JsonValueKind.Undefined
            ? data
            : null;

    /// <summary>
    /// Creates an error with <c>data</c>, as an ACP agent throws it to answer a request with that error.
    /// </summary>
    /// <param name="code">The JSON-RPC error code, such as <see cref="AcpErrorCodes.AuthRequired"/>.</param>
    /// <param name="message">The JSON-RPC error message.</param>
    /// <param name="data">The <c>data</c> of the error.</param>
    /// <returns>The exception.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="data"/> is an undefined element.</exception>
    public static AcpJsonRpcException WithData(int code, string message, JsonElement data)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (data.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException("The data of an error must be a JSON value.", nameof(data));
        }

        var payload = AcpJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("code", code);
            writer.WriteString("message", message);
            writer.WritePropertyName("data");
            data.WriteTo(writer);
            writer.WriteEndObject();
        });
        return new AcpJsonRpcException(code, message, payload);
    }
}
