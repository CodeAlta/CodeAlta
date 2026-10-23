namespace CodeAlta.Plugin.Mobile;

/// <summary>
/// The spaces the Mobile plugin is turned on in, as Settings &gt; Plugins saves them: <c>spaces</c> of
/// <c>[plugins.mobile]</c> in the global configuration (<c>~/.alta/config.toml</c>). Plugins have no service that reads
/// them, so the file is read, never written. The server is one per computer, so a space cannot start or stop it: the
/// spaces choose the sessions the phone lists and opens.
/// </summary>
internal static class MobileSpaces
{
    /// <summary>The ids of the spaces, or null when the plugin is in every space (no list, an empty one, or <c>default</c>, which holds every project).</summary>
    internal static IReadOnlyList<string>? Read()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".alta", "config.toml");
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Whether <c>[plugins.mobile] enabled</c> leaves the plugin on: true unless it says <c>false</c>.</summary>
    internal static bool Enabled()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".alta", "config.toml");
            return !File.Exists(path) || Value(File.ReadAllText(path), "enabled")?.Split('#')[0].Trim() != "false";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>Reads <c>spaces = [..]</c> of the <c>[plugins.mobile]</c> table; a list over lines too.</summary>
    internal static IReadOnlyList<string>? Parse(string toml)
    {
        var value = Value(toml, "spaces");
        if (value is null) return null;
        var ids = value.Split([',', '[', ']'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static item => item.Split('#')[0].Trim().Trim('"', '\'')).Where(static item => item.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        return ids.Count == 0 || ids.Contains("default", StringComparer.OrdinalIgnoreCase) ? null : ids;
    }

    // The raw value of a key of the [plugins.mobile] table; a list is read until its closing bracket.
    private static string? Value(string toml, string key)
    {
        var inTable = false;
        string? value = null;
        var lines = toml.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('[')) { inTable = line is "[plugins.mobile]" or "[plugins.\"mobile\"]"; continue; }
            if (!inTable || !line.StartsWith(key, StringComparison.Ordinal)) continue;
            var eq = line.IndexOf('=');
            if (eq < 0 || line[..eq].Trim() != key) continue;
            value = line[(eq + 1)..].Trim();
            if (value.StartsWith('[')) while (!value.Contains(']') && i + 1 < lines.Length) value += lines[++i];
            break;
        }

        return value;
    }
}
