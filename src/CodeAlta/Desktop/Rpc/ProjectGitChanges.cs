using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Desktop.Rpc;

/// <summary>What git printed, and whether reading stopped at the size limit before git was done.</summary>
/// <param name="Bytes">The standard output, at most the limit that was asked for.</param>
/// <param name="Truncated">More output followed; git was stopped.</param>
internal readonly record struct GitOutput(byte[] Bytes, bool Truncated);

/// <summary>One changed file of a work tree, as the comparison against a commit reports it.</summary>
/// <param name="Path">The path relative to the work tree, with forward slashes.</param>
/// <param name="OriginalPath">The path the file had in the commit, when it was renamed or copied.</param>
/// <param name="Status">
/// <c>modified</c>, <c>added</c>, <c>deleted</c>, <c>renamed</c>, <c>copied</c>, <c>conflicted</c> or
/// <c>untracked</c>.
/// </param>
/// <param name="Insertions">Lines added; null for a binary file and for a file whose lines were not counted.</param>
/// <param name="Deletions">Lines removed; null for a binary file and for a file whose lines were not counted.</param>
/// <param name="Binary">Git or the line count found the file to be no text (a submodule is reported this way too).</param>
/// <param name="OriginalBlob">The object id of the content in the commit; null when the file is not in it.</param>
/// <param name="Stamp">The length and last write time of the file in the work tree; null when it is not there.</param>
/// <param name="Blob">
/// The object id of the new content where git knows it: the content of a commit compared with its parent, or of
/// the index. Null for a file that only the work tree holds, and for a deleted one.
/// </param>
internal sealed record GitChange(string Path, string? OriginalPath, string Status, int? Insertions, int? Deletions, bool Binary,
    string? OriginalBlob, (long Length, long WriteTicks)? Stamp, string? Blob = null)
{
    /// <summary>
    /// Identifies both contents of the file: it changes when the commit's content or the file on disk does.
    /// Computed from the values as they are now: a copy made with another stamp has another revision.
    /// </summary>
    internal string Revision => ProjectGitChanges.Hash($"{Status}\n{OriginalBlob}\n{Blob}\n{Stamp?.Length}\n{Stamp?.WriteTicks}\n{OriginalPath}");
}

/// <summary>One commit of the history of a branch.</summary>
/// <param name="Id">The full object id.</param>
/// <param name="ShortId">The abbreviated id git prints.</param>
/// <param name="Author">The name of the author.</param>
/// <param name="Time">When it was authored, as a round-trip UTC date and time.</param>
/// <param name="Subject">The first line of its message.</param>
internal sealed record ProjectGitCommit(string Id, string ShortId, string Author, string Time, string Subject);

/// <summary>The changed files of a work tree against one commit, read at one moment.</summary>
/// <param name="Files">The files, ordered by path.</param>
/// <param name="Insertions">The sum of the lines added.</param>
/// <param name="Deletions">The sum of the lines removed.</param>
/// <param name="Truncated">There were more files, or more output, than is read.</param>
internal sealed record GitChangeSet(GitChange[] Files, int Insertions, int Deletions, bool Truncated)
{
    /// <summary>Identifies the whole list: it changes when a file enters, leaves or changes on either side.</summary>
    internal string Revision { get; } = ProjectGitChanges.Hash(string.Join('\n', Files.Select(static file =>
        $"{file.Path}\t{file.Insertions}\t{file.Deletions}\t{file.Binary}\t{file.Revision}")) + (Truncated ? "\n+" : ""));
}

/// <summary>
/// Reads what <c>git diff --raw --numstat -z</c> and <c>git ls-files --others -z</c> print, counts the lines of
/// untracked files, and decodes file contents for the changes view.
/// </summary>
internal static class ProjectGitChanges
{
    /// <summary>The tree without any file: what a repository without a commit is compared with.</summary>
    internal const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    /// <summary>Most files one list holds; more are left out and the list says so.</summary>
    internal const int MaximumFiles = 3000;

    /// <summary>Largest untracked file whose lines are counted, in bytes.</summary>
    internal const int MaximumCountedBytes = 2 * 1024 * 1024;

    /// <summary>Most untracked files whose lines are counted for one list; the lines of the others stay unknown.</summary>
    internal const int MaximumCountedFiles = 500;

    private const int BinaryProbeBytes = 8000;

    /// <summary>The arguments of the comparison of the work tree with a commit: status and line counts of each file.</summary>
    internal static string[] DiffArguments(string commit) =>
        ["diff", "--raw", "--numstat", "-z", "--no-abbrev", "-M", "--no-ext-diff", "--no-textconv", "--ignore-submodules=dirty", commit, "--"];

