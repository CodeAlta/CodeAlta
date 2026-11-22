// Checks the file citations of analyses: every `path` or `path:line` in backticks must exist in the repository,
// and the line must be inside the file. A citation that fails marks a claim to distrust.
// A path without a line that does not exist is listed and not counted: it may be a file to create.
//
//   dotnet run check_refs.cs -- <analysis.md>... [--repo <folder>]
//
// Exit code 1 when a citation fails.
using System.Diagnostics;
using System.Text.RegularExpressions;

var token = new Regex(@"`([^`\s]+)`");
var citation = new Regex(@"^(?<path>[\w.@+\-/\\\[\]()]+?\.[A-Za-z0-9]{1,10})(?::(?<a>\d+)(?:-(?<b>\d+))?)?$");

string? repo = null;
var files = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--repo" && i + 1 < args.Length)
    {
        repo = args[++i];
    }
    else if (args[i] is "-h" or "--help")
    {
        Console.WriteLine("dotnet run check_refs.cs -- <analysis.md>... [--repo <folder>]\nA name with * or ? stands for the files of its folder that match.");
        return 0;
    }
    else
    {
        files.AddRange(Expand(args[i]));
    }
}

if (files.Count == 0)
{
    Console.Error.WriteLine("no file to check: name the analyses, or a pattern that matches them");
    return 1;
}

// The repository is the one the analyses are in: the script itself runs from the folder of the skill.
var root = Path.GetFullPath(repo ?? GitRoot(files.Count > 0 ? Path.GetDirectoryName(Path.GetFullPath(files[0]))! : ".") ?? ".");
int failed = 0, verified = 0;
foreach (var name in files)
{
    string[] lines;
    try
    {
        lines = File.ReadAllLines(name);
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"cannot read {name}: {e.Message}");
        return 1;
    }

    for (var number = 1; number <= lines.Length; number++)
    {
        foreach (Match found in token.Matches(lines[number - 1]))
        {
            var text = found.Groups[1].Value;
            var m = citation.Match(text.TrimEnd('.', ',', ';'));
            if (!m.Success)
            {
                continue;
            }

            var cited = m.Groups["path"].Value.Replace('\\', '/');
            var path = Path.Combine(root, cited);
            // A name without a folder is a citation only when that file is at the root: `obj.field:80` is not one.
            if (!cited.Contains('/') && !File.Exists(path))
            {
                continue;
            }

            verified++;
            var hasLine = m.Groups["a"].Success;
            if (!File.Exists(path))
            {
                // Without a line it may be a file the analysis proposes: listed, not counted.
                failed += hasLine ? 1 : 0;
                Console.WriteLine($"{name}:{number}: `{text}`: no such file" + (hasLine ? "" : " (no line cited: a proposed file?)"));
                continue;
            }

            if (!hasLine)
            {
                continue;
            }

            var first = int.Parse(m.Groups["a"].Value);
            var last = m.Groups["b"].Success ? int.Parse(m.Groups["b"].Value) : first;
            var data = File.ReadAllBytes(path);
            var count = data.AsSpan().Count((byte)'\n') + (data.Length > 0 && data[^1] != (byte)'\n' ? 1 : 0);
            if (first < 1 || last < first)
            {
                failed++;
                Console.WriteLine($"{name}:{number}: `{text}`: not a line range");
            }
            else if (last > count)
            {
                failed++;
                Console.WriteLine($"{name}:{number}: `{text}`: the file has {count} lines");
            }
        }
    }
}

Console.WriteLine($"{verified} citations checked in {files.Count} file(s), {failed} failed");
return failed > 0 ? 1 : 0;

// Shells that do not expand `*` (PowerShell, cmd) pass the pattern as it is.
static IEnumerable<string> Expand(string pattern)
{
    var name = Path.GetFileName(pattern);
    if (!name.Contains('*') && !name.Contains('?'))
    {
        return [pattern];
    }

    var folder = Path.GetDirectoryName(pattern) is { Length: > 0 } d ? d : ".";
    return Directory.Exists(folder) ? Directory.GetFiles(folder, name).Order(StringComparer.Ordinal) : [];
}

static string? GitRoot(string folder)
{
    try
    {
        var info = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-C", folder, "rev-parse", "--show-toplevel" })
        {
            info.ArgumentList.Add(a);
        }

        using var process = Process.Start(info)!;
        var text = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return process.ExitCode == 0 && text.Length > 0 ? text : null;
    }
    catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
    {
        return null;
    }
}
