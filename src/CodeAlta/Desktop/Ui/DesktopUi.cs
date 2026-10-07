using System.Text.Json;

namespace CodeAlta.Desktop.Ui;

/// <summary>
/// One of the tools that see and drive the window. It has the name, the arguments and the result text of the
/// tool of Chrome DevTools MCP it stands for, so that a caller that knows those tools uses these unchanged.
/// </summary>
/// <param name="Name">The name of the tool, such as <c>take_snapshot</c> or <c>click</c>.</param>
/// <param name="Description">What the tool does, for the caller that chooses among the tools.</param>
/// <param name="InputSchema">The JSON Schema of the arguments, which is an object schema.</param>
/// <param name="ReadOnly">Whether the tool only reads from the window.</param>
internal sealed record DesktopUiTool(string Name, string Description, JsonElement InputSchema, bool ReadOnly);

/// <summary>An image a tool gives back, such as a screenshot.</summary>
/// <param name="Data">The encoded image.</param>
/// <param name="MediaType">The media type of <paramref name="Data"/>, such as <c>image/png</c>.</param>
internal sealed record DesktopUiImage(ReadOnlyMemory<byte> Data, string MediaType);

/// <summary>What a tool call gives back.</summary>
/// <param name="Text">The text of the result. When the tool failed, why it did and, where known, what to do instead.</param>
/// <param name="Images">The images that go with the text.</param>
/// <param name="IsError">Whether the tool failed.</param>
internal sealed record DesktopUiToolResult(string Text, IReadOnlyList<DesktopUiImage> Images, bool IsError)
{
    /// <summary>The result of a call that did nothing, for the reason given.</summary>
    internal static DesktopUiToolResult Error(string text) => new(text, [], true);
}

/// <summary>Where a caller may have a tool save a file: a screenshot, a snapshot.</summary>
/// <param name="BaseDirectory">The folder a relative path starts from.</param>
/// <param name="Roots">The folders a file may be saved in, with their subfolders.</param>
/// <param name="Description">What the folders are, for a caller whose path is refused; null names them one by one.</param>
internal sealed record DesktopUiFiles(string BaseDirectory, IReadOnlyList<string> Roots, string? Description = null);

/// <summary>
/// The tools that see and drive the window of the application: text snapshots of the page, screenshots, clicks,
/// typing, script evaluation, and the console and network logs of the page.
/// </summary>
/// <remarks>
/// A caller has full control over the window, and through it over everything the user can do in it. The
/// sessions of the application get the tools when they ask for them, and other applications through the MCP
/// server.
/// </remarks>
internal interface IDesktopUi
{
    /// <summary>Gets the tools, sorted by name. The list is the same for the whole run of the application.</summary>
    IReadOnlyList<DesktopUiTool> Tools { get; }

    /// <summary>
    /// Gets the folder the tools read files from (a script, a file to upload), and where they save a file a
    /// caller names without a folder it may write to.
    /// </summary>
    string FilesDirectory { get; }

    /// <summary>Runs a tool.</summary>
    /// <param name="name">The name of a tool of <see cref="Tools"/>.</param>
    /// <param name="arguments">The arguments, an object that follows the schema of the tool; a null value stands for an argument that is not given.</param>
    /// <param name="files">Where the caller may have the tool save a file.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The result. A tool that cannot do what it was asked completes with <see cref="DesktopUiToolResult.IsError"/> set.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    Task<DesktopUiToolResult> CallAsync(string name, JsonElement arguments, DesktopUiFiles files, CancellationToken cancellationToken);
}
