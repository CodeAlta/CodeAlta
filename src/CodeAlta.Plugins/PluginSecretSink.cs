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
    // Every value told before the host attached, in order and once each: none is dropped, however many.
    private readonly List<string> _pending = [];
    private readonly HashSet<string> _pendingSet = new(StringComparer.Ordinal);
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
            _pendingSet.Clear();
        }

        foreach (var value in pending) mask(value);
    }

    /// <inheritdoc />
    public CodeAlta.Agent.Runtime.IBoundSecretSource? BoundSecrets { get; private set; }

    /// <summary>Attaches the secrets kept in vaults the host holds, which the plugins that resolve references read.</summary>
    /// <param name="source">The source.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public void AttachBound(CodeAlta.Agent.Runtime.IBoundSecretSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        BoundSecrets = source;
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
                // Kept until the host attaches, every one: a value dropped here would be given out unmasked.
                if (_pendingSet.Add(value)) _pending.Add(value);
                return;
            }
        }

        target(value);
    }
}
