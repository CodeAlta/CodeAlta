using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Credentials.Providers;

/// <summary>
/// The standard Unix password manager, <c>pass</c>, or <c>gopass</c>: files of a store encrypted with GnuPG, whose agent asks
/// the passphrase itself (pinentry) when it has none. CodeAlta never asks it.
/// </summary>
/// <remarks>
/// A reference is the path of an entry in the store, such as <c>web/github</c>, which gives its first line (the password), or
/// <c>web/github#user</c>, which gives the value of its <c>user: …</c> line.
/// </remarks>
internal sealed class PassProvider : ICredentialProvider
{
    internal const string TypeName = "pass";

    private readonly VaultCommandRunner _runner;
    private readonly string? _configuredPath;
    private readonly string _program;
    private readonly string? _store;

    public PassProvider(PluginCredentialProviderContext context, VaultCommandRunner runner)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
        _configuredPath = context.GetString("path");
        _program = context.GetString("program")?.ToLowerInvariant() ?? "pass";
        _store = VaultText.ExpandPath(context.GetString("store"));
    }

    /// <summary>Gets how long a read that may not show a prompt waits for the agent of GnuPG: longer means a pinentry waits for somebody.</summary>
    internal TimeSpan NonInteractiveTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public static PluginCredentialProviderContribution Contribution(VaultCommandRunner runner) => new()
    {
        Type = TypeName,
        DisplayName = "pass",
        Description = "Reads the entries of a password store of pass or gopass, decrypted by GnuPG, whose agent asks its passphrase itself.",
        Icon = "gnuprivacyguard",
        Website = new Uri("https://www.passwordstore.org/"),
        ReferenceHint = "web/github#user",
        Settings =
        [
            new("program", "Program", PluginCredentialProviderSettingKind.Text) { Placeholder = "pass", Description = "pass or gopass." },
            new("store", "Store", PluginCredentialProviderSettingKind.Path) { Placeholder = "~/.password-store", Description = "The folder of the store (PASSWORD_STORE_DIR); leave empty for the one of the program." },
            new("path", "Path of the program", PluginCredentialProviderSettingKind.Path) { Description = "Leave empty to find it on the PATH." },
        ],
        Create = context => new PassProvider(context, runner),
    };

    private bool IsGopass => _program == "gopass";

    private string? Program => _program is "pass" or "gopass" ? VaultCommandRunner.FindProgram(_program, _configuredPath) : null;

    // The folder the picker lists: the one of the settings, else the default store of the program.
    private string StoreFolder
    {
        get
        {
            if (_store is not null) return _store;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var gopass = Path.Combine(home, ".local", "share", "gopass", "stores", "root");
            return IsGopass && Directory.Exists(gopass) ? gopass : Path.Combine(home, ".password-store");
        }
    }

    public ValueTask<CredentialProviderStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (_program is not ("pass" or "gopass")) return ValueTask.FromResult(CredentialProviderStatus.Unavailable($"The program '{_program}' is not pass nor gopass."));
        if (Program is not { } program) return ValueTask.FromResult(NotInstalled());
        // The agent of GnuPG holds the passphrase and asks it itself: the store is ready whenever the program is there.
        return ValueTask.FromResult(CredentialProviderStatus.Ready() with { ProgramPath = program });
    }

    public ValueTask<CredentialProviderStatus> UnlockAsync(CredentialUnlockContext context, CancellationToken cancellationToken) => GetStatusAsync(cancellationToken);

    public async ValueTask<CredentialValue> ReadAsync(string reference, bool interactive, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        var (entry, key) = Split(reference);
        if (Program is not { } program) throw new CredentialProviderException(CredentialFailure.NotInstalled, $"{_program} is not installed.");
        IReadOnlyList<string> arguments = IsGopass
            ? key is null ? ["show", "-o", entry] : ["show", entry, key]
            : ["show", entry];
        var result = await _runner.RunAsync(program, arguments, new VaultCommandOptions
        {
            Environment = ReadEnvironment(interactive),
            Timeout = interactive ? TimeSpan.FromMinutes(2) : NonInteractiveTimeout,
        }, cancellationToken).ConfigureAwait(false);
        try
        {
            if (result.TimedOut)
                throw VaultText.Failure(CredentialFailure.Locked, "GnuPG did not give the passphrase in time: is pinentry waiting for you?", result);
            if (result.ExitCode != 0)
            {
                var error = result.Error;
                var notFound = error.Contains("not in the password store", StringComparison.OrdinalIgnoreCase) || error.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    || error.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
                if (!notFound && !interactive && NeedsPassphrase(error))
                    throw VaultText.Failure(CredentialFailure.Locked, "GnuPG needs the passphrase of its key: unlock the store to let pinentry ask for it.", result);
                throw VaultText.Failure(notFound ? CredentialFailure.NotFound : CredentialFailure.Failed,
                    notFound ? $"The entry {entry} is not in the password store." : $"{_program} did not give {entry}: {result.Summary()}.", result);
            }

            var text = result.OutputText();
            var value = IsGopass ? VaultText.TrimOneNewline(text) : key is null ? FirstLine(text) : KeyLine(text, key);
            if (string.IsNullOrEmpty(value)) throw new CredentialProviderException(CredentialFailure.NotFound, key is null ? $"The entry {entry} is empty." : $"The entry {entry} has no line '{key}:'.");
            return new CredentialValue(value);
        }
        finally
        {
            VaultText.Zero(result.Output);
        }
    }

    public ValueTask<CredentialBrowsePage> BrowseAsync(CredentialBrowseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // The names of the files are the names of the entries: the store is listed without running the program, and nothing is decrypted.
        var root = Path.GetFullPath(StoreFolder);
        if (!Directory.Exists(root)) throw new CredentialProviderException(CredentialFailure.Failed, $"The password store {root} is not found.");
        var relative = request.Parent is null ? string.Empty
            : request.Parent.StartsWith("folder:", StringComparison.Ordinal) ? request.Parent["folder:".Length..].Trim('/')
            : throw new CredentialProviderException(CredentialFailure.NotFound, "This entry of the password store is not known.");
        var folder = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!(folder == root || folder.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            || !Directory.Exists(folder))
            throw new CredentialProviderException(CredentialFailure.NotFound, "This folder of the password store is not known.");

        var prefix = relative.Length == 0 ? string.Empty : relative + "/";
        var entries = new List<CredentialBrowseEntry>();
        entries.AddRange(Directory.EnumerateDirectories(folder)
            .Select(Path.GetFileName)
            .Where(static name => !string.IsNullOrEmpty(name) && !name.StartsWith('.'))
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .Select(name => new CredentialBrowseEntry($"folder:{prefix}{name}", name!, CredentialBrowseEntryKind.Folder)));
        entries.AddRange(Directory.EnumerateFiles(folder)
            .Where(static file => file.EndsWith(".gpg", StringComparison.Ordinal))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(static name => !string.IsNullOrEmpty(name) && !name.StartsWith('.'))
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .Select(name => new CredentialBrowseEntry($"{prefix}{name}", name!, CredentialBrowseEntryKind.Field) { Reference = $"{prefix}{name}" }));
        return ValueTask.FromResult(new CredentialBrowsePage(entries) { Path = relative.Length == 0 ? null : relative.Replace("/", " › ", StringComparison.Ordinal) });
    }

    // A read that may not show a prompt runs GnuPG with --pinentry-mode error: without the passphrase in its agent, it fails at
    // once instead of starting pinentry. pass adds PASSWORD_STORE_GPG_OPTS to the options of gpg; gopass reads GOPASS_GPG_OPTS,
    // else PASSWORD_STORE_GPG_OPTS. The options the user has there are kept.
    internal Dictionary<string, string>? ReadEnvironment(bool interactive)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_store is not null) environment["PASSWORD_STORE_DIR"] = _store;
        if (!interactive)
        {
            foreach (var name in new[] { "PASSWORD_STORE_GPG_OPTS", "GOPASS_GPG_OPTS" })
            {
                var own = System.Environment.GetEnvironmentVariable(name);
                // gopass reads PASSWORD_STORE_GPG_OPTS only when GOPASS_GPG_OPTS is not set: an empty one is left unset.
                if (name == "GOPASS_GPG_OPTS" && string.IsNullOrWhiteSpace(own)) continue;
                environment[name] = string.IsNullOrWhiteSpace(own) ? NoPinentry : $"{own.Trim()} {NoPinentry}";
            }
        }

        return environment.Count == 0 ? null : environment;
    }

    private const string NoPinentry = "--pinentry-mode error";

    // What gpg writes when it would need pinentry: "No pinentry", "No secret key" (the key is there, its passphrase is not given),
    // "Inappropriate ioctl for device", or a cancellation.
    private static bool NeedsPassphrase(string error)
        => error.Contains("pinentry", StringComparison.OrdinalIgnoreCase) || error.Contains("No secret key", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Inappropriate ioctl", StringComparison.OrdinalIgnoreCase) || error.Contains("cancel", StringComparison.OrdinalIgnoreCase)
            || error.Contains("passphrase", StringComparison.OrdinalIgnoreCase);

    internal static (string Entry, string? Key) Split(string reference)
    {
        var at = reference.LastIndexOf('#');
        var entry = (at < 0 ? reference : reference[..at]).Trim().Trim('/');
        var key = at < 0 ? null : reference[(at + 1)..].Trim();
        if (entry.Length == 0 || key is { Length: 0 } || entry.StartsWith('-'))
            throw new CredentialProviderException(CredentialFailure.NotFound, $"'{reference}' is not an entry of a password store (path, or path#key).");
        return (entry, key);
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOf('\n');
        return (end < 0 ? text : text[..end]).TrimEnd('\r');
    }

    // The value of a "key: value" line after the first one (the first line is the password).
    private static string? KeyLine(string text, string key)
    {
        var lines = text.Split('\n');
        for (var index = 1; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return line[(colon + 1)..].Trim();
        }

        return null;
    }

    private CredentialProviderStatus NotInstalled() => new(CredentialProviderState.NotInstalled, $"{_program} is not installed.")
    {
        Install = IsGopass
            ? new CredentialProviderInstall(
                OperatingSystem.IsMacOS() ? ["brew", "install", "gopass"] : null,
                new Uri("https://github.com/gopasspw/gopass/blob/master/docs/setup.md"))
            : new CredentialProviderInstall(OperatingSystem.IsMacOS() ? ["brew", "install", "pass"] : null, new Uri("https://www.passwordstore.org/")),
    };
}
