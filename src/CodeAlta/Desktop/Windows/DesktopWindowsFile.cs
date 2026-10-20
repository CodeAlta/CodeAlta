using System.Text.Json;
using System.Text.Json.Serialization;
using XenoAtom.Logging;

namespace CodeAlta.Desktop.Windows;

/// <summary>
/// What <c>windows.json</c> keeps of the windows of the application, in the desktop data root beside
/// <c>window.json</c> (the placement of the main window): whether the windows come back at the next start, the
/// name, icon and color of the main window, and every secondary window that was open when the application ended
/// or that the user saved by naming it. The bounds of a secondary window are in its own placement file,
/// <c>window-&lt;id&gt;.json</c>, which NeoAstra writes.
/// </summary>
internal sealed class DesktopWindowsFile
{
    /// <summary>The name of the file in the data root.</summary>
    internal const string FileName = "windows.json";

    /// <summary>The largest file that is read, and the most secondary windows that are kept.</summary>
    internal const int MaximumBytes = 4 * 1024 * 1024, MaximumWindows = 64;

    /// <summary>Gets or sets the version of the format.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Gets or sets whether the secondary windows that were open come back at the next start.</summary>
    public bool RestoreAtStartup { get; set; } = true;

    /// <summary>Gets or sets the name, icon and color of the main window, when the user gave any.</summary>
    public DesktopWindowRecord? Main { get; set; }

    /// <summary>Gets or sets the secondary windows that were open, and the saved ones.</summary>
    public List<DesktopWindowRecord> Windows { get; set; } = [];

    /// <summary>Reads the file of a data root; a missing or unreadable file gives the defaults.</summary>
    /// <param name="dataRoot">The desktop data root.</param>
    internal static DesktopWindowsFile Load(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var path = Path.Combine(dataRoot, FileName);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaximumBytes) return new();
            var file = JsonSerializer.Deserialize(File.ReadAllBytes(path), DesktopWindowsJsonContext.Default.DesktopWindowsFile) ?? new();
            return file.Normalized();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Warn($"The windows of the last run could not be read from {path}: {exception.Message}");
            return new();
        }
    }

    /// <summary>Writes the file of a data root, replacing the one there atomically. Never throws.</summary>
    /// <param name="dataRoot">The desktop data root.</param>
    /// <returns>False when the file could not be written.</returns>
    internal bool Save(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var path = Path.Combine(dataRoot, FileName);
        var temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(dataRoot);
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(this, DesktopWindowsJsonContext.Default.DesktopWindowsFile));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Warn($"The windows could not be written to {path}: {exception.Message}");
            try { File.Delete(temporary); } catch (Exception) { /* Nothing more to do. */ }
            return false;
        }
    }

    // Logging never stops the windows, also where it is not set up (tests).
    private static void Warn(string message)
    {
        try { LogManager.GetLogger("CodeAlta.Desktop").Warn(message); }
        catch (InvalidOperationException) { /* No log to write to. */ }
    }

    // What another build or a hand edit wrote is kept only as far as it is understood.
    private DesktopWindowsFile Normalized()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Windows = [.. (Windows ?? []).Where(window => window is not null && DesktopWindowRecord.IsValidId(window.Id) && seen.Add(window.Id))
            .Select(static window => window.Normalized()).Take(MaximumWindows)];
        Main = Main?.Normalized();
        if (Main is not null) Main.Id = DesktopWindows.MainId;
        return this;
    }
}

/// <summary>A window as <c>windows.json</c> keeps it.</summary>
internal sealed class DesktopWindowRecord
{
    /// <summary>The most tabs a window keeps, and the largest state of its page.</summary>
    internal const int MaximumTabs = 128, MaximumPageState = 512 * 1024;

    /// <summary>Gets or sets the id of the window: <c>main</c>, or letters and digits for a secondary window.</summary>
    public string Id { get; set; } = "";

    /// <summary>Gets or sets the name the user gave the window; null for none.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the icon of the window, one of the icons of the spaces; null for none.</summary>
    public string? Icon { get; set; }

    /// <summary>Gets or sets the color of the window, as <c>#rrggbb</c>; null for none.</summary>
    public string? Color { get; set; }

    /// <summary>Gets or sets whether the window is kept when it is closed: a window the user named is.</summary>
    public bool Saved { get; set; }

    /// <summary>Gets or sets whether the window was open when the application last ended.</summary>
    public bool Open { get; set; }

    /// <summary>Gets or sets the space the window shows; null for the default one.</summary>
    public string? SpaceId { get; set; }

    /// <summary>Gets or sets the display the window goes to whenever it is connected; null for where it was.</summary>
    public string? PinnedDisplayId { get; set; }

    /// <summary>Gets or sets the name of that display, to name it while it is not connected.</summary>
    public string? PinnedDisplayName { get; set; }

    /// <summary>Gets or sets the virtual desktop the window was on, where the platform tells it.</summary>
    public string? VirtualDesktopId { get; set; }

    /// <summary>Gets or sets the tabs of the window, as <c>session:&lt;id&gt;</c>, <c>editor:&lt;project&gt;</c> and so on.</summary>
    public List<string> Tabs { get; set; } = [];

    /// <summary>Gets or sets what the page of the window keeps of its tabs and panes, which only the page reads.</summary>
    public string? PageState { get; set; }

    /// <summary>Whether an id is one a window can have: <c>main</c>, or 1 to 32 letters and digits.</summary>
    internal static bool IsValidId(string? id)
        => id is { Length: > 0 and <= 32 } && (id == DesktopWindows.MainId || id.All(char.IsAsciiLetterOrDigit));

    /// <summary>A copy of the record.</summary>
    internal DesktopWindowRecord Clone() => new()
    {
        Id = Id, Name = Name, Icon = Icon, Color = Color, Saved = Saved, Open = Open, SpaceId = SpaceId, PinnedDisplayId = PinnedDisplayId,
        PinnedDisplayName = PinnedDisplayName, VirtualDesktopId = VirtualDesktopId, Tabs = [.. Tabs], PageState = PageState,
    };

    internal DesktopWindowRecord Normalized()
    {
        Name = Text(Name, 64);
        Icon = Icon is not null && CodeAlta.Catalog.SpaceDescriptor.IsValidIcon(Icon) ? Icon : null;
        Color = Color is not null && CodeAlta.Catalog.SpaceDescriptor.IsValidColor(Color) ? Color : null;
        SpaceId = Text(SpaceId, 128);
        PinnedDisplayId = Text(PinnedDisplayId, 256);
        PinnedDisplayName = PinnedDisplayId is null ? null : Text(PinnedDisplayName, 128);
        VirtualDesktopId = Text(VirtualDesktopId, 128);
        Tabs = [.. (Tabs ?? []).Where(static tab => tab is { Length: > 0 and <= 1024 }).Distinct(StringComparer.Ordinal).Take(MaximumTabs)];
        PageState = PageState is { Length: > 0 and <= MaximumPageState } ? PageState : null;
        return this;
    }

    private static string? Text(string? value, int limit)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) || text.Length > limit || text.Any(char.IsControl) ? null : text;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true)]
[JsonSerializable(typeof(DesktopWindowsFile))]
internal sealed partial class DesktopWindowsJsonContext : JsonSerializerContext;
