using System.Text.Json;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.CommandLine;
// XenoAtom.CommandLine has a Command too: these two lines keep Command.Shell and name the other one.
using Command = CodeAlta.Plugins.Abstractions.Command;
using AltaCommand = XenoAtom.CommandLine.Command;

// A command of the `alta` tool: a session runs `alta dice roll --sides 20` and reads the JSON line it prints.
[Plugin("alta-command", DisplayName = "Dice", Description = "Adds `alta dice roll` to the alta tool of the sessions.")]
public sealed class DicePlugin : PluginBase
{
    public override IEnumerable<PluginAltaCommandContribution> GetAltaCommands()
    {
        yield return new PluginAltaCommandContribution
        {
            // The first word is the root command; it must not be a root of CodeAlta (session, project, plugin, ...).
            Path = "dice roll",
            Description = "Rolls a die.",
            CreateCommandNode = CreateDice,
        };
    }

    // Called for each invocation: the context has the output, the caller and the working folder of that one.
    private static AltaCommand CreateDice(PluginAltaCommandContext context)
    {
        var sides = "6";
        var roll = new AltaCommand("roll", "Rolls one die and prints the result as a JSON record.")
        {
            new CommandUsage(),
            new HelpOption(),
        };
        roll.Add("sides=", "Number of sides of the die. Defaults to 6.", value => sides = value ?? sides);
        roll.Add((_, _) =>
        {
            if (!int.TryParse(sides, out var count) || count < 2)
            {
                context.Stderr.WriteLine(JsonSerializer.Serialize(new { type = "alta.dice.error", message = "--sides is a number from 2." }));
                return ValueTask.FromResult(2);
            }

            // One JSON object per line; the exit code 0 says the command succeeded.
            context.Stdout.WriteLine(JsonSerializer.Serialize(new { type = "alta.dice.roll", sides = count, value = Random.Shared.Next(1, count + 1) }));
            return ValueTask.FromResult(0);
        });

        var dice = new AltaCommand("dice", "Dice commands.")
        {
            new CommandUsage(),
            new HelpOption(),
        };
        dice.Add(roll);
        return dice;
    }
}
