using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// Shapes a persisted user message or tool result that has images for a history row: the row lists the images
/// by index, title and media type, and neither its text nor its details name the image files.
/// </summary>
internal static class HistoryImageProjection
{
    /// <summary>Most images listed for one message; the text and the details still name no file of the others.</summary>
    internal const int MaximumImages = 64;

    /// <summary>Longest image title sent, in UTF-16 units.</summary>
    internal const int MaximumTitleLength = 256;

    private const int MaximumMediaTypeLength = 64;

    /// <summary>Projects the images of a user message or of a tool result.</summary>
    /// <param name="message">A completed content event.</param>
    /// <param name="text">The row's text; the lines of a user message that name the image files are removed.</param>
    /// <param name="details">The row's details; the paths of the images are removed.</param>
    /// <param name="cost">The worst-case JSON size of the returned images, in bytes.</param>
    /// <returns>The images in index order, or null when the message is not a user message or a tool result with images.</returns>
    internal static HistoryImage[]? Project(AgentContentCompletedEvent message, ref string? text, ref string? details, out int cost)
    {
        cost = 0;
        if (message.Kind is not (AgentContentKind.User or AgentContentKind.ToolOutput)) return null;
        var recorded = PromptImageHistory.ReadImages(message.Details);
        if (recorded.Count == 0) return null;
        if (text is not null && message.Kind == AgentContentKind.User) text = PromptImageHistory.RemoveImageLines(text, recorded);
        details = WithoutPaths(message.Details!.Value);
        var images = new HistoryImage[Math.Min(recorded.Count, MaximumImages)];
        for (var index = 0; index < images.Length; index++)
        {
            var image = new HistoryImage(index, Title(recorded[index].Title), MediaType(recorded[index].MediaType));
            images[index] = image;
            cost += 96 + 6 * (image.Title.Length + (image.MediaType?.Length ?? 0));
        }

        return images;
    }

    /// <summary>Removes the paths of the images a tool result saved from the details of a tool activity.</summary>
    /// <param name="details">The details of an activity event.</param>
    /// <returns>The details as JSON text, without the path of a saved image; null when there are none.</returns>
    internal static string? ActivityDetails(JsonElement? details)
    {
        if (details is not { } value) return null;
        var text = value.GetRawText();
        // Nearly every activity has no image: its details are sent as they were recorded.
        return value.ValueKind == JsonValueKind.Object && text.Contains("\"localImage\"", StringComparison.Ordinal) ? WithoutPaths(value) : text;
    }

    // The same details without the path of a local image item, of a legacy attachment or of a tool result's image.
    private static string WithoutPaths(JsonElement details)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in details.EnumerateObject())
            {
                var attachments = property.NameEquals("attachments");
                if (property.Value.ValueKind == JsonValueKind.Array && (attachments || property.NameEquals("items")))
                {
                    WriteItems(writer, property, attachments);
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.Object || !property.NameEquals("result"))
                {
                    property.WriteTo(writer);
                    continue;
                }

                writer.WriteStartObject(property.Name);
                foreach (var field in property.Value.EnumerateObject())
                {
                    if (field.Value.ValueKind == JsonValueKind.Array && field.NameEquals("items")) WriteItems(writer, field, all: false);
                    else field.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    // A list whose local images (every entry, for legacy attachments) lose their path.
    private static void WriteItems(Utf8JsonWriter writer, JsonProperty list, bool all)
    {
        writer.WriteStartArray(list.Name);
        foreach (var item in list.Value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !(all || PromptImageHistory.IsLocalImage(item)))
            {
                item.WriteTo(writer);
                continue;
            }

            writer.WriteStartObject();
            foreach (var field in item.EnumerateObject())
                if (!field.NameEquals("path")) field.WriteTo(writer);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    // A title is user metadata: control characters and unpaired surrogates are dropped, never sent.
    private static string Title(string value)
    {
        var text = new StringBuilder(Math.Min(value.Length, MaximumTitleLength));
        for (var index = 0; index < value.Length && text.Length < MaximumTitleLength; index++)
        {
            var character = value[index];
            if (char.IsControl(character)) continue;
            if (!char.IsSurrogate(character)) { text.Append(character); continue; }
            if (!char.IsHighSurrogate(character) || index + 1 == value.Length || !char.IsLowSurrogate(value[index + 1])) continue;
            if (text.Length + 2 > MaximumTitleLength) break;
            text.Append(character).Append(value[++index]);
        }

        var title = text.ToString().Trim();
        return title.Length == 0 ? "image" : title;
    }

    private static string? MediaType(string value)
        => value.Length is > 6 and <= MaximumMediaTypeLength && value.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            && !value.AsSpan(6).ContainsAnyExcept("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.+-")
            ? value.ToLowerInvariant() : null;
}

/// <summary>One image of a user message or of a tool result; the page asks for its content by this index.</summary>
internal sealed record HistoryImage(int Index, string Title, string? MediaType);
