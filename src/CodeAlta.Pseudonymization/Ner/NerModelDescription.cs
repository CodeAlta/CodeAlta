using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Pseudonymization.Ner;

/// <summary>How the text of a model is cut into tokens.</summary>
public sealed record NerTokenizerDescription
{
    /// <summary>The kind of a SentencePiece tokenizer (<c>.model</c>): CamemBERT, XLM-RoBERTa.</summary>
    public const string SentencePiece = "sentencepiece";

    /// <summary>The kind of a WordPiece tokenizer (<c>vocab.txt</c>): BERT.</summary>
    public const string WordPiece = "wordpiece";

    /// <summary>Gets the kind: <see cref="SentencePiece"/> or <see cref="WordPiece"/>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = SentencePiece;

    /// <summary>Gets the file of the tokenizer, in the folder of the model.</summary>
    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    /// <summary>Gets whether a WordPiece tokenizer puts the text in lower case first (an uncased model).</summary>
    [JsonPropertyName("lowerCase")]
    public bool LowerCase { get; set; }

    /// <summary>
    /// Gets what is added to the id of a SentencePiece piece to give the id of the model: a model trained with fairseq
    /// (CamemBERT) numbers its pieces after its special tokens.
    /// </summary>
    [JsonPropertyName("idOffset")]
    public int IdOffset { get; set; }

    /// <summary>Gets the id of the model for a piece SentencePiece does not know; null for the one of SentencePiece.</summary>
    [JsonPropertyName("unknownId")]
    public int? UnknownId { get; set; }

    /// <summary>Gets the id a sequence starts with (<c>&lt;s&gt;</c>, <c>[CLS]</c>); null for the one of the tokenizer.</summary>
    [JsonPropertyName("startId")]
    public int? StartId { get; set; }

    /// <summary>Gets the id a sequence ends with (<c>&lt;/s&gt;</c>, <c>[SEP]</c>); null for the one of the tokenizer.</summary>
    [JsonPropertyName("endId")]
    public int? EndId { get; set; }

    /// <summary>Gets the id the shorter sequences of a batch are padded with; null for the one of the tokenizer.</summary>
    [JsonPropertyName("padId")]
    public int? PadId { get; set; }
}

/// <summary>
/// A model that gives each token of a text a label (token classification), such as a NER model of the BERT family, and how
/// its labels become the kinds of the pseudonymizer. It is the file <c>ner-model.json</c> in the folder of the model.
/// </summary>
public sealed record NerModelDescription
{
    /// <summary>The name of the file that describes a model, in its folder.</summary>
    public const string FileName = "ner-model.json";

    /// <summary>Gets the id of the model.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets the name shown.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Gets the file of the ONNX graph, in the folder of the model.</summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = "model.onnx";

    /// <summary>Gets how the text is cut into tokens.</summary>
    [JsonPropertyName("tokenizer")]
    public NerTokenizerDescription Tokenizer { get; set; } = new();

    /// <summary>Gets the most tokens of one sequence, the start and end tokens included.</summary>
    [JsonPropertyName("maxTokens")]
    public int MaxTokens { get; set; } = 512;

    /// <summary>
    /// Gets the labels of the model by their index (<c>O</c>, <c>B-PER</c>, <c>I-LOC</c>...); null reads them from the
    /// <c>id2label</c> of its <c>config.json</c>.
    /// </summary>
    [JsonPropertyName("labels")]
    public IReadOnlyList<string>? Labels { get; set; }

    /// <summary>
    /// Gets the kind of the pseudonymizer each entity of the model is (<c>PER</c> to <c>PERSON</c>); an entity it does not
    /// name, or names with null, is ignored.
    /// </summary>
    [JsonPropertyName("entities")]
    public IReadOnlyDictionary<string, string?> Entities { get; set; } = new Dictionary<string, string?>
    {
        ["PER"] = PiiEntities.Person,
        ["LOC"] = PiiEntities.Location,
        ["ORG"] = PiiEntities.Organization,
    };

