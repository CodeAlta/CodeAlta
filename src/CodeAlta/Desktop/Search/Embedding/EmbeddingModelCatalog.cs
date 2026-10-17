namespace CodeAlta.Desktop.Search.Embedding;

/// <summary>How the text of a model is cut into tokens.</summary>
internal enum EmbeddingTokenizerKind
{
    /// <summary>SentencePiece (<c>tokenizer.model</c>), with the start and the end of a sentence: EmbeddingGemma.</summary>
    SentencePiece,

    /// <summary>A byte-level BPE (<c>tokenizer.json</c>) with the pre-tokenizer of its file, between the start and end tokens of the model: Granite.</summary>
    ByteLevelBpe,
}

/// <summary>What the graph of a model gives, and how a vector is made of it.</summary>
internal enum EmbeddingPooling
{
    /// <summary>A <c>sentence_embedding</c> output, already pooled and normalized.</summary>
    SentenceEmbedding,

    /// <summary>A <c>last_hidden_state</c> output: its first token, normalized.</summary>
    FirstToken,
}

/// <summary>One file of a model, pinned by its repository, its commit and its hash.</summary>
/// <param name="Name">Its name in the folder of the model.</param>
/// <param name="Repository">The Hugging Face repository, <c>owner/name</c>.</param>
/// <param name="Commit">The commit it is read at.</param>
/// <param name="Path">Its path in the repository.</param>
/// <param name="Size">Its exact size in bytes.</param>
/// <param name="Sha256">The SHA-256 of its content, lower-case hexadecimal.</param>
internal sealed record EmbeddingFile(string Name, string Repository, string Commit, string Path, long Size, string Sha256)
{
    /// <summary>Gets the address it is downloaded from.</summary>
    public Uri Address => new($"https://huggingface.co/{Repository}/resolve/{Commit}/{string.Join('/', Path.Split('/').Select(Uri.EscapeDataString))}");
}

/// <summary>A model that turns texts into vectors, and how it is asked.</summary>
/// <param name="Id">The name of the preset in the configuration (<c>[search] model</c>) and of its folder.</param>
/// <param name="Name">The name shown.</param>
/// <param name="Author">Who made it.</param>
/// <param name="Summary">What is said of it in the settings: its languages and what it is good at.</param>
/// <param name="License">Its license.</param>
/// <param name="Dimensions">The width of its vectors.</param>
/// <param name="ModelFile">The file of its graph.</param>
/// <param name="TokenizerFile">The file of its tokenizer.</param>
/// <param name="Tokenizer">How its tokenizer is read.</param>
/// <param name="Pooling">How a vector is made of its output.</param>
/// <param name="QueryPrompt">The text a query is put in: <c>{query}</c> is replaced.</param>
/// <param name="CodeQueryPrompt">The text a query for tools and skills is put in.</param>
/// <param name="DocumentPrompt">The text a document is put in: <c>{title}</c> and <c>{text}</c> are replaced.</param>
/// <param name="PromptsVersion">The version of the prompts: vectors made with other prompts are made again.</param>
/// <param name="Files">The files to download.</param>
internal sealed record EmbeddingModelDefinition(string Id, string Name, string Author, string Summary, string License, int Dimensions,
    string ModelFile, string TokenizerFile, EmbeddingTokenizerKind Tokenizer, EmbeddingPooling Pooling,
    string QueryPrompt, string CodeQueryPrompt, string DocumentPrompt, string PromptsVersion, IReadOnlyList<EmbeddingFile> Files)
{
    /// <summary>The most tokens of a text: what is beyond is not embedded.</summary>
    public const int MaximumTokens = 512;

    /// <summary>Gets the token a text starts with, for a tokenizer that does not add it; -1 for none.</summary>
    public int StartToken { get; init; } = -1;

    /// <summary>Gets the token a text ends with, for a tokenizer that does not add it; -1 for none.</summary>
    public int EndToken { get; init; } = -1;

    /// <summary>Gets the token the shorter texts of a batch are padded with.</summary>
    public int PadToken { get; init; }

    /// <summary>Gets the size of the download.</summary>
    public long Size => Files.Sum(static file => file.Size);

    /// <summary>Gets the revision of the model: the commit of its graph.</summary>
    public string Revision => Files.First(file => file.Name == ModelFile).Commit;

    /// <summary>Gets the name of the folder of the model in the cache: its id and the start of its revision.</summary>
    public string FolderName => $"{Id}-{Revision[..12]}";

    /// <summary>Gets the text a query is embedded as.</summary>
    public string Query(string query, bool code) => (code ? CodeQueryPrompt : QueryPrompt).Replace("{query}", query, StringComparison.Ordinal);

    /// <summary>Gets the text a document is embedded as.</summary>
    public string Document(string? title, string text)
        => DocumentPrompt.Replace("{title}", string.IsNullOrWhiteSpace(title) ? "none" : title.ReplaceLineEndings(" "), StringComparison.Ordinal).Replace("{text}", text, StringComparison.Ordinal);
}

