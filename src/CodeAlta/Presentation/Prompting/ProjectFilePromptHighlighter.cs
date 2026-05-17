using System.Text;
using CodeAlta.Catalog;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Text;
using XenoAtom.Ansi;
using XenoAtom.Terminal.UI;

namespace CodeAlta.Presentation.Prompting;

internal static class ProjectFilePromptHighlighter
{
    public static void AddRuns(string? text, string? projectRoot, Theme theme, List<StyledRun> runs)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(runs);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        foreach (var token in ProjectFilePromptReferenceParser.Parse(text))
        {
            if (token.Kind != ProjectFilePromptTokenKind.Reference)
            {
                continue;
            }

            var style = IsResolved(projectRoot, token)
                ? Style.None.WithForeground(theme.Primary ?? theme.Accent ?? theme.Foreground ?? Color.Default)
                : Style.None.WithForeground(theme.Error ?? theme.Warning ?? theme.Foreground ?? Color.Default);
            runs.Add(new StyledRun(token.StartIndex, token.Length, style));
        }
    }

    private static bool IsResolved(string? projectRoot, ProjectFilePromptToken token)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) ||
            token.IsMalformed ||
            string.IsNullOrWhiteSpace(token.LookupText))
        {
            return false;
        }

        try
        {
            var normalizedPath = NormalizeLookupPath(token.LookupText!);
            var fullPath = Path.Combine(
                Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                normalizedPath.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(fullPath) || Directory.Exists(fullPath);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string NormalizeLookupPath(string path)
    {
        var trimmed = path.Trim().Replace('\\', '/');
        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0 ||
            segments.Any(static segment => segment is "." or ".."))
        {
            throw new ArgumentException("Path must be project-relative.", nameof(path));
        }

        return string.Join('/', segments);
    }
}
