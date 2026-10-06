using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The user's color schemes of the desktop window: one JSON file per scheme in the <c>color-schemes</c> folder
/// of the CodeAlta root (<c>~/.alta/color-schemes</c>). A scheme names the built-in scheme it starts from and
/// the colors it chooses for the light, the dark and the darker theme; the page makes the palette from them.
/// </summary>
/// <remarks>
/// The service only reads, checks, writes and removes these files. It needs no host: the window shows its
/// colors before a host exists, and while a configuration file is being repaired.
/// </remarks>
[NeoRpcService("colorSchemes", Version = 1)]
internal sealed class ColorSchemesService
{
    /// <summary>The folder of the schemes under the CodeAlta root.</summary>
    internal const string FolderName = "color-schemes";

    /// <summary>Largest number of schemes one listing returns.</summary>
    internal const int MaximumSchemes = 64;

    /// <summary>Largest scheme file that is read, in bytes.</summary>
    internal const int MaximumFileBytes = 16 * 1024;

    /// <summary>Largest name of a scheme, in UTF-16 units.</summary>
    internal const int MaximumNameLength = 64;

    private const int MaximumIdLength = 64;
    private const int MaximumProblems = 16;
    private const int MaximumMessageLength = 256;
    private const string Extension = ".json";
    private const string DefaultBase = "blueprint";
    private static readonly string[] Themes = ["light", "dark", "darker"];
    private static readonly string[] ColorNames = ["background", "text", "muted", "accent", "success", "warning", "danger"];
    private static readonly string[] ReservedNames = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    private readonly string? _directory;
    private readonly Func<string, bool> _reveal;
    private readonly Lock _gate = new(); // The folder takes one operation at a time.

    /// <summary>Creates an unavailable service for a window without a CodeAlta root.</summary>
    internal ColorSchemesService()
    {
        _reveal = static _ => false;
    }

    /// <summary>Creates the service over the schemes of a CodeAlta root.</summary>
    /// <param name="root">The CodeAlta root (<c>~/.alta</c>); the schemes are in its <c>color-schemes</c> folder.</param>
    /// <param name="reveal">Shows a file or a folder in the file manager; the one of the system by default.</param>
    /// <exception cref="ArgumentException"><paramref name="root"/> is blank.</exception>
    internal ColorSchemesService(string root, Func<string, bool>? reveal = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _directory = Path.Combine(Path.GetFullPath(root), FolderName);
        _reveal = reveal ?? DesktopFileReveal.Show;
    }

    /// <summary>
    /// Lists the schemes, by file name. A file that is not a scheme is not listed: it is reported with the
    /// reason, so that someone who writes a file by hand sees what to correct.
    /// </summary>
    [NeoRpcMethod("list")]
    public ColorSchemesListResponse List(ColorSchemesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_directory is null) return new("unavailable", null, [], []);
        var schemes = new List<ColorSchemeDocument>();
        var problems = new List<ColorSchemeProblem>();
        void Report(string file, string message)
        {
            if (problems.Count < MaximumProblems) problems.Add(new(Bound(file), Bound(message)));
        }

        try
        {
            lock (_gate)
            {
                if (!Directory.Exists(_directory)) return new("ok", _directory, [], []);
                var files = Directory.EnumerateFiles(_directory, "*" + Extension, SearchOption.TopDirectoryOnly)
                    .Select(static path => Path.GetFileName(path))
                    // The pattern also matches longer extensions on some systems; staging files start with a dot.
                    .Where(static name => name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) && !name.StartsWith('.'))
                    // By id, not by file name: "night" comes before "night-2".
                    .OrderBy(static name => name[..^Extension.Length], StringComparer.OrdinalIgnoreCase).ToArray();
                foreach (var file in files)
                {
                    if (schemes.Count == MaximumSchemes)
                    {
                        Report(file, $"More than {MaximumSchemes} color schemes: this one and the following are not listed.");
                        break;
                    }

                    var id = file[..^Extension.Length];
                    if (IdProblem(id) is { } refusal) Report(file, refusal);
                    else if (Read(Path.Combine(_directory, file), id, out var problem) is { } scheme) schemes.Add(scheme);
                    else Report(file, problem!);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new("read_failed", _directory, [], []);
        }

        return new("ok", _directory, schemes, problems);
    }

