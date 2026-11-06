using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Automations;

/// <summary>A trigger source as the engine uses it: one of CodeAlta, or one a running plugin gives.</summary>
/// <param name="Trigger">What the source says of its trigger, and the source itself.</param>
/// <param name="PluginId">The plugin that gives it; null for a source of CodeAlta.</param>
/// <param name="ScopeProjectId">The project of a plugin of a project, whose automations only use it; null for any.</param>
internal sealed record AutomationSourceEntry(PluginAutomationTriggerContribution Trigger, string? PluginId, string? ScopeProjectId)
{
    /// <summary>The type of the source.</summary>
    public string Type => Trigger.Type;
}

/// <summary>
/// The trigger sources of a host: those of CodeAlta first, then those of the running plugins, one per type (the first
/// that gives it). It follows the signals of the sources, which ask to be looked at now.
/// </summary>
internal sealed class AutomationSourceRegistry
{
    private readonly object _gate = new();
    private readonly List<PluginAutomationTriggerContribution> _builtIn = [];
    private readonly Dictionary<PluginTriggerSignal, Action> _followed = [];
    private Func<IReadOnlyList<PluginAutomationTriggerRegistration>>? _plugins;

    /// <summary>Raised, on any thread, with the type of a source that asks to be looked at now.</summary>
    internal event Action<string>? Signaled;

    /// <summary>Gets or sets what gives the triggers of the running plugins as they are now; null for a host without plugins.</summary>
    internal Func<IReadOnlyList<PluginAutomationTriggerRegistration>>? Plugins
    {
        get { lock (_gate) return _plugins; }
        set { lock (_gate) _plugins = value; }
    }

    /// <summary>Adds a source of CodeAlta, whose type is a bare name.</summary>
    /// <param name="trigger">The source.</param>
    /// <exception cref="ArgumentException">Its type is not one of CodeAlta.</exception>
    internal void AddBuiltIn(PluginAutomationTriggerContribution trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        if (!AutomationSourceTypes.BuiltIn.Contains(trigger.Type, StringComparer.Ordinal)) throw new ArgumentException($"'{trigger.Type}' is not a trigger source of CodeAlta.", nameof(trigger));
        lock (_gate) _builtIn.Add(trigger);
    }

    /// <summary>Lists the sources as they are now.</summary>
    internal IReadOnlyList<AutomationSourceEntry> List()
    {
        var entries = new List<AutomationSourceEntry>();
        var types = new HashSet<string>(StringComparer.Ordinal);
        PluginAutomationTriggerContribution[] builtIn;
        Func<IReadOnlyList<PluginAutomationTriggerRegistration>>? source;
        lock (_gate) (builtIn, source) = ([.. _builtIn], _plugins);
        foreach (var trigger in builtIn)
        {
            if (types.Add(trigger.Type)) entries.Add(new(trigger, null, null));
        }

        IReadOnlyList<PluginAutomationTriggerRegistration> plugins;
        try
        {
            plugins = source?.Invoke() ?? [];
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The plugins could not be listed now: their sources come back at the next look.
            plugins = [];
        }

        foreach (var registration in plugins)
        {
            // The type of a plugin has its prefix: it never takes the place of a source of CodeAlta.
            if (PluginAutomationTriggerContribution.IsType(registration.Contribution.Type) && types.Add(registration.Contribution.Type))
                entries.Add(new(registration.Contribution, registration.PluginId, registration.Scope == PluginScope.Global ? null : registration.ScopeProjectId));
        }

        Follow(entries);
        return entries;
    }

    // Follows the signals of the sources there are, and leaves those of the sources gone.
    private void Follow(IReadOnlyList<AutomationSourceEntry> entries)
    {
        lock (_gate)
        {
            var signals = new Dictionary<PluginTriggerSignal, string>();
            foreach (var entry in entries)
            {
                if (entry.Trigger.Signal is { } signal) signals.TryAdd(signal, entry.Type);
            }

            foreach (var (signal, handler) in _followed.ToArray())
            {
                if (signals.ContainsKey(signal)) continue;
                signal.Raised -= handler;
                _followed.Remove(signal);
            }

            foreach (var (signal, type) in signals)
            {
                if (_followed.ContainsKey(signal)) continue;
                Action handler = () => Signaled?.Invoke(type);
                signal.Raised += handler;
                _followed[signal] = handler;
            }
        }
    }
}