    /// <summary>The arguments of the comparison of two commits (or trees): what the second changed.</summary>
    internal static string[] DiffArguments(string from, string to) =>
        ["diff", "--raw", "--numstat", "-z", "--no-abbrev", "-M", "--no-ext-diff", "--no-textconv", "--ignore-submodules=dirty", from, to, "--"];

    /// <summary>The arguments that print the newest commits of the current branch, one record each.</summary>
    internal static string[] LogArguments(int count) =>
        ["log", "--no-show-signature", "--max-count=" + count.ToString(CultureInfo.InvariantCulture), "--format=%H%x1f%h%x1f%an%x1f%aI%x1f%s%x1e", "HEAD", "--"];

    /// <summary>Reads the records of <see cref="LogArguments"/>; a record that is not whole is left out.</summary>
    internal static List<ProjectGitCommit> ParseLog(ReadOnlySpan<byte> output)
    {
        var commits = new List<ProjectGitCommit>();
        foreach (var record in Encoding.UTF8.GetString(output).Split('\u001e'))
        {
            var fields = record.Trim('\r', '\n').Split('\u001f');
            if (fields.Length != 5 || !IsObjectId(fields[0]) || fields[1].Length is 0 or > 64 || !fields[0].StartsWith(fields[1], StringComparison.Ordinal)
                || !DateTimeOffset.TryParse(fields[3], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)) continue;
            commits.Add(new(fields[0], fields[1], Line(fields[2], 100), time.UtcDateTime.ToString("o", CultureInfo.InvariantCulture), Line(fields[4], 200)));
        }

        return commits;

        // One bounded line without control characters.
        static string Line(string text, int limit)
        {
            var line = new string([.. text.Where(static character => !char.IsControl(character))]).Trim();
            return line.Length <= limit ? line : string.Concat(line.AsSpan(0, char.IsHighSurrogate(line[limit - 1]) ? limit - 1 : limit), "…");
        }
    }

    /// <summary>The arguments that list the files git neither tracks nor ignores.</summary>
    internal static readonly string[] UntrackedArguments = ["ls-files", "--others", "--exclude-standard", "-z", "--"];

    /// <summary>
    /// Reads the records of <c>git diff --raw --numstat -z</c>: first one status record per file, then one line
    /// count per file. A record cut short by a truncated output is left out.
    /// </summary>
    /// <returns>The files in the order git printed them, without stamps.</returns>
    internal static List<GitChange> ParseDiff(ReadOnlySpan<byte> output)
    {
        var tokens = Tokens(output);
        var changes = new List<GitChange>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var at = 0; at < tokens.Count;)
        {
            var token = tokens[at++];
            if (token.StartsWith(':'))
            {
                // :<old mode> <new mode> <old id> <new id> <status>, then the path, or the old and the new path.
                var fields = token.Split(' ');
                if (fields.Length != 5 || fields[4].Length == 0) break;
                var letter = fields[4][0];
                var paired = letter is 'R' or 'C';
                if (at + (paired ? 2 : 1) > tokens.Count) break;
                var original = paired ? tokens[at++] : null;
                var path = tokens[at++];
                if (!Acceptable(path) || original is not null && !Acceptable(original) || index.ContainsKey(path)) continue;
                var status = letter switch { 'A' => "added", 'D' => "deleted", 'R' => "renamed", 'C' => "copied", 'U' => "conflicted", _ => "modified" };
                var blob = letter is 'A' || fields[2].AsSpan().TrimStart('0').IsEmpty || !IsObjectId(fields[2]) ? null : fields[2];
                var next = letter is 'D' || fields[3].AsSpan().TrimStart('0').IsEmpty || !IsObjectId(fields[3]) ? null : fields[3];
                // A submodule (a commit, not a file) has no text on either side.
                var submodule = fields[0] == ":160000" || fields[1] == "160000";
                index[path] = changes.Count;
                changes.Add(new(path, original, status, null, null, submodule, submodule ? null : blob, null, submodule ? null : next));
                continue;
            }

            // <added>\t<removed>\t<path>, or <added>\t<removed>\t and then the old and the new path.
            var first = token.IndexOf('\t');
            var second = first < 0 ? -1 : token.IndexOf('\t', first + 1);
            if (second < 0) break;
            var name = token[(second + 1)..];
            if (name.Length == 0)
            {
                if (at + 2 > tokens.Count) break;
                at++;
                name = tokens[at++];
            }

            if (!index.TryGetValue(name, out var position)) continue;
            var binary = token.AsSpan(0, first) is "-";
            if (binary) changes[position] = changes[position] with { Binary = true };
            else if (int.TryParse(token.AsSpan(0, first), NumberStyles.None, CultureInfo.InvariantCulture, out var added)
                && int.TryParse(token.AsSpan(first + 1, second - first - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var removed))
                changes[position] = changes[position] with { Insertions = added, Deletions = removed };
        }

        return changes;
    }

