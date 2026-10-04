using System.Globalization;
using System.Text.Json;
using NeoAstra;

namespace CodeAlta.Desktop;

/// <summary>
/// The theme the window last showed: whether it is dark, and its background. The page reports it; the next
/// start paints the native window in that background, so the window is never white while its view and the
/// application load.
/// </summary>
/// <param name="Dark">Whether the theme is dark.</param>
/// <param name="Background">The background of the workspace.</param>
internal sealed record DesktopAppearance(bool Dark, NeoColor Background)
{
    private const string FileName = "appearance.json";

    /// <summary>The default dark theme, for a first start.</summary>
    internal static DesktopAppearance Default { get; } = new(true, new NeoColor(0x1c, 0x21, 0x27, 0xff));

    /// <summary>The background as <c>#rrggbb</c>.</summary>
    internal string BackgroundText => string.Create(CultureInfo.InvariantCulture, $"#{Background.Red:x2}{Background.Green:x2}{Background.Blue:x2}");

    /// <summary>Creates an appearance from what a page sent: <c>dark</c> or <c>light</c>, and <c>#rrggbb</c>.</summary>
    internal static bool TryCreate(string? theme, string? background, out DesktopAppearance appearance)
    {
        appearance = Default;
        if (theme is not ("dark" or "light") || background is not { Length: 7 } || background[0] != '#'
            || !uint.TryParse(background.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var color)) return false;
        appearance = new(theme == "dark", new NeoColor((byte)(color >> 16), (byte)(color >> 8), (byte)color, 0xff));
        return true;
    }

    /// <summary>
    /// Makes the browser view paint this background where no document has painted yet, instead of white.
    /// It is read when a view is created, so this comes before the first one.
    /// </summary>
    internal void ApplyToBrowser()
    {
        // WebView2 takes its default background from this variable (AARRGGBB); other engines ignore it.
        if (OperatingSystem.IsWindows())
            Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", string.Create(CultureInfo.InvariantCulture, $"FF{Background.Red:X2}{Background.Green:X2}{Background.Blue:X2}"));
    }

    /// <summary>Reads the appearance kept under a data root; the default when there is none or it cannot be read.</summary>
    internal static DesktopAppearance Load(string dataRoot)
    {
        try
        {
            var path = Path.Combine(dataRoot, FileName);
            if (!File.Exists(path) || new FileInfo(path).Length > 1024) return Default;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("theme", out var theme) && theme.ValueKind == JsonValueKind.String
                && root.TryGetProperty("background", out var background) && background.ValueKind == JsonValueKind.String
                && TryCreate(theme.GetString(), background.GetString(), out var appearance) ? appearance : Default;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return Default; // The window starts in the default colors.
        }
    }

    /// <summary>Keeps the appearance under a data root for the next start. A failure only means that start uses the previous one.</summary>
    internal void Save(string dataRoot)
    {
        try
        {
            Directory.CreateDirectory(dataRoot);
            var path = Path.Combine(dataRoot, FileName);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, $$"""{"theme":"{{(Dark ? "dark" : "light")}}","background":"{{BackgroundText}}"}""");
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Not worth failing a theme change for.
        }
    }
}
