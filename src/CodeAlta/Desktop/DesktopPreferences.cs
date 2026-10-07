using System.Text.Json;

namespace CodeAlta.Desktop;

/// <summary>What closing the window does.</summary>
internal enum DesktopCloseBehavior
{
    /// <summary>The user is asked, each time, until an answer is remembered.</summary>
    Ask,

    /// <summary>
    /// The window is hidden and the application keeps running, with its icon in the notification area (the menu
    /// bar on macOS); sessions keep running.
    /// </summary>
    KeepRunning,

    /// <summary>The application exits.</summary>
    Exit,
}

/// <summary>
/// What the host itself has to know about how the window behaves; kept in the application data root, beside
/// the appearance. The page's own preferences (theme, language, layout) stay in the page.
/// </summary>
/// <param name="OnClose">What closing the window does.</param>
/// <param name="McpServer">Whether the MCP server of the application runs: other applications drive the window through it.</param>
/// <param name="SessionWidth">
/// The width of the conversations, in percent of the space of a session: the host keeps it because the
/// <c>alta appearance</c> commands read and change it.
/// </param>
internal sealed record DesktopPreferences(DesktopCloseBehavior OnClose, bool McpServer = true, int SessionWidth = DesktopPreferences.DefaultSessionWidth)
{
    /// <summary>The least width of a conversation, in percent.</summary>
    internal const int MinimumSessionWidth = CodeAlta.LiveTool.IAltaAppearance.MinimumSessionWidth;

    /// <summary>The width of a conversation that takes the whole space of a session.</summary>
    internal const int DefaultSessionWidth = CodeAlta.LiveTool.IAltaAppearance.DefaultSessionWidth;

    /// <summary>Whether a number is a width that can be kept.</summary>
    internal static bool IsSessionWidth(int value) => value is >= MinimumSessionWidth and <= DefaultSessionWidth;

    private const string FileName = "preferences.json";
    private const int MaximumFileBytes = 4096;

    /// <summary>The preferences of a new profile: closing the window asks what to do.</summary>
    internal static DesktopPreferences Default { get; } = new(DesktopCloseBehavior.Ask);

    /// <summary>Reads the preferences; a missing, oversized or malformed file gives the defaults.</summary>
    internal static DesktopPreferences Load(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        try
        {
            var path = Path.Combine(dataRoot, FileName);
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is 0 or > MaximumFileBytes) return Default;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Default;
            // The server runs unless it was turned off: a file written before it existed says nothing about it.
            var server = !(root.TryGetProperty("mcpServer", out var running) && running.ValueKind == JsonValueKind.False);
            // The whole space unless the file says otherwise, with a width that is one.
            var width = root.TryGetProperty("sessionWidth", out var wide) && wide.ValueKind == JsonValueKind.Number && wide.TryGetInt32(out var percent) && IsSessionWidth(percent)
                ? percent : DefaultSessionWidth;
            if (root.TryGetProperty("onClose", out var value) && value.ValueKind == JsonValueKind.String && TryParse(value.GetString(), out var behavior))
                return new(behavior, server, width);
            // Written before the question existed, by the switch of the settings: the user had chosen.
            return root.TryGetProperty("closeToTray", out var kept) && kept.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? new(kept.GetBoolean() ? DesktopCloseBehavior.KeepRunning : DesktopCloseBehavior.Exit, server, width) : Default with { McpServer = server, SessionWidth = width };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return Default;
        }
    }

    /// <summary>Writes the preferences; a failure leaves the previous file, and the choice holds until the application exits.</summary>
    internal bool Save(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        try
        {
            Directory.CreateDirectory(dataRoot);
            var path = Path.Combine(dataRoot, FileName);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            // The server runs unless the file says otherwise: only the choice to turn it off is written.
            File.WriteAllText(temporary, "{\"onClose\":\"" + Name(OnClose) + "\"" + (McpServer ? "" : ",\"mcpServer\":false")
                + (SessionWidth == DefaultSessionWidth ? "" : ",\"sessionWidth\":" + SessionWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)) + "}");
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>The name of a behavior in the file and for the page: <c>ask</c>, <c>keep</c> or <c>exit</c>.</summary>
    internal static string Name(DesktopCloseBehavior behavior) => behavior switch
    {
        DesktopCloseBehavior.KeepRunning => "keep",
        DesktopCloseBehavior.Exit => "exit",
        _ => "ask",
    };

    /// <summary>The behavior that <paramref name="name"/> names; false for anything else.</summary>
    internal static bool TryParse(string? name, out DesktopCloseBehavior behavior)
    {
        behavior = name switch
        {
            "keep" => DesktopCloseBehavior.KeepRunning,
            "exit" => DesktopCloseBehavior.Exit,
            _ => DesktopCloseBehavior.Ask,
        };
        return name is "ask" or "keep" or "exit";
    }
}
