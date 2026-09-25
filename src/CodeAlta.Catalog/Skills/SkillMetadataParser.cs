using System.Diagnostics.CodeAnalysis;
using System.Text;
using SharpYaml;
using SharpYaml.Events;
using SharpYaml.Schemas;

namespace CodeAlta.Catalog.Skills;

/// <summary>Outcome of parsing only the supported, bounded in-memory skill metadata subset.</summary>
public enum SkillMetadataStatus
{
    /// <summary>Supported metadata shape and local field checks passed; not effective skill validation.</summary>
    Parsed,
    /// <summary>Required fields, syntax, or supported field values are invalid.</summary>
    Invalid,
    /// <summary>YAML syntax or structures outside this deliberately limited subset were observed.</summary>
    Unsupported,
    /// <summary>Input, parser work, or output budget was exceeded; no partial metadata is returned.</summary>
    TooLarge,
}

/// <summary>One fixed diagnostic code per refusal; no user input or exception messages are included.</summary>
public enum SkillMetadataDiagnostic
{
    /// <summary>No refusal.</summary>
    None,
    /// <summary>Opening or closing frontmatter delimiter is absent.</summary>
    FrontmatterMissing,
    /// <summary>YAML could not be parsed.</summary>
    Syntax,
    /// <summary>Required name or description is absent.</summary>
    RequiredField,
    /// <summary>A supported field has an invalid key, type, or value.</summary>
    Field,
    /// <summary>A YAML anchor, alias, tag, directive, or additional document is unsupported.</summary>
    YamlFeature,
    /// <summary>A collection or complex key is unsupported.</summary>
    Structure,
    /// <summary>A top-level or metadata key is repeated.</summary>
    DuplicateKey,
    /// <summary>Full supplied text exceeds its UTF-16 or UTF-8 re-encoding budget.</summary>
    TextLimit,
    /// <summary>Frontmatter exceeds its own input budget.</summary>
    FrontmatterLimit,
    /// <summary>A scalar exceeds its per-field character budget.</summary>
    ScalarLimit,
    /// <summary>Field, token, or parser-event count exceeded its budget.</summary>
    WorkLimit,
    /// <summary>Aggregate returned key/value text exceeds its budget.</summary>
    OutputLimit,
}

/// <summary>Bounded metadata-only result. A refused result never contains partially parsed fields.</summary>
/// <param name="Status">Local parse status, not a discovery, enablement, or activation decision.</param>
/// <param name="Diagnostic">At most one fixed-size diagnostic code.</param>
/// <param name="Frontmatter">Metadata only on <see cref="SkillMetadataStatus.Parsed"/>.</param>
public sealed record SkillMetadataResult(SkillMetadataStatus Status, SkillMetadataDiagnostic Diagnostic, SkillFrontmatter? Frontmatter);

/// <summary>Opt-in, in-memory skill metadata parsing; does not read files or invoke legacy discovery.</summary>
public static class SkillMetadataParser
{
    /// <summary>Maximum UTF-16 units of the entire already-read document.</summary>
    public const int MaximumTextCharacters = 256 * 1024;
    /// <summary>Maximum bytes when re-encoding the supplied string as UTF-8 (not its original file byte count).</summary>
    public const int MaximumTextBytes = 256 * 1024;
    /// <summary>Maximum UTF-16 units between the frontmatter delimiters.</summary>
    public const int MaximumFrontmatterCharacters = 8192;
    /// <summary>Maximum decoded UTF-16 units in any one scalar.</summary>
    public const int MaximumScalarCharacters = 2048;
    /// <summary>Maximum total top-level and metadata key/value pairs.</summary>
    public const int MaximumFields = 16;
    /// <summary>Maximum YAML scanner tokens before invoking the event parser.</summary>
    public const int MaximumTokens = 256;
    /// <summary>Maximum YAML parser events, including stream/document events.</summary>
    public const int MaximumEvents = 128;
    /// <summary>Maximum UTF-16 units across returned field keys and values.</summary>
    public const int MaximumOutputCharacters = 4096;

