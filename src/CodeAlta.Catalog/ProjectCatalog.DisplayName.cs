using System.Text;
using SharpYaml;
using SharpYaml.Model;
using Path = System.IO.Path;

namespace CodeAlta.Catalog;

/// <summary>The result of an exact, conditional project display-name update.</summary>
public enum ProjectDisplayNameRenameStatus
{
    /// <summary>The original catalog file was updated.</summary>
    Updated,
    /// <summary>The catalog identity, path, or file revision changed; nothing was written.</summary>
    Conflict,
    /// <summary>The source cannot be edited without changing unsupported YAML structure; nothing was written.</summary>
    Unsupported,
}

/// <summary>A project display name and its source file's raw-byte revision.</summary>
/// <param name="DisplayName">Current name.</param>
/// <param name="SourcePath">Exact catalog source path to pass to a conditional rename.</param>
/// <param name="Revision">Revision to pass to a conditional rename.</param>
public sealed record ProjectDisplayNameSnapshot(string DisplayName, string SourcePath, TextFileRevision Revision);

public sealed partial class ProjectCatalog
{
    private readonly TextFileCodec _projectNameFiles = new();

    /// <summary>Reads the exact project's current display name and source revision for a conditional rename.</summary>
    /// <param name="projectId">Persisted project ID.</param>
    /// <param name="projectPath">Exact normalized catalog project path, not the source file path.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The current name/revision, or null if the identity is not present or its source is unsupported.</returns>
    /// <exception cref="ArgumentException">An identity is empty.</exception>
    /// <exception cref="IOException">Reading the catalog failed.</exception>
    /// <exception cref="DecoderFallbackException">The source is not supported Unicode text.</exception>
    /// <exception cref="YamlException">A catalog entry contains malformed YAML.</exception>
    public async Task<ProjectDisplayNameSnapshot?> ReadDisplayNameAsync(string projectId, string projectPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var project = await GetByIdAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (project is null || project.Id != projectId || project.ProjectPath != projectPath) return null;
        var source = await ReadNameSourceAsync(project, projectId, projectPath, cancellationToken).ConfigureAwait(false);
        return source is null ? null : new(source.Value.Name, source.Value.Path, source.Value.Snapshot.Revision);
    }

