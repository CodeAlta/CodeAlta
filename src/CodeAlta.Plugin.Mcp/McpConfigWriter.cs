namespace CodeAlta.Plugin.Mcp;

internal sealed class McpConfigWriter
{
    public async Task<McpConfigMutationResult> AddOrUpdateServerAsync(string path, McpConfigScope scope, McpServerDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();

        var created = !File.Exists(path);
        var document = created
            ? McpConfigFormatAdapter.CreateEmptyDocument()
            : McpConfigFormatAdapter.ParseDocument(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
        McpConfigFormatAdapter.AddOrUpdateServer(document, definition);
        await WriteDocumentAsync(path, document, cancellationToken).ConfigureAwait(false);
        return new McpConfigMutationResult
        {
            Path = path,
            Scope = scope,
            Flavor = document.Flavor,
            CreatedFile = created,
            Changed = true,
        };
    }

    public async Task<McpConfigMutationResult> RemoveServerAsync(string path, McpConfigScope scope, string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(path))
        {
            return new McpConfigMutationResult
            {
                Path = path,
                Scope = scope,
                Flavor = McpConfigFlavor.CodeAlta,
                CreatedFile = false,
                Changed = false,
            };
        }

        var document = McpConfigFormatAdapter.ParseDocument(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
        var changed = McpConfigFormatAdapter.RemoveServer(document, key);
        if (changed)
        {
            await WriteDocumentAsync(path, document, cancellationToken).ConfigureAwait(false);
        }

        return new McpConfigMutationResult
        {
            Path = path,
            Scope = scope,
            Flavor = document.Flavor,
            CreatedFile = false,
            Changed = changed,
        };
    }

    private static async Task WriteDocumentAsync(string path, McpConfigDocument document, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var content = McpConfigFormatAdapter.Serialize(document.Root);
        await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
    }
}
