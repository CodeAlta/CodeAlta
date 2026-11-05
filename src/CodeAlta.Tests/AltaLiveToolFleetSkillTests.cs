using System.Text.RegularExpressions;
using CodeAlta.Catalog;
using CodeAlta.LiveTool;

namespace CodeAlta.Tests;

/// <summary>The built-in skill <c>codealta-fleet</c> names only commands and options of the <c>alta</c> tree, so it cannot go stale.</summary>
public sealed partial class AltaLiveToolTests
{
    [TestMethod]
    public void FleetSkill_NamesOnlyCommandsAndOptionsOfTheTree()
    {
        var skill = File.ReadAllText(FleetSkillPath());
        StringAssert.Contains(skill, "name: codealta-fleet");
        StringAssert.Contains(skill, "`alta fleet --help`");
        StringAssert.Contains(skill, "never a way out of a sandbox");

        using var root = TempDirectory.Create();
        var registry = new AltaCommandRegistry();
        var dispatcher = new AltaCommandDispatcher(registry, new AltaServiceCollection().Add(new CatalogOptions { GlobalRoot = root.Path }).Add(registry)
            .Add<IAltaFleet>(new FakeFleet()).Add<IAltaAutomations>(new FakeAutomations()).Add<IAltaSessionMachineMoves>(new RecordingMachineMoves()));
        var commands = dispatcher.Describe(AltaCallerIdentity.Cli, root.Path).ToDictionary(static command => command.Path, StringComparer.Ordinal);
        var named = Regex.Matches(skill, "`alta ([^`]+)`").Select(static match => match.Groups[1].Value).ToArray();
        Assert.IsGreaterThan(15, named.Length, "the skill explains the important commands");
        foreach (var text in named)
        {
            var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var words = tokens.TakeWhile(static token => !token.StartsWith('-') && !token.StartsWith('<')).ToArray();
            // The longest leading words that are a command; the words after it are its arguments.
            var length = Enumerable.Range(1, words.Length).Reverse().FirstOrDefault(count => commands.ContainsKey(string.Join(' ', words.Take(count))));
            if (length == 0)
            {
                var group = string.Join(' ', words);
                Assert.IsTrue(commands.Keys.Any(path => path.StartsWith(group + " ", StringComparison.Ordinal)), $"`alta {text}` names no command of the tree");
                continue;
            }

            var command = commands[string.Join(' ', words.Take(length))];
            Assert.IsTrue(words.Length - length <= command.Arguments.Count || command.Arguments.Any(static argument => argument.IsList), $"`alta {text}` names no command of the tree");
            var options = command.Options.SelectMany(static option => option.Aliases.Prepend(option.Name)).ToHashSet(StringComparer.Ordinal);
            foreach (var option in tokens.Where(static token => token.StartsWith("--", StringComparison.Ordinal) && token != "--help"))
                Assert.IsTrue(options.Contains(option[2..]), $"`alta {text}`: `{command.Path}` has no option {option}");
        }
    }

    private static string FleetSkillPath()
    {
        var copied = Path.Combine(AppContext.BaseDirectory, "BuiltinSkills", "codealta-fleet", "SKILL.md");
        if (File.Exists(copied)) return copied;
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var source = Path.Combine(folder.FullName, "CodeAlta.Catalog", "BuiltinSkills", "codealta-fleet", "SKILL.md");
            if (File.Exists(source)) return source;
        }

        throw new FileNotFoundException("The skill codealta-fleet was not found.");
    }
}