    /// <summary>
    /// Writes a scheme. Without an id it is a new one, and its file is named after its name; with the id of a
    /// listed scheme it replaces that file. <c>invalid</c> carries the reason.
    /// </summary>
    [NeoRpcMethod("save")]
    public ColorSchemeSaveResponse Save(ColorSchemeSaveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_directory is null) return new("unavailable", null, null);
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0) return new("invalid", null, "A color scheme needs a name.");
        if (name.Length > MaximumNameLength || name.Any(char.IsControl) || !IsWellFormed(name))
            return new("invalid", null, $"The name of a color scheme is at most {MaximumNameLength} characters on one line.");
        var basis = string.IsNullOrWhiteSpace(request.Base) ? DefaultBase : request.Base.Trim();
        if (!IsBase(basis)) return new("invalid", null, "The base of a color scheme is the id of a built-in scheme, such as blueprint.");
        if (request.Id is not null && IdProblem(request.Id) is { } refusal) return new("invalid", null, refusal);
        var themes = new ColorSchemeColors?[] { request.Light, request.Dark, request.Darker };
        var colors = new string?[Themes.Length][];
        for (var theme = 0; theme < Themes.Length; theme++)
        {
            colors[theme] = Values(themes[theme]);
            for (var color = 0; color < ColorNames.Length; color++)
            {
                if (colors[theme][color] is not { } value) continue;
                if (!TryNormalizeColor(value, out var normalized))
                    return new("invalid", null, $"{Themes[theme]}.{ColorNames[color]} is not a color such as #1a2b3c.");
                colors[theme][color] = normalized;
            }
        }

        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                var id = request.Id ?? NewId(name);
                if (id is null) return new("conflict", null, null);
                var path = Path.Combine(_directory, id + Extension);
                // Staged beside the file and moved over it: a reader never sees half a scheme.
                var staging = Path.Combine(_directory, $".{id}.{Guid.NewGuid():N}.tmp");
                try
                {
                    File.WriteAllBytes(staging, Render(name, basis, colors));
                    File.Move(staging, path, overwrite: true);
                }
                finally
                {
                    if (File.Exists(staging)) File.Delete(staging);
                }

                return new("ok", id, null);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new("write_failed", null, null);
        }
    }

    /// <summary>Removes the file of a scheme. <c>not_found</c> when there is none.</summary>
    [NeoRpcMethod("delete")]
    public ColorSchemeChangeResponse Delete(ColorSchemeIdRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_directory is null) return new("unavailable", null);
        if (request.Id is null || IdProblem(request.Id) is not null) return new("invalid", "This is not the id of a color scheme.");
        try
        {
            lock (_gate)
            {
                var path = Path.Combine(_directory, request.Id + Extension);
                if (!File.Exists(path)) return new("not_found", null);
                File.Delete(path);
                return new("ok", null);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new("write_failed", null);
        }
    }

    /// <summary>
    /// Shows the file of a scheme in the file manager of the system, or the folder of the schemes when no id
    /// is given. The folder is created when it does not exist yet, so that there is a place to put a file.
    /// </summary>
    [NeoRpcMethod("reveal")]
    public ColorSchemeChangeResponse Reveal(ColorSchemeIdRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_directory is null) return new("unavailable", null);
        if (request.Id is not null && IdProblem(request.Id) is not null) return new("invalid", "This is not the id of a color scheme.");
        try
        {
            string target;
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                target = request.Id is null ? _directory : Path.Combine(_directory, request.Id + Extension);
                if (request.Id is not null && !File.Exists(target)) return new("not_found", null);
            }

            return _reveal(target) ? new("ok", null) : new("failed", null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new("failed", null);
        }
    }

    // Reads one scheme file; null with the reason when it is not a scheme.
    private static ColorSchemeDocument? Read(string path, string id, out string? problem)
    {
        problem = null;
        byte[] bytes;
        try
        {
            if (new FileInfo(path).Length > MaximumFileBytes)
            {
                problem = $"The file is larger than {MaximumFileBytes / 1024} KB.";
                return null;
            }

            bytes = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            problem = "The file could not be read.";
            return null;
        }

        try
        {
            // Written by hand as well as by the application: comments and a trailing comma are fine.
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                problem = "A color scheme is a JSON object.";
                return null;
            }

            var name = id;
            if (root.TryGetProperty("name", out var nameValue))
            {
                if (nameValue.ValueKind != JsonValueKind.String || nameValue.GetString()!.Trim() is not { Length: > 0 and <= MaximumNameLength } text || text.Any(char.IsControl))
                {
                    problem = $"name is a text of at most {MaximumNameLength} characters.";
                    return null;
                }

                name = text;
            }

            var basis = DefaultBase;
            if (root.TryGetProperty("base", out var baseValue))
            {
                if (baseValue.ValueKind != JsonValueKind.String || !IsBase(baseValue.GetString()!))
                {
                    problem = "base is the id of a built-in scheme, such as blueprint.";
                    return null;
                }

                basis = baseValue.GetString()!;
            }

            var colors = new ColorSchemeColors[Themes.Length];
            for (var theme = 0; theme < Themes.Length; theme++)
            {
                var values = new string?[ColorNames.Length];
                if (root.TryGetProperty(Themes[theme], out var section))
                {
                    if (section.ValueKind != JsonValueKind.Object)
                    {
                        problem = $"{Themes[theme]} is an object of colors.";
                        return null;
                    }

                    for (var color = 0; color < ColorNames.Length; color++)
                    {
                        if (!section.TryGetProperty(ColorNames[color], out var value) || value.ValueKind == JsonValueKind.Null) continue;
                        if (value.ValueKind != JsonValueKind.String || !TryNormalizeColor(value.GetString()!, out var normalized))
                        {
                            problem = $"{Themes[theme]}.{ColorNames[color]} is not a color such as #1a2b3c.";
                            return null;
                        }

                        values[color] = normalized;
                    }
                }

                colors[theme] = Colors(values);
            }

            return new(id, name, basis, colors[0], colors[1], colors[2]);
        }
        catch (JsonException exception)
        {
            problem = exception.LineNumber is { } line ? $"The file is not valid JSON (line {line + 1})." : "The file is not valid JSON.";
            return null;
        }
    }

    // The file of a scheme: its name, its base and the colors it chooses, a theme without one left out.
    private static byte[] Render(string name, string basis, string?[][] colors)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("name", name);
            writer.WriteString("base", basis);
            for (var theme = 0; theme < Themes.Length; theme++)
            {
                if (colors[theme].All(static value => value is null)) continue;
                writer.WriteStartObject(Themes[theme]);
                for (var color = 0; color < ColorNames.Length; color++)
                    if (colors[theme][color] is { } value) writer.WriteString(ColorNames[color], value);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    // An id for a new scheme, from its name: "Deep Sea" is deep-sea, then deep-sea-2 while that file exists.
    private string? NewId(string name)
    {
        var builder = new StringBuilder();
        foreach (var character in name.Normalize(NormalizationForm.FormKD))
        {
            if (builder.Length == 48) break;
            if (char.IsAsciiLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
            else if (builder.Length > 0 && builder[^1] != '-' && (char.IsWhiteSpace(character) || character is '-' or '_' or '.')) builder.Append('-');
        }

        var stem = builder.ToString().Trim('-');
        if (stem.Length == 0 || IdProblem(stem) is not null) stem = "scheme";
        for (var number = 1; number <= 99; number++)
        {
            var id = number == 1 ? stem : string.Create(CultureInfo.InvariantCulture, $"{stem}-{number}");
            if (!File.Exists(Path.Combine(_directory!, id + Extension))) return id;
        }

        return null;
    }

    // An id is the name of the scheme's file without its extension: it must be a plain file name everywhere.
    private static string? IdProblem(string id)
    {
        const string rule = "The file name of a color scheme has 1 to 64 letters, digits, dots, dashes or underscores and starts with a letter or a digit.";
        if (id.Length is 0 or > MaximumIdLength || !char.IsAsciiLetterOrDigit(id[0])) return rule;
        foreach (var character in id)
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_')) return rule;
        var device = id.Split('.')[0];
        return ReservedNames.Contains(device, StringComparer.OrdinalIgnoreCase) ? "This file name is reserved on Windows." : null;
    }

    private static bool IsBase(string value)
    {
        if (value.Length is 0 or > MaximumIdLength || !char.IsAsciiLetterLower(value[0])) return false;
        foreach (var character in value)
            if (!char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) && character != '-') return false;
        return true;
    }

    /// <summary>Normalizes <c>#rgb</c> or <c>#rrggbb</c>, in either case, to lowercase <c>#rrggbb</c>.</summary>
    internal static bool TryNormalizeColor(string value, out string color)
    {
        color = string.Empty;
        var text = value.AsSpan().Trim();
        if (text.Length is not (4 or 7) || text[0] != '#') return false;
        foreach (var digit in text[1..])
            if (!char.IsAsciiHexDigit(digit)) return false;
        color = text.Length == 7
            ? text.ToString().ToLowerInvariant()
            : string.Create(7, text.ToString(), static (span, source) =>
            {
                span[0] = '#';
                for (var index = 0; index < 3; index++) span[1 + 2 * index] = span[2 + 2 * index] = char.ToLowerInvariant(source[1 + index]);
            });
        return true;
    }

    private static bool IsWellFormed(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])) index++;
            else if (char.IsSurrogate(text[index])) return false;
        }

        return true;
    }

    private static string?[] Values(ColorSchemeColors? colors) => colors is null
        ? new string?[ColorNames.Length]
        : [Blank(colors.Background), Blank(colors.Text), Blank(colors.Muted), Blank(colors.Accent), Blank(colors.Success), Blank(colors.Warning), Blank(colors.Danger)];

    private static ColorSchemeColors Colors(string?[] values) => new(values[0], values[1], values[2], values[3], values[4], values[5], values[6]);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string Bound(string text) => text.Length > MaximumMessageLength ? text[..MaximumMessageLength] : text;
}

