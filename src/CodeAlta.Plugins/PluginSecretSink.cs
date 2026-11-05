using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins;

/// <summary>
/// The masking the plugins of a runtime tell the values of secrets they give out (<see cref="IPluginSecretService"/>): the host
/// attaches its masking once it runs (<see cref="Attach"/>), and what the plugins told before is told to it then, so that a
/// plugin started before the host (a runtime started early) masks what it gave from the start.
/// </summary>
public sealed class PluginSecretSink : IPluginSecretService
{
    private readonly Lock _gate = new();
    private readonly List<string> _pending = [];
    private Action<string>? _target;

    /// <summary>Attaches the masking of the host: it is told what the plugins told before, then each value they tell.</summary>
    /// <param name="mask">The masking.</param>
    /// <exception cref="ArgumentNullException"><paramref name="mask"/> is <see langword="null"/>.</exception>
    public void Attach(Action<string> mask)
    {
        ArgumentNullException.ThrowIfNull(mask);
        string[] pending;
        lock (_gate)
        {
            _target = mask;
            pending = [.. _pending];
            _pending.Clear();
        }

        foreach (var value in pending) mask(value);
    }

    /// <inheritdoc />
    public void Mask(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Action<string>? target;
        lock (_gate)
        {
            target = _target;
            if (target is null)
            {
                // Bounded meanwhile: a host that never attaches keeps a few values of no use, never all of them.
                if (_pending.Count < 1024 && !_pending.Contains(value, StringComparer.Ordinal)) _pending.Add(value);
                return;
            }
        }

        target(value);
    }
}
