namespace CodeAlta.Pseudonymization.Ner;

/// <summary>The label a model gives a token, and how sure it is of it.</summary>
/// <param name="Label">The index of the label.</param>
/// <param name="Probability">Its probability, from 0 to 1.</param>
public readonly record struct TokenLabel(int Label, float Probability);

/// <summary>Runs a token classification model: the label of each token of sequences of ids.</summary>
public interface ITokenClassifier
{
    /// <summary>Gives the label of each token of sequences.</summary>
    /// <param name="sequences">The sequences of ids, each with its start and end ids.</param>
    /// <param name="cancellationToken">Stops the run.</param>
    /// <returns>For each sequence, the label of each of its tokens, start and end included.</returns>
    ValueTask<IReadOnlyList<TokenLabel[]>> ClassifyAsync(IReadOnlyList<int[]> sequences, CancellationToken cancellationToken);
}

/// <summary>
/// Finds names, places and organizations with a token classification model (a NER model of the BERT family): the text
/// is cut into the tokens of the model, read in windows the size the model takes, and the tokens of a kind that follow
/// each other become one result, scored by their mean probability. Which model, which tokenizer and which labels is what
/// its <see cref="NerModelDescription"/> says: one recognizer runs them all.
/// </summary>
public sealed partial class TokenClassificationRecognizer : PiiRecognizer
{
    // The sequences of one call of the model: a bounded batch keeps its memory bounded.
    private const int BatchSize = 8;

    // How far back a window may end to end at the start of a word rather than cut one.
    private const int WordSearch = 32;

    private const string Edges = " \t\r\n,;:.()!?«»\"'’";

    // The titles of a person, French and English, with the spaces after them.
    [System.Text.RegularExpressions.GeneratedRegex(@"\G(?:(?:Monsieur|Madame|Mademoiselle|Maître|Maitre|Docteur|Professeur|M\.|MM\.|Mme|Mmes|Mlle|Me|Dr|Pr|Mr|Mrs|Ms|Miss|Sir|Lady|Prof)\.?\s+)+",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex Title();

    private readonly NerModelDescription _description;
    private readonly NerTokenizer _tokenizer;
    private readonly ITokenClassifier _classifier;

    /// <summary>Creates the recognizer.</summary>
    /// <param name="description">The model.</param>
    /// <param name="tokenizer">Its tokenizer.</param>
    /// <param name="classifier">What runs it.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The description has no labels.</exception>
    public TokenClassificationRecognizer(NerModelDescription description, NerTokenizer tokenizer, ITokenClassifier classifier)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentNullException.ThrowIfNull(classifier);
        if (description.Labels is not { Count: > 0 }) throw new ArgumentException("The model has no labels.", nameof(description));
        _description = description;
        _tokenizer = tokenizer;
        _classifier = classifier;
    }

    /// <inheritdoc />
    public override string Name => "ner:" + _description.Id;

    /// <inheritdoc />
    public override async ValueTask<IReadOnlyList<PiiSpan>> AnalyzeAsync(string text, CancellationToken cancellationToken)
        => (await AnalyzeAsync([text], cancellationToken).ConfigureAwait(false))[0];

    /// <inheritdoc />
    public override async ValueTask<IReadOnlyList<IReadOnlyList<PiiSpan>>> AnalyzeAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var windows = new List<(int Text, IReadOnlyList<NerToken> Tokens)>();
        for (var index = 0; index < texts.Count; index++)
        {
            foreach (var window in Windows(texts[index], _tokenizer.Tokenize(texts[index]))) windows.Add((index, window));
        }

        // A batch is padded to its longest sequence: windows of similar lengths go together, and their labels back to their place.
        var order = Enumerable.Range(0, windows.Count).OrderBy(index => windows[index].Tokens.Count).ToArray();
        var labels = new TokenLabel[windows.Count][];
        for (var first = 0; first < order.Length; first += BatchSize)
        {
            var members = order.AsSpan(first, Math.Min(BatchSize, order.Length - first)).ToArray();
            var batch = members.Select(index => (int[])[_tokenizer.StartId, .. windows[index].Tokens.Select(static token => token.Id), _tokenizer.EndId]).ToArray();
            var results = await _classifier.ClassifyAsync(batch, cancellationToken).ConfigureAwait(false);
            if (results.Count != batch.Length) throw new InvalidOperationException("The model answered for another number of sequences.");
            for (var index = 0; index < batch.Length; index++)
            {
                if (results[index].Length != batch[index].Length) throw new InvalidOperationException("The model answered for another number of tokens.");
                labels[members[index]] = results[index];
            }
        }

        var spans = new List<PiiSpan>[texts.Count];
        for (var index = 0; index < texts.Count; index++) spans[index] = [];
        for (var index = 0; index < windows.Count; index++)
        {
            var (text, tokens) = windows[index];
            Decode(texts[text], tokens, labels[index], spans[text]);
        }

        return spans;
    }