    /// <summary>Conditionally renames only the display name in an existing canonical or legacy project file.</summary>
    /// <remarks>Only a unique, direct root scalar is edited; all other bytes, encoding, and Markdown remain intact.
    /// Missing, tagged, and multiline display names and observed linked catalog paths are unsupported;
    /// malformed YAML (including duplicate keys and unresolved aliases) is rejected by the catalog loader.
    /// A shared codec serializes cooperating saves. External editors can still race after its final revision check;
    /// this is not a cross-process atomic compare-and-swap. No conflict is retried automatically.</remarks>
    /// <param name="projectId">Exact persisted project ID.</param>
    /// <param name="projectPath">Exact normalized catalog project path.</param>
    /// <param name="expectedSourcePath">Exact source path captured by <see cref="ReadDisplayNameAsync"/>.</param>
    /// <param name="expectedRevision">Previously observed source-file revision (not Missing).</param>
    /// <param name="displayName">Nonblank, unpadded, well-formed name, at most 256 UTF-16 units.</param>
    /// <param name="cancellationToken">Cancels before commit.</param>
    /// <returns>Updated, Conflict, or Unsupported; no new catalog path is created.</returns>
    /// <exception cref="ArgumentException">An identity, revision, or name is invalid.</exception>
    /// <exception cref="IOException">The catalog read or conditional save failed.</exception>
    /// <exception cref="DecoderFallbackException">The source is not supported Unicode text.</exception>
    /// <exception cref="UnauthorizedAccessException">The source is read-only.</exception>
    /// <exception cref="OperationCanceledException">Cancelled before commit.</exception>
    /// <exception cref="YamlException">A catalog entry contains malformed YAML.</exception>
    public async Task<ProjectDisplayNameRenameStatus> RenameDisplayNameAsync(string projectId, string projectPath, string expectedSourcePath,
        TextFileRevision expectedRevision, string displayName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSourcePath);
        ArgumentNullException.ThrowIfNull(expectedRevision);
        if (!expectedRevision.Exists) throw new ArgumentException("An existing source revision is required.", nameof(expectedRevision));
        if (!ValidDisplayName(displayName)) throw new ArgumentException("A bounded, unpadded Unicode display name is required.", nameof(displayName));
        var project = await GetByIdAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (project is null || project.Id != projectId || project.ProjectPath != projectPath
            || project.SourcePath != expectedSourcePath)
            return ProjectDisplayNameRenameStatus.Conflict;
        var source = await ReadNameSourceAsync(project, projectId, projectPath, cancellationToken).ConfigureAwait(false);
        if (source is null) return ProjectDisplayNameRenameStatus.Unsupported;
        var (path, snapshot, name, start, end) = source.Value;
        if (snapshot.Revision != expectedRevision) return ProjectDisplayNameRenameStatus.Conflict;
        // A change confined to the scalar token preserves unknown keys, comments, whitespace, body, and newline style.
        var escaped = new StringBuilder(displayName.Length + 2).Append('"');
        foreach (var ch in displayName)
        {
            if (ch is '"' or '\\') escaped.Append('\\');
            escaped.Append(ch);
        }
        escaped.Append('"');
        var updated = string.Concat(snapshot.Text.AsSpan(0, start), escaped.ToString(), snapshot.Text.AsSpan(end));
        // Do not claim success if a YAML parser interprets the replacement differently.
        if (ParseName(updated) is not { } parsed || parsed.Name != displayName)
            return ProjectDisplayNameRenameStatus.Unsupported;
        var updatedProject = _serializer.DeserializeProjectMarkdown(updated);
        if (updatedProject.Id != projectId || updatedProject.Slug != project.Slug
            || NormalizePath(updatedProject.ProjectPath) != projectPath || updatedProject.DisplayName != displayName)
            return ProjectDisplayNameRenameStatus.Unsupported;
        var result = await _projectNameFiles.SaveAsync(new TextFileSaveRequest(path, updated,
            snapshot.Encoding, snapshot.HasByteOrderMark, expectedRevision), cancellationToken).ConfigureAwait(false);
        return result.IsConflict ? ProjectDisplayNameRenameStatus.Conflict : ProjectDisplayNameRenameStatus.Updated;
    }

    private async Task<(string Path, TextFileSnapshot Snapshot, string Name, int Start, int End)?> ReadNameSourceAsync(
        ProjectDescriptor project, string projectId, string projectPath, CancellationToken cancellationToken, string field = "display_name")
    {
        if (project.SourcePath is null) return null;
        var sourcePath = Path.GetFullPath(project.SourcePath);
        var projectsRoot = Path.GetFullPath(_options.ProjectsRoot);
        var flat = Path.GetFullPath(Path.Combine(projectsRoot, $"{project.Slug}.md"));
        var legacy = Path.GetFullPath(Path.Combine(projectsRoot, project.Slug, "readme.md"));
        if (sourcePath != flat && sourcePath != legacy) return null;
        // TextFileCodec follows file links; an observed catalog link is not authority to edit its target.
        foreach (var path in new[] { _options.GlobalRoot, projectsRoot, Path.GetDirectoryName(sourcePath)!, sourcePath })
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
        var snapshot = await _projectNameFiles.LoadAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var current = _serializer.DeserializeProjectMarkdown(snapshot.Text);
        if (current.Id != projectId || NormalizePath(current.ProjectPath) != projectPath || current.Slug != project.Slug)
            return null;
        var parsed = ParseName(snapshot.Text, field);
        return parsed is null ? null : (sourcePath, snapshot, parsed.Value.Name, parsed.Value.Start, parsed.Value.End);
    }

    // Parse YAML structure before using source marks to replace just one scalar token. Refuse complex
    // keys, duplicates, aliases, tagged/multiline values, and shapes with no explicit display_name.
    private static (string Name, int Start, int End)? ParseName(string text, string field = "display_name")
    {
        var firstEnd = text.IndexOf('\n');
        if (firstEnd < 0 || text.AsSpan(0, firstEnd).TrimEnd('\r') is not "---") return null;
        var begin = firstEnd + 1;
        var end = -1;
        for (var pos = begin; pos < text.Length;)
        {
            var next = text.IndexOf('\n', pos);
            if (next < 0) next = text.Length;
            if (text.AsSpan(pos, next - pos).TrimEnd('\r') is "---") { end = pos; break; }
            pos = next + 1;
        }
        if (end < 0) return null;
        var yaml = text[begin..end];
        using var reader = new StringReader(yaml);
        var stream = YamlStream.Load(reader);
        if (stream.Count != 1 || stream[0].Contents is not YamlMapping mapping) return null;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        YamlValue? value = null;
        foreach (var entry in mapping)
        {
            if (entry.Key is not YamlValue key || !string.IsNullOrEmpty(key.Anchor) || !string.IsNullOrEmpty(key.Tag)
                || !keys.Add(key.Value) || key.Value == "<<") return null;
            if (key.Value == field) value = entry.Value as YamlValue;
        }
        if (value is null || !string.IsNullOrEmpty(value.Anchor) || !string.IsNullOrEmpty(value.Tag)
            || value.Scalar.Style is not (ScalarStyle.Plain or ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted)) return null;
        var start = begin + value.Scalar.Start.Index;
        var finish = begin + value.Scalar.End.Index;
        if (start < begin || finish <= start || finish > end || text.AsSpan(start, finish - start).Contains('\n')) return null;
        return (value.Value, start, finish);
    }

    private static bool ValidDisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value != value.Trim()) return false;
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsControl(value[i])) return false;
            if (!char.IsSurrogate(value[i])) continue;
            if (!char.IsHighSurrogate(value[i]) || ++i == value.Length || !char.IsLowSurrogate(value[i])) return false;
        }
        return true;
    }
}
