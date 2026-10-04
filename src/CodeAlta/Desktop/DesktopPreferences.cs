using System.Text.Json;

namespace CodeAlta.Desktop;

/// <summary>
/// What the host itself has to know about how the window behaves; kept in the application data root, beside
/// the appearance. The page's own preferences (theme, language, layout) stay in the page.
/// </summary>
/// <param name="CloseToTray">
/// Closing the window hides it and leaves the application running, with its icon in the notification area
/// (the menu bar on macOS); sessions keep running. Off, closing the window exits.
/// </param>
internal sealed record DesktopPreferences(bool CloseToTray)
{
    private const string FileName = "preferences.json";
    private const int MaximumFileBytes = 4096;

    /// <summary>The preferences of a new profile: the application keeps running when its window is closed.</summary>
    internal static DesktopPreferences Default { get; } = new(CloseToTray: true);

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
            if (document.RootElement.ValueKind != JsonValueKind.Object) return Default;
            return document.RootElement.TryGetProperty("closeToTray", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? new(value.GetBoolean()) : Default;
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
            File.WriteAllText(temporary, "{\"closeToTray\":" + (CloseToTray ? "true" : "false") + "}");
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
