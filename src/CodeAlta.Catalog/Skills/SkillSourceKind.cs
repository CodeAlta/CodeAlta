namespace CodeAlta.Catalog.Skills;

/// <summary>
/// Identifies the provenance of a discovered skill.
/// </summary>
public enum SkillSourceKind
{
    /// <summary>
    /// Project-local CodeAlta skill root (<c>.alta/skills</c>).
    /// </summary>
    ProjectAlta,

    /// <summary>
    /// Project-local common Agent Skills root (<c>.agents/skills</c>).
    /// </summary>
    ProjectCommon,

    /// <summary>
    /// User-level CodeAlta skill root (<c>~/.alta/skills</c>).
    /// </summary>
    UserAlta,

    /// <summary>
    /// User-level common Agent Skills root (<c>~/.agents/skills</c>).
    /// </summary>
    UserCommon,

    /// <summary>
    /// Plugin-contributed skill root.
    /// </summary>
    Plugin,

    /// <summary>
    /// Built-in skill root.
    /// </summary>
    Builtin,

    /// <summary>
    /// Temporary or explicit test/tooling root.
    /// </summary>
    Temporary,

    /// <summary>
    /// Project-local skill root of GitHub Copilot (<c>.github/skills</c>).
    /// </summary>
    ProjectCopilot,

    /// <summary>
    /// User-level skill root of GitHub Copilot (<c>~/.copilot/skills</c>).
    /// </summary>
    UserCopilot,
}

/// <summary>What is said of a source of skills beyond its kind.</summary>
public static class SkillSourceKindExtensions
{
    /// <summary>Whether a source is a folder of the layout of GitHub Copilot: its skills are read as Copilot writes them.</summary>
    /// <param name="kind">The source.</param>
    /// <returns>True for the Copilot folders of a project and of the user.</returns>
    public static bool IsCopilot(this SkillSourceKind kind) => kind is SkillSourceKind.ProjectCopilot or SkillSourceKind.UserCopilot;
}
