namespace CodeAlta.Catalog;

/// <summary>
/// Represents a project resolved to a concrete checkout path.
/// </summary>
public sealed record ResolvedProject
{
    /// <summary>
    /// Gets the source project descriptor.
    /// </summary>
    public required ProjectDescriptor Project { get; init; }

    /// <summary>
    /// Gets the concrete checkout path.
    /// </summary>
    public required string CheckoutPath { get; init; }

    /// <summary>
    /// Gets the project-local <c>.alta</c> root path.
    /// </summary>
    public required string CodeAltaRoot { get; init; }
}

