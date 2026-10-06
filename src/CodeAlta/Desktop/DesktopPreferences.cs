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
internal sealed record DesktopPreferences(DesktopCloseBehavior OnClose)
{
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
            if (root.TryGetProperty("onClose", out var value) && value.ValueKind == JsonValueKind.String && TryParse(value.GetString(), out var behavior))
                return new(behavior);
            // Written before the question existed, by the switch of the settings: the user had chosen.
            return root.TryGetProperty("closeToTray", out var kept) && kept.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? new(kept.GetBoolean() ? DesktopCloseBehavior.KeepRunning : DesktopCloseBehavior.Exit) : Default;
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
            File.WriteAllText(temporary, "{\"onClose\":\"" + Name(OnClose) + "\"}");
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
