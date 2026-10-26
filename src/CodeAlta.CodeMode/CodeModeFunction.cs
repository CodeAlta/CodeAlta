namespace CodeAlta.CodeMode;

/// <summary>
/// A function a script can call, such as <c>tools.read_file</c> or <c>alta.session.create</c>. The engine builds
/// its namespaces from the paths, and every call reaches the <see cref="ICodeModeCallHandler"/> of the run with
/// the path and the arguments as JSON.
/// </summary>
/// <param name="Path">The dotted path of the function, such as <c>tools.read_file</c>. Each segment is an identifier.</param>
public sealed record CodeModeFunction(string Path)
{
    /// <summary>Gets the description shown in its declaration.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the JSON Schema of the argument object, or null when the function takes any object.</summary>
    public string? InputSchemaJson { get; init; }

    /// <summary>Gets the JSON Schema of the value the call resolves with, or null.</summary>
    public string? OutputSchemaJson { get; init; }

    /// <summary>
    /// Gets the TypeScript type of the value the call resolves with, when it is known as text rather than as a
    /// schema (for example a record type of <c>alta</c>). It wins over <see cref="OutputSchemaJson"/>.
    /// </summary>
    public string? ResultType { get; init; }

    /// <summary>
    /// Gets whether calls of this function may run at the same time as other calls. Functions that change files
    /// or state run one at a time, so that <c>Promise.all</c> over them stays ordered.
    /// </summary>
    public bool Parallel { get; init; } = true;

    /// <summary>Gets annotations shown in the declaration, such as <c>mutating</c>.</summary>
    public IReadOnlyList<string> Annotations { get; init; } = [];
}
