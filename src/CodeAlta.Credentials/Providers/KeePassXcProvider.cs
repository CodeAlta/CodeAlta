using System.Collections.Immutable;
using System.Text;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Credentials.Providers;

/// <summary>
/// A KeePass database (<c>.kdbx</c>), through <c>keepassxc-cli</c>: its unlock takes the password of the database the user types
/// in the window, checked once and kept in memory only until CodeAlta locks it; every call gives it on the standard input of the
/// program. A database that opens with its key file alone needs nobody.
/// </summary>
/// <remarks>A reference is <c>&lt;entry path&gt;#&lt;attribute&gt;</c>, such as <c>Work/GitHub#Password</c>; <c>#totp</c> is its current code.</remarks>
internal sealed class KeePassXcProvider : ICredentialProvider, ICredentialBatchReader
{
    internal const string TypeName = "keepassxc";
    private const string MacProgram = "/Applications/KeePassXC.app/Contents/MacOS/keepassxc-cli";
    private static readonly ImmutableArray<string> EntryFields = ["Password", "UserName", "URL", "Notes", "totp"];
    // The most of an export that is read: a larger one is cut, is then not the export of a database, and each secret is read by show.
    private const int ExportLimit = 128 * 1024 * 1024;
    // On Windows, Qt shows what a program says of its command line (its version, its help, an option it does not know) in a
    // dialog the user must close when the program has no console. Its output is redirected here, which already prevents it; this
    // variable of Qt does too, whatever starts the program.
    private static readonly System.Collections.Frozen.FrozenDictionary<string, string> NoMessageBoxes
        = System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(new Dictionary<string, string> { ["QT_COMMAND_LINE_PARSER_NO_GUI_MESSAGE_BOXES"] = "1" }, StringComparer.Ordinal);

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
            return CredentialProviderStatus.Locked(result.TimedOut ? "keepassxc-cli did not answer in time."
                : Refused(result)
                    ? "KeePassXC refused the password of the database."
                    : $"KeePassXC did not open the database: {result.Summary()}.").WithDetail(result) with { Account = status.Account, ProgramPath = status.ProgramPath };
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

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<CredentialReadResult>> ReadManyAsync(IReadOnlyList<string> references, bool interactive, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(references);
        var results = new CredentialReadResult[references.Count];
        var wanted = new (string Entry, string Attribute)?[references.Count];
        var asked = 0;
        for (var index = 0; index < references.Count; index++)
        {
            try
            {
                var (entry, attribute) = Split(references[index]);
                // A one-time code is computed by keepassxc-cli, as it is needed.
                if (attribute.Equals("totp", StringComparison.OrdinalIgnoreCase)) continue;
                wanted[index] = (entry, attribute);
                asked++;
            }
            catch (CredentialProviderException exception)
            {
                results[index] = new CredentialReadResult(null, exception);
            }
        }

        // Each call of keepassxc-cli opens the database again, which takes about a second by design of its key derivation: its
        // export is one opening for every entry. Only the attributes asked for are read of it; a value it cannot give as show
        // would (see KeePassXcExport) is read by show.
        if (asked > 1)
        {
            try
            {
                var (program, password) = Ready();
                var result = await RunAsync(program, ["export", "-q", "-f", "xml"], [], password, TimeSpan.FromSeconds(60), cancellationToken, ExportLimit).ConfigureAwait(false);
                try
                {
                    // A refused password is forgotten: each read below then says that the database is locked, without a call.
                    if (result.ExitCode != 0) _ = Failure(result, null);
                    else if (KeePassXcExport.Values(result.Output, wanted) is { } values)
                    {
                        for (var index = 0; index < values.Length; index++)
                        {
                            if (values[index] is { } value) results[index] = new CredentialReadResult(new CredentialValue(value), null);
                        }
                    }
                }
                finally
                {
                    VaultText.Zero(result.Output);
                }
            }
            catch (CredentialProviderException)
            {
                // Not installed, no database, locked: each read says so.
            }
        }

        for (var index = 0; index < references.Count; index++)
        {
            if (results[index] != default) continue;
            try
            {
                results[index] = new CredentialReadResult(await ReadAsync(references[index], interactive, cancellationToken).ConfigureAwait(false), null);
            }
            catch (CredentialProviderException exception)
            {
                results[index] = new CredentialReadResult(null, exception);
            }
        }

        return results;
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

    // The database refused the password or the key file. keepassxc-cli 2.8 run with -q writes nothing then and ends with code 1,
    // while it still writes why it found no entry or no attribute.
    private static bool Refused(VaultCommandResult result)
        => result.Error.Contains("Invalid credentials", StringComparison.OrdinalIgnoreCase) || result.Error.Contains("wrong key", StringComparison.OrdinalIgnoreCase)
            || result.ExitCode != 0 && !result.TimedOut && string.IsNullOrWhiteSpace(result.Error);

    private CredentialProviderException Failure(VaultCommandResult result, string? reference)
    {
        var error = result.Error;
        if (result.TimedOut) return new CredentialProviderException(CredentialFailure.Failed, "keepassxc-cli did not answer in time.");
        if (Refused(result))
        {
            // The database changed its password: the one held is of no use.
            Lock();
            return new CredentialProviderException(CredentialFailure.Locked, "KeePassXC refused the password of the database: unlock it again.");
        }

        if (error.Contains("Could not find", StringComparison.OrdinalIgnoreCase) || error.Contains("unknown attribute", StringComparison.OrdinalIgnoreCase)
            || error.Contains("No TOTP", StringComparison.OrdinalIgnoreCase) || error.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return VaultText.Failure(CredentialFailure.NotFound, reference is null ? "KeePassXC has no such entry." : $"KeePassXC has no {reference}.", result);
        return VaultText.Failure(CredentialFailure.Failed, reference is null ? $"KeePassXC failed: {result.Summary()}." : $"KeePassXC could not read {reference}: {result.Summary()}.", result);
    }

    // keepassxc-cli <command> <options> [-k <key file>] [--no-password] <database> <arguments>, with the password on its standard input.
    private Task<VaultCommandResult> RunAsync(string program, IReadOnlyList<string> command, IReadOnlyList<string> arguments, string? password, TimeSpan timeout, CancellationToken cancellationToken,
        int maximumOutput = 4 * 1024 * 1024)
    {
        var all = new List<string>(command);
        if (_keyFile is not null) all.AddRange(["-k", _keyFile]);
        if (_noPassword) all.Add("--no-password");
        all.Add(_database!);
        all.AddRange(arguments);
        return _runner.RunAsync(program, all, new VaultCommandOptions
        {
            Input = _noPassword ? null : password + "\n",
            Timeout = timeout,
            MaximumOutput = maximumOutput,
            Environment = NoMessageBoxes,
        }, cancellationToken);
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

// Reads the attributes of entries from what `keepassxc-cli export -f xml` writes: the KeePass XML of the whole database, with its
// values in clear. It gives a value only where `keepassxc-cli show -a` would print the same: the entry is the first, in the order
// of the file, whose path (the names of its groups under the root, then its title) is the one asked for, or whose title is when
// the path names no group, as show finds it; the attribute has exactly that name; and its value is one line without a placeholder
// ({USERNAME}, {REF:...}), which show resolves and the export does not. Everything else is left to show. Only the names (groups,
// titles, keys) and the values asked for are decoded: every other value stays bytes, which the caller zeroes.
internal static class KeePassXcExport
{
    private enum Tag
    {
        Other,
        Group,
        Entry,
        History,
        String,
        Key,
        Value,
        Name,
    }

    /// <summary>Reads the attributes asked for.</summary>
    /// <param name="xml">What the export wrote.</param>
    /// <param name="wanted">The entry path and the attribute of each reference; null for a reference that is not asked of the export.</param>
    /// <returns>The value of each reference, null where show must read it; null when the bytes are not the export of a database.</returns>
    public static string?[]? Values(ReadOnlySpan<byte> xml, IReadOnlyList<(string Entry, string Attribute)?> wanted)
    {
        var values = new string?[wanted.Count];
        var found = new bool[wanted.Count];
        var paths = new string?[wanted.Count];
        // The name of each attribute as the file writes it; null for Uuid and Tags, which are fields of the entry for show.
        var attributes = new byte[]?[wanted.Count];
        for (var index = 0; index < wanted.Count; index++)
        {
            if (wanted[index] is not { } asked) continue;
            // As show does: a path that names a group starts at the root.
            paths[index] = !asked.Entry.StartsWith('/') && asked.Entry.Contains('/') ? "/" + asked.Entry : asked.Entry;
            if (asked.Attribute is not ("Uuid" or "Tags")) attributes[index] = Encoding.UTF8.GetBytes(asked.Attribute);
        }

        var open = new List<Tag>();
        // The path of each open group, "/" for the root and "/Work/" under it; null until its name is read.
        var groups = new List<string?>();
        var strings = new List<(Range Key, Range Value)>();
        Range key = default, value = default;
        var text = 0;
        // The depth of the element whose content is skipped (the history of an entry); 0 for none.
        var skipped = 0;
        var position = 0;
        var entries = 0;
        while (true)
        {
            var next = xml[position..].IndexOf((byte)'<');
            if (next < 0) break;
            var start = position + next;
            var rest = xml[start..];
            if (rest.StartsWith("<?"u8))
            {
                var end = rest.IndexOf("?>"u8);
                if (end < 0) return null;
                position = start + end + 2;
                continue;
            }

            if (rest.StartsWith("<!--"u8))
            {
                var end = rest.IndexOf("-->"u8);
                if (end < 0) return null;
                position = start + end + 3;
                continue;
            }

            // A document type or a CDATA section: not what keepassxc-cli writes.
            if (rest.StartsWith("<!"u8)) return null;
            var close = EndOfTag(rest);
            if (close < 0) return null;
            position = start + close + 1;
            if (rest[1] == (byte)'/')
            {
                if (open.Count == 0 || open[^1] != TagOf(rest[2..close].TrimEnd((byte)' '))) return null;
                var tag = open[^1];
                var parent = open.Count > 1 ? open[^2] : Tag.Other;
                open.RemoveAt(open.Count - 1);
                if (skipped > 0)
                {
                    if (open.Count < skipped) skipped = 0;
                    continue;
                }

                switch (tag)
                {
                    case Tag.Name when parent == Tag.Group && groups.Count > 1 && groups[^1] is null:
                        if (groups[^2] is not { } above || Decode(xml[text..start]) is not { } name) return null;
                        groups[^1] = above + name + "/";
                        break;
                    case Tag.Key when parent == Tag.String:
                        key = text..start;
                        break;
                    case Tag.Value when parent == Tag.String:
                        value = text..start;
                        break;
                    case Tag.String when parent == Tag.Entry:
                        strings.Add((key, value));
                        break;
                    case Tag.Entry when parent == Tag.Group:
                        if (groups[^1] is not { } path || !Take(xml, path, strings, paths, attributes, found, values)) return null;
                        entries++;
                        break;
                    case Tag.Group:
                        groups.RemoveAt(groups.Count - 1);
                        break;
                }

                continue;
            }

            var empty = rest[close - 1] == (byte)'/';
            var nameEnd = rest[1..close].IndexOfAny((byte)' ', (byte)'/', (byte)'\t');
            var opened = TagOf(nameEnd < 0 ? rest[1..close] : rest[1..(1 + nameEnd)]);
            if (skipped > 0)
            {
                if (!empty) open.Add(opened);
                continue;
            }

            var inside = open.Count > 0 ? open[^1] : Tag.Other;
            switch (opened)
            {
                case Tag.Group when !empty:
                    groups.Add(groups.Count == 0 ? "/" : null);
                    break;
                case Tag.Entry when inside == Tag.Group && !empty:
                    strings.Clear();
                    break;
                case Tag.String when inside == Tag.Entry:
                    key = value = default;
                    break;
                case Tag.Key or Tag.Value when inside == Tag.String && empty:
                    // An empty element: an attribute without a value.
                    if (opened == Tag.Key) key = position..position;
                    else value = position..position;
                    break;
            }

            if (empty) continue;
            open.Add(opened);
            text = position;
            // The older versions of an entry are entries too: none of them is read.
            if (opened == Tag.History) skipped = open.Count;
        }

        // Cut, or not a database: nothing of it is taken.
        return open.Count == 0 && groups.Count == 0 && (entries > 0 || xml.IndexOf("<KeePassFile>"u8) >= 0) ? values : null;
    }

    // One entry of a group: the references that name it and no entry before it take their attribute from it. False when its
    // text cannot be read.
    private static bool Take(ReadOnlySpan<byte> xml, string group, List<(Range Key, Range Value)> strings, string?[] paths, byte[]?[] attributes, bool[] found, string?[] values)
    {
        string? title = null;
        foreach (var (key, value) in strings)
        {
            if (!xml[key].SequenceEqual("Title"u8)) continue;
            if ((title = Decode(xml[value])) is null) return false;
            break;
        }

        title ??= string.Empty;
        var full = group + title;
        for (var index = 0; index < paths.Length; index++)
        {
            if (found[index] || paths[index] is not { } path) continue;
            if (!(path == full || !path.StartsWith('/') && path == title)) continue;
            // The first entry is the one show reads, whatever it holds.
            found[index] = true;
            if (attributes[index] is not { } name) continue;
            foreach (var (key, value) in strings)
            {
                if (!xml[key].SequenceEqual(name)) continue;
                if (Decode(xml[value]) is not { } read) return false;
                if (read.Length > 0 && read.AsSpan().IndexOfAny('{', '\r', '\n') < 0) values[index] = read;
                break;
            }
        }

        return true;
    }

    // The index of the > that ends the tag a span starts with, which a quoted attribute value can hold too; -1 when it does not end.
    private static int EndOfTag(ReadOnlySpan<byte> tag)
    {
        byte quote = 0;
        for (var index = 1; index < tag.Length; index++)
        {
            var character = tag[index];
            if (quote != 0)
            {
                if (character == quote) quote = 0;
            }
            else if (character is (byte)'"' or (byte)'\'')
            {
                quote = character;
            }
            else if (character == (byte)'>')
            {
                return index;
            }
        }

        return -1;
    }

    private static Tag TagOf(ReadOnlySpan<byte> name)
        => name.SequenceEqual("Group"u8) ? Tag.Group
            : name.SequenceEqual("Entry"u8) ? Tag.Entry
            : name.SequenceEqual("History"u8) ? Tag.History
            : name.SequenceEqual("String"u8) ? Tag.String
            : name.SequenceEqual("Key"u8) ? Tag.Key
            : name.SequenceEqual("Value"u8) ? Tag.Value
            : name.SequenceEqual("Name"u8) ? Tag.Name
            : Tag.Other;

    // The text of an element: its bytes with the references of XML (&amp; &#10; &#x41;) replaced; null for one that is not XML.
    private static string? Decode(ReadOnlySpan<byte> text)
    {
        if (text.IndexOfAny((byte)'&', (byte)'<') < 0) return Encoding.UTF8.GetString(text);
        var decoded = new StringBuilder(text.Length);
        while (text.Length > 0)
        {
            var at = text.IndexOf((byte)'&');
            if (at < 0)
            {
                at = text.Length;
            }

            var plain = text[..at];
            // An element inside the text: not a text.
            if (plain.IndexOf((byte)'<') >= 0) return null;
            decoded.Append(Encoding.UTF8.GetString(plain));
            if (at == text.Length) break;
            var end = text[at..].IndexOf((byte)';');
            if (end < 2 || end > 12) return null;
            var reference = text[(at + 1)..(at + end)];
            if (reference.SequenceEqual("amp"u8)) decoded.Append('&');
            else if (reference.SequenceEqual("lt"u8)) decoded.Append('<');
            else if (reference.SequenceEqual("gt"u8)) decoded.Append('>');
            else if (reference.SequenceEqual("quot"u8)) decoded.Append('"');
            else if (reference.SequenceEqual("apos"u8)) decoded.Append('\'');
            else if (reference[0] == (byte)'#')
            {
                var hexadecimal = reference.Length > 1 && reference[1] is (byte)'x' or (byte)'X';
                var digits = Encoding.ASCII.GetString(reference[(hexadecimal ? 2 : 1)..]);
                if (!int.TryParse(digits, hexadecimal ? System.Globalization.NumberStyles.AllowHexSpecifier : System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var code) || code is <= 0 or > 0x10FFFF or >= 0xD800 and <= 0xDFFF)
                    return null;
                decoded.Append(char.ConvertFromUtf32(code));
            }
            else
            {
                return null;
            }

            text = text[(at + end + 1)..];
        }

        return decoded.ToString();
    }
}
