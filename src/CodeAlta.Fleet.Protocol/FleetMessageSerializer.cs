using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Fleet.Protocol;

/// <summary>
/// Reads and writes the frames of the fleet protocol, with source-generated contracts.
/// </summary>
/// <remarks>
/// A frame is read in two passes: the <c>t</c> property is found first, wherever it is in the object, then the frame
/// is read as its kind. Unknown properties are ignored and an unknown kind reads as <see langword="null"/>, so that a
/// newer peer's additions do not break an older one.
/// </remarks>
public static class FleetMessageSerializer
{
    /// <summary>Writes a frame as UTF-8 JSON.</summary>
    /// <param name="message">The frame.</param>
    /// <returns>The UTF-8 JSON of the frame.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException"><paramref name="message"/> is of a kind this library does not define.</exception>
    public static byte[] Serialize(FleetMessage message)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        Serialize(message, buffer);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Writes a frame as UTF-8 JSON to a buffer.</summary>
    /// <param name="message">The frame.</param>
    /// <param name="destination">The buffer the JSON is written to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> or <paramref name="destination"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException"><paramref name="message"/> is of a kind this library does not define.</exception>
    public static void Serialize(FleetMessage message, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(destination);
        var context = FleetProtocolJsonContext.Default;
        var body = message switch
        {
            FleetRequest request => JsonSerializer.SerializeToUtf8Bytes(request, context.FleetRequest),
            FleetResponse response => JsonSerializer.SerializeToUtf8Bytes(response, context.FleetResponse),
            FleetEvent fleetEvent => JsonSerializer.SerializeToUtf8Bytes(fleetEvent, context.FleetEvent),
            FleetPing ping => JsonSerializer.SerializeToUtf8Bytes(ping, context.FleetPing),
            FleetPong pong => JsonSerializer.SerializeToUtf8Bytes(pong, context.FleetPong),
            FleetHelloChallenge challenge => JsonSerializer.SerializeToUtf8Bytes(challenge, context.FleetHelloChallenge),
            FleetHello hello => JsonSerializer.SerializeToUtf8Bytes(hello, context.FleetHello),
            FleetWelcome welcome => JsonSerializer.SerializeToUtf8Bytes(welcome, context.FleetWelcome),
            FleetHandshakeError error => JsonSerializer.SerializeToUtf8Bytes(error, context.FleetHandshakeError),
            _ => throw new NotSupportedException($"The frame type '{message.GetType().Name}' is not part of the fleet protocol."),
        };

        // The kind is written first, so that a reader that looks for it finds it at once: {"t":"<kind>", then the body
        // of the contract without its opening brace. A contract is always written as an object.
        WriteUtf8(destination, "{\"t\":\"");
        WriteUtf8(destination, message.Type);
        WriteUtf8(destination, body.Length > 2 ? "\"," : "\"");
        destination.Write(body.AsSpan(1));
    }

    private static void WriteUtf8(IBufferWriter<byte> destination, string text)
    {
        var span = destination.GetSpan(System.Text.Encoding.UTF8.GetMaxByteCount(text.Length));
        destination.Advance(System.Text.Encoding.UTF8.GetBytes(text, span));
    }

    /// <summary>Reads a frame from UTF-8 JSON.</summary>
    /// <param name="utf8Json">The UTF-8 JSON of the frame.</param>
    /// <returns>The frame, or <see langword="null"/> when its kind is unknown to this version.</returns>
    /// <exception cref="JsonException">The text is not a JSON object with a string <c>t</c> property, or a known kind does not read.</exception>
    public static FleetMessage? Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        var context = FleetProtocolJsonContext.Default;
        return ReadKind(utf8Json) switch
        {
            FleetRequest.Kind => Read(utf8Json, context.FleetRequest),
            FleetResponse.Kind => Read(utf8Json, context.FleetResponse),
            FleetEvent.Kind => Read(utf8Json, context.FleetEvent),
            FleetPing.Kind => Read(utf8Json, context.FleetPing),
            FleetPong.Kind => Read(utf8Json, context.FleetPong),
            FleetHelloChallenge.Kind => Read(utf8Json, context.FleetHelloChallenge),
            FleetHello.Kind => Read(utf8Json, context.FleetHello),
            FleetWelcome.Kind => Read(utf8Json, context.FleetWelcome),
            FleetHandshakeError.Kind => Read(utf8Json, context.FleetHandshakeError),
            _ => null,
        };
    }

    private static T Read<T>(ReadOnlySpan<byte> utf8Json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) where T : FleetMessage
        => JsonSerializer.Deserialize(utf8Json, typeInfo) ?? throw new JsonException("A frame cannot be null.");

    private static string ReadKind(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new JsonException("A frame must be a JSON object.");
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var isKind = reader.ValueTextEquals("t"u8);
            if (!reader.Read()) break;
            if (isKind)
            {
                if (reader.TokenType != JsonTokenType.String) throw new JsonException("The 't' property of a frame must be a string.");
                return reader.GetString()!;
            }

            reader.Skip();
        }

        throw new JsonException("A frame must have a 't' property.");
    }
}

/// <summary>The source-generated contracts of the frames.</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FleetRequest))]
[JsonSerializable(typeof(FleetResponse))]
[JsonSerializable(typeof(FleetEvent))]
[JsonSerializable(typeof(FleetPing))]
[JsonSerializable(typeof(FleetPong))]
[JsonSerializable(typeof(FleetHelloChallenge))]
[JsonSerializable(typeof(FleetHello))]
[JsonSerializable(typeof(FleetWelcome))]
[JsonSerializable(typeof(FleetHandshakeError))]
internal sealed partial class FleetProtocolJsonContext : JsonSerializerContext
{
}
