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

        if (name == "fill" && result.IsError && result.Text.Contains(NotFillable, StringComparison.Ordinal)
            && await FillEditorAsync(arguments, cancellationToken).ConfigureAwait(false) is { } filled) return filled;
        if (name == "resize_page" && !result.IsError) await FitPageAsync(arguments, cancellationToken).ConfigureAwait(false);

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

    // What the automation says of an element it cannot fill.
    private const string NotFillable = "is not an input, a text area, a select, a toggle, or editable content";

    // The event the page takes to replace the text of one of its editors (frontend/src/monaco/automationTyping.ts).
    private const string EditorFillEvent = "codealta:fill";

    // An editor of the page (the prompt, a file) takes its text from the browser's own text input, which no event
    // of a tool feeds, and is none of the elements the automation fills. The page replaces the text of such an
    // editor itself when it is asked to with an event. Null when the element is no editor that can be written in.
    private async Task<DesktopUiToolResult?> FillEditorAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("uid", out var uid) || uid.ValueKind != JsonValueKind.String
            || !arguments.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.String) return null;
        var page = PageOf(arguments);
        // A handled event is canceled, which is what the page answers with.
        var handled = await EvaluateAsync(
            "(element) => !element.dispatchEvent(new CustomEvent(\"" + EditorFillEvent + "\", { bubbles: true, cancelable: true, detail: \""
                + JsonEncodedText.Encode(value.GetString()!) + "\" }))",
            uid.GetString(), page, cancellationToken).ConfigureAwait(false);
        if (handled is not { ValueKind: JsonValueKind.True }) return null;
        var text = "Successfully filled out the element";
        if (arguments.TryGetProperty("includeSnapshot", out var include) && include.ValueKind == JsonValueKind.True)
        {
            var snapshot = await _automation.CallToolAsync("take_snapshot", Clone(page is { } shown ? new JsonObject { ["pageId"] = shown } : []), cancellationToken).ConfigureAwait(false);
            if (!snapshot.IsError) text += "\n" + snapshot.Text;
        }

        return new DesktopUiToolResult(text, [], false);
    }

    // The automation gives a window the size asked for its page plus what the window has beyond the page, and
    // counts the window in the pixels of the screen and the page in its own: on a display that scales, the page
    // does not get the size it was asked for. The size is asked again, corrected by what the page shows.
    private async Task FitPageAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("width", out var width) || !width.TryGetInt32(out var wantedWidth)
            || !arguments.TryGetProperty("height", out var height) || !height.TryGetInt32(out var wantedHeight)) return;
        var page = PageOf(arguments);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (await EvaluateAsync("() => [window.innerWidth, window.innerHeight, window.devicePixelRatio]", null, page, cancellationToken).ConfigureAwait(false)
                is not { ValueKind: JsonValueKind.Array } shown || shown.GetArrayLength() != 3
                || !shown[0].TryGetInt32(out var shownWidth) || !shown[1].TryGetInt32(out var shownHeight) || !shown[2].TryGetDouble(out var scale)) return;
            if (shownWidth == wantedWidth && shownHeight == wantedHeight) return;
            var (nextWidth, nextHeight) = (FitRequest(wantedWidth, shownWidth, scale), FitRequest(wantedHeight, shownHeight, scale));
            // A window that cannot take the size (the screen, its own minimum) is left as it is.
            if (nextWidth <= 0 || nextHeight <= 0 || (nextWidth == wantedWidth && nextHeight == wantedHeight)) return;
            var call = new JsonObject { ["width"] = nextWidth, ["height"] = nextHeight };
            if (page is { } id) call["pageId"] = id;
            if ((await _automation.CallToolAsync("resize_page", Clone(call), cancellationToken).ConfigureAwait(false)).IsError) return;
        }
    }

    /// <summary>
    /// The size to ask the automation for, so that a page that shows <paramref name="shown"/> gets
    /// <paramref name="wanted"/> on a display with this scale: the automation adds what the window has beyond
    /// the page, which it measures as the pixels of the window less the size of the page.
    /// </summary>
    internal static int FitRequest(int wanted, int shown, double scale)
        => (int)Math.Round(wanted * scale - shown * (scale - 1), MidpointRounding.AwayFromZero);

    private static int? PageOf(JsonElement arguments)
        => arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("pageId", out var pageId) && pageId.ValueKind == JsonValueKind.Number ? pageId.GetInt32() : null;

    // Runs a function in a page, for an element when one is named, and gives what it returned; null when it failed.
    private async Task<JsonElement?> EvaluateAsync(string function, string? uid, int? page, CancellationToken cancellationToken)
    {
        var call = new JsonObject { ["function"] = function };
        if (uid is not null) call["args"] = new JsonArray(uid);
        if (page is { } id) call["pageId"] = id;
        var evaluated = await _automation.CallToolAsync("evaluate_script", Clone(call), cancellationToken).ConfigureAwait(false);
        if (evaluated.IsError) return null;
        // The result is a line of text, then the value as a block of JSON.
        const string open = "```json";
        var text = evaluated.Text;
        var start = text.IndexOf(open, StringComparison.Ordinal);
        var end = start < 0 ? -1 : text.IndexOf("```", start + open.Length, StringComparison.Ordinal);
        if (end < 0) return null;
        try
        {
            using var document = JsonDocument.Parse(text.AsMemory(start + open.Length, end - start - open.Length));
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

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
