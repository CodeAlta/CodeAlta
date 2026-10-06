using System.Text.Json;

namespace CodeAlta.Desktop.Rpc;

// Display-only projection of supplied records, before raw JSON is shortened.
// Paths are never filesystem targets. The caller reserves the returned wire cost.
internal static class HistoryFileProjection
{
    internal static HistoryFileSet? Project(string? details, int budget, out int cost)
    {
        cost = 0;
        if (details is null || budget < 512) return null;
        using var document = JsonDocument.Parse(details);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        var candidates = new List<(string Path, string? Kind, string? Diff)>();
        var partial = false;
        if (root.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Array)
        {
            foreach (var change in changes.EnumerateArray())
            {
                if (candidates.Count >= 32) { partial = true; break; }
                var path = Text(change, "path");
                if (path is null) { partial = true; continue; }
                var kind = Text(change, "operation");
                if (change.ValueKind == JsonValueKind.Object && change.TryGetProperty("kind", out var value)) kind ??= Text(value, "type");
                candidates.Add((path, kind, Text(change, "diff")));
            }
        }
        else if (Text(root, "path") is { } path)
            candidates.Add((path, Text(root, "operation"), Text(root, "diff")));
        else if (Text(root, "diff") is { } diff)
        {
            // Explicit unified-diff file headers only; never infer names from prose.
            var sections = diff.StartsWith("diff --git ", StringComparison.Ordinal)
                ? diff[11..].Split("\ndiff --git ", StringSplitOptions.None) : [];
            foreach (var section in sections)
            {
                if (candidates.Count >= 32) { partial = true; break; }
                var lines = section.Split('\n');
                var before = lines.FirstOrDefault(line => line.StartsWith("--- ", StringComparison.Ordinal))?[4..].TrimEnd('\r');
                var after = lines.FirstOrDefault(line => line.StartsWith("+++ ", StringComparison.Ordinal))?[4..].TrimEnd('\r');
                if (before is null || after is null || !(before == "/dev/null" || before.StartsWith("a/", StringComparison.Ordinal))
                    || !(after == "/dev/null" || after.StartsWith("b/", StringComparison.Ordinal))) { partial = true; continue; }
                var name = after == "/dev/null" ? before[2..] : after[2..];
                var hunk = Array.FindIndex(lines, line => line.StartsWith("@@ ", StringComparison.Ordinal));
                candidates.Add((name, before == "/dev/null" ? "create" : after == "/dev/null" ? "delete" : "update",
                    hunk < 0 ? null : string.Join('\n', lines.Skip(hunk))));
            }
            if (sections.Length == 0) partial = true;
        }
        else return null;
        var rows = new List<HistoryFileRow>();
        var diffs = new List<string?>();
        cost = 256;
        // Reserve names for every candidate before spending space on diffs.
        foreach (var candidate in candidates)
        {
            if (candidate.Path.Length is 0 or > 512 || candidate.Path.Any(char.IsControl)) { partial = true; continue; }
            var kind = candidate.Kind?.Length <= 64 ? candidate.Kind : null;
            var rowCost = 256 + 6 * (candidate.Path.Length + (kind?.Length ?? 0));
            if (cost + rowCost > budget) { partial = true; break; }
            cost += rowCost;
            var (added, removed) = CountLines(candidate.Diff);
            rows.Add(new(candidate.Path, kind, null, added, removed));
            diffs.Add(candidate.Diff);
        }
        for (var index = 0; index < rows.Count; index++)
        {
            var supplied = diffs[index];
            if (supplied is null) continue;
            if (supplied.Length > 4096 || cost + 6 * supplied.Length > budget) { partial = true; continue; }
            rows[index] = rows[index] with { Diff = supplied };
            cost += 6 * supplied.Length;
        }
        return new(rows.ToArray(), partial);
    }

    private static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    // Match the TUI's supplied-diff statistics, independently of the preview budget.
    internal static (int? Added, int? Removed) CountLines(string? diff)
    {
        if (string.IsNullOrWhiteSpace(diff)) return (null, null);
        var added = 0;
        var removed = 0;
        foreach (var line in diff.AsSpan().EnumerateLines())
        {
            if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal)) continue;
            if (line.StartsWith("+", StringComparison.Ordinal)) added++;
            else if (line.StartsWith("-", StringComparison.Ordinal)) removed++;
        }
        return added > 0 || removed > 0 ? (added, removed) : (null, null);
    }
}

internal sealed record HistoryFileRow(string Path, string? Kind, string? Diff, int? Added = null, int? Removed = null);
internal sealed record HistoryFileSet(HistoryFileRow[] Rows, bool Partial);