    /// <summary>Parses supplied complete text without reading any path or inferring a directory name.</summary>
    /// <remarks>
    /// Only one mapping with scalar string keys/values and an optional one-level metadata string map is supported.
    /// Optional null values are absent; plain scalar types follow SharpYaml's Core schema. Anchors, aliases, tags,
    /// directives, extra documents, duplicate/complex keys, sequences, and other nested maps are refused. Only
    /// name, description, license, compatibility, allowed-tools, and metadata are accepted. No body text, path,
    /// directory-name match, trust, visibility, enablement, or effective discovery is validated. The caller owns
    /// the full text and its original bytes; neither this method nor a parsed status authenticates a file read.
    /// </remarks>
    /// <param name="text">Already-read complete skill document, optionally beginning with a UTF-8 BOM character.</param>
    /// <param name="cancellationToken">Checked before parsing and between scanner tokens and parser events.</param>
    /// <returns>Parsed fields or a fixed-code refusal without partial metadata.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="text"/> is null.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled.</exception>
    public static SkillMetadataResult Parse(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        if (text.Length > MaximumTextCharacters || Encoding.UTF8.GetByteCount(text) > MaximumTextBytes)
            return Refused(SkillMetadataStatus.TooLarge, SkillMetadataDiagnostic.TextLimit);

        // Delimit only the Markdown frontmatter envelope. YAML structure is parsed by SharpYaml, not split on ':' or indentation.
        var start = text.Length > 0 && text[0] == '\uFEFF' ? 1 : 0;
        if (text.Length <= start + 3 || !text.AsSpan(start, 3).SequenceEqual("---") ||
            text[start + 3] != '\n' && !(text[start + 3] == '\r' && text.Length > start + 4 && text[start + 4] == '\n'))
            return Refused(SkillMetadataStatus.Invalid, SkillMetadataDiagnostic.FrontmatterMissing);
        var contentStart = start + (text[start + 3] == '\n' ? 4 : 5);
        for (var cursor = contentStart; cursor <= text.Length && cursor - contentStart <= MaximumFrontmatterCharacters + 4;)
        {
            var end = cursor;
            while (end < text.Length && text[end] != '\n' && end - contentStart <= MaximumFrontmatterCharacters + 4) end++;
            if (end - contentStart > MaximumFrontmatterCharacters + 4)
                return Refused(SkillMetadataStatus.TooLarge, SkillMetadataDiagnostic.FrontmatterLimit);
            if (text.AsSpan(cursor, end - cursor).Trim().SequenceEqual("---"))
            {
                if (cursor - contentStart > MaximumFrontmatterCharacters)
                    return Refused(SkillMetadataStatus.TooLarge, SkillMetadataDiagnostic.FrontmatterLimit);
                return ParseFrontmatter(text.Substring(contentStart, cursor - contentStart), cancellationToken);
            }
            if (end == text.Length) break;
            cursor = end + 1;
        }
        return text.Length - contentStart > MaximumFrontmatterCharacters
            ? Refused(SkillMetadataStatus.TooLarge, SkillMetadataDiagnostic.FrontmatterLimit)
            : Refused(SkillMetadataStatus.Invalid, SkillMetadataDiagnostic.FrontmatterMissing);
    }

    private static SkillMetadataResult ParseFrontmatter(string yaml, CancellationToken cancellationToken)
    {
        try
        {
            // A token pass bounds scanner work before the parser can construct even a small event stream.
            var scanner = new Scanner<LookAheadBuffer>(new LookAheadBuffer(new StringReader(yaml), Scanner<LookAheadBuffer>.MaxBufferLength));
            var tokens = 0;
            while (scanner.MoveNext())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++tokens > MaximumTokens) Refuse(SkillMetadataStatus.TooLarge, SkillMetadataDiagnostic.WorkLimit);
            }

