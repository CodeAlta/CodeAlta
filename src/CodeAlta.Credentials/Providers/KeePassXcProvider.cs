using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Credentials.Providers;

/// <summary>
/// A KeePass database (<c>.kdbx</c>), through <c>keepassxc-cli</c>: its unlock takes the password of the database the user types
/// in the window, checked once and kept in memory only until CodeAlta locks it; every call gives it on the standard input of the
/// program. A database that opens with its key file alone needs nobody.
/// </summary>
/// <remarks>A reference is <c>&lt;entry path&gt;#&lt;attribute&gt;</c>, such as <c>Work/GitHub#Password</c>; <c>#totp</c> is its current code.</remarks>
internal sealed class KeePassXcProvider : ICredentialProvider
{
    internal const string TypeName = "keepassxc";
    private const string MacProgram = "/Applications/KeePassXC.app/Contents/MacOS/keepassxc-cli";
    private static readonly string[] EntryFields = ["Password", "UserName", "URL", "Notes", "totp"];

    private readonly VaultCommandRunner _runner;
    private readonly string? _configuredPath;
    private readonly string? _database;
    private readonly string? _keyFile;
    private readonly bool _noPassword;
    private readonly Lock _gate = new();
    private string? _password;

    public KeePassXcProvider(PluginCredentialProviderContext context, VaultCommandRunner runner)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
        _configuredPath = context.GetString("path");
        _database = VaultText.ExpandPath(context.GetString("database"));
        _keyFile = VaultText.ExpandPath(context.GetString("key_file"));
        _noPassword = context.GetBoolean("no_password") == true;
    }

    public static PluginCredentialProviderContribution Contribution(VaultCommandRunner runner) => new()
    {
        Type = TypeName,
        DisplayName = "KeePassXC",
        Description = "Reads the entries of a KeePass database through keepassxc-cli, unlocked with the password of the database.",
        Icon = "keepassxc",
        Website = new Uri("https://keepassxc.org/docs/KeePassXC_UserGuide#_command_line_tool"),
        ReferenceHint = "Work/GitHub#Password",
        UnlockAsksPassword = true,
        Settings =
        [
            new("database", "Database", PluginCredentialProviderSettingKind.Path) { Required = true, Placeholder = "~/Passwords.kdbx", Description = "The KeePass database file (.kdbx)." },
            new("key_file", "Key file", PluginCredentialProviderSettingKind.Path) { Description = "Optional: the key file that opens the database with its password." },
            new("no_password", "No password", PluginCredentialProviderSettingKind.Boolean) { Description = "The database opens with its key file alone." },
            new("path", "Path of keepassxc-cli", PluginCredentialProviderSettingKind.Path) { Description = "Leave empty to find keepassxc-cli on the PATH or beside KeePassXC." },
        ],
        Create = context => new KeePassXcProvider(context, runner),
    };

    private string? Program
        => VaultCommandRunner.FindProgram("keepassxc-cli", _configuredPath)
            ?? (_configuredPath is null && OperatingSystem.IsMacOS() && File.Exists(MacProgram) ? MacProgram : null);

    private string? Password
    {
        get
        {
            lock (_gate) return _password;
        }
    }

    public ValueTask<CredentialProviderStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (Program is not { } program) return ValueTask.FromResult(NotInstalled());
        if (_database is null) return ValueTask.FromResult(CredentialProviderStatus.Unavailable("No KeePass database is set.") with { ProgramPath = program });
        var account = Path.GetFileName(_database);
        if (!File.Exists(_database)) return ValueTask.FromResult(CredentialProviderStatus.Unavailable($"The KeePass database {_database} is not found.") with { ProgramPath = program });
        if (_keyFile is not null && !File.Exists(_keyFile)) return ValueTask.FromResult(CredentialProviderStatus.Unavailable($"The key file {_keyFile} is not found.") with { ProgramPath = program });
        if (!_noPassword && Password is null) return ValueTask.FromResult(CredentialProviderStatus.Locked("Type the password of the database to unlock it.") with { Account = account, ProgramPath = program });
        return ValueTask.FromResult(CredentialProviderStatus.Ready() with { Account = account, ProgramPath = program });
    }

    public async ValueTask<CredentialProviderStatus> UnlockAsync(CredentialUnlockContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.State is CredentialProviderState.NotInstalled or CredentialProviderState.Unavailable) return status;
        var password = _noPassword ? null : context.Password;
        if (!_noPassword && string.IsNullOrEmpty(password))
            return status.State == CredentialProviderState.Ready ? status : CredentialProviderStatus.Locked("Type the password of the database to unlock it.") with { Account = status.Account, ProgramPath = status.ProgramPath };

        // The password is checked by opening the database: a listing of its root holds names only.
        var result = await RunAsync(status.ProgramPath!, ["ls", "-q"], [], password, TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
        VaultText.Zero(result.Output);
        if (result.ExitCode != 0)
        {
            Lock();
            return CredentialProviderStatus.Locked(result.TimedOut ? "keepassxc-cli did not answer in time." : $"KeePassXC: {VaultText.ErrorLine(result)}") with { Account = status.Account, ProgramPath = status.ProgramPath };
        }

        lock (_gate) _password = password;
        return CredentialProviderStatus.Ready() with { Account = status.Account, ProgramPath = status.ProgramPath };
    }

    public async ValueTask<CredentialValue> ReadAsync(string reference, bool interactive, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        var (entry, attribute) = Split(reference);
        var (program, password) = Ready();
        var totp = attribute.Equals("totp", StringComparison.OrdinalIgnoreCase);
        var result = totp
            ? await RunAsync(program, ["show", "-q", "-t"], [entry], password, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false)
            : await RunAsync(program, ["show", "-q", "-s", "-a", attribute], [entry], password, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        try
        {
            if (result.ExitCode != 0) throw Failure(result, reference);
            var value = VaultText.TrimOneNewline(result.OutputText());
            if (value.Length == 0) throw new CredentialProviderException(CredentialFailure.NotFound, $"The KeePass entry {entry} has no {attribute}.");
            return new CredentialValue(value) { ExpiresAt = totp ? OnePasswordProvider.NextTotpWindow() : null };
        }
        finally
        {
            VaultText.Zero(result.Output);
        }
    }

    public async ValueTask<CredentialBrowsePage> BrowseAsync(CredentialBrowseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var parent = request.Parent;
        if (parent is not null && parent.StartsWith("entry:", StringComparison.Ordinal))
        {
            var entry = parent["entry:".Length..];
            return new CredentialBrowsePage([.. EntryFields.Select(field => new CredentialBrowseEntry($"{entry}#{field}", field == "totp" ? "one-time password" : field, CredentialBrowseEntryKind.Field)
            {
                Reference = $"{entry}#{field}",
                Detail = field switch { "Password" => "concealed", "totp" => "one-time password", _ => null },
            })]) { Path = entry.Replace("/", " › ", StringComparison.Ordinal) };
        }

        var group = parent is null ? string.Empty : parent.StartsWith("group:", StringComparison.Ordinal) ? parent["group:".Length..]
            : throw new CredentialProviderException(CredentialFailure.NotFound, "This entry of KeePassXC is not known.");
        var (program, password) = Ready();
        var result = await RunAsync(program, ["ls", "-R", "-f", "-q"], [], password, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        try
        {
            if (result.ExitCode != 0) throw Failure(result, null);
            return List(result.OutputText(), group);
        }
        finally
        {
            VaultText.Zero(result.Output);
        }
    }

    public void Lock()
    {
        lock (_gate) _password = null;
    }

    // The groups and entries right under a group, from the flat listing of the database: "Group/" for a group, "Group/Entry" for an entry.
    internal static CredentialBrowsePage List(string listing, string group)
    {
        var groups = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var entries = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var raw in listing.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            // An empty group is listed as "Group/[empty]".
            if (line.EndsWith("[empty]", StringComparison.Ordinal)) line = line[..^"[empty]".Length];
            if (line.Trim().Length == 0 || !line.StartsWith(group, StringComparison.Ordinal)) continue;
            var rest = line[group.Length..];
            var slash = rest.IndexOf('/');
            // A deeper line names the group it is in, even when the listing has no line of the group itself.
            if (slash >= 0)
            {
                if (slash > 0) groups.Add(rest[..slash]);
            }
            else if (rest.Length > 0)
            {
                entries.Add(rest);
            }
        }

        var page = new List<CredentialBrowseEntry>();
        page.AddRange(groups.Select(name => new CredentialBrowseEntry($"group:{group}{name}/", name, CredentialBrowseEntryKind.Folder) { Detail = "group" }));
        page.AddRange(entries.Select(name => new CredentialBrowseEntry($"entry:{group}{name}", name, CredentialBrowseEntryKind.Folder)));
        return new CredentialBrowsePage(page) { Path = group.Length == 0 ? null : group.TrimEnd('/').Replace("/", " › ", StringComparison.Ordinal) };
    }

    internal static (string Entry, string Attribute) Split(string reference)
    {
        var at = reference.LastIndexOf('#');
        var entry = (at < 0 ? reference : reference[..at]).Trim();
        var attribute = at < 0 ? "Password" : reference[(at + 1)..].Trim();
        if (entry.Length == 0 || attribute.Length == 0)
            throw new CredentialProviderException(CredentialFailure.NotFound, $"'{reference}' is not a KeePassXC reference (<entry path>#<attribute>).");
        return (entry, attribute);
    }

    private (string Program, string? Password) Ready()
    {
        if (Program is not { } program) throw new CredentialProviderException(CredentialFailure.NotInstalled, "keepassxc-cli is not installed.");
        if (_database is null || !File.Exists(_database)) throw new CredentialProviderException(CredentialFailure.Failed, $"The KeePass database {_database} is not found.");
        var password = Password;
        // The password is typed in the window only: without it nothing is run.
        if (!_noPassword && password is null) throw new CredentialProviderException(CredentialFailure.Locked, "KeePassXC is locked: unlock it with the password of the database.");
        return (program, password);
    }

    private CredentialProviderException Failure(VaultCommandResult result, string? reference)
    {
        var error = result.Error;
        if (result.TimedOut) return new CredentialProviderException(CredentialFailure.Failed, "keepassxc-cli did not answer in time.");
        if (error.Contains("Invalid credentials", StringComparison.OrdinalIgnoreCase) || error.Contains("wrong key", StringComparison.OrdinalIgnoreCase))
        {
            // The database changed its password: the one held is of no use.
            Lock();
            return new CredentialProviderException(CredentialFailure.Locked, "KeePassXC refused the password of the database: unlock it again.");
        }

        if (error.Contains("Could not find", StringComparison.OrdinalIgnoreCase) || error.Contains("unknown attribute", StringComparison.OrdinalIgnoreCase)
            || error.Contains("No TOTP", StringComparison.OrdinalIgnoreCase) || error.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return new CredentialProviderException(CredentialFailure.NotFound, $"KeePassXC: {VaultText.ErrorLine(result)}");
        return new CredentialProviderException(CredentialFailure.Failed, reference is null ? $"KeePassXC: {VaultText.ErrorLine(result)}" : $"KeePassXC could not read {reference}: {VaultText.ErrorLine(result)}");
    }

    // keepassxc-cli <command> <options> [-k <key file>] [--no-password] <database> <arguments>, with the password on its standard input.
    private Task<VaultCommandResult> RunAsync(string program, IReadOnlyList<string> command, IReadOnlyList<string> arguments, string? password, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var all = new List<string>(command);
        if (_keyFile is not null) all.AddRange(["-k", _keyFile]);
        if (_noPassword) all.Add("--no-password");
        all.Add(_database!);
        all.AddRange(arguments);
        return _runner.RunAsync(program, all, new VaultCommandOptions { Input = _noPassword ? null : password + "\n", Timeout = timeout }, cancellationToken);
    }

    private static CredentialProviderStatus NotInstalled() => new(CredentialProviderState.NotInstalled, "keepassxc-cli is not installed: it comes with KeePassXC.")
    {
        Install = new CredentialProviderInstall(
            OperatingSystem.IsWindows() ? ["winget", "install", "--id", "KeePassXCTeam.KeePassXC", "-e"]
            : OperatingSystem.IsMacOS() ? ["brew", "install", "--cask", "keepassxc"]
            : null,
            new Uri("https://keepassxc.org/download/")),
    };
}
