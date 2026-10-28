using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace CodeAlta.Plugin.Mobile;

/// <summary>
/// The body of a request that carries a prompt: its fields (the text and the choices), read as an object, and its attachments,
/// kept as the UTF-8 the phone sent until they are written for alta. Nothing turns the files into strings here: their base64 is
/// checked and copied as it came.
/// </summary>
internal sealed class MobilePromptBody : IDisposable
{
    private JsonDocument? _document;

    private MobilePromptBody(JsonObject fields, JsonDocument? document, JsonElement? attachments)
    {
        Fields = fields;
        _document = document;
        Attachments = attachments;
    }

    /// <summary>Gets every member of the body but its attachments.</summary>
    internal JsonObject Fields { get; }

    /// <summary>Gets the attachments as the phone sent them, or null when it sent none; gone once <see cref="ReleaseAttachments"/> ran.</summary>
    internal JsonElement? Attachments { get; private set; }

    /// <summary>Reads the JSON object a request posted, at most <paramref name="maximum"/> bytes.</summary>
    /// <returns>The body, or null when it is not a JSON object, has another type or is too large.</returns>
    internal static async Task<MobilePromptBody?> ReadAsync(HttpRequest request, long maximum, CancellationToken cancellationToken)
    {
        if (request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true || request.ContentLength > maximum) return null;
        try
        {
            // The server's limit for the route (MaxRequestBodySize) stops a body without a length that grows past it.
            return From(await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken).ConfigureAwait(false));
        }
        catch (JsonException)
        {
            return null;
        }
        catch (BadHttpRequestException)
        {
            return null;
        }
    }

    /// <summary>A body read whole, such as the one of a test.</summary>
    /// <returns>The body, or null when it is not a JSON object.</returns>
    internal static MobilePromptBody? Parse(string json)
    {
        try { return From(JsonDocument.Parse(json)); }
        catch (JsonException) { return null; }
    }

    /// <summary>Lets go of the attachments, once they were written for alta: what stays is the fields.</summary>
    internal void ReleaseAttachments()
    {
        Attachments = null;
        Interlocked.Exchange(ref _document, null)?.Dispose();
    }

    /// <inheritdoc />
    public void Dispose() => ReleaseAttachments();

    private static MobilePromptBody? From(JsonDocument document)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            return null;
        }

        var fields = new JsonObject();
        JsonElement? attachments = null;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.NameEquals("attachments")) attachments = property.Value;
            else fields[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }

        if (attachments is not null) return new MobilePromptBody(fields, document, attachments);
        document.Dispose();
        return new MobilePromptBody(fields, null, null);
    }
}
