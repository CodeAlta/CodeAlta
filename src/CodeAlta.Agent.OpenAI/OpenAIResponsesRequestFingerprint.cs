#pragma warning disable OPENAI001

using System.Buffers;
using System.Buffers.Binary;
using System.ClientModel.Primitives;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;
using OpenAI.Responses;

namespace CodeAlta.Agent.OpenAI;

/// <summary>
/// Fingerprints of the JSON of a Responses request, by which a request is found to continue the previous response of
/// a session (<c>previous_response_id</c>) when the request without its input and the first items of its input are the
/// same as before.
/// </summary>
/// <remarks>
/// A fingerprint is the first 128 bits of the SHA-256 of the JSON that was compared as text: the serialization of the
/// SDK, read as the parser read the text of it, and the request rewritten without <c>input</c>,
/// <c>previous_response_id</c> and <c>client_metadata</c>. Two fingerprints are equal when those texts are, and a
/// session keeps 16 bytes for each item of its conversation instead of the text of the conversation.
/// </remarks>
internal static class OpenAIResponsesRequestFingerprint
{
    /// <summary>Gets the fingerprint of the request without its input, its previous response and its client metadata.</summary>
    public static UInt128 OfRequestWithoutInput(CreateResponseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options is not IPersistableModel<CreateResponseOptions> persistable)
        {
            return OfText(options.ToString() ?? string.Empty, withoutInput: true);
        }

        using var document = JsonDocument.Parse(ReadAsTheTextWasRead(persistable.Write(new ModelReaderWriterOptions("J"))));
        var buffer = new ArrayBufferWriter<byte>();
        WriteWithoutInput(document, buffer);
        return Hash(buffer.WrittenSpan);
    }

    /// <summary>Gets the fingerprint of every item of the input of the request, in order.</summary>
    public static UInt128[] OfInputItems(CreateResponseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var fingerprints = new UInt128[options.InputItems.Count];
        for (var index = 0; index < fingerprints.Length; index++)
        {
            fingerprints[index] = OfItem(options.InputItems[index]);
        }

        return fingerprints;
    }

    /// <summary>Gets the fingerprint of one item of an input or of an output.</summary>
    public static UInt128 OfItem(ResponseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item is not IPersistableModel<ResponseItem> persistable)
        {
            return OfText(item.ToString() ?? string.Empty, withoutInput: false);
        }

        var json = ReadAsTheTextWasRead(persistable.Write(new ModelReaderWriterOptions("J"))).Span;
        // The text was the root value, without the white space around it; the parser refused anything else.
        var reader = new Utf8JsonReader(json);
        reader.Read();
        var start = (int)reader.TokenStartIndex;
        reader.Skip();
        var end = (int)reader.BytesConsumed;
        while (reader.Read())
        {
        }

        return Hash(json[start..end]);
    }

    // The text was the serialization decoded from UTF-8 (an invalid sequence read as U+FFFD), then parsed: what the parser
    // read is that text in UTF-8, which is the serialization itself when it is valid UTF-8, as it always is.
    private static ReadOnlyMemory<byte> ReadAsTheTextWasRead(BinaryData data)
    {
        var bytes = data.ToMemory();
        return Utf8.IsValid(bytes.Span)
            ? bytes
            : Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes.Span));
    }

    // The text of a model that cannot write itself, as it was used: parsed, then its root value.
    private static UInt128 OfText(string text, bool withoutInput)
    {
        using var document = JsonDocument.Parse(text);
        if (!withoutInput)
        {
            return Hash(Encoding.UTF8.GetBytes(document.RootElement.GetRawText()));
        }

        var buffer = new ArrayBufferWriter<byte>();
        WriteWithoutInput(document, buffer);
        return Hash(buffer.WrittenSpan);
    }

    private static void WriteWithoutInput(JsonDocument document, IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!property.NameEquals("input"u8) &&
                !property.NameEquals("previous_response_id"u8) &&
                !property.NameEquals("client_metadata"u8))
            {
                property.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }

    private static UInt128 Hash(ReadOnlySpan<byte> utf8Json)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(utf8Json, digest);
        return BinaryPrimitives.ReadUInt128LittleEndian(digest);
    }
}
