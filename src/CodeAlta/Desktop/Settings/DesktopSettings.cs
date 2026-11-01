using System.Collections.Frozen;
using System.Text.Json;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Desktop.Windows;
using CodeAlta.LiveTool;

namespace CodeAlta.Desktop.Settings;

/// <summary>The settings a source other than the catalog declares: those of the plugins.</summary>
internal interface IDesktopSettingsExtension
{
    /// <summary>Raised when the settings it declares changed (a plugin started, stopped or was built again).</summary>
    event Action? Changed;

    /// <summary>Lists the settings it declares now.</summary>
    IReadOnlyList<AltaSettingDescriptor> List();

    /// <summary>Reads a value of one of its settings.</summary>
    ValueTask<AltaSettingRead> GetAsync(AltaSettingDescriptor setting, string? projectId, CancellationToken cancellationToken);

    /// <summary>Writes a value of one of its settings, already checked against the setting; null for its default.</summary>
    ValueTask<AltaSettingWrite> SetAsync(AltaSettingDescriptor setting, JsonElement? value, string? projectId, AltaSettingOrigin origin, CancellationToken cancellationToken);
}

/// <summary>
/// The settings of the application as one registry: the built-in ones of the catalog, bound to the stores the host keeps
/// or kept by the page of the main window, and those of the plugins. It is what <c>alta settings</c>, the search of
/// Settings and the index read, and the one place that decides who may change what.
/// </summary>
/// <remarks>
/// The settings the page keeps (theme, language, fonts...) stay in the page: it reports their values here, and applies
/// the changes it is passed. A change made by anyone but the window is told to the page, which shows it with Undo.
/// </remarks>
internal sealed class DesktopSettings : IAltaSettings
{
    private static readonly TimeSpan WindowAnswer = TimeSpan.FromSeconds(3);
    // The page applies a change it is asked for only before this, well before the host stops waiting for its answer:
    // a change the host reports as not made is never made later.
    private static readonly TimeSpan WindowApply = TimeSpan.FromSeconds(2.5);

    private readonly DesktopSettingsCatalog _catalog;
    private readonly FrozenDictionary<string, DesktopSettingBinding> _bindings;
    private readonly DesktopPageWatchers<SettingsEvent> _watchers = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, JsonElement> _window = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<AltaSettingChoice>> _windowChoices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource<string?>> _pending = new(StringComparer.Ordinal);
    // One change of a setting of the host at a time: each reads the value before and after its own write.
    private readonly SemaphoreSlim _hostWrites = new(1, 1);
    private IDesktopSettingsExtension? _plugins;