internal sealed record ColorSchemesRequest;

/// <summary>
/// The colors a scheme chooses for one theme, each as <c>#rrggbb</c>. A color that is null is left to the
/// scheme's base (for the darker theme, to its dark theme).
/// </summary>
/// <param name="Background">The window background; the other surfaces follow it.</param>
/// <param name="Text">The text.</param>
/// <param name="Muted">Muted text; icons and borders follow it.</param>
/// <param name="Accent">Buttons, links and selections.</param>
/// <param name="Success">What succeeded or was added.</param>
/// <param name="Warning">What needs attention.</param>
/// <param name="Danger">What failed or was removed.</param>
internal sealed record ColorSchemeColors(string? Background, string? Text, string? Muted, string? Accent, string? Success, string? Warning, string? Danger);

/// <param name="Id">The name of the scheme's file without its extension.</param>
/// <param name="Name">The name shown for the scheme.</param>
/// <param name="Base">The id of the built-in scheme it starts from.</param>
/// <param name="Light">The colors chosen for the light theme.</param>
/// <param name="Dark">The colors chosen for the dark theme.</param>
/// <param name="Darker">The colors chosen for the darker theme, which otherwise follows the dark one.</param>
internal sealed record ColorSchemeDocument(string Id, string Name, string Base, ColorSchemeColors Light, ColorSchemeColors Dark, ColorSchemeColors Darker);

