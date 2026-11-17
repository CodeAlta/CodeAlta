using CodeAlta.NativeEngines;
using CodeAlta.Pseudonymization;
using CodeAlta.Pseudonymization.Ner;

namespace CodeAlta.Plugin.Pseudonymizer;

/// <summary>One file of a model, pinned by its repository, its commit and its hash.</summary>
/// <param name="Name">Its name in the folder of the model.</param>
/// <param name="Repository">The Hugging Face repository, <c>owner/name</c>.</param>
/// <param name="Commit">The commit it is read at.</param>
/// <param name="Path">Its path in the repository.</param>
/// <param name="Size">Its exact size in bytes.</param>
/// <param name="Sha256">The SHA-256 of its content for a file of Git LFS, or null.</param>
/// <param name="GitSha1">The Git blob hash of a small file, or null.</param>
internal sealed record NerModelFile(string Name, string Repository, string Commit, string Path, long Size, string? Sha256, string? GitSha1 = null)
{
    /// <summary>Gets the address it is downloaded from.</summary>
    public Uri Address => new($"https://huggingface.co/{Repository}/resolve/{Commit}/{string.Join('/', Path.Split('/').Select(Uri.EscapeDataString))}");

    /// <summary>Gets the file as the downloader checks it.</summary>
    public PinnedFile Pinned => new(Name, Size, Sha256, GitSha1);
}

/// <summary>A model that finds names, which the plugin can download.</summary>
/// <param name="Description">How it is run, its id and its name: written beside it as <see cref="NerModelDescription.FileName"/>.</param>
/// <param name="Files">The files to download.</param>
internal sealed record NerModelPreset(NerModelDescription Description, IReadOnlyList<NerModelFile> Files)
{
    // The catalog keeps its models for the life of the application, and the properties of a description can be set (it is
    // read from a file): the preset keeps a description of its own, and gives a copy of it.
    private readonly NerModelDescription _description = Copy(Description);

    /// <summary>Gets how it is run, its id and its name, as a copy: changing it changes nothing of the model.</summary>
    public NerModelDescription Description => Copy(_description);

    /// <summary>Gets its id: the choice of the setting and the start of the name of its folder.</summary>
    public string Id => _description.Id;

    /// <summary>Gets the name shown.</summary>
    public string Name => _description.Name ?? _description.Id;

    /// <summary>Gets the size of the download.</summary>
    public long Size => Files.Sum(static file => file.Size);

    /// <summary>Gets the name of its folder: its id and the start of the commit of its graph.</summary>
    public string FolderName => $"{Id}-{Files.First(file => file.Name == _description.Model).Commit[..12]}";

    private static NerModelDescription Copy(NerModelDescription description) => description with { Tokenizer = description.Tokenizer with { } };
}

/// <summary>
/// The models that find names the plugin can download, pinned by commit and hash. Each is a token classification model
/// that <see cref="TokenClassificationRecognizer"/> runs; any other one is given as a folder (the choice <c>custom</c>).
/// </summary>
internal static class NerModelCatalog
{
    /// <summary>The id of the French model, the default.</summary>
    public const string CamembertId = "camembert";

    /// <summary>The id of the English model.</summary>
    public const string EnglishId = "bert-en";

    /// <summary>The id of the multilingual model.</summary>
    public const string MultilingualId = "bert-multilingual";

    /// <summary>The id of a model of the user, given by its folder.</summary>
    public const string CustomId = "custom";

    /// <summary>Gets the models, the default first.</summary>
    public static IReadOnlyList<NerModelPreset> Presets { get; } =
    [
        // French, MIT.
        new(new NerModelDescription
            {
                Id = CamembertId,
                Name = "CamemBERT-NER",
                Model = "model.onnx",
                // CamemBERT was trained with fairseq: its ids are those of SentencePiece plus 4, after <s>NOTUSED, <pad>,
                // </s>NOTUSED and <unk>; <s> is 5 and </s> 6 (the tokenizer of camembert-base).
                Tokenizer = new NerTokenizerDescription
                {
                    Kind = NerTokenizerDescription.SentencePiece, File = "sentencepiece.bpe.model", IdOffset = 4, UnknownId = 3, StartId = 5, EndId = 6, PadId = 1,
                },
            },
            Files("Jean-Baptiste/camembert-ner", "ef35fe7767c1dad71f5c853838cdd80d0b3441ed",
                ("model.onnx", "model.onnx", 440422178, "c9dfa2ec9e0bfad82d606782ba3e0ab94c001c46481514e0658fc20a624845c5", null),
                ("sentencepiece.bpe.model", "sentencepiece.bpe.model", 810912, "988bc5a00281c6d210a5d34bd143d0363741a432fefe741bf71e61b1869d4314", null),
                ("config.json", "config.json", 892, null, "1830e8fcc7d7b3c91a9fe97256ab15a712e844fc"))),
        // English, MIT.
        new(new NerModelDescription
            {
                Id = EnglishId,
                Name = "BERT-base-NER",
                Model = "model_quantized.onnx",
                Tokenizer = new NerTokenizerDescription { Kind = NerTokenizerDescription.WordPiece, File = "vocab.txt" },
            },
            Files("Xenova/bert-base-NER", "8e892123e8b7c2c0c2bd1dcb598b7d244c4e53aa",
                ("model_quantized.onnx", "onnx/model_quantized.onnx", 108952255, "caaee70a5518ec7f9e46e5308fcc9263a8c227703a9ce46cf61c69a552349648", null),
                ("vocab.txt", "vocab.txt", 213450, null, "2ea941cc79a6f3d7985ca6991ef4f67dad62af04"),
                ("config.json", "config.json", 999, null, "3468d1d58f51f87b6e58025a0bfbc513afbc8e4b"))),
        // Ten languages, French and English among them, AFL-3.0.
        new(new NerModelDescription
            {
                Id = MultilingualId,
                Name = "BERT multilingual NER (HRL)",
                Model = "model.onnx",
                Tokenizer = new NerTokenizerDescription { Kind = NerTokenizerDescription.WordPiece, File = "vocab.txt" },
            },
            Files("Davlan/bert-base-multilingual-cased-ner-hrl", "e756de7f7b8f64fea0c3d7c3872f1322fab747b1",
                ("model.onnx", "onnx/model.onnx", 709345293, "6c018415dc8129b358e9d629543c17481ad067ad02f9a6b8750473f161f9c5bd", null),
                ("vocab.txt", "onnx/vocab.txt", 995526, null, "e837bab60a5d204e29622d127c2dafe508aa0731"),
                ("config.json", "config.json", 1105, null, "1e9c0c7e90f85ad93afa48beecee55e22430d009"))),
    ];

    /// <summary>Gets a model by its id, or null.</summary>
    public static NerModelPreset? Find(string? id)
        => Presets.FirstOrDefault(preset => string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase));

    // The files of a model, all read from one repository at one commit.
    private static NerModelFile[] Files(string repository, string commit, params (string Name, string Path, long Size, string? Sha256, string? GitSha1)[] files)
        => [.. files.Select(file => new NerModelFile(file.Name, repository, commit, file.Path, file.Size, file.Sha256, file.GitSha1))];
}
