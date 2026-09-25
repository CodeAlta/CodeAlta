using System.Text;
using SharpYaml;

namespace CodeAlta.Catalog;

/// <summary>Result of a bounded catalog-wide exact identity check. Only Match and Missing certify a completed read.</summary>
public enum ProjectOwnershipStatus
{
    /// <summary>A unique ID and path were found in a complete scan.</summary>
    Match,
    /// <summary>No matching ID and path exist in a complete scan.</summary>
    Missing,
    /// <summary>Conflicting IDs, paths, or flat/legacy sources were observed.</summary>
    Ambiguous,
    /// <summary>An enumeration, file, or aggregate actual-byte budget prevented a complete scan.</summary>
    Incomplete,
    /// <summary>Input, encoding, or project metadata was invalid.</summary>
    Invalid,
    /// <summary>A linked/reparse path or I/O failure prevented a trustworthy read.</summary>
    ReadError,
}

/// <summary>A bounded ownership result. No file path or source text is returned.</summary>
/// <param name="Status">Whether the complete bounded scan established ownership, absence, or an uncertainty.</param>
/// <param name="Archived">Archived state from the preferred flat source or only legacy source, only on Match.</param>
/// <param name="EntriesVisited">Root entries and legacy readmes encountered, including an over-limit sentinel.</param>
/// <param name="BytesRead">Actual source bytes read, including oversize sentinels.</param>
public sealed record ProjectOwnershipResult(ProjectOwnershipStatus Status, bool? Archived, int EntriesVisited, int BytesRead)
{
    /// <summary>Whether the entire catalog was scanned and the result certifies a unique match or absence.</summary>
    public bool Complete => Status is ProjectOwnershipStatus.Match or ProjectOwnershipStatus.Missing;
}

public sealed partial class ProjectCatalog
{
    /// <summary>Maximum bytes read from one source, excluding one sentinel (which counts toward the aggregate budget).</summary>
    public const int MaximumOwnershipFileBytes = 32 * 1024;
    /// <summary>Maximum bytes actually read across all sources, including sentinels.</summary>
    public const int MaximumOwnershipTotalBytes = 512 * 1024;
    /// <summary>Maximum encountered root entries and legacy readmes before an incomplete result.</summary>
    public const int MaximumOwnershipEntries = 128;

    /// <summary>Checks a known project ID and normalized absolute project path against a complete budgeted catalog scan.</summary>
    /// <remarks>Does not use the ordinary unbounded loader, infer filenames from IDs, or establish an atomic
    /// snapshot against external writers or path swaps. A failed/partial scan never certifies ownership or absence.
    /// Equivalent flat and legacy entries require the same ID, slug and normalized path; flat archived state wins.
    /// All other duplicate IDs/paths fail closed, even if they do not match the requested identity.</remarks>
    /// <param name="projectId">Host-supplied project ID, not a catalog source filename.</param>
    /// <param name="expectedProjectPath">Host-supplied normalized absolute project path.</param>
    /// <param name="cancellationToken">Cancels enumeration and actual reads.</param>
    /// <returns>Explicit match, missing, ambiguous, incomplete, invalid, or read-error evidence.</returns>
    /// <exception cref="OperationCanceledException">The caller canceled before completion.</exception>
    public async Task<ProjectOwnershipResult> ReadBoundedOwnershipAsync(string? projectId, string? expectedProjectPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ProjectId.TryParse(projectId, out var requestedId) || expectedProjectPath is not { Length: > 0 and <= 4096 }
            || !Path.IsPathFullyQualified(expectedProjectPath)) return new(ProjectOwnershipStatus.Invalid, null, 0, 0);
        try
        {
            if (!string.Equals(NormalizePath(expectedProjectPath), expectedProjectPath, StringComparison.Ordinal))
                return new(ProjectOwnershipStatus.Invalid, null, 0, 0);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return new(ProjectOwnershipStatus.Invalid, null, 0, 0); }

