using CodeAlta.Catalog;

namespace CodeAlta.Node;

// The configuration of the node edited by hand (#414): a provider written into config.toml, changed or taken out while the node runs
// is applied within a second, as a change from the desktop is, rather than at the next start. Each provider section is compared with
// what was applied last, so that only the providers that changed start again; the changes the node writes itself are applied already.
// A file that does not parse leaves the running providers as they are, and the page of the machine says where it is wrong.
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
        var timer = new Timer(_ => ApplyConfigurationChanges(), null, Timeout.Infinite, Timeout.Infinite);
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

    /// <summary>Applies the providers whose section of the configuration changed since they were last applied.</summary>
    /// <returns>The keys of the providers applied again.</returns>
    internal IReadOnlyList<string> ApplyConfigurationChanges()
    {
        List<string> changed;
        lock (_gate)
        {
            if (_applied is not { } applied) return [];
            if (Fingerprints(host.CatalogOptions.ConfigPath, out var error) is not { } now)
            {
                _configurationError = error;
                return [];
            }

            _configurationError = null;
            changed = [.. now.Where(pair => !applied.TryGetValue(pair.Key, out var before) || before != pair.Value).Select(static pair => pair.Key)
                .Concat(applied.Keys.Where(key => !now.ContainsKey(key)))];
            _applied = now;
        }

        foreach (var key in changed)
        {
            // A provider of the node is started again or taken away; one of a plugin is registered again by its plugin.
            lock (_gate) Apply(Store, key);
            RegisterPlugin(key);
        }

        return changed;
    }

    // What was written is what is applied: a change the node made itself is not applied a second time when the watch sees it.
    private void RememberApplied()
    {
        if (_applied is null) return;
        if (Fingerprints(host.CatalogOptions.ConfigPath, out _) is { } now) _applied = now;
    }

    // The section of each provider of a configuration file, as text; null when the file does not read, with why (null while it is
    // still being written: it is read again at its next change).
    private static Dictionary<string, string>? Fingerprints(string path, out string? error)
    {
        error = null;
        try
        {
            return new Dictionary<string, string>(CodeAltaConfigStore.ReadProviderSections(path), StringComparer.OrdinalIgnoreCase);
        }
        catch (InvalidDataException exception)
        {
            error = exception.Message.Length <= 512 ? exception.Message : exception.Message[..512];
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed class Watch(FileSystemWatcher watcher, Timer timer) : IDisposable
    {
        public void Dispose()
        {
            watcher.Dispose();
            timer.Dispose();
        }
    }
}