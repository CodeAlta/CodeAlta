using System.Text.RegularExpressions;
using CodeAlta.Catalog;

namespace CodeAlta.Node;

// The configuration of the node edited by hand (#414): a provider written into config.toml, changed or taken out while the node runs
// is applied within a second, as a change from the desktop is, rather than at the next start. Each provider section is compared with
// what was applied last, so that only the providers that changed start again; a change the node writes itself is remembered for its
// own section only, so that a hand edit of another section waits for the watch. A file that does not read leaves the running
// providers as they are, and the page of the machine says where it is wrong, never what it holds (a key typed in a broken line).
internal sealed partial class NodeProviders
{
    private static readonly TimeSpan ConfigurationSettle = TimeSpan.FromMilliseconds(750);

    // The sections of the providers of the configuration as last applied, by key; null before the watch starts.
    private Dictionary<string, string>? _applied;
    private string? _configurationError;

    /// <summary>Gets why the configuration file of the node could not be read when it last changed; null when it reads.</summary>
    internal string? ConfigurationError { get { lock (_gate) return _configurationError; } }

    /// <summary>Watches the configuration file of the node and applies the providers changed in it, until disposed.</summary>
    /// <returns>Ends the watch.</returns>
    internal IDisposable WatchConfiguration()
    {
        var path = host.CatalogOptions.ConfigPath;
        var folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        lock (_gate) _applied = Fingerprints(path, out _) ?? [];
        var timer = new Timer(_ => ApplyConfigurationChangesSafely(), null, Timeout.Infinite, Timeout.Infinite);
        var watcher = new FileSystemWatcher(folder, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        // An editor writes a file in several steps: the changes are applied once it settled.
        void Changed(object? sender, FileSystemEventArgs args) => timer.Change(ConfigurationSettle, Timeout.InfiniteTimeSpan);
        watcher.Changed += Changed;
        watcher.Created += Changed;
        watcher.Deleted += Changed;
        watcher.Renamed += (sender, args) => timer.Change(ConfigurationSettle, Timeout.InfiniteTimeSpan);
        watcher.EnableRaisingEvents = true;
        return new Watch(watcher, timer);
    }

    // The callback of the timer: nothing it meets may end the process.
    private void ApplyConfigurationChangesSafely()
    {
        try
        {
            ApplyConfigurationChanges();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            lock (_gate) _configurationError = "The configuration file could not be applied: " + exception.GetType().Name + ".";
        }
    }

    /// <summary>Applies the providers whose section of the configuration changed since they were last applied.</summary>
    /// <returns>The keys of the providers applied again.</returns>
    internal IReadOnlyList<string> ApplyConfigurationChanges() => ApplyConfigurationChanges(requireRead: false);

    /// <summary>Applies the providers whose section of the configuration changed since they were last applied.</summary>
    /// <param name="requireRead">
    /// Whether a file that cannot be read now (another program holds it, or it is being written) is a failure: an explicit
    /// apply must not say the configuration was applied when it was not read; the watch reads it again at its next change.
    /// </param>
    /// <returns>The keys of the providers applied again.</returns>
    /// <exception cref="IOException"><paramref name="requireRead"/> is set and the file cannot be read now.</exception>
    internal IReadOnlyList<string> ApplyConfigurationChanges(bool requireRead)
    {
        Dictionary<string, string> now;
        List<string> changed;
        lock (_gate)
        {
            if (_applied is not { } applied) return [];
            if (Fingerprints(host.CatalogOptions.ConfigPath, out var error, out var unreadable) is not { } read)
            {
                if (error is not null) _configurationError = error;
                if (unreadable && requireRead) throw new IOException("config.toml cannot be read now: another program holds it, or it is being written.");
                return [];
            }

            now = read;
            changed = [.. now.Where(pair => !applied.TryGetValue(pair.Key, out var before) || before != pair.Value).Select(static pair => pair.Key)
                .Concat(applied.Keys.Where(key => !now.ContainsKey(key)))];
        }

        // The typed configuration may still refuse what the sections read (a value of the wrong kind): nothing is applied then, and
        // the page says the file has an error, until it is changed again.
        try
        {
            _ = Store.LoadGlobalProviderDefinitions(includeDisabled: true);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or InvalidOperationException)
        {
            lock (_gate) _configurationError = SafeError(exception);
            return [];
        }

        var external = TryRead(Store.LoadGlobalExternalProviders) ?? [];
        var applies = new List<string>();
        foreach (var key in changed)
        {
            // A provider of a plugin is registered again by its plugin (retried while it is still prepared); one of the node, or one
            // taken out, is started again or taken away as a change from the desktop is.
            if (external.FirstOrDefault(provider => string.Equals(provider.ProviderKey, key, StringComparison.OrdinalIgnoreCase)) is { } provider
                && PluginProviderTypes?.Find(provider.ProviderType) is not null)
                RegisterPlugin(key);
            else
                lock (_gate) Apply(Store, key);
            lock (_gate) Remember(key, now);
            applies.Add(key);
        }

        lock (_gate) _configurationError = null;
        return applies;
    }

    // What the node wrote itself, for one provider, is applied already: its section is remembered, and only it, so that a hand edit
    // of another section made meanwhile is still applied by the watch.
    private void RememberApplied(string key)
    {
        if (_applied is null) return;
        if (Fingerprints(host.CatalogOptions.ConfigPath, out _) is { } now) Remember(key, now);
    }

    private void Remember(string key, IReadOnlyDictionary<string, string> sections)
    {
        if (_applied is not { } applied) return;
        if (sections.TryGetValue(key, out var section)) applied[key] = section;
        else applied.Remove(key);
    }

    // The section of each provider of a configuration file, as text; null when the file does not read, with why (null while it is
    // still being written: it is read again at its next change).
    private static Dictionary<string, string>? Fingerprints(string path, out string? error) => Fingerprints(path, out error, out _);

    // The same, saying whether the file could not be read at all (locked, being written, not accessible) rather than not parsed.
    private static Dictionary<string, string>? Fingerprints(string path, out string? error, out bool unreadable)
    {
        error = null;
        unreadable = false;
        try
        {
            return new Dictionary<string, string>(CodeAltaConfigStore.ReadProviderSections(path), StringComparer.OrdinalIgnoreCase);
        }
        catch (InvalidDataException exception)
        {
            error = SafeError(exception);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            unreadable = true;
            return null;
        }
    }

    /// <summary>
    /// Says where a configuration file does not read, never what it holds: the message of the parser quotes the text it met, which
    /// may be a key typed in a broken line. Only the line and the column are kept.
    /// </summary>
    /// <param name="exception">The failure of the read.</param>
    /// <returns>A sentence the page of the machine can show.</returns>
    internal static string SafeError(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (LocationPattern().Match(current.Message) is { Success: true } location)
                return $"config.toml does not read at line {location.Groups["line"].Value}, column {location.Groups["column"].Value}.";
        }

        return "config.toml does not read: a value has the wrong form.";
    }

    // The position the TOML parser writes before its message: path(line,column).
    [GeneratedRegex(@"\((?<line>\d+),(?<column>\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex LocationPattern();

    private sealed class Watch(FileSystemWatcher watcher, Timer timer) : IDisposable
    {
        public void Dispose()
        {
            watcher.Dispose();
            timer.Dispose();
        }
    }
}
