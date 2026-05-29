namespace CodeAlta.Frontend.Commands;

internal sealed class ShellCommandRegistry
{
    private readonly IReadOnlyList<ShellCommand> _commands;
    private readonly Dictionary<string, ShellCommand> _byId;
    private readonly Dictionary<string, ShellCommand> _byName;

    public ShellCommandRegistry(IEnumerable<ShellCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        var list = commands.ToArray();
        Validate(list);
        _commands = list;
        _byId = list.ToDictionary(static command => command.Id, StringComparer.Ordinal);
        _byName = list
            .Where(static command => !string.IsNullOrWhiteSpace(command.Name) && command.ShowInCommandPalette)
            .ToDictionary(static command => command.Name!, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ShellCommand> Commands => _commands;

    public IEnumerable<ShellCommand> CommandsFor(ShellCommandPlacement placement)
        => _commands.Where(command => (command.Placement & placement) != 0);

    public bool TryGetById(string id, out ShellCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return _byId.TryGetValue(id, out command!);
    }

    public bool TryGetByName(string name, out ShellCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _byName.TryGetValue(name, out command!);
    }

    private static void Validate(IReadOnlyList<ShellCommand> commands)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in commands)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (string.IsNullOrWhiteSpace(command.Id))
            {
                throw new ArgumentException("Shell command IDs must be non-empty.", nameof(commands));
            }

            if (string.IsNullOrWhiteSpace(command.Label))
            {
                throw new ArgumentException($"Shell command '{command.Id}' must have a non-empty label.", nameof(commands));
            }

            if (string.IsNullOrWhiteSpace(command.Description))
            {
                throw new ArgumentException($"Shell command '{command.Id}' must have a non-empty description.", nameof(commands));
            }

            if (!ids.Add(command.Id))
            {
                throw new ArgumentException($"Duplicate shell command ID '{command.Id}'.", nameof(commands));
            }

            if (command.Gesture is not null && command.Sequence is not null)
            {
                throw new ArgumentException($"Shell command '{command.Id}' cannot declare both Gesture and Sequence.", nameof(commands));
            }

            if (command.AdditionalHelpBindings is List<string>)
            {
                throw new ArgumentException($"Shell command '{command.Id}' uses a mutable help binding collection.", nameof(commands));
            }

            if (!string.IsNullOrWhiteSpace(command.Name) && command.ShowInCommandPalette && !names.Add(command.Name))
            {
                throw new ArgumentException($"Duplicate command-palette name '{command.Name}'.", nameof(commands));
            }
        }
    }
}