    /// <summary>Creates the registry over the catalog and the bindings of the host.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    internal DesktopSettings(DesktopSettingsCatalog catalog, FrozenDictionary<string, DesktopSettingBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(bindings);
        _catalog = catalog;
        _bindings = bindings;
    }

    /// <summary>Gets the catalog of the built-in settings.</summary>
    internal DesktopSettingsCatalog Catalog => _catalog;

    /// <summary>Chooses the window Settings opens in, and brings it to the front; null for the main one, as it is.</summary>
    internal Func<string?, string>? Router { get; set; }

    /// <summary>Writes a line to the log of the application: each change, and who made it; null to log nothing.</summary>
    internal Action<string>? Log { get; set; }

    /// <summary>Raised when the settings that exist changed: the index and the pages read them again.</summary>
    internal event Action? CatalogChanged;

    /// <summary>Gets or sets the settings of the plugins; null where plugins declare none.</summary>
    internal IDesktopSettingsExtension? Plugins
    {
        get => Volatile.Read(ref _plugins);
        set
        {
            if (Volatile.Read(ref _plugins) is { } old) old.Changed -= OnPluginsChanged;
            Volatile.Write(ref _plugins, value);
            if (value is not null) value.Changed += OnPluginsChanged;
            OnPluginsChanged();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<AltaSettingDescriptor> List()
    {
        var built = _catalog.Settings.Select(Present);
        return Plugins is { } plugins ? [.. built, .. plugins.List()] : [.. built];
    }

    /// <inheritdoc />
    public async ValueTask<AltaSettingRead> GetAsync(string id, string? projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (Find(id, projectId) is not { } setting) return new(AltaSettingStatus.NotFound, null, null, $"No setting is named {id}.");
        if (setting.Kind == AltaSettingKinds.Collection) return new(AltaSettingStatus.Managed, null, null, Managed(setting));
        if (!setting.Available) return new(AltaSettingStatus.Unavailable, null, null, $"{setting.Id} is not available here.");
        if (setting.Plugin is not null) return Plugins is { } plugins ? await plugins.GetAsync(setting, projectId, cancellationToken).ConfigureAwait(false) : Unavailable(setting);
        if (setting.Owner == "window")
        {
            lock (_gate)
            {
                return _window.TryGetValue(setting.Id, out var value)
                    ? new(AltaSettingStatus.Ok, value, SettingValues.Same(value, setting.Default) ? "default" : "window", null) { Choices = _windowChoices.GetValueOrDefault(setting.Id) }
                    : new(AltaSettingStatus.Unavailable, null, null, $"{setting.Id} is kept by the window, which has not given it yet.");
            }
        }

        if (!_bindings.TryGetValue(setting.Id, out var binding))
            return new(setting.Kind == AltaSettingKinds.Complex ? AltaSettingStatus.Complex : AltaSettingStatus.Unavailable, null, null, $"{setting.Id} is read on its page: Settings › {setting.PagePath}.");
        try
        {
            var value = await Task.Run(binding.Read, cancellationToken).ConfigureAwait(false);
            return new(AltaSettingStatus.Ok, value, SettingValues.Same(value, setting.Default) || setting.Default is null && setting.Kind == AltaSettingKinds.Complex ? "default" : "user", null)
            {
                Choices = binding.Choices?.Invoke(),
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or FormatException)
        {
            return new(AltaSettingStatus.Failed, null, null, $"{setting.Id} could not be read: {exception.Message}");
        }
    }

    /// <inheritdoc />
    public async ValueTask<AltaSettingWrite> SetAsync(string id, JsonElement? value, string? projectId, AltaSettingOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(origin);
        if (Find(id, projectId) is not { } setting) return new(AltaSettingStatus.NotFound, null, null, $"No setting is named {id}.");
        if (setting.Kind == AltaSettingKinds.Collection) return new(AltaSettingStatus.Managed, null, null, Managed(setting));
        if (setting.Kind == AltaSettingKinds.Complex) return new(AltaSettingStatus.Complex, null, null, $"{setting.Id} is changed on its page: Settings › {setting.PagePath}.");
        if (!setting.Writable) return new(AltaSettingStatus.PageOnly, null, null, $"{setting.Id} is changed on its page, which asks first: Settings › {setting.PagePath}.");
        // The settings that protect the user are the user's: only the window changes them.
        if (!origin.IsWindow && setting.Agents != AltaSettingAccess.ReadWrite)
            return new(AltaSettingStatus.ReadOnly, null, null, $"Only the user changes {setting.Id}, in Settings › {setting.PagePath}.");
        if (!setting.Available) return new(AltaSettingStatus.Unavailable, null, null, $"{setting.Id} is not available here.");

        var wanted = value is { ValueKind: not JsonValueKind.Null } provided ? provided : setting.Default;
        var choices = setting.Owner == "window" ? WindowChoices(setting.Id) : _bindings.GetValueOrDefault(setting.Id)?.Choices?.Invoke();
        JsonElement normalized = default;
        if (setting.Plugin is null)
        {
            if (wanted is not { } checkedValue) return new(AltaSettingStatus.Invalid, null, null, $"{setting.Id} has no default: give a value.");
            if (SettingValues.Check(setting, checkedValue, choices, out normalized) is { } error) return new(AltaSettingStatus.Invalid, null, null, error);
        }

        AltaSettingWrite result;
        if (setting.Plugin is not null)
        {
            if (Plugins is not { } plugins) return new(AltaSettingStatus.Unavailable, null, null, $"The plugin of {setting.Id} is not running.");
            // A plugin keeps no default in the file: a reset removes the value.
            JsonElement? pluginValue = null;
            if (value is { ValueKind: not JsonValueKind.Null } given)
            {
                if (SettingValues.Check(setting, given, choices, out var checkedValue) is { } error) return new(AltaSettingStatus.Invalid, null, null, error);
                pluginValue = checkedValue;
            }

            result = await plugins.SetAsync(setting, pluginValue, projectId, origin, cancellationToken).ConfigureAwait(false);
        }
        else if (setting.Owner == "window")
        {
            result = await SetWindowAsync(setting, normalized, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            result = await SetHostAsync(setting, normalized, cancellationToken).ConfigureAwait(false);
        }

        if (result.Status == AltaSettingStatus.Ok && !SettingValues.Same(result.Value, result.Previous)) Announce(setting, result, projectId, origin);
        return result;
    }

    /// <inheritdoc />
    public bool Open(string? page, string? id, string? callerSessionId)
    {
        var setting = id is null ? null : Find(id, null);
        var shown = page ?? setting?.Page;
        if (shown is null) return false;
        return _watchers.Publish(new SettingsEvent("show") { Page = shown, Id = setting?.Id }, Router?.Invoke(callerSessionId) ?? DesktopWindows.MainLabel);
    }

    /// <summary>Registers a page for the events of the settings; disposing the result ends it.</summary>
    /// <param name="label">The label of the view of the window of the page; null for the main one.</param>
    /// <param name="watcher">What receives the events.</param>
    internal IDisposable Watch(string? label, Action<SettingsEvent> watcher) => _watchers.Watch(label, watcher);

    /// <summary>
    /// Takes the values of the settings the page of the main window keeps: all of them when it starts, then each one
    /// that changes; and the answer to a change it was asked for.
    /// </summary>
    /// <param name="values">The ids and the values, with the choices of a choice whose choices change.</param>
    /// <param name="requestId">The change this answers, or null.</param>
    /// <param name="error">Why the page refused the change, or null.</param>
    internal void ReportWindow(IReadOnlyList<(string Id, JsonElement Value, IReadOnlyList<AltaSettingChoice>? Choices)> values, string? requestId, string? error)
    {
        ArgumentNullException.ThrowIfNull(values);
        TaskCompletionSource<string?>? answer = null;
        lock (_gate)
        {
            foreach (var (id, value, choices) in values)
            {
                if (_catalog.Find(id) is not { Owner: "window" }) continue;
                _window[id] = value;
                if (choices is not null) _windowChoices[id] = choices;
            }

            if (requestId is not null && _pending.Remove(requestId, out answer)) { }
        }

        answer?.TrySetResult(error);
    }

    // What the window is given of a built-in setting: its availability where the host decides it.
    private AltaSettingDescriptor Present(AltaSettingDescriptor setting)
    {
        if (setting.Owner == "window" || setting.Kind is AltaSettingKinds.Collection) return setting;
        if (!_bindings.TryGetValue(setting.Id, out var binding)) return setting.Kind == AltaSettingKinds.Complex ? setting : setting with { Available = false };
        return binding.Available is { } available && !available() ? setting with { Available = false } : setting;
    }

    // A setting by its id: a plugin of the project named, else one of the user. Two plugins of two projects may have the
    // same id; a setting of another project than the one named is never taken.
    private AltaSettingDescriptor? Find(string id, string? projectId)
    {
        var key = id.Trim();
        if (_catalog.Find(key) is { } builtIn) return Present(builtIn);
        var matches = Plugins?.List().Where(setting => string.Equals(setting.Id, key, StringComparison.Ordinal)).ToList() ?? [];
        return matches.FirstOrDefault(setting => projectId is not null && string.Equals(setting.Project, projectId, StringComparison.Ordinal))
            ?? matches.FirstOrDefault(static setting => setting.Project is null)
            ?? (projectId is null ? matches.FirstOrDefault() : null);
    }

    private IReadOnlyList<AltaSettingChoice>? WindowChoices(string id)
    {
        lock (_gate) return _windowChoices.GetValueOrDefault(id);
    }

    private async ValueTask<AltaSettingWrite> SetHostAsync(AltaSettingDescriptor setting, JsonElement value, CancellationToken cancellationToken)
    {
        if (!_bindings.TryGetValue(setting.Id, out var binding) || binding.Write is not { } write) return Unwritable(setting);
        await _hostWrites.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var previous = binding.Read();
                if (write(value) is { } refused) return new AltaSettingWrite(AltaSettingStatus.Invalid, null, previous, refused);
                return new AltaSettingWrite(AltaSettingStatus.Ok, binding.Read(), previous, null);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or FormatException)
        {
            return new(AltaSettingStatus.Failed, null, null, $"{setting.Id} could not be written: {exception.Message}");
        }
        finally
        {
            _hostWrites.Release();
        }
    }

    // The page of the main window keeps the setting: it is asked to change it, and answers with the new value.
    private async ValueTask<AltaSettingWrite> SetWindowAsync(AltaSettingDescriptor setting, JsonElement value, CancellationToken cancellationToken)
    {
        JsonElement? previous;
        var requestId = Guid.NewGuid().ToString("N");
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            previous = _window.TryGetValue(setting.Id, out var known) ? known : null;
            _pending[requestId] = answer;
        }

        try
        {
            var expires = DateTimeOffset.UtcNow.Add(WindowApply).ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!_watchers.Publish(new SettingsEvent("write") { Id = setting.Id, Value = SettingValues.Text(value), RequestId = requestId, Expires = expires }, DesktopWindows.MainLabel))
                return new(AltaSettingStatus.Unavailable, null, previous, $"{setting.Id} is kept by the window, which is not there.");
            var error = await answer.Task.WaitAsync(WindowAnswer, cancellationToken).ConfigureAwait(false);
            if (error is not null) return new(AltaSettingStatus.Invalid, null, previous, error);
            lock (_gate) return new(AltaSettingStatus.Ok, _window.TryGetValue(setting.Id, out var now) ? now : value, previous, null);
        }
        catch (TimeoutException)
        {
            return new(AltaSettingStatus.Unavailable, null, previous, $"The window did not answer the change of {setting.Id}.");
        }
        finally
        {
            lock (_gate) _pending.Remove(requestId);
        }
    }

    // A change made by anyone but the user in the window: the window shows it with Undo, and the log keeps it.
    private void Announce(AltaSettingDescriptor setting, AltaSettingWrite result, string? projectId, AltaSettingOrigin origin)
    {
        Log?.Invoke($"{setting.Id} changed from {SettingValues.Text(result.Previous)} to {SettingValues.Text(result.Value)} by {origin.Kind}{(origin.SessionId is { } session ? " " + session : "")}{(origin.Name is { } name ? " " + name : "")}.");
        if (origin.IsWindow) return;
        _watchers.Publish(new SettingsEvent("changed")
        {
            Id = setting.Id, Value = SettingValues.Text(result.Value), Previous = SettingValues.Text(result.Previous),
            Origin = origin.Kind, SessionId = origin.SessionId, Who = origin.Name, ProjectId = projectId,
        }, DesktopWindows.MainLabel);
    }

    private void OnPluginsChanged()
    {
        CatalogChanged?.Invoke();
        _watchers.Publish(new SettingsEvent("catalog"), null);
    }

    private static string Managed(AltaSettingDescriptor setting)
        => setting.ManagedBy is { } commands
            ? $"{setting.Id} is a list of items: use `{commands}`, or Settings › {setting.PagePath}."
            : $"{setting.Id} is a list of items, changed in Settings › {setting.PagePath}.";

    private static AltaSettingRead Unavailable(AltaSettingDescriptor setting) => new(AltaSettingStatus.Unavailable, null, null, $"{setting.Id} is not available here.");

    private static AltaSettingWrite Unwritable(AltaSettingDescriptor setting) => new(AltaSettingStatus.PageOnly, null, null, $"{setting.Id} is changed on its page: Settings › {setting.PagePath}.");
}
