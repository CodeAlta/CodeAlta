using System.Collections.Immutable;
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
/// <param name="Zoom">
/// The zoom of the window's view, in percent: the host keeps it because it applies it to the view before the page
/// loads.
/// </param>
/// <param name="ReviewPermissions">
/// Whether the commands and the file changes of a session are reviewed instead of being approved automatically,
/// when neither the session nor its provider has a permission mode that says it. Off unless the user turned it
/// on: the sessions bypass permissions, as they did before the setting existed. The host keeps it because it is
/// the default permission policy of the sessions it owns, read at every send.
/// </param>
/// <param name="InheritPermissions">
/// Whether a session that another session creates takes the permission mode of its creator. Off unless the user
/// turned it on: such a session does not ask. The host keeps it because it reads it when a session is created.
/// </param>
internal sealed record DesktopPreferences(DesktopCloseBehavior OnClose, bool McpServer = true, int SessionWidth = DesktopPreferences.DefaultSessionWidth,
    int Zoom = DesktopPreferences.DefaultZoom, bool ReviewPermissions = false, bool InheritPermissions = false)
{
    /// <summary>The least width of a conversation, in percent.</summary>
    internal const int MinimumSessionWidth = CodeAlta.LiveTool.IAltaAppearance.MinimumSessionWidth;

    /// <summary>The width of a conversation that takes the whole space of a session.</summary>
    internal const int DefaultSessionWidth = CodeAlta.LiveTool.IAltaAppearance.DefaultSessionWidth;

    /// <summary>Whether a number is a width that can be kept.</summary>
    internal static bool IsSessionWidth(int value) => value is >= MinimumSessionWidth and <= DefaultSessionWidth;

    /// <summary>The zoom of a view that is not zoomed, in percent.</summary>
    internal const int DefaultZoom = 100;

    // The steps of the zoom keys: Chrome's preset zoom levels, in whole percent. The first and the last are the
    // bounds of a zoom that is kept, and NeoAstra's own bounds of a zoom factor.
    private static readonly ImmutableArray<int> ZoomSteps = [25, 33, 50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200, 250, 300, 400, 500];

    /// <summary>Whether a number is a zoom that can be kept.</summary>
    internal static bool IsZoom(int value) => value >= ZoomSteps[0] && value <= ZoomSteps[^1];

    /// <summary>
    /// The zoom one step above (<paramref name="direction"/> 1) or below (-1) <paramref name="zoom"/>, within the
    /// bounds; 0 resets it.
    /// </summary>
    internal static int NextZoom(int zoom, int direction) => direction switch
    {
        > 0 => ZoomSteps.FirstOrDefault(step => step > zoom, ZoomSteps[^1]),
        < 0 => ZoomSteps.LastOrDefault(step => step < zoom, ZoomSteps[0]),
        _ => DefaultZoom,
    };

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
            // Approved automatically unless the file says otherwise: only the choice to review is written.
            var review = root.TryGetProperty("reviewPermissions", out var reviewed) && reviewed.ValueKind == JsonValueKind.True;
            // A session another session creates does not ask unless the file says otherwise.
            var inherit = root.TryGetProperty("inheritPermissions", out var inherited) && inherited.ValueKind == JsonValueKind.True;
            var zoom = root.TryGetProperty("zoom", out var zoomed) && zoomed.ValueKind == JsonValueKind.Number && zoomed.TryGetInt32(out var factor) && IsZoom(factor)
                ? factor : DefaultZoom;
            if (root.TryGetProperty("onClose", out var value) && value.ValueKind == JsonValueKind.String && TryParse(value.GetString(), out var behavior))
                return new(behavior, server, width, zoom, review, inherit);
            // Written before the question existed, by the switch of the settings: the user had chosen.
            return root.TryGetProperty("closeToTray", out var kept) && kept.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? new(kept.GetBoolean() ? DesktopCloseBehavior.KeepRunning : DesktopCloseBehavior.Exit, server, width, zoom, review, inherit)
                : Default with { McpServer = server, SessionWidth = width, Zoom = zoom, ReviewPermissions = review, InheritPermissions = inherit };
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
                + (SessionWidth == DefaultSessionWidth ? "" : ",\"sessionWidth\":" + SessionWidth.ToString(System.Globalization.CultureInfo.InvariantCulture))
                + (Zoom == DefaultZoom ? "" : ",\"zoom\":" + Zoom.ToString(System.Globalization.CultureInfo.InvariantCulture))
                + (ReviewPermissions ? ",\"reviewPermissions\":true" : "") + (InheritPermissions ? ",\"inheritPermissions\":true" : "") + "}");
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