    /// <summary>Reads the paths of <c>git ls-files --others -z</c>.</summary>
    internal static List<string> ParseUntracked(ReadOnlySpan<byte> output) => Tokens(output).FindAll(Acceptable);

    /// <summary>
    /// Counts the lines of a text the way <c>git diff --numstat</c> does for an added file: one per line end,
    /// and one more for a last line without one.
    /// </summary>
    /// <returns>The number of lines, or null when the content is no text.</returns>
    internal static int? CountLines(ReadOnlySpan<byte> content)
    {
        if (IsBinary(content)) return null;
        var lines = content.Count((byte)'\n');
        return content.Length > 0 && content[^1] != (byte)'\n' ? lines + 1 : lines;
    }

    /// <summary>
    /// Decodes a file content for display: UTF-16 with a byte order mark, otherwise UTF-8.
    /// </summary>
    /// <returns>The text, or null when the content is no text.</returns>
    internal static string? DecodeText(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith<byte>([0xFF, 0xFE])) return Encoding.Unicode.GetString(content[2..]);
        if (content.StartsWith<byte>([0xFE, 0xFF])) return Encoding.BigEndianUnicode.GetString(content[2..]);
        if (IsBinary(content)) return null;
        return Encoding.UTF8.GetString(content.StartsWith<byte>([0xEF, 0xBB, 0xBF]) ? content[3..] : content);
    }

    /// <summary>The length and last write time of a file of the work tree, or null when it is not a readable file.</summary>
    internal static (long Length, long WriteTicks)? Stamp(string workTree, string path)
    {
        try
        {
            var file = new FileInfo(Path.Combine(workTree, path));
            return file.Exists ? (file.Length, file.LastWriteTimeUtc.Ticks) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a file of the work tree for the changes view, unless it is larger than <paramref name="maximumBytes"/>,
    /// a link, or no longer there.
    /// </summary>
    /// <returns>The bytes with the state <c>text</c>, or no bytes with <c>absent</c>, <c>too_large</c> or <c>unreadable</c>.</returns>
    internal static (byte[]? Bytes, string State) ReadWorkTreeFile(string workTree, string path, int maximumBytes)
    {
        try
        {
            var full = Path.GetFullPath(Path.Combine(workTree, path));
            // The list names files of this work tree only; a path that leaves it is never read.
            var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workTree)) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return (null, "unreadable");
            var file = new FileInfo(full);
            if (!file.Exists) return (null, "absent");
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0) return (null, "unreadable");
            if (file.Length > maximumBytes) return (null, "too_large");
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[maximumBytes + 1];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return read > maximumBytes ? (null, "too_large") : (buffer[..read], "text");
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return (null, "absent");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException)
        {
            return (null, "unreadable");
        }
    }

    /// <summary>A short, stable name for a text: the first eight bytes of its SHA-256.</summary>
    internal static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 8));

    /// <summary>Whether a text is a SHA-1 or SHA-256 object id.</summary>
    internal static bool IsObjectId(ReadOnlySpan<char> text) => text.Length is 40 or 64 && !text.ContainsAnyExcept("0123456789abcdef");

    // Git's own test: a NUL in the first part of the content.
    private static bool IsBinary(ReadOnlySpan<byte> content) => content[..Math.Min(content.Length, BinaryProbeBytes)].Contains((byte)0);

    // A path git prints is relative and stays inside the work tree; anything else is not shown, and never read.
    private static bool Acceptable(string path) => path.Length is > 0 and <= 1024 && !Path.IsPathRooted(path) && !(OperatingSystem.IsWindows() && path.Contains('\\'))
        && !path.AsSpan().ContainsAnyInRange('\0', '\u001f') && !path.Split('/').Any(static segment => segment is "" or "." or "..");

    // The NUL-terminated records of a -z output; a last record without its NUL was cut and is dropped.
    private static List<string> Tokens(ReadOnlySpan<byte> output)
    {
        var tokens = new List<string>();
        while (output.IndexOf((byte)0) is >= 0 and var end)
        {
            tokens.Add(Encoding.UTF8.GetString(output[..end]));
            output = output[(end + 1)..];
        }

        return tokens;
    }
}
