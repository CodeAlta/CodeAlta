namespace CodeAlta.Browser.Toolbar;

/// <summary>
/// The CodeAlta toolbar added to the documents of a session's tabs, and how the host talks to it. Its tools, Annotate
/// first, are parts of the one script.
/// </summary>
internal static class ToolbarScript
{
    /// <summary>The name of the isolated world the toolbar runs in: not the one of the browser agent, so neither sees the other.</summary>
    public const string WorldName = "codealta-toolbar";

    /// <summary>The binding the toolbar reports through; it exists in the toolbar's world only, never in the page's.</summary>
    public const string BindingName = "__codealtaToolbarMessage";

    /// <summary>The global object of the toolbar in its world.</summary>
    public const string GlobalName = "__codealtaToolbar";

    /// <summary>Gets the source of the toolbar, which installs itself once in a document.</summary>
    public static string Source { get; } = Load();

    /// <summary>An expression that calls the toolbar of a document, or answers false when the document has none.</summary>
    /// <param name="call">The call, such as <c>clear()</c>.</param>
    public static string Expression(string call) => $"(globalThis.{GlobalName} ? globalThis.{GlobalName}.{call} : false)";

    private static string Load()
    {
        using var stream = typeof(ToolbarScript).Assembly.GetManifestResourceStream("CodeAlta.Browser.codealta-toolbar.js")
            ?? throw new InvalidOperationException("The CodeAlta toolbar is missing from the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
