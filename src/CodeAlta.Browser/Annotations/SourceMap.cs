using System.Text.Json;

namespace CodeAlta.Browser.Annotations;

/// <summary>
/// A source map (version 3) as browsers and bundlers write it: for a line and a column of the generated file, the
/// original file, line and column. Index maps (<c>sections</c>) are not read.
/// </summary>
internal sealed class SourceMap
{
    private const string Base64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    // For each generated line, its segments sorted by generated column: (column, source, original line, original column).
    // A segment of the generated column alone starts code that comes from no source: its source is -1.
    private readonly List<(int Column, int Source, int Line, int OriginalColumn)>[] _lines;

    private SourceMap(IReadOnlyList<string> sources, List<(int, int, int, int)>[] lines)
    {
        Sources = sources;
        _lines = lines;
    }

    /// <summary>Gets the original files, with the source root of the map before each.</summary>
    public IReadOnlyList<string> Sources { get; }

    /// <summary>Reads a source map, or gives null when the text is not one.</summary>
    public static SourceMap? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("mappings", out var mappings) || mappings.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("sources", out var sourcesValue) || sourcesValue.ValueKind != JsonValueKind.Array) return null;
            var sourceRoot = root.TryGetProperty("sourceRoot", out var rootValue) && rootValue.ValueKind == JsonValueKind.String ? rootValue.GetString() ?? string.Empty : string.Empty;
            var sources = sourcesValue.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String ? Join(sourceRoot, item.GetString() ?? string.Empty) : string.Empty)
                .ToArray();
            return new SourceMap(sources, Decode(mappings.GetString() ?? string.Empty, sources.Length));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Join(string root, string source)
    {
        if (root.Length == 0 || source.Contains("://", StringComparison.Ordinal) || source.StartsWith('/')) return source;
        return root.EndsWith('/') ? root + source : root + "/" + source;
    }

    /// <summary>
    /// The original place of a place of the generated file, both from 0, or null when the map says nothing of it or says
    /// that it comes from no source.
    /// </summary>
    public (string Source, int Line, int Column)? Find(int line, int column)
    {
        if (line < 0) return null;
        // The segment that starts at or before the column on that line; at the start of a line, the last one of an earlier line.
        for (var current = Math.Min(line, _lines.Length - 1); current >= 0; current--)
        {
            var segments = _lines[current];
            if (segments.Count == 0) continue;
            (int Column, int Source, int Line, int OriginalColumn)? found = null;
            if (current == line)
            {
                foreach (var segment in segments)
                {
                    if (segment.Column > column) break;
                    found = segment;
                }

                found ??= segments[0];
            }
            else found = segments[^1];

            // The segment that covers the place decides: one without a source does not let an earlier line answer for it.
            return found is { Source: >= 0 } hit && hit.Source < Sources.Count ? (Sources[hit.Source], hit.Line, hit.OriginalColumn) : null;
        }

        return null;
    }

    private static List<(int, int, int, int)>[] Decode(string mappings, int sourceCount)
    {
        var lines = new List<List<(int, int, int, int)>> { new() };
        int source = 0, originalLine = 0, originalColumn = 0;
        var fields = new int[5];
        var index = 0;
        var generatedColumn = 0;
        while (index < mappings.Length)
        {
            var character = mappings[index];
            if (character == ';')
            {
                lines.Add([]);
                generatedColumn = 0;
                index++;
                continue;
            }

            if (character == ',')
            {
                index++;
                continue;
            }

            var count = 0;
            while (index < mappings.Length && mappings[index] is not (',' or ';'))
            {
                if (!TryReadVlq(mappings, ref index, out var value)) return [.. lines];
                if (count < fields.Length) fields[count] = value;
                count++;
            }

            if (count == 0) continue;
            generatedColumn += fields[0];
            if (count >= 4)
            {
                source += fields[1];
                originalLine += fields[2];
                originalColumn += fields[3];
                lines[^1].Add((generatedColumn, source >= 0 && source < sourceCount ? source : -1, originalLine, originalColumn));
            }
            else
            {
                // The code from this column comes from no source (code the bundler added): the place before does not go on.
                lines[^1].Add((generatedColumn, -1, 0, 0));
            }
        }

        foreach (var line in lines) line.Sort(static (left, right) => left.Item1.CompareTo(right.Item1));
        return [.. lines];
    }

    private static bool TryReadVlq(string text, ref int index, out int value)
    {
        value = 0;
        var shift = 0;
        var result = 0;
        while (true)
        {
            if (index >= text.Length) return false;
            var digit = Base64.IndexOf(text[index++], StringComparison.Ordinal);
            if (digit < 0 || shift > 30) return false;
            result += (digit & 31) << shift;
            if ((digit & 32) == 0) break;
            shift += 5;
        }

        value = (result & 1) != 0 ? -(result >> 1) : result >> 1;
        return true;
    }
}
