using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Catalog.Worktrees;
using NeoAstra;

namespace CodeAlta.Desktop.Ui;

/// <summary>
/// The tools of the window, over the browser automation of NeoAstra, which has the operations of Chrome
/// DevTools MCP: the same names, the same arguments, the same result text.
/// </summary>
/// <remarks>
/// <para>
/// The pages of the automation are the views of the application. The two tools that open and close browser
/// windows (<c>new_page</c>, <c>close_page</c>) are left out: these tools are for the window of the
/// application, not for browsing.
/// </para>
/// <para>
/// Automation is on from the creation of this object to its disposal: create it before the views load a
/// document, so that the console messages and the requests of the page are known from its first one.
/// </para>
/// </remarks>
internal sealed class DesktopUiAutomation : IDesktopUi, IAsyncDisposable
{
    private readonly NeoAutomation _automation;

    /// <summary>Turns automation on for the views of an application.</summary>
    /// <param name="application">The application whose views the tools see and drive.</param>
    /// <param name="filesDirectory">The folder the tools read their files from; created when it does not exist.</param>
    /// <exception cref="ArgumentNullException"><paramref name="application"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="filesDirectory"/> is blank.</exception>
    internal DesktopUiAutomation(NeoApplication application, string filesDirectory)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentException.ThrowIfNullOrWhiteSpace(filesDirectory);
        FilesDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(filesDirectory));
        Directory.CreateDirectory(FilesDirectory);
        _automation = new NeoAutomation(application, new NeoAutomationOptions { AllowedDirectories = { FilesDirectory } });
        Tools = Describe(_automation.Tools.Select(static tool => new DesktopUiTool(tool.Name, tool.Description, tool.InputSchema, tool.IsReadOnly)));
    }

    /// <inheritdoc />
    public IReadOnlyList<DesktopUiTool> Tools { get; }

    /// <inheritdoc />
    public string FilesDirectory { get; }

    /// <summary>
    /// The tools as the window offers them: the ones for browser windows are left out, and the texts that speak
    /// of a browser or of the folders of the automation say what holds here.
    /// </summary>
    internal static IReadOnlyList<DesktopUiTool> Describe(IEnumerable<DesktopUiTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return [.. tools.Where(static tool => tool.Name is not ("new_page" or "close_page")).Select(static tool => tool.Name switch
        {
            "list_pages" => tool with { Description = tool.Description + " The pages are the windows of CodeAlta Desktop." },
            "navigate_page" => tool with { Description = tool.Description + " The page of CodeAlta Desktop only loads the application: use the type \"reload\" to load it again." },
            "take_screenshot" => tool with { InputSchema = WithDescription(tool.InputSchema, "filePath", "The path of the file to save the screenshot to instead of attaching it to the response: absolute, or relative to the working folder of the caller.") },
            "take_snapshot" => tool with { InputSchema = WithDescription(tool.InputSchema, "filePath", "The path of the file to save the snapshot to instead of attaching it to the response: absolute, or relative to the working folder of the caller.") },
            _ => tool,
        }).OrderBy(static tool => tool.Name, StringComparer.Ordinal)];
    }

    /// <inheritdoc />
    public async Task<DesktopUiToolResult> CallAsync(string name, JsonElement arguments, DesktopUiFiles files, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(files);
        if (!Tools.Any(tool => tool.Name == name)) return DesktopUiToolResult.Error($"Unknown tool \"{name}\". Call a tool of the list of tools.");

        // A file the caller names is saved where that caller may write. The automation writes into its own folder
        // only: it saves the file there, and the file is then moved to where it was asked for.
        string? target = null, staged = null;
        if (name is "take_screenshot" or "take_snapshot" && arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("filePath", out var requested) && requested.ValueKind == JsonValueKind.String)
        {
            if (!TryResolveFile(requested.GetString()!, files, out target, out var refusal)) return DesktopUiToolResult.Error(refusal);
            staged = Path.Combine(FilesDirectory, ".staged", Guid.NewGuid().ToString("N") + Path.GetExtension(target));
            arguments = WithValue(arguments, "filePath", staged);
        }

        NeoAutomationToolResult result;
        try
        {
            result = await _automation.CallToolAsync(name, arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return DesktopUiToolResult.Error("The window is closing.");
        }

        var text = result.Text;
        if (staged is not null)
        {
            if (result.IsError || !File.Exists(staged)) Remove(staged);
            else
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target!)!);
                    File.Move(staged, target!, overwrite: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    Remove(staged);
                    return DesktopUiToolResult.Error($"The file could not be saved to {target}: {exception.Message}");
                }

                text = text.Replace(staged, target, StringComparison.Ordinal);
            }
        }
        else if (result.IsError && text.Contains("outside the directories this application allows for files", StringComparison.Ordinal))
        {
            // The text does not name the folder, and nothing else tells a caller where a file has to be.
            text += $" Put the file in {FilesDirectory}.";
        }

        return new DesktopUiToolResult(text, [.. result.Images.Select(static image => new DesktopUiImage(image.Data, image.ContentType))], result.IsError);
    }

    /// <summary>Turns automation off.</summary>
    public ValueTask DisposeAsync() => _automation.DisposeAsync();

    /// <summary>
    /// Resolves the file a caller wants a tool to save: a relative path starts from the caller's folder, and the
    /// file has to be inside one of the folders the caller may write to.
    /// </summary>
    /// <param name="requested">The path as the caller gave it.</param>
    /// <param name="files">Where the caller may have a file saved.</param>
    /// <param name="path">The full path of the file.</param>
    /// <param name="refusal">Why the path is refused, for the caller.</param>
    /// <returns>Whether the file may be saved there.</returns>
    internal static bool TryResolveFile(string requested, DesktopUiFiles files, [NotNullWhen(true)] out string? path, [NotNullWhen(false)] out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(files);
        path = null;
        if (string.IsNullOrWhiteSpace(requested))
        {
            refusal = "The argument \"filePath\" must be a file path.";
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(requested, files.BaseDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            refusal = "The argument \"filePath\" is not a valid file path.";
            return false;
        }

        if (!files.Roots.Any(root => GitWorktreeService.IsWithin(full, root)))
        {
            refusal = $"The path of \"filePath\" is outside the folders a file can be saved in: {files.Description ?? string.Join(", ", files.Roots)}.";
            return false;
        }

        if (Directory.Exists(full))
        {
            refusal = $"\"{full}\" is a folder: \"filePath\" names a file.";
            return false;
        }

        path = full;
        refusal = null;
        return true;
    }

    // The schema of a tool with another description for one of its arguments.
    private static JsonElement WithDescription(JsonElement schema, string property, string description)
    {
        if (JsonNode.Parse(schema.GetRawText()) is not JsonObject root || root["properties"] is not JsonObject properties
            || properties[property] is not JsonObject argument) return schema;
        argument["description"] = description;
        return Clone(root);
    }

    // The arguments of a call with another value for one of them.
    private static JsonElement WithValue(JsonElement arguments, string property, string value)
    {
        var root = JsonNode.Parse(arguments.GetRawText())!.AsObject();
        root[property] = value;
        return Clone(root);
    }

    private static JsonElement Clone(JsonObject node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    private static void Remove(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // It stays in the folder of the tools, which is theirs.
        }
    }
}