/// <summary>
/// The models search by meaning can download, pinned by commit and hash. Both run on the ONNX Runtime that sherpa-onnx
/// ships, which speech shares.
/// </summary>
internal static class EmbeddingModelCatalog
{
    /// <summary>The id of the default preset.</summary>
    public const string DefaultId = "embeddinggemma-2";

    /// <summary>Gets the presets, the default first.</summary>
    public static IReadOnlyList<EmbeddingModelDefinition> Presets { get; } =
    [
        new(DefaultId, "EmbeddingGemma 2", "Google", "100+ languages, strong on code", "Apache-2.0", 768,
            "model_quantized.onnx", "tokenizer.model", EmbeddingTokenizerKind.SentencePiece, EmbeddingPooling.SentenceEmbedding,
            "task: search result | query: {query}", "task: code retrieval | query: {query}", "title: {title} | text: {text}", "1",
            [
                new("model_quantized.onnx", "onnx-community/embeddinggemma-2-ONNX", "daa72c51243991dfcaf9f9137d2c573d8f7790c0", "onnx/model_quantized.onnx",
                    495165, "d06edd601f851c633a2519304cbeb8dc6170d7ceb61b436625c17fb9b6e74953"),
                new("model_quantized.onnx_data", "onnx-community/embeddinggemma-2-ONNX", "daa72c51243991dfcaf9f9137d2c573d8f7790c0", "onnx/model_quantized.onnx_data",
                    313724928, "278a7ff1248c3618e4bd11a607fc54f7bdc7778854230f3956d3f86bd9db4f3b"),
                new("tokenizer.model", "google/embeddinggemma-2", "914f7f89142e33e77833254d9c9b90c3cef7303b", "tokenizer.model",
                    4689013, "e594c8a90eb08d8bda498ff4747977dc827ae0c3c56b5c0d41a605a22d02ef03"),
            ]),
        new("granite-97m-multilingual-r2", "Granite 97M multilingual", "IBM", "200+ languages, light and fast", "Apache-2.0", 384,
            "model_int8.onnx", "tokenizer.json", EmbeddingTokenizerKind.ByteLevelBpe, EmbeddingPooling.FirstToken,
            "{query}", "{query}", "{text}", "1",
            [
                new("model_int8.onnx", "onnx-community/granite-embedding-97m-multilingual-r2-ONNX", "536a9f241cb3f02a9c5995a1e708c784bd274859", "onnx/model_int8.onnx",
                    97858099, "704c1ebca5fbb7cd83ced41827658ac4c9990c64f7f2874d22b78044e5022e22"),
                new("tokenizer.json", "onnx-community/granite-embedding-97m-multilingual-r2-ONNX", "536a9f241cb3f02a9c5995a1e708c784bd274859", "tokenizer.json",
                    25301671, "51947676cae1f991fa51c6b9a24e14ee5460e5f0b9f692f13bb3159829d1592a"),
            ]) { StartToken = 179934, EndToken = 179938, PadToken = 179935 },
    ];

    /// <summary>Gets a preset by its id, or null.</summary>
    public static EmbeddingModelDefinition? Find(string? id)
        => Presets.FirstOrDefault(preset => string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase));
}
