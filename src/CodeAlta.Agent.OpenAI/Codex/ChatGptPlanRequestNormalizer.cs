using System.Text.Json;

namespace CodeAlta.Agent.OpenAI.Codex;

internal static class ChatGptPlanRequestNormalizer
{
    public static BinaryData Normalize(BinaryData payload, bool isHttp)
    {
        using var document = JsonDocument.Parse(payload.ToMemory());
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                // Public ChatGPT plan usage has a narrower contract than platform API keys
                // and the former private Codex endpoint, including for extra_body overrides.
                if (property.Name is "background" or "conversation" or "max_output_tokens" or "max_tool_calls" or
                    "metadata" or "moderation" or "multi_agent" or "prompt" or "prompt_cache_retention" or
                    "safety_identifier" or "temperature" or "top_logprobs" or "top_p" or "truncation" or "user" or
                    "store" or "stream" or "client_metadata" or "stream_options" ||
                    (isHttp && property.Name == "previous_response_id"))
                {
                    continue;
                }

                writer.WritePropertyName(property.Name);
                if (property.Name == "tools" && property.Value.GetArrayLength() > 0)
                {
                    writer.WriteStartArray();
                    writer.WriteStartObject();
                    writer.WriteString("type", "namespace");
                    writer.WriteString("name", "codealta");
                    writer.WriteString("description", "CodeAlta local tools");
                    writer.WritePropertyName("tools");
                    writer.WriteStartArray();
                    foreach (var tool in property.Value.EnumerateArray())
                    {
                        if (tool.GetProperty("type").GetString() is not ("function" or "custom"))
                        {
                            throw new InvalidOperationException("This hosted tool is not supported by ChatGPT plan usage.");
                        }

                        tool.WriteTo(writer);
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    writer.WriteEndArray();
                }
                else if (property.Name == "input")
                {
                    writer.WriteStartArray();
                    foreach (var item in property.Value.EnumerateArray())
                    {
                        writer.WriteStartObject();
                        foreach (var field in item.EnumerateObject())
                        {
                            if (field.Name == "role" && field.Value.GetString() == "system")
                            {
                                writer.WriteString("role", "developer");
                            }
                            else
                            {
                                field.WriteTo(writer);
                            }
                        }

                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }
                else
                {
                    property.Value.WriteTo(writer);
                }
            }

            writer.WriteBoolean("store", false);
            if (isHttp)
            {
                writer.WriteBoolean("stream", true);
            }

            writer.WriteEndObject();
        }

        return BinaryData.FromBytes(output.ToArray());
    }
}
