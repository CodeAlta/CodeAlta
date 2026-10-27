namespace CodeAlta.LiveTool.Scripts;

/// <summary>A command of the <c>alta</c> tree as a caller gets it: its path, its arguments and options, its policy.</summary>
/// <param name="Path">The path, such as <c>session create</c>.</param>
/// <param name="Description">The one-line description of its help.</param>
/// <param name="Arguments">Its positional arguments, in their order.</param>
/// <param name="Options">Its options, without help.</param>
public sealed record AltaCommandDescriptor(
    string Path,
    string? Description,
    IReadOnlyList<AltaArgumentDescriptor> Arguments,
    IReadOnlyList<AltaOptionDescriptor> Options)
{
    /// <summary>Gets the policy the command declares, or null when it declares none.</summary>
    public AltaCommandPolicy? Policy { get; init; }
}

/// <summary>A positional argument of a command.</summary>
/// <param name="Name">The name of its prototype, such as <c>session-id</c>.</param>
/// <param name="Optional">Whether the command runs without it.</param>
/// <param name="IsList">Whether it takes several values.</param>
public sealed record AltaArgumentDescriptor(string Name, bool Optional, bool IsList)
{
    /// <summary>Gets its description.</summary>
    public string? Description { get; init; }
}

/// <summary>An option of a command.</summary>
/// <param name="Name">Its long name, such as <c>same-model-as</c>.</param>
/// <param name="Aliases">Its other names.</param>
/// <param name="TakesValue">Whether it takes a value (<c>--name=value</c>) or is a flag.</param>
public sealed record AltaOptionDescriptor(string Name, IReadOnlyList<string> Aliases, bool TakesValue)
{
    /// <summary>Gets its description.</summary>
    public string? Description { get; init; }
}