            var parser = Parser.CreateParser(new StringReader(yaml), maxDepth: 4);
            var events = 0;
            ParsingEvent? Next()
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++events > MaximumEvents) Refuse(SkillMetadataStatus.TooLarge, SkillMetadataDiagnostic.WorkLimit);
                return parser.MoveNext() ? parser.Current : null;
            }
            if (Next() is not StreamStart)
                Refuse(SkillMetadataStatus.Unsupported, SkillMetadataDiagnostic.Structure);
            var document = Next();
            if (document is StreamEnd)
                Refuse(SkillMetadataStatus.Invalid, SkillMetadataDiagnostic.RequiredField);
            if (document is not DocumentStart { IsImplicit: true })
                Refuse(SkillMetadataStatus.Unsupported, SkillMetadataDiagnostic.YamlFeature);
            var rootNode = Next();
            CheckFeature(rootNode);
            if (rootNode is Scalar { Value.Length: 0, Anchor: null, Tag: null })
                Refuse(SkillMetadataStatus.Invalid, SkillMetadataDiagnostic.RequiredField);
            var root = rootNode as MappingStart;
            if (root is null) Refuse(SkillMetadataStatus.Unsupported, SkillMetadataDiagnostic.Structure);

            var schema = new CoreSchema();
            var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            var fieldCount = 0;
            var outputLength = 0;
            string? ScalarText(Scalar scalar, bool nullable)
            {
                CheckFeature(scalar);
                if (scalar.Value.Length > MaximumScalarCharacters)
                    Refuse(SkillMetadataStatus.TooLarge, SkillMetadataDiagnostic.ScalarLimit);
                if (!schema.TryParse(scalar, false, out var tag, out _))
                    Refuse(SkillMetadataStatus.Invalid, SkillMetadataDiagnostic.Field);
                if (tag == JsonSchema.NullShortTag && nullable) return null;
                if (tag != FailsafeSchema.StrShortTag)
                    Refuse(SkillMetadataStatus.Invalid, SkillMetadataDiagnostic.Field);
                return scalar.Value;
            }
            void Count(string key, string? value)
            {
                if (++fieldCount > MaximumFields) Refuse(SkillMetadataStatus.TooLarge, SkillMetadataDiagnostic.WorkLimit);
                outputLength += key.Length + (value?.Length ?? 0);
                if (outputLength > MaximumOutputCharacters)
                    Refuse(SkillMetadataStatus.TooLarge, SkillMetadataDiagnostic.OutputLimit);
            }
            void ReadMetadata()
            {
                while (Next() is var item && item is not MappingEnd)
                {
                    CheckFeature(item);
                    if (item is not Scalar) Refuse(SkillMetadataStatus.Unsupported, SkillMetadataDiagnostic.Structure);
                    var key = (Scalar)item;
                    var name = ScalarText(key, nullable: false)!;
                    if (name == "<<") Refuse(SkillMetadataStatus.Unsupported, SkillMetadataDiagnostic.YamlFeature);
                    if (metadata.ContainsKey(name)) Refuse(SkillMetadataStatus.Unsupported, SkillMetadataDiagnostic.DuplicateKey);
                    var rawValue = Next();
                    CheckFeature(rawValue);
                    var value = rawValue as Scalar;
                    if (value is null) Refuse(SkillMetadataStatus.Unsupported, SkillMetadataDiagnostic.Structure);
                    var content = ScalarText(value, nullable: false)!;
                    Count(name, content);
                    metadata.Add(name, content);
                }
            }
            while (Next() is var item && item is not MappingEnd)
            {
                CheckFeature(item);
                if (item is not Scalar) Refuse(SkillMetadataStatus.Unsupported, SkillMetadataDiagnostic.Structure);
                var key = (Scalar)item;
                var name = ScalarText(key, nullable: false)!;
                if (name == "<<") Refuse(SkillMetadataStatus.Unsupported, SkillMetadataDiagnostic.YamlFeature);
                if (fields.ContainsKey(name)) Refuse(SkillMetadataStatus.Unsupported, SkillMetadataDiagnostic.DuplicateKey);
                if (name is not ("name" or "description" or "license" or "compatibility" or "allowed-tools" or "metadata"))
                    Refuse(SkillMetadataStatus.Invalid, SkillMetadataDiagnostic.Field);
                var value = Next();
                CheckFeature(value);
                if (name == "metadata" && value is MappingStart)
                {
                    Count(name, null);
                    ReadMetadata();
                    fields.Add(name, null);
                }
                else if (value is Scalar scalar)
                {
                    var content = ScalarText(scalar, nullable: true);
                    if (name == "metadata" && content is not null)
                        Refuse(SkillMetadataStatus.Unsupported, SkillMetadataDiagnostic.Structure);
                    Count(name, content);
                    fields.Add(name, content?.Trim());
                }
                else Refuse(SkillMetadataStatus.Unsupported, SkillMetadataDiagnostic.Structure);
            }
            if (Next() is not DocumentEnd { IsImplicit: true } || Next() is not StreamEnd || Next() is not null)
                Refuse(SkillMetadataStatus.Unsupported, SkillMetadataDiagnostic.YamlFeature);
            fields.TryGetValue("name", out var declaredName);
            fields.TryGetValue("description", out var description);
            if (string.IsNullOrWhiteSpace(declaredName) || string.IsNullOrWhiteSpace(description))
                Refuse(SkillMetadataStatus.Invalid, SkillMetadataDiagnostic.RequiredField);
            if (!ValidName(declaredName) || declaredName.Length > 64 || description.Length > 1024 ||
                fields.GetValueOrDefault("compatibility") is { Length: > 500 })
                Refuse(SkillMetadataStatus.Invalid, SkillMetadataDiagnostic.Field);
            return new SkillMetadataResult(SkillMetadataStatus.Parsed, SkillMetadataDiagnostic.None, new SkillFrontmatter
            {
                Name = declaredName,
                Description = description,
                License = fields.GetValueOrDefault("license"),
                Compatibility = fields.GetValueOrDefault("compatibility"),
                AllowedTools = fields.GetValueOrDefault("allowed-tools"),
                Metadata = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(metadata),
            });
        }
        catch (Refusal refusal) { return Refused(refusal.Status, refusal.Diagnostic); }
        catch (YamlException) { return Refused(SkillMetadataStatus.Invalid, SkillMetadataDiagnostic.Syntax); }
        catch (ArgumentException) { return Refused(SkillMetadataStatus.Invalid, SkillMetadataDiagnostic.Syntax); }
    }

    private static bool ValidName(string name)
    {
        if (name.StartsWith("-", StringComparison.Ordinal) || name.EndsWith("-", StringComparison.Ordinal) || name.Contains("--", StringComparison.Ordinal)) return false;
        foreach (var rune in name.EnumerateRunes())
            if (rune.Value != '-' && (!Rune.IsLetterOrDigit(rune) || Rune.IsLetter(rune) && Rune.ToLowerInvariant(rune) != rune)) return false;
        return true;
    }

    private static void CheckFeature(ParsingEvent? node)
    {
        if (node is AnchorAlias || node is NodeEvent { Anchor: not null } || node is NodeEvent { Tag: not null })
            Refuse(SkillMetadataStatus.Unsupported, SkillMetadataDiagnostic.YamlFeature);
    }

    private static SkillMetadataResult Refused(SkillMetadataStatus status, SkillMetadataDiagnostic diagnostic) => new(status, diagnostic, null);
    [DoesNotReturn]
    private static void Refuse(SkillMetadataStatus status, SkillMetadataDiagnostic diagnostic) => throw new Refusal(status, diagnostic);

    private sealed class Refusal(SkillMetadataStatus status, SkillMetadataDiagnostic diagnostic) : Exception
    {
        public SkillMetadataStatus Status { get; } = status;
        public SkillMetadataDiagnostic Diagnostic { get; } = diagnostic;
    }
}
