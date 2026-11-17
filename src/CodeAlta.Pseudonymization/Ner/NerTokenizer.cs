using Microsoft.ML.Tokenizers;

namespace CodeAlta.Pseudonymization.Ner;

/// <summary>One token of a text: its id for the model and where it is in the text.</summary>
/// <param name="Id">The id of the model.</param>
/// <param name="Start">Where its characters start in the text.</param>
/// <param name="End">Where they end.</param>
public readonly record struct NerToken(int Id, int Start, int End);

/// <summary>
/// Cuts a text into the tokens of a model, with the place of each in the text. The text is first cut at its spaces; each
/// word is then given to the tokenizer of the model, as the model reads it in a sentence.
/// </summary>
public sealed class NerTokenizer
{
    private readonly Tokenizer _tokenizer;
    private readonly NerTokenizerDescription _description;
    private readonly int _unknownId;
    private readonly HashSet<int> _skipped;

    private NerTokenizer(Tokenizer tokenizer, NerTokenizerDescription description, int startId, int endId, int padId, int unknownId, HashSet<int> skipped)
    {
        _tokenizer = tokenizer;
        _description = description;
        StartId = startId;
        EndId = endId;
        PadId = padId;
        _unknownId = unknownId;
        _skipped = skipped;
    }

    /// <summary>Gets the id a sequence starts with.</summary>
    public int StartId { get; }

    /// <summary>Gets the id a sequence ends with.</summary>
    public int EndId { get; }

    /// <summary>Gets the id the shorter sequences of a batch are padded with.</summary>
    public int PadId { get; }

    /// <summary>Loads the tokenizer of a model.</summary>
    /// <param name="directory">The folder of the model.</param>
    /// <param name="description">How its text is cut into tokens.</param>
    /// <returns>The tokenizer.</returns>
    /// <exception cref="InvalidDataException">The file of the tokenizer cannot be read.</exception>
    public static NerTokenizer Load(string directory, NerTokenizerDescription description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(description);
        var path = Path.Combine(directory, description.File);
        try
        {
            if (description.Kind == NerTokenizerDescription.WordPiece)
            {
                var bert = BertTokenizer.Create(path, new BertOptions { LowerCaseBeforeTokenization = description.LowerCase });
                return new NerTokenizer(bert, description,
                    description.StartId ?? bert.ClassificationTokenId, description.EndId ?? bert.SeparatorTokenId, description.PadId ?? bert.PaddingTokenId,
                    description.UnknownId ?? bert.UnknownTokenId, [bert.ClassificationTokenId, bert.SeparatorTokenId]);
            }

            using var stream = File.OpenRead(path);
            var pieces = SentencePieceTokenizer.Create(stream, addBeginningOfSentence: false, addEndOfSentence: false);
            var offset = description.IdOffset;
            return new NerTokenizer(pieces, description,
                description.StartId ?? pieces.BeginningOfSentenceId + offset, description.EndId ?? pieces.EndOfSentenceId + offset, description.PadId ?? 0,
                description.UnknownId ?? pieces.UnknownId + offset, [pieces.BeginningOfSentenceId, pieces.EndOfSentenceId]);
        }
        // A file that is not a tokenizer fails in many ways (its parser throws its own exceptions): the model does not load.
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new InvalidDataException($"The tokenizer {path} cannot be read: {exception.Message}", exception);
        }
    }

    /// <summary>Cuts a text into tokens.</summary>
    /// <param name="text">The text.</param>
    /// <returns>Its tokens, in order, each with its place in the text.</returns>
    public IReadOnlyList<NerToken> Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = new List<NerToken>();
        var index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            var start = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index])) index++;
            if (index > start) AddWord(text, start, index, tokens);
        }

        return tokens;
    }

    private void AddWord(string text, int start, int end, List<NerToken> tokens)
    {
        var word = text[start..end];
        var encoded = _tokenizer.EncodeToTokens(word, out _);
        // The place of each token is found again in the word from its text: the tokenizer may give it in its own
        // normalized form (the "▁" of SentencePiece, the "##" of WordPiece, a lower case).
        var position = 0;
        foreach (var token in encoded)
        {
            if (_skipped.Contains(token.Id) && token.Id != _unknownId) continue;
            var value = token.Value.Replace("▁", string.Empty, StringComparison.Ordinal);
            if (_description.Kind == NerTokenizerDescription.WordPiece && value.StartsWith("##", StringComparison.Ordinal)) value = value[2..];
            var id = Map(token.Id);
            if (value.Length == 0)
            {
                // A piece that is only the mark of a word start ("▁") holds no character.
                tokens.Add(new NerToken(id, start + position, start + position));
                continue;
            }

            var found = word.IndexOf(value, position, StringComparison.OrdinalIgnoreCase);
            if (found < 0 || id == _unknownId)
            {
                // An unknown piece, or a normalized one: it covers what is left of the word up to the next piece.
                var next = Math.Min(word.Length, position + Math.Max(1, value.Length));
                tokens.Add(new NerToken(id, start + position, start + next));
                position = next;
                continue;
            }

            tokens.Add(new NerToken(id, start + found, start + found + value.Length));
            position = found + value.Length;
        }
    }

    private int Map(int id)
    {
        if (_description.Kind == NerTokenizerDescription.WordPiece) return id;
        var unknown = ((SentencePieceTokenizer)_tokenizer).UnknownId;
        return id == unknown ? _unknownId : id + _description.IdOffset;
    }
}
