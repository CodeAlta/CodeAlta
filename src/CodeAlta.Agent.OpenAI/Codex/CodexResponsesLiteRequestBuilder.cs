#pragma warning disable OPENAI001
#pragma warning disable SCME0001

using System.ClientModel.Primitives;
using System.Text.Json;
using OpenAI;
using OpenAI.Responses;

namespace CodeAlta.Agent.OpenAI.Codex;

internal static class CodexResponsesLiteRequestBuilder
{
    public static void Apply(CreateResponseOptions options, string? developerInstructions)
    {
        ArgumentNullException.ThrowIfNull(options);

        var tools = options.Tools.Select(SerializeModel).ToArray();
        var inputItems = options.InputItems.Select(SerializeModel).ToArray();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("type", "additional_tools");
            writer.WriteString("role", "developer");
            writer.WritePropertyName("tools");
            writer.WriteStartArray();
            foreach (var tool in tools)
            {
                tool.RootElement.WriteTo(writer);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();

            if (!string.IsNullOrEmpty(developerInstructions))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "message");
                writer.WriteString("role", "developer");
                writer.WritePropertyName("content");
                writer.WriteStartArray();
                writer.WriteStartObject();
                writer.WriteString("type", "input_text");
                writer.WriteString("text", developerInstructions);
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            foreach (var inputItem in inputItems)
            {
                WriteWithoutImageDetail(writer, inputItem.RootElement);
            }

            writer.WriteEndArray();
        }

        foreach (var tool in tools)
        {
            tool.Dispose();
        }

        foreach (var inputItem in inputItems)
        {
            inputItem.Dispose();
        }

        options.Patch.Set("$.input"u8, BinaryData.FromBytes(stream.ToArray()));
        if (options.ReasoningOptions is not null)
        {
            options.ReasoningOptions.Patch.Set("$.context"u8, "all_turns");
        }
        options.Instructions = null;
        options.InputItems.Clear();
        options.Tools.Clear();
        options.Patch.Remove("$.tools"u8);
        options.Patch.Remove("$.instructions"u8);
        options.ParallelToolCallsEnabled = false;
    }

    private static JsonDocument SerializeModel<T>(T model)
        where T : IPersistableModel<T>
        => JsonDocument.Parse(ModelReaderWriter.Write(model, new ModelReaderWriterOptions("J"), OpenAIContext.Default));

    private static void WriteWithoutImageDetail(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var isInputImage = element.TryGetProperty("type"u8, out var type) &&
                    type.ValueKind == JsonValueKind.String &&
                    string.Equals(type.GetString(), "input_image", StringComparison.Ordinal);
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    if (isInputImage && property.NameEquals("detail"u8))
                    {
                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    WriteWithoutImageDetail(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteWithoutImageDetail(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
