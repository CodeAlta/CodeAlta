using System.Globalization;
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Agent;
using CodeAlta.LiveTool;

namespace CodeAlta.Orchestration.Runtime;

// Deliberately not the general live-tool factory. No services, filesystem, target or command lookup.
internal static class OwnedSessionAskTool
{
    internal static AgentToolDefinition Create(AgentToolHandler handler)
    {
        using var schema = JsonDocument.Parse("""
            {"type":"object","properties":{"args":{"type":"array","items":{"type":"string"}},"stdin":{"type":"string"}},"required":["args","stdin"],"additionalProperties":false}
            """);
        return new(new("alta", "Only args [\"ask\",\"--stdin\"] are available. stdin is file-free JSON, e.g. {\"questions\":[{\"title\":\"Decision\",\"question\":\"How should I proceed?\",\"freeform\":{}}]}. Questions require title and question, plus choices [{\"title\":\"Choice\"}] or freeform {}. Optional description fields are plain text. At most 12 questions, 20 choices each, 8192 aggregate text characters. One ask per send. After alta.ask.queued, yield; do not poll or retry. No other alta commands or explicit targets are available.", schema.RootElement.Clone()), handler);
    }

    internal static AltaAskRequest ParseInvocation(AgentToolInvocation invocation)
    {
        if (invocation.ToolName != "alta") throw new ArgumentException("Only ask is available.");
        // Bound the complete DOM before GetString/property-name decoding allocates owned strings.
        // JsonElement.WriteTo asks this fixed writer for storage; oversized size hints fail, not grow.
        using (var writer = new Utf8JsonWriter(new BoundedBuffer())) { invocation.Arguments.WriteTo(writer); writer.Flush(); }
        var root = invocation.Arguments;
        Object(root, "args", "stdin");
        var args = root.GetProperty("args");
        if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() != 2
            || args[0].ValueKind != JsonValueKind.String || args[1].ValueKind != JsonValueKind.String
            || args[0].GetString() != "ask" || args[1].GetString() != "--stdin") throw new ArgumentException("Only ask --stdin is available.");
        return ParseRequest(root.GetProperty("stdin").GetString() ?? throw new ArgumentException("stdin is required."));
    }

    internal static AltaAskRequest ParseRequest(string json)
    {
        try
        {
            if (!Text(json, 65536)) throw new ArgumentException("Invalid raw ask input.");
            ValidateEscapes(json);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            Object(root, "questions");
            var questions = root.GetProperty("questions");
            Array(questions, 12, required: true);
            var result = new List<AltaAskQuestion>(questions.GetArrayLength());
            var budget = 8192;
            foreach (var question in questions.EnumerateArray())
            {
                Object(question, "title", "question", "description", "choices", "freeform");
                var title = Field(question, "title", 120, ref budget);
                var text = Field(question, "question", 4000, ref budget);
                var description = Field(question, "description", 4000, ref budget);
                var choices = new List<AltaAskChoice>();
                if (question.TryGetProperty("choices", out var values))
                {
                    Array(values, 20, required: false);
                    foreach (var choice in values.EnumerateArray())
                    {
                        Object(choice, "title", "description");
                        choices.Add(new() { Title = Field(choice, "title", 120, ref budget), Description = Field(choice, "description", 4000, ref budget) });
                    }
                }
                AltaAskFreeform? freeform = null;
                if (question.TryGetProperty("freeform", out var free))
                {
                    Object(free, "title", "placeholder");
                    freeform = new() { Title = Field(free, "title", 120, ref budget), Placeholder = Field(free, "placeholder", 500, ref budget) };
                }
                result.Add(new() { Title = title, Question = text, Description = description, Choices = choices.AsReadOnly(), Freeform = freeform });
            }
            // File has already been rejected, so normalization cannot enter its path resolution branch.
            return AltaAskValidator.ValidateAndNormalize(new() { Questions = result.AsReadOnly() });
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new ArgumentException("Invalid ask input.", nameof(json), ex); }
    }

    internal static IReadOnlyList<AltaAskAnswer> CaptureAnswers(AltaAskRequest request, IReadOnlyList<AltaAskAnswer> answers)
    {
        if (answers is null || answers.Count != request.Questions.Count || answers.Count > 12) throw new ArgumentException("Answer every original question.");
        var captured = new AltaAskAnswer[answers.Count];
        var seen = new HashSet<int>();
        var budget = 8192;
        for (var i = 0; i < answers.Count; i++)
        {
            var answer = answers[i];
            if (answer is null || answer.QuestionIndex < 0 || answer.QuestionIndex >= request.Questions.Count
                || !seen.Add(answer.QuestionIndex) || answer.SelectedChoiceIndexes is null || answer.SelectedChoiceIndexes.Count > 20)
                throw new ArgumentException("Invalid answer index or choices.");
            var question = request.Questions[answer.QuestionIndex];
            var choices = answer.SelectedChoiceIndexes.ToArray();
            if (choices.Distinct().Count() != choices.Length || choices.Any(index => index < 0 || index >= question.Choices.Count))
                throw new ArgumentException("Invalid choice.");
            if (answer.FreeformText is { } text)
            {
                if (question.Freeform is null || !Text(text, 8192) || (budget -= text.Length) < 0) throw new ArgumentException("Invalid freeform answer.");
            }
            if (choices.Length == 0 && string.IsNullOrWhiteSpace(answer.FreeformText)) throw new ArgumentException("An answer is required.");
            captured[i] = answer with { SelectedChoiceIndexes = System.Array.AsReadOnly(choices) };
        }
        return System.Array.AsReadOnly(captured);
    }

    internal static bool Handle(OwnedAskHandle? value) => value is not null && value.OperationId != Guid.Empty && value.RuntimeInstanceId != Guid.Empty
        && value.AttachmentGeneration is > 0 and <= 9007199254740991 && value.ResponseGeneration is >= 0 and <= 256
        && Identity(value.ProviderId) && Identity(value.SessionId) && Identity(value.RunId)
        && Guid.TryParseExact(value.AskId, "D", out var ask) && ask != Guid.Empty && ask.ToString("D") == value.AskId;

    internal static bool Identity(string? value) => value is not null && !string.IsNullOrWhiteSpace(value)
        && value == value.Trim() && Text(value, 256) && !value.Any(char.IsControl);

    internal static bool Text(string? value, int maximum)
    {
        if (value is null || value.Length > maximum) return false;
        for (var i = 0; i < value.Length; i++)
            if (value[i] == '\0' || (char.IsSurrogate(value[i]) && (!char.IsHighSurrogate(value[i]) || ++i == value.Length || !char.IsLowSurrogate(value[i])))) return false;
        return true;
    }

    private static void Object(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Object required.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) throw new ArgumentException("Unknown or duplicate field.");
    }

    private static void Array(JsonElement value, int maximum, bool required)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maximum || (required && value.GetArrayLength() == 0)) throw new ArgumentException("Invalid array length.");
    }

    private static string? Field(JsonElement parent, string name, int maximum, ref int budget)
    {
        if (!parent.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null) return null;
        var text = field.GetString();
        if (!Text(text, maximum) || (budget -= text!.Length) < 0) throw new ArgumentException("Ask text exceeds its budget.");
        return text;
    }

    // Reject escaped lone surrogates before GetString can decode them. Escaped backslashes are skipped.
    private static void ValidateEscapes(string json)
    {
        for (var i = 0; i < json.Length; i++)
        {
            if (json[i] != '\\' || ++i >= json.Length || json[i] != 'u') continue;
            if (i + 4 >= json.Length || !ushort.TryParse(json.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)) throw new ArgumentException("Invalid escape.");
            i += 4;
            if (char.IsLowSurrogate((char)code)) throw new ArgumentException("Unpaired surrogate.");
            if (!char.IsHighSurrogate((char)code)) continue;
            if (i + 6 >= json.Length || json[i + 1] != '\\' || json[i + 2] != 'u'
                || !ushort.TryParse(json.AsSpan(i + 3, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var low)
                || !char.IsLowSurrogate((char)low)) throw new ArgumentException("Unpaired surrogate.");
            i += 6;
        }
    }

    private sealed class BoundedBuffer : IBufferWriter<byte>
    {
        private readonly byte[] _bytes = new byte[128 * 1024];
        private int _written;
        public void Advance(int count)
        {
            if (count < 0 || count > _bytes.Length - _written) throw new ArgumentException("Producer input is too large.");
            _written += count;
        }
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (sizeHint < 0 || sizeHint > _bytes.Length - _written || _written == _bytes.Length) throw new ArgumentException("Producer input is too large.");
            return _bytes.AsMemory(_written);
        }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OwnedAskToolReply))]
internal sealed partial class OwnedAskJsonContext : JsonSerializerContext;
internal sealed record OwnedAskToolReply(string Type, string AskId, string SessionId, bool ShouldPoll, bool ShouldYield);