    /// <summary>Reads the description of the model of a folder, and its labels.</summary>
    /// <param name="directory">The folder of the model.</param>
    /// <returns>The description, with its labels.</returns>
    /// <exception cref="InvalidDataException">The folder has no description, or it does not read.</exception>
    public static NerModelDescription Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var path = Path.Combine(directory, FileName);
        NerModelDescription description;
        try
        {
            description = JsonSerializer.Deserialize(File.ReadAllBytes(path), NerJsonContext.Default.NerModelDescription)
                ?? throw new InvalidDataException($"{path} is empty.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException($"The model description {path} cannot be read: {exception.Message}", exception);
        }

        var labels = description.Labels ?? ReadLabels(Path.Combine(directory, "config.json"));
        if (labels.Count == 0) throw new InvalidDataException($"The model of {directory} names no label.");
        if (description.Tokenizer.Kind is not (NerTokenizerDescription.SentencePiece or NerTokenizerDescription.WordPiece))
            throw new InvalidDataException($"The tokenizer '{description.Tokenizer.Kind}' of {directory} is not supported: sentencepiece or wordpiece.");
        if (description.MaxTokens is < 16 or > 4096) throw new InvalidDataException($"The model of {directory} takes {description.MaxTokens} tokens: between 16 and 4096.");
        // The host and worker can read an older catalog or custom model without rewriting its definition.
        var entities = new Dictionary<string, string?>(description.Entities.Count, StringComparer.Ordinal);
        foreach (var (label, kind) in description.Entities)
        {
            var current = kind is null ? null : PiiEntities.CanonicalName(kind);
            if (current is not null && !PiiEntities.All.Contains(current)) throw new InvalidDataException($"'{kind}' is not a kind of personal data the pseudonymizer knows.");
            entities.Add(label, current);
        }

        return description with { Labels = labels, Entities = entities };
    }

    /// <summary>Writes the description in the folder of its model, as <see cref="FileName"/>.</summary>
    /// <param name="directory">The folder of the model.</param>
    /// <exception cref="IOException">The file cannot be written.</exception>
    public void Save(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        File.WriteAllBytes(Path.Combine(directory, FileName), JsonSerializer.SerializeToUtf8Bytes(this, NerJsonContext.Default.NerModelDescription));
    }

    // The labels by index, from the id2label of the configuration of a model of Hugging Face.
    private static IReadOnlyList<string> ReadLabels(string config)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(config));
            if (!document.RootElement.TryGetProperty("id2label", out var map) || map.ValueKind != JsonValueKind.Object) return [];
            var labels = new SortedDictionary<int, string>();
            foreach (var property in map.EnumerateObject())
            {
                if (int.TryParse(property.Name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index) && property.Value.GetString() is { } label)
                    labels[index] = label;
            }

            return labels.Count > 0 && labels.Keys.Last() == labels.Count - 1 ? [.. labels.Values] : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException($"The labels of the model cannot be read from {config}: {exception.Message}", exception);
        }
    }

    /// <summary>Gets the kind a label stands for, and whether it begins an entity (<c>B-</c>).</summary>
    /// <param name="label">The label, such as <c>B-PER</c>, <c>I-LOC</c> or <c>O</c>.</param>
    /// <returns>The kind, or null for a label of no kind the pseudonymizer hides.</returns>
    internal (string? Entity, bool Begins) Read(string label)
    {
        if (label is "O" or "") return (null, false);
        var begins = label.StartsWith("B-", StringComparison.Ordinal) || label.StartsWith("S-", StringComparison.Ordinal);
        var name = label.Length > 2 && label[1] == '-' ? label[2..] : label;
        return (Entities.TryGetValue(name, out var entity) ? entity : null, begins);
    }
}

[JsonSerializable(typeof(NerModelDescription))]
internal sealed partial class NerJsonContext : JsonSerializerContext;
