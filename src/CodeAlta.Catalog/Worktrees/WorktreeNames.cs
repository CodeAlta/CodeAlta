namespace CodeAlta.Catalog.Worktrees;

/// <summary>
/// Names for the worktrees CodeAlta creates: an adjective and a noun, as in <c>quiet-heron</c>,
/// <c>amber-denali</c> or <c>brisk-zephyr</c>. The nouns are summits, stars, rivers, trees, flowers, birds and
/// other animals, winds, stones, lands, music, ships, old cities, lights, spices, tools and fruits.
/// </summary>
/// <remarks>
/// A name is lower-case letters and one hyphen, so it is at once a folder name and the end of a branch name
/// (<c>alta/quiet-heron</c>). There are <see cref="Count"/> of them.
/// </remarks>
public static partial class WorktreeNames
{
    private const int FreshPicks = 24;
    private const int LargestSuffix = 10_000;

    /// <summary>Gets the adjectives, in alphabetical order.</summary>
    public static ReadOnlySpan<string> Adjectives => AdjectiveWords;

    /// <summary>Gets the nouns, in alphabetical order.</summary>
    public static ReadOnlySpan<string> Nouns => NounWords;

    /// <summary>Gets how many different names there are.</summary>
    public static int Count => AdjectiveWords.Length * NounWords.Length;

    /// <summary>Picks a name.</summary>
    /// <param name="random">The source of the choice.</param>
    /// <returns>An adjective and a noun joined by a hyphen.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="random"/> is null.</exception>
    public static string Pick(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        return AdjectiveWords[random.Next(AdjectiveWords.Length)] + "-" + NounWords[random.Next(NounWords.Length)];
    }

    /// <summary>
    /// Picks a name that is not taken: another one a few times, then the last one with a number
    /// (<c>quiet-heron-2</c>).
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