    // Windows of at most what the model takes, each ending at the start of a word when one is near.
    private IEnumerable<IReadOnlyList<NerToken>> Windows(string text, IReadOnlyList<NerToken> tokens)
    {
        var size = _description.MaxTokens - 2;
        var start = 0;
        while (start < tokens.Count)
        {
            var end = Math.Min(tokens.Count, start + size);
            if (end < tokens.Count)
            {
                for (var cut = end; cut > Math.Max(start + 1, end - WordSearch); cut--)
                {
                    var at = tokens[cut].Start;
                    if (at > 0 && char.IsWhiteSpace(text[at - 1]))
                    {
                        end = cut;
                        break;
                    }
                }
            }

            yield return tokens.Skip(start).Take(end - start).ToArray();
            start = end;
        }
    }

    // The tokens of one kind that follow each other are one result, unless a token begins another (a B- label) or starts a
    // line: the lines of an address block or of a list hold one name each, which the model runs into each other. A word is
    // of the kind of its first token, as the "first" aggregation of Hugging Face reads it: the model gives the parts of a
    // word labels of their own ("DUP" a place and "ONT" a person, "33" a place and "000" nothing), and a word is never cut.
    private void Decode(string text, IReadOnlyList<NerToken> tokens, TokenLabel[] labels, List<PiiSpan> spans)
    {
        string? entity = null, word = null;
        int start = 0, end = 0, count = 0, previous = -1;
        double total = 0;
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            // A token that holds no character (a lone "▁") says nothing of where a name is.
            if (token.End == token.Start) continue;
            var label = labels[index + 1];
            var inWord = token.Start == previous && char.IsLetterOrDigit(text[token.Start - 1]) && char.IsLetterOrDigit(text[token.Start]);
            previous = token.End;
            var (kind, begins) = inWord ? (word, false)
                : label.Label >= 0 && label.Label < _description.Labels!.Count ? _description.Read(_description.Labels[label.Label]) : (null, false);
            word = kind;
            var newLine = entity is not null && token.Start > end && text.AsSpan(end, token.Start - end).ContainsAny('\n', '\r');
            if (kind is null || kind != entity || begins || newLine)
            {
                Close();
                if (kind is null) continue;
                (entity, start, count, total) = (kind, token.Start, 0, 0);
            }

            end = token.End;
            count++;
            total += label.Probability;
        }

        Close();

        void Close()
        {
            if (entity is not null && count > 0)
            {
                var first = start;
                var last = end;
                while (first < last && Edges.Contains(text[first], StringComparison.Ordinal)) first++;
                while (last > first && Edges.Contains(text[last - 1], StringComparison.Ordinal)) last--;
                // A title before a name stays: "Madame [PERSON_001]" keeps the gender and the grammar of the sentence, and
                // tells nobody who she is.
                if (entity == PiiEntities.Person && Title().Match(text, first, last - first) is { Success: true } title && title.Length < last - first) first += title.Length;
                if (last > first) spans.Add(new PiiSpan(entity, first, last - first, total / count, Name));
            }

            entity = null;
        }
    }
}
