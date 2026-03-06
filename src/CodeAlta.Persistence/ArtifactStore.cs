using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using SharpYaml;

namespace CodeAlta.Persistence;

/// <summary>
/// Provides markdown artifact read/write operations with YAML frontmatter.
/// </summary>
public sealed class ArtifactStore
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ArtifactStore"/> class.
    /// </summary>
    public ArtifactStore()
    {
    }

    /// <summary>
    /// Writes an artifact document to a path.
    /// </summary>
    /// <param name="path">The destination file path.</param>
    /// <param name="document">Artifact document content.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The normalized file path.</returns>
    /// <exception cref="ArgumentNullException">Thrown when arguments are <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when required frontmatter fields are missing.</exception>
    public async Task<string> WriteMarkdownAsync(
        string path,
        ArtifactDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(document.Frontmatter);

        if (string.IsNullOrWhiteSpace(document.Frontmatter.Id))
        {
            throw new ArgumentException("Frontmatter id is required.", nameof(document));
        }

        if (string.IsNullOrWhiteSpace(document.Frontmatter.Type))
        {
            throw new ArgumentException("Frontmatter type is required.", nameof(document));
        }

        var normalizedPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(normalizedPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        if (string.IsNullOrWhiteSpace(document.Frontmatter.CreatedAt))
        {
            document.Frontmatter.CreatedAt = now;
        }

        document.Frontmatter.UpdatedAt = now;
        document.Frontmatter.Tags ??= [];

        var yaml = NormalizeYaml(YamlSerializer.Serialize(document.Frontmatter));
        var contents = new StringBuilder()
            .AppendLine("---")
            .AppendLine(yaml)
            .AppendLine("---")
            .AppendLine()
            .Append(document.Body ?? string.Empty)
            .ToString();

        await File.WriteAllTextAsync(normalizedPath, contents, cancellationToken).ConfigureAwait(false);
        return normalizedPath;
    }

    /// <summary>
    /// Reads an artifact markdown file from disk.
    /// </summary>
    /// <param name="path">Artifact file path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The parsed artifact document.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    /// <exception cref="InvalidDataException">Thrown when frontmatter is missing.</exception>
    public async Task<ArtifactDocument> ReadMarkdownAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Artifact file was not found.", path);
        }

        var contents = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        if (!TrySplitFrontmatter(contents, out var frontmatterText, out var body))
        {
            throw new InvalidDataException("Artifact is missing YAML frontmatter.");
        }

        var frontmatter = YamlSerializer.Deserialize<ArtifactFrontmatter>(frontmatterText) ?? new ArtifactFrontmatter();
        frontmatter.Tags ??= [];
        frontmatter.Links ??= new ArtifactLinks();
        frontmatter.Links.Tasks ??= [];
        frontmatter.Links.Files ??= [];

        return new ArtifactDocument
        {
            Frontmatter = frontmatter,
            Body = body,
        };
    }

    /// <summary>
    /// Extracts plain text from markdown content for indexing.
    /// </summary>
    /// <param name="markdown">Markdown source content.</param>
    /// <returns>Plain text representation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="markdown"/> is <see langword="null"/>.</exception>
    public string ExtractPlainText(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var document = Markdown.Parse(markdown);
        var builder = new StringBuilder();
        foreach (var block in document)
        {
            AppendBlockText(block, builder);
        }

        return builder.ToString().Trim();
    }

    private static void AppendBlockText(Block block, StringBuilder builder)
    {
        switch (block)
        {
            case ContainerBlock container:
                foreach (var child in container)
                {
                    AppendBlockText(child, builder);
                }

                break;

            case LeafBlock leaf:
                if (leaf.Inline is not null)
                {
                    AppendInlineText(leaf.Inline.FirstChild, builder);
                    builder.AppendLine();
                }
                else if (leaf.Lines.Count > 0)
                {
                    foreach (var line in leaf.Lines.Lines)
                    {
                        builder.AppendLine(line.ToString());
                    }
                }

                break;
        }
    }

    private static void AppendInlineText(Inline? inline, StringBuilder builder)
    {
        for (var current = inline; current is not null; current = current.NextSibling)
        {
            switch (current)
            {
                case LiteralInline literal:
                    builder.Append(literal.Content.ToString());
                    break;

                case CodeInline code:
                    builder.Append(code.Content);
                    break;

                case LineBreakInline:
                    builder.AppendLine();
                    break;

                case ContainerInline container:
                    AppendInlineText(container.FirstChild, builder);
                    break;
            }
        }
    }

    private static bool TrySplitFrontmatter(
        string contents,
        out string frontmatter,
        out string body)
    {
        const string delimiter = "---";
        frontmatter = string.Empty;
        body = contents;

        if (!contents.StartsWith(delimiter, StringComparison.Ordinal))
        {
            return false;
        }

        var textReader = new StringReader(contents);
        _ = textReader.ReadLine();
        var frontmatterBuilder = new StringBuilder();

        string? line;
        while ((line = textReader.ReadLine()) is not null)
        {
            if (line.Trim() == delimiter)
            {
                body = textReader.ReadToEnd() ?? string.Empty;
                frontmatter = frontmatterBuilder.ToString();
                return true;
            }

            frontmatterBuilder.AppendLine(line);
        }

        return false;
    }

    private static string NormalizeYaml(string yaml)
    {
        var normalized = yaml.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            normalized = normalized["---\n".Length..];
        }

        if (normalized.EndsWith("\n...\n", StringComparison.Ordinal))
        {
            normalized = normalized[..^5];
        }

        return normalized.TrimEnd('\n');
    }
}
