namespace CodeAlta.Catalog.Skills;

/// <summary>
/// The precedence of each kind of skill root, lower first: of two skills of one name, the one of the root with the
/// lower precedence wins and the other is shadowed. The kinds are a hundred apart, so that the folders a user adds keep
/// the order of their list inside their kind (<see cref="SkillFolders.MaximumFolders"/> each).
/// </summary>
/// <remarks>
/// A folder of the project comes first, then what the user added for the project, then for the space of the session,
/// then the folders of the user, then what the user added for every space: the closer to the work, the stronger. The
/// skills that ship with CodeAlta and with plugins come last.
/// </remarks>
public static class SkillRootPrecedence
{
    /// <summary><c>&lt;project&gt;/.alta/skills</c>.</summary>
    public const int ProjectAlta = 0;

    /// <summary><c>&lt;project&gt;/.agents/skills</c> and <c>&lt;project&gt;/.github/skills</c>, ordered by their path.</summary>
    public const int ProjectCommon = 100;

    /// <summary>
    /// The folders of skills of the folders a sub-project inherits from (<see cref="ProjectResourceRoots"/>), after its
    /// own: of the nearest first, and in each its <c>.alta/skills</c> before its <c>.agents/skills</c> and
    /// <c>.github/skills</c>. The nearest folder wins on a name.
    /// </summary>
    public const int InheritedProject = 110;

    // The room of the inherited roots, before the folders the user added for the project.
    private const int InheritedRootStep = 4;
    private const int MaximumInheritedRoots = (ProjectFolder - InheritedProject) / InheritedRootStep;

    /// <summary>Gets the precedence of a folder of skills of a project root.</summary>
    /// <param name="kind"><see cref="ProjectAlta"/> or <see cref="ProjectCommon"/>.</param>
    /// <param name="index">The place of the root among the project roots: 0 for the project itself, then those it inherits from, the nearest first.</param>
    /// <returns>The precedence: <paramref name="kind"/> for the project itself, else one of <see cref="InheritedProject"/> and after.</returns>
    public static int OfProjectRoot(int kind, int index)
        => index <= 0 ? kind
            : InheritedProject + (Math.Min(index, MaximumInheritedRoots) - 1) * InheritedRootStep + (kind == ProjectAlta ? 0 : 1);

    /// <summary>The folders the user added for the project, in the order of their list.</summary>
    public const int ProjectFolder = 200;

    /// <summary>The folders the user added for the space of the session, in the order of their list.</summary>
    public const int SpaceFolder = 300;

    /// <summary><c>~/.alta/skills</c>.</summary>
    public const int UserAlta = 400;

    /// <summary><c>~/.agents/skills</c> and <c>~/.copilot/skills</c>, ordered by their path.</summary>
    public const int UserCommon = 500;

    /// <summary>The folders the user added for every space, in the order of their list.</summary>
    public const int UserFolder = 600;

    /// <summary>The skills that ship with CodeAlta; the roots of plugins come at it and after it.</summary>
    public const int Builtin = 700;
}
