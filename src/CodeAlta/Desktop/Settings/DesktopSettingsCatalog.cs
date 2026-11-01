using System.Collections.Frozen;
using System.Text.Json;
using CodeAlta.LiveTool;

namespace CodeAlta.Desktop.Settings;

/// <summary>
/// The built-in settings and the pages of Settings, as <c>builtInSettings.json</c> declares them. The page of the window
/// reads the same file for its search; the host embeds it, so agents and the index read what the window shows.
/// </summary>
/// <remarks>The texts are the English texts of the pages: the window translates them, agents read them as they are.</remarks>
internal sealed class DesktopSettingsCatalog
{
    private const string ResourceName = "CodeAlta.Desktop.Settings.builtInSettings.json";

    private readonly FrozenDictionary<string, AltaSettingDescriptor> _byId;

    private DesktopSettingsCatalog(IReadOnlyList<AltaSettingDescriptor> settings, FrozenDictionary<string, (string Group, string Label)> pages)
    {
        Settings = settings;
        Pages = pages;
        _byId = settings.ToFrozenDictionary(static setting => setting.Id, StringComparer.Ordinal);
    }

    /// <summary>Gets the built-in settings, in the order of the pages.</summary>
    internal IReadOnlyList<AltaSettingDescriptor> Settings { get; }

    /// <summary>Gets the fixed pages of Settings: their group and their name, in English.</summary>
    internal FrozenDictionary<string, (string Group, string Label)> Pages { get; }

    /// <summary>Gets a built-in setting, or null.</summary>
    internal AltaSettingDescriptor? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>Gets the group and the name of a page, in English: <c>Agent &amp; models › Sessions</c>; the id itself for a page the catalog does not know.</summary>
    internal string PathOf(string page) => Pages.TryGetValue(page, out var entry) ? $"{entry.Group} › {entry.Label}" : page;

    /// <summary>Reads the catalog embedded in the application.</summary>
    /// <exception cref="InvalidDataException">The resource is missing or malformed: a build defect.</exception>
    internal static DesktopSettingsCatalog Load()
    {
        using var stream = typeof(DesktopSettingsCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException($"The resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Reads a catalog from its text.</summary>
    /// <exception cref="InvalidDataException">The text is not a catalog.</exception>
    internal static DesktopSettingsCatalog Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            var root = document.RootElement;
            var pages = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
            foreach (var group in root.GetProperty("groups").EnumerateArray())
            {
                var name = group.GetProperty("group").GetString()!;
                foreach (var page in group.GetProperty("pages").EnumerateArray()) pages[page.GetProperty("id").GetString()!] = (name, page.GetProperty("label").GetString()!);
            }

            var frozenPages = pages.ToFrozenDictionary(StringComparer.Ordinal);
            var settings = new List<AltaSettingDescriptor>();
            foreach (var entry in root.GetProperty("settings").EnumerateArray())
            {
                var page = entry.GetProperty("page").GetString()!;
                var path = frozenPages.TryGetValue(page, out var known) ? $"{known.Item1} › {known.Item2}" : page;
                settings.Add(new AltaSettingDescriptor(entry.GetProperty("id").GetString()!, entry.GetProperty("label").GetString()!, Text(entry, "help"), page, path,
                    Text(entry, "section"), entry.GetProperty("kind").GetString()!, Choices(entry), Number(entry, "minimum"), Number(entry, "maximum"),
                    entry.TryGetProperty("default", out var value) ? value.Clone() : null, Text(entry, "agents") ?? AltaSettingAccess.Read, Strings(entry, "keywords"))
                {
                    Owner = Text(entry, "owner") ?? "host",
                    Writable = !entry.TryGetProperty("writable", out var writable) || writable.ValueKind != JsonValueKind.False,
                    ManagedBy = Text(entry, "managedBy"),
                    Applies = Text(entry, "applies") ?? "now",
                    Scopes = entry.TryGetProperty("scopes", out _) ? Strings(entry, "scopes") : ["user"],
                });
            }

            return new DesktopSettingsCatalog(settings, frozenPages);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException("The catalog of the settings is malformed: " + exception.Message, exception);
        }
    }

    private static string? Text(JsonElement entry, string name) => entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Number(JsonElement entry, string name) => entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static string[] Strings(JsonElement entry, string name)
        => entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? [.. value.EnumerateArray().Select(static item => item.GetString() ?? "")] : [];

    // A choice has a label of the window (translated there) or a text shown as it is (a language's own name).
    private static AltaSettingChoice[] Choices(JsonElement entry)
        => entry.TryGetProperty("choices", out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Select(static choice => new AltaSettingChoice(choice.GetProperty("value").GetString()!, Text(choice, "label") ?? Text(choice, "text")))]
            : [];
}
