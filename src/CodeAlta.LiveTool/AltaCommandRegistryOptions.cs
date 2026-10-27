using System.Collections.Frozen;

namespace CodeAlta.LiveTool;

/// <summary>
/// Options of an <see cref="AltaCommandRegistry"/>: which built-in <c>alta</c> commands a host offers.
/// </summary>
public sealed record AltaCommandRegistryOptions
{
    /// <summary>Gets the options of a host that offers every built-in command its services support.</summary>
    public static AltaCommandRegistryOptions Default { get; } = new();

    /// <summary>
    /// Gets the built-in roots of the command-line application: the commands that need neither a user to answer
    /// (<c>ask</c>, <c>review</c>) nor a window (<c>window</c>, <c>canvas</c>, <c>editor</c>...), nor a host that
    /// outlives the run (<c>reminder</c>, <c>automation</c>).
    /// </summary>
    public static FrozenSet<string> CliBuiltInRoots { get; } = FrozenSet.Create(
        StringComparer.Ordinal,
        "version",
        "project",
        "space",
        "session",
        "sandbox",
        "job",
        "workflow",
        "script",
        "skill",
        "skills",
        "skills_activate",
        "tool",
        "provider",
        "model",
        "prompt",
        "plugin",
        "task",
        "plan",
        "bookmark",
        "issue",
        "issues",
        "notes",
        "note");

    // Declared after the set it reads: static initializers run in the order of the source.
    /// <summary>
    /// Gets the options of the command-line application (<c>altacli</c>), which runs sessions without a user:
    /// the built-in roots of <see cref="CliBuiltInRoots"/> only.
    /// </summary>
    public static AltaCommandRegistryOptions Cli { get; } = new() { AllowedBuiltInRoots = CliBuiltInRoots };

    /// <summary>
    /// Gets the names of the built-in root commands the registry offers, or <see langword="null"/> for all of
    /// them (the default).
    /// </summary>
    /// <remarks>
    /// A root that is allowed is still offered only when the services of the host support it. The allow-list
    /// applies to the command tree, its help, the policies that <c>alta tool list</c> and
    /// <c>alta tool capability list</c> report, and the guidance of the root help. A built-in root added later is
    /// left out until it is listed. The commands of plugins are not filtered.
    /// </remarks>
    public IReadOnlySet<string>? AllowedBuiltInRoots { get; init; }

    /// <summary>Determines whether the registry offers the built-in root command <paramref name="root"/>.</summary>
    /// <param name="root">The name of a root command, such as <c>session</c>.</param>
    /// <returns><see langword="true"/> when there is no allow-list or when it names <paramref name="root"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> is <see langword="null"/>.</exception>
    public bool AllowsBuiltInRoot(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return AllowedBuiltInRoots is not { } allowed || allowed.Contains(root);
    }
}
