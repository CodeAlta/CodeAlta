namespace CodeAlta.Catalog.Worktrees;

/// <summary>
/// Names for the worktrees CodeAlta creates: an adjective, a noun and four random characters, as in
/// <c>quiet-heron-7k2m</c>, <c>amber-denali-x4pq</c> or <c>brisk-zephyr-9bdt</c>. The nouns are summits, stars,
/// rivers, trees, flowers, birds and other animals, winds, stones, lands, music, ships, old cities, lights, spices,
/// tools and fruits.
/// </summary>
/// <remarks>
/// <para>
/// A name is lower-case letters, digits and hyphens, so it is at once a folder name and the end of a branch name
/// (<c>alta/quiet-heron-7k2m</c>). There are <see cref="Count"/> pairs of words.
/// </para>
/// <para>
/// The characters at the end keep the names of two agents apart when neither can see what the other took:
/// another clone of the repository, another machine, a branch that is only on the remote. Without them, two
/// such worktrees of the same two words would push the same branch.
/// </para>
/// </remarks>
public static partial class WorktreeNames
{
    private const int FreshPicks = 24;
    private const int LargestSuffix = 10_000;

    /// <summary>How many random characters end a name.</summary>
    public const int SuffixLength = 4;

    // Digits and consonants that are not mistaken for one another: no vowel, so no word by accident.
    private const string SuffixAlphabet = "23456789bcdfghjkmnpqrstvwxz";

    /// <summary>Gets the adjectives, in alphabetical order.</summary>
    public static ReadOnlySpan<string> Adjectives => AdjectiveWords;

    /// <summary>Gets the nouns, in alphabetical order.</summary>
    public static ReadOnlySpan<string> Nouns => NounWords;

    /// <summary>Gets how many different pairs of an adjective and a noun there are.</summary>
    public static int Count => AdjectiveWords.Length * NounWords.Length;

    /// <summary>Picks a name.</summary>
    /// <param name="random">The source of the choice.</param>
    /// <returns>An adjective, a noun and <see cref="SuffixLength"/> random characters, joined by hyphens.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="random"/> is null.</exception>
    public static string Pick(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        var words = AdjectiveWords[random.Next(AdjectiveWords.Length)] + "-" + NounWords[random.Next(NounWords.Length)];
        return words + "-" + string.Create(SuffixLength, random, static (span, source) =>
        {
            foreach (ref var character in span)
            {
                character = SuffixAlphabet[source.Next(SuffixAlphabet.Length)];
            }
        });
    }

    /// <summary>
    /// Picks a name that is not taken: another one a few times, then the last one with a number
    /// (<c>quiet-heron-7k2m-2</c>).
    /// </summary>
    /// <param name="random">The source of the choice.</param>
    /// <param name="taken">Answers whether a name is in use already, as a folder or as a branch.</param>
    /// <returns>A name <paramref name="taken"/> answered false for.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">Every name tried is taken.</exception>
    public static string PickFree(Random random, Func<string, bool> taken)
    {
        ArgumentNullException.ThrowIfNull(random);
        ArgumentNullException.ThrowIfNull(taken);
        var name = Pick(random);
        for (var attempt = 0; attempt < FreshPicks && taken(name); attempt++)
        {
            name = Pick(random);
        }

        if (!taken(name))
        {
            return name;
        }

        for (var suffix = 2; suffix <= LargestSuffix; suffix++)
        {
            var numbered = name + "-" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!taken(numbered))
            {
                return numbered;
            }
        }

        throw new InvalidOperationException("No worktree name is free.");
    }
}
