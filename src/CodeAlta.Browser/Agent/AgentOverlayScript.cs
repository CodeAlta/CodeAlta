namespace CodeAlta.Browser.Agent;

/// <summary>
/// The overlay that shows the user where an agent acts in a tab of a browser with a window: a cursor, a ring where it
/// presses, a glow around the page and a label. It runs in an isolated world of its own, neither the agent's nor the
/// toolbar's, so that none of them sees the others, and the page sees none.
/// </summary>
internal static class AgentOverlayScript
{
    /// <summary>The name of the isolated world the overlay runs in.</summary>
    public const string WorldName = "codealta-agent-overlay";

    /// <summary>The global object of the overlay in its world.</summary>
    public const string GlobalName = "__codealtaAgentOverlay";

    /// <summary>
    /// How long the glow and the label stay after the last action of the agent: they span the time an agent thinks
    /// between two steps, and leave a page soon after it is done.
    /// </summary>
    public static readonly TimeSpan ActivityDuration = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long an input waits at most for the cursor to arrive where it acts: the overlay itself resolves within 600 ms,
    /// and a page too busy to answer is not waited for longer.
    /// </summary>
    public static readonly TimeSpan ArrivalLimit = TimeSpan.FromMilliseconds(800);

    /// <summary>How long any other call of the overlay may take: it never holds a tool longer.</summary>
    public static readonly TimeSpan CallLimit = TimeSpan.FromSeconds(1);

    /// <summary>Gets the source of the overlay, which installs itself once in the document of a tab.</summary>
    public static string Source { get; } = Load();

    /// <summary>An expression that calls the overlay of a document, or answers null when the world has none.</summary>
    /// <param name="call">The call, such as <c>press()</c>.</param>
    public static string Expression(string call) => $"(globalThis.{GlobalName} ? globalThis.{GlobalName}.{call} : null)";

    private static string Load()
    {
        using var stream = typeof(AgentOverlayScript).Assembly.GetManifestResourceStream("CodeAlta.Browser.codealta-agent-overlay.js")
            ?? throw new InvalidOperationException("The agent overlay is missing from the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
