using System.Text.RegularExpressions;

namespace CodeAlta.Catalog.Tests;

/// <summary>
/// The built-in skill <c>improve-codebase-health</c>: every file and script its instructions name is in its folder.
/// What its scripts do is tested in <c>CodeAlta.Plugins.Tests</c>, with the other builds of C# files.
/// </summary>
[TestClass]
public sealed class HealthSkillTests
{
    [TestMethod]
    public void TheSkill_NamesOnlyFilesItHas()
    {
        var skillRoot = Path.Combine(AppContext.BaseDirectory, "BuiltinSkills", "improve-codebase-health");
        var missing = new List<string>();
        foreach (var file in Directory.GetFiles(skillRoot, "*.md"))
        {
            var text = File.ReadAllText(file);
            // A link to a file of the skill: a relative path with an extension, not a URL and not a placeholder.
            var links = Regex.Matches(text, @"\]\((?<path>[^)#\s:]+\.\w+)\)").Select(m => m.Groups["path"].Value);
            // A script, as a command runs it or as the text names it.
            var scripts = Regex.Matches(text, @"(?:dotnet run |`)(?<name>\w+\.cs)\b").Select(m => "scripts/" + m.Groups["name"].Value);
            foreach (var path in links.Concat(scripts).Distinct())
            {
                if (!File.Exists(Path.Combine(skillRoot, path)))
                {
                    missing.Add($"{Path.GetFileName(file)}: {path}");
                }
            }
        }

        Assert.AreEqual(0, missing.Count, string.Join(Environment.NewLine, missing));
    }
}
