using CodeAlta.Catalog.Skills;

namespace CodeAlta.Catalog;

/// <summary>A trusted application file-open contract with backend-resolved skill provenance.</summary>
/// <remarks>
/// This is document workflow policy, not a renderer grant or sandbox. Ordinary trusted path opens
/// remain writable; only skill management can supply skill provenance. Link checks are observations,
/// not protection against external path replacement races.
/// </remarks>
public sealed class TextFileDocument
{
    /// <summary>Creates an ordinary writable file document.</summary>
    /// <exception cref="ArgumentException">The path is blank or invalid.</exception>
    public TextFileDocument(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        FullPath = Path.GetFullPath(fullPath);
    }

    internal TextFileDocument(string fullPath, SkillSourceKind sourceKind, string sourceId) : this(fullPath)
    {
        SkillSourceKind = sourceKind;
        SkillSourceId = sourceId;
    }

    /// <summary>Gets the normalized document path.</summary>
    public string FullPath { get; }

    /// <summary>Gets the catalog-resolved skill source, or null for an ordinary file.</summary>
    public SkillSourceKind? SkillSourceKind { get; }

    /// <summary>Gets the catalog-resolved skill source identifier, or null for an ordinary file.</summary>
    public string? SkillSourceId { get; }

    /// <summary>Gets whether this document is bundled immutable skill content.</summary>
    public bool IsReadOnly => SkillSourceKind == Skills.SkillSourceKind.Builtin;

    internal void ValidatePath()
    {
        if (SkillSourceKind is null) return;
        for (var path = FullPath; path is not null; path = Path.GetDirectoryName(path))
        {
            if (new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null ||
                ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
            {
                throw new IOException("Linked or reparse skill document paths are not supported.");
            }
        }
    }
}
