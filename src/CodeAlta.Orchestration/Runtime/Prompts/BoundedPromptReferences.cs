using System.Diagnostics;
using System.Text;
using CodeAlta.Agent;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime.Prompts;

// Metadata references, not byte uploads. Like DirectoryCompletionReader, checks every
// ancestor and refuses reparse points. This is not a handle-relative hostile-filesystem sandbox.
internal sealed class BoundedPromptReferences
{
    private readonly object _gate = new();
    private readonly List<(string Root, string Path)> _recent = [];

    internal OwnedReferenceSearchResult Search(string root, string query, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var rows = new List<OwnedReferenceMatch>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var pending = new Stack<(string Path, int Depth)>();
        var clock = Stopwatch.StartNew();
        var entries = 0;
        var omitted = false;
        try
        {
            if (!SafeAncestors(root)) return new("unavailable", [], true);
            (string Root, string Path)[] recent;
            lock (_gate) recent = _recent.Where(item => item.Root == root).Take(5).ToArray();
            foreach (var item in recent)
                if (TryItem(root, item.Path, out var directory) && item.Path.Contains(query, StringComparison.OrdinalIgnoreCase) && seen.Add(item.Path))
                    rows.Add(new(item.Path, directory, true));
            pending.Push((root, 0));
            while (pending.TryPop(out var current))
            {
                token.ThrowIfCancellationRequested();
                if (clock.ElapsedMilliseconds >= 250 || entries >= 4096) { omitted = true; break; }
                if (!SafeAncestors(current.Path)) { omitted = true; continue; }
                foreach (var path in Directory.EnumerateFileSystemEntries(current.Path, "*", SearchOption.TopDirectoryOnly))
                {
                    token.ThrowIfCancellationRequested();
                    if (++entries > 4096 || clock.ElapsedMilliseconds >= 250 || rows.Count >= 64) { omitted = true; break; }
                    var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                    if (Path.GetFileName(path).StartsWith('.') || !TryItem(root, relative, out var directory)) { omitted = true; continue; }
                    if (relative.Contains(query, StringComparison.OrdinalIgnoreCase) && seen.Add(relative)) rows.Add(new(relative, directory, false));
                    if (directory) { if (current.Depth < 6) pending.Push((path, current.Depth + 1)); else omitted = true; }
                }
                if (rows.Count >= 64) { omitted = true; break; }
            }
            return new(omitted ? "incomplete" : "ok", rows.ToArray(), omitted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        { return new("read_error", rows.ToArray(), true); }
    }

    internal AgentInput Resolve(string prompt, string root, CancellationToken token)
        => ResolveCore(prompt, root, token, null);

    internal OwnedReferenceObservation Observe(string prompt, string root, CancellationToken token)
    {
        var spans = new List<OwnedReferenceSpan>();
        _ = ResolveCore(prompt, root, token, spans);
        var omitted = spans.Count > 256;
        return new("ok", spans.Take(256).ToArray(), omitted);
    }

    private AgentInput ResolveCore(string prompt, string root, CancellationToken token, List<OwnedReferenceSpan>? spans)
    {
        token.ThrowIfCancellationRequested();
        void RecordSpan(int start, int length, string status)
        {
            // Keep one extra marker for explicit truncation, never a prompt-sized span list.
            if (spans is { Count: < 257 }) spans.Add(new(start, length, status));
        }
        var text = new StringBuilder(prompt.Length);
        var attachments = new List<AgentInputItem>();
        var cursor = 0;
        var clock = Stopwatch.StartNew();
        IReadOnlyList<ProjectFilePromptToken> references;
        // The shared parser throws for descending ranges. Preserve the entire
        // original rather than accidentally broadening it to an unrestricted file.
        try { references = ProjectFilePromptReferenceParser.Parse(prompt); }
        catch (ArgumentException) { RecordSpan(0, prompt.Length, "unresolved"); return AgentInput.Text(prompt); }
        foreach (var reference in references)
        {
            token.ThrowIfCancellationRequested();
            text.Append(prompt, cursor, reference.StartIndex - cursor);
            cursor = reference.StartIndex + reference.Length;
            if (reference.Kind == ProjectFilePromptTokenKind.EscapedAt) { RecordSpan(reference.StartIndex, reference.Length, "escaped"); text.Append('@'); continue; }
            var path = reference.LookupText;
            var range = reference.LineRange;
            var invalidSuffix = reference.RawText.StartsWith('@') && cursor < prompt.Length
                && (prompt[cursor] == ':' || range is not null && !char.IsWhiteSpace(prompt[cursor]) && !",;!?)].}>".Contains(prompt[cursor], StringComparison.Ordinal));
            if (reference.IsMalformed || invalidSuffix || path is null || attachments.Count >= 32 || clock.ElapsedMilliseconds >= 250
                || range is not null && (range.EndLine > 1_000_000 || range.EndLine - range.StartLine >= 2000)
                || !TryItem(root, path, out var directory) || directory && range is not null)
            { RecordSpan(reference.StartIndex, reference.Length, "unresolved"); text.Append(reference.RawText); continue; }
            path = path.Replace('\\', '/').TrimEnd('/');
            var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            var label = (reference.DisplayText ?? path).Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal)
                .Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal)
                .Replace("`", "\\`", StringComparison.Ordinal).Replace("*", "\\*", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
            var target = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
            if (range is not null) target += $":{range.StartLine}-{range.EndLine}";
            if (text.Length + label.Length + target.Length + 4 + prompt.Length - cursor > 131072)
            { RecordSpan(reference.StartIndex, reference.Length, "unresolved"); text.Append(reference.RawText); continue; }
            RecordSpan(reference.StartIndex, reference.Length, "resolved");
            text.Append('[').Append(label).Append("](").Append(target).Append(')');
            attachments.Add(directory ? new AgentInputItem.Directory(full, path)
                : new AgentInputItem.File(full, path, range is null ? null : new AgentLineRange(range.StartLine, range.EndLine)));
            if (spans is null) lock (_gate)
            {
                _recent.RemoveAll(item => item.Root == root && item.Path == path);
                _recent.Insert(0, (root, path));
                if (_recent.Count > 64) _recent.RemoveAt(64);
            }
        }
        text.Append(prompt, cursor, prompt.Length - cursor);
        return new AgentInput(new AgentInputItem[] { new AgentInputItem.Text(text.ToString()) }.Concat(attachments).ToArray());
    }

    private static bool TryItem(string root, string relative, out bool directory)
    {
        directory = false;
        if (relative.Length is 0 or > 1024 || relative.Any(char.IsControl) || relative.IndexOfAny([':', '"', '<', '>', '|', '*', '?']) >= 0
            || Path.IsPathRooted(relative) || relative.StartsWith('/') || relative.StartsWith('\\')) return false;
        var parts = relative.Replace('\\', '/').TrimEnd('/').Split('/');
        if (parts.Length > 8 || parts.Any(part => part.Length == 0 || part is "." or ".." || part != part.Trim() || part.EndsWith('.'))) return false;
        try
        {
            var full = Path.Combine(root, Path.Combine(parts));
            if (!SafeAncestors(full)) return false;
            directory = File.GetAttributes(full).HasFlag(FileAttributes.Directory);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { return false; }
    }

    private static bool SafeAncestors(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Length > 4096) return false;
        var root = Path.GetPathRoot(path);
        if (OperatingSystem.IsWindows() && (root?.Length != 3 || root[1] != ':')) return false;
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return false;
        return true;
    }
}

/// <summary>A project-relative file/directory match. No renderer root is accepted as authority.</summary>
/// <param name="Path">Project-relative path.</param><param name="Directory">Whether this is a directory.</param><param name="Recent">Whether observed in recent resolved inputs.</param>
public sealed record OwnedReferenceMatch(string Path, bool Directory, bool Recent);

/// <summary>A bounded metadata search, with explicit incomplete/error status.</summary>
/// <param name="Status">Search status.</param><param name="Items">At most 64 matches.</param><param name="Omitted">Whether results may be omitted.</param>
public sealed record OwnedReferenceSearchResult(string Status, IReadOnlyList<OwnedReferenceMatch> Items, bool Omitted);

/// <summary>A UTF-16 raw-prompt span observed by the dispatch parser and bounded path policy.</summary>
/// <param name="Start">Start offset.</param><param name="Length">Span length.</param><param name="Status">Resolved metadata, unresolved literal, or escaped at sign.</param>
public sealed record OwnedReferenceSpan(int Start, int Length, string Status);

/// <summary>A read-only metadata observation, not a promise of final Send resolution.</summary>
/// <param name="Status">Read status.</param><param name="Items">At most 256 raw-prompt spans.</param><param name="Omitted">Whether spans were omitted.</param>
public sealed record OwnedReferenceObservation(string Status, IReadOnlyList<OwnedReferenceSpan> Items, bool Omitted);