/// <summary>A file of the folder that is not a scheme, and why.</summary>
internal sealed record ColorSchemeProblem(string File, string Message);

/// <param name="Status"><c>ok</c>, <c>unavailable</c> or <c>read_failed</c>.</param>
/// <param name="Directory">The folder of the schemes; null when unavailable. It may not exist yet.</param>
/// <param name="Schemes">The schemes, by file name.</param>
/// <param name="Problems">The files that are not schemes.</param>
internal sealed record ColorSchemesListResponse(string Status, string? Directory, IReadOnlyList<ColorSchemeDocument> Schemes, IReadOnlyList<ColorSchemeProblem> Problems);

/// <param name="Id">The scheme to replace; null for a new scheme.</param>
/// <param name="Name">The name shown for the scheme.</param>
/// <param name="Base">The id of the built-in scheme it starts from; <c>blueprint</c> when blank.</param>
/// <param name="Light">The colors chosen for the light theme.</param>
/// <param name="Dark">The colors chosen for the dark theme.</param>
/// <param name="Darker">The colors chosen for the darker theme.</param>
internal sealed record ColorSchemeSaveRequest(string? Id, string? Name, string? Base, ColorSchemeColors? Light, ColorSchemeColors? Dark, ColorSchemeColors? Darker);

/// <param name="Status"><c>ok</c>, <c>invalid</c>, <c>conflict</c> (no free file name), <c>write_failed</c> or <c>unavailable</c>.</param>
/// <param name="Id">The id of the scheme that was written; null unless <c>ok</c>.</param>
/// <param name="Message">Why the values were not accepted; null otherwise.</param>
internal sealed record ColorSchemeSaveResponse(string Status, string? Id, string? Message);

/// <param name="Id">The id of a scheme; null where the folder itself is meant.</param>
internal sealed record ColorSchemeIdRequest(string? Id);

/// <param name="Status"><c>ok</c>, <c>invalid</c>, <c>not_found</c>, <c>write_failed</c>, <c>failed</c> or <c>unavailable</c>.</param>
/// <param name="Message">Why the request was not accepted; null otherwise.</param>
internal sealed record ColorSchemeChangeResponse(string Status, string? Message);
