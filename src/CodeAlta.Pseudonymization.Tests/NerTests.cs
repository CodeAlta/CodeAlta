using CodeAlta.Pseudonymization.Ner;

namespace CodeAlta.Pseudonymization.Tests;

[TestClass]
public sealed class NerTests
{
    // A folder with camembert/sentencepiece.bpe.model and bert/vocab.txt, the tokenizers of Jean-Baptiste/camembert-ner and
    // of bert-base-NER: the tests of the real tokenizers run when it is given, and are skipped otherwise.
    private const string TokenizersVariable = "CODEALTA_TEST_NER_TOKENIZERS";

    [TestMethod]
    public void TheTokenizerOfCamembert_GivesTheIdsOfTheReferenceTokenizer()
    {
        var folder = TokenizerFolder("camembert");
        var tokenizer = NerTokenizer.Load(folder, new NerTokenizerDescription
        {
            Kind = NerTokenizerDescription.SentencePiece, File = "sentencepiece.bpe.model", IdOffset = 4, UnknownId = 3, StartId = 5, EndId = 6, PadId = 1,
        });
        const string text = "Maître Durand reçoit Mme Sophie Martin, née à Saint-Étienne, au 14 rue de la Paix.";

        var tokens = tokenizer.Tokenize(text);

        // The ids of the tokenizer of camembert-base (tokenizers 0.x, tokenizer.json), start and end included.
        CollectionAssert.AreEqual(new[] { 5, 4999, 28770, 3982, 2439, 7834, 2928, 7, 4076, 15, 377, 26, 14294, 7, 36, 476, 839, 8, 13, 16806, 9, 6 },
            (int[])[tokenizer.StartId, .. tokens.Select(static token => token.Id), tokenizer.EndId]);
        Assert.AreEqual("Martin", text[tokens[5].Start..tokens[5].End]);
        Assert.AreEqual("Étienne", text[tokens[11].Start..tokens[11].End]);
    }

    [TestMethod]
    public void TheTokenizerOfBert_GivesTheIdsOfTheReferenceTokenizer()
    {
        var folder = TokenizerFolder("bert");
        var tokenizer = NerTokenizer.Load(folder, new NerTokenizerDescription { Kind = NerTokenizerDescription.WordPiece, File = "vocab.txt" });
        const string text = "John Smith met Mary O'Neil at Microsoft in London.";

        var tokens = tokenizer.Tokenize(text);

        CollectionAssert.AreEqual(new[] { 101, 1287, 2159, 1899, 2090, 152, 112, 6003, 1120, 6998, 1107, 1498, 119, 102 },
            (int[])[tokenizer.StartId, .. tokens.Select(static token => token.Id), tokenizer.EndId]);
        Assert.AreEqual("Neil", text[tokens[6].Start..tokens[6].End]);
    }