        var entries = 0;
        var bytes = 0;
        ProjectOwnershipResult result(ProjectOwnershipStatus status, bool? archived = null) => new(status, archived, entries, bytes);
        var root = _options.ProjectsRoot;
        var byPath = new Dictionary<string, (ProjectId Id, string Slug, bool Flat, bool Archived)>(StringComparer.OrdinalIgnoreCase);
        var byId = new Dictionary<ProjectId, string>();
        var bySlug = new Dictionary<string, (ProjectId Id, string Path, bool Flat)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (root.Length > 4096 || !Path.IsPathFullyQualified(root)) return result(ProjectOwnershipStatus.Invalid);
            // Check ancestors too: a normal-looking projects/ child may sit under a linked catalog root.
            var ancestors = new Stack<DirectoryInfo>();
            for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
                ancestors.Push(directory);
            while (ancestors.Count > 0)
            {
                var directory = ancestors.Pop();
                FileAttributes attributes;
                try { attributes = File.GetAttributes(directory.FullName); }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                { return result(ProjectOwnershipStatus.Missing); }
                if (attributes.HasFlag(FileAttributes.ReparsePoint) || !attributes.HasFlag(FileAttributes.Directory))
                    return result(ProjectOwnershipStatus.ReadError);
            }
            foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++entries > MaximumOwnershipEntries) return result(ProjectOwnershipStatus.Incomplete);
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) return result(ProjectOwnershipStatus.ReadError);
                string? source = null;
                var flat = !attributes.HasFlag(FileAttributes.Directory);
                if (flat && entry.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    // The ordinary loader does not rank a root readme.md as a flat descriptor.
                    if (string.Equals(Path.GetFileName(entry), "readme.md", StringComparison.OrdinalIgnoreCase))
                        return result(ProjectOwnershipStatus.Ambiguous);
                    source = entry;
                }
                else if (!flat)
                {
                    var readme = Path.Combine(entry, "readme.md");
                    FileAttributes readmeAttributes;
                    try { readmeAttributes = File.GetAttributes(readme); }
                    catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { continue; }
                    if (readmeAttributes.HasFlag(FileAttributes.ReparsePoint)) return result(ProjectOwnershipStatus.ReadError);
                    if (!readmeAttributes.HasFlag(FileAttributes.Directory))
                    {
                        if (++entries > MaximumOwnershipEntries) return result(ProjectOwnershipStatus.Incomplete);
                        source = readme;
                    }
                }
                if (source is null) continue;
                if (bytes >= MaximumOwnershipTotalBytes) return result(ProjectOwnershipStatus.Incomplete);
                var allowed = Math.Min(MaximumOwnershipFileBytes + 1, MaximumOwnershipTotalBytes - bytes);
                var buffer = new byte[allowed];
                var length = 0;
                await using (var stream = new FileStream(source, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    while (length < allowed)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var count = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
                        if (count == 0) break;
                        length += count;
                        bytes += count;
                    }
                    // Without an EOF observation, a full budget cannot certify completion.
                    if (length == allowed) return result(ProjectOwnershipStatus.Incomplete);
                }
                if (length > MaximumOwnershipFileBytes) return result(ProjectOwnershipStatus.Incomplete);
                var text = Decode(buffer.AsSpan(0, length));
                var descriptor = _serializer.DeserializeProjectMarkdown(text);
                if (descriptor.Id.Length > 256 || descriptor.ProjectPath.Length > 4096 || descriptor.Slug.Length > 64
                    || !Path.IsPathFullyQualified(descriptor.ProjectPath)
                    || !string.Equals(descriptor.Slug, flat ? Path.GetFileNameWithoutExtension(source)
                        : Path.GetFileName(Path.GetDirectoryName(source)), StringComparison.OrdinalIgnoreCase))
                    return result(ProjectOwnershipStatus.Invalid);
                descriptor.Validate();
                cancellationToken.ThrowIfCancellationRequested();
                var id = descriptor.ProjectId;
                var path = NormalizePath(descriptor.ProjectPath);
                if (bySlug.TryGetValue(descriptor.Slug, out var previousSlug))
                {
                    if (previousSlug.Id != id || !string.Equals(previousSlug.Path, path, StringComparison.OrdinalIgnoreCase)
                        || previousSlug.Flat == flat) return result(ProjectOwnershipStatus.Ambiguous);
                }
                else bySlug.Add(descriptor.Slug, (id, path, flat));
                if (byPath.TryGetValue(path, out var previous))
                {
                    if (previous.Id != id || previous.Slug != descriptor.Slug || previous.Flat == flat)
                        return result(ProjectOwnershipStatus.Ambiguous);
                    if (flat) byPath[path] = (id, descriptor.Slug, true, descriptor.Archived);
                }
                else
                {
                    if (byId.TryGetValue(id, out var previousPath) && !string.Equals(previousPath, path, StringComparison.OrdinalIgnoreCase))
                        return result(ProjectOwnershipStatus.Ambiguous);
                    byPath.Add(path, (id, descriptor.Slug, flat, descriptor.Archived));
                    byId[id] = path;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (byId.TryGetValue(requestedId, out var matchingPath)
                && string.Equals(matchingPath, expectedProjectPath, StringComparison.OrdinalIgnoreCase))
                return result(ProjectOwnershipStatus.Match, byPath[matchingPath].Archived);
            return result(ProjectOwnershipStatus.Missing);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return result(ProjectOwnershipStatus.ReadError); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException or FormatException
            or YamlException or DecoderFallbackException
            or NotSupportedException or PathTooLongException)
        { return result(ProjectOwnershipStatus.Invalid); }
    }

    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        var encoding = (Encoding)new UTF8Encoding(false, true);
        var skip = 0;
        if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) skip = 3;
        else if (bytes.StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) { encoding = new UTF32Encoding(false, true, true); skip = 4; }
        else if (bytes.StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) { encoding = new UTF32Encoding(true, true, true); skip = 4; }
        else if (bytes.StartsWith(new byte[] { 0xff, 0xfe })) { encoding = new UnicodeEncoding(false, true, true); skip = 2; }
        else if (bytes.StartsWith(new byte[] { 0xfe, 0xff })) { encoding = new UnicodeEncoding(true, true, true); skip = 2; }
        return encoding.GetString(bytes[skip..]);
    }
}