    [TestMethod]
    public async Task TheLabelsOfTheModel_BecomeTheResultsOfTheirKind()
    {
        using var folder = new TempVocabulary("[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]", "John", "Smith", "met", "Mary", "in", "London", ".", "##s");
        var description = new NerModelDescription
        {
            Id = "fake",
            Labels = ["O", "B-PER", "I-PER", "B-LOC", "I-LOC", "B-MISC"],
            Tokenizer = new NerTokenizerDescription { Kind = NerTokenizerDescription.WordPiece, File = "vocab.txt" },
        };
        var tokenizer = NerTokenizer.Load(folder.Path, description.Tokenizer);
        // John Smith met Mary in London . : two people (a B- ends the first), one place, the rest outside.
        var classifier = new ScriptedClassifier([0, 1, 2, 0, 1, 0, 3, 0, 0]);
        var recognizer = new TokenClassificationRecognizer(description, tokenizer, classifier);
        const string text = "John Smith met Mary in London.";

        var spans = await recognizer.AnalyzeAsync(text, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "PERSONNE:John Smith", "PERSONNE:Mary", "LIEU:London" },
            spans.Select(span => $"{span.Entity}:{text.Substring(span.Start, span.Length)}").ToArray());
        Assert.AreEqual(0.9, spans[0].Score, 1e-6);
    }

    [TestMethod]
    public async Task AResult_EndsAtTheEndOfALine()
    {
        using var folder = new TempVocabulary("[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]", "CEDEX", "DURAND", "PIERRE", "MARTIN", "SOPHIE");
        var description = new NerModelDescription
        {
            Id = "fake",
            Labels = ["O", "B-PER", "I-PER", "B-LOC", "I-LOC"],
            Tokenizer = new NerTokenizerDescription { Kind = NerTokenizerDescription.WordPiece, File = "vocab.txt" },
        };
        var tokenizer = NerTokenizer.Load(folder.Path, description.Tokenizer);
        // The address block of a letter: the model runs the name of the person of one line into the name of the next one.
        var classifier = new ScriptedClassifier([0, 3, 2, 2, 2, 2, 0]);
        var recognizer = new TokenClassificationRecognizer(description, tokenizer, classifier);
        const string text = "CEDEX\nDURAND PIERRE\r\nMARTIN SOPHIE";

        var spans = await recognizer.AnalyzeAsync(text, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "LIEU:CEDEX", "PERSONNE:DURAND PIERRE", "PERSONNE:MARTIN SOPHIE" },
            spans.Select(span => $"{span.Entity}:{text.Substring(span.Start, span.Length)}").ToArray());
    }

    [TestMethod]
    public async Task AWord_HasTheKindOfItsFirstToken()
    {
        using var folder = new TempVocabulary("[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]", "Madame", "DUP", "##ONT", "33", "##000", "BORDEAUX");
        var description = new NerModelDescription
        {
            Id = "fake",
            Labels = ["O", "B-PER", "I-PER", "B-LOC", "I-LOC"],
            Tokenizer = new NerTokenizerDescription { Kind = NerTokenizerDescription.WordPiece, File = "vocab.txt" },
        };
        var tokenizer = NerTokenizer.Load(folder.Path, description.Tokenizer);
        // The model gives the parts of one word labels of their own: a place and a person in one name, a postal code half outside.
        var classifier = new ScriptedClassifier([0, 0, 1, 4, 3, 0, 3, 0]);
        var recognizer = new TokenClassificationRecognizer(description, tokenizer, classifier);
        const string text = "Madame DUPONT 33000 BORDEAUX";

        var spans = await recognizer.AnalyzeAsync(text, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "PERSONNE:DUPONT", "LIEU:33000", "LIEU:BORDEAUX" },
            spans.Select(span => $"{span.Entity}:{text.Substring(span.Start, span.Length)}").ToArray());
    }

    [TestMethod]
    public void ADescription_KeepsTheDefaultsOfWhatItDoesNotSay()
    {
        using var folder = new TempVocabulary("[PAD]");
        File.WriteAllText(System.IO.Path.Combine(folder.Path, NerModelDescription.FileName),
            """{"id":"x","labels":["O","B-PER"],"tokenizer":{"kind":"wordpiece","file":"vocab.txt"}}""");

        var description = NerModelDescription.Load(folder.Path);

        Assert.AreEqual(512, description.MaxTokens);
        Assert.AreEqual("model.onnx", description.Model);
        Assert.AreEqual(PiiEntities.Person, description.Entities["PER"]);
    }

    [TestMethod]
    public void ADescription_WithAnUnknownKind_IsRefused()
    {
        using var folder = new TempVocabulary("[PAD]");
        File.WriteAllText(System.IO.Path.Combine(folder.Path, NerModelDescription.FileName),
            """{"id":"x","labels":["O","B-PER"],"tokenizer":{"kind":"wordpiece","file":"vocab.txt"},"entities":{"PER":"PEOPLE"}}""");

        Assert.ThrowsExactly<InvalidDataException>(() => NerModelDescription.Load(folder.Path));
    }

    private static string TokenizerFolder(string name)
    {
        var root = Environment.GetEnvironmentVariable(TokenizersVariable);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(System.IO.Path.Combine(root, name)))
            Assert.Inconclusive($"Set {TokenizersVariable} to a folder with the tokenizers of the models to run this test.");
        return System.IO.Path.Combine(root!, name);
    }

    // Labels every token of each sequence from a script, with a probability of 0.9.
    private sealed class ScriptedClassifier(int[] labels) : ITokenClassifier
    {
        public ValueTask<IReadOnlyList<TokenLabel[]>> ClassifyAsync(IReadOnlyList<int[]> sequences, CancellationToken cancellationToken)
        {
            Assert.AreEqual(labels.Length, sequences[0].Length, "The script has a label for each token, start and end included.");
            return ValueTask.FromResult<IReadOnlyList<TokenLabel[]>>([.. sequences.Select(_ => labels.Select(static label => new TokenLabel(label, 0.9f)).ToArray())]);
        }
    }

    private sealed class TempVocabulary : IDisposable
    {
        public TempVocabulary(params string[] tokens)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codealta-ner-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            File.WriteAllLines(System.IO.Path.Combine(Path, "vocab.txt"), tokens);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
