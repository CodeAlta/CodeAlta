using System.Collections.Concurrent;
using Acornima.Ast;
using Jint;

namespace CodeAlta.CodeMode;

/// <summary>
/// Parsed scripts kept for the next runs: the prelude of each API, and scripts that run again (library scripts).
/// A prepared script is immutable and thread-safe, so engines of any session share it. One cache per host,
/// bounded; never a static.
/// </summary>
public sealed class CodeModeScriptCache
{
    private readonly ConcurrentDictionary<string, Prepared<Script>> _scripts = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _order = new();
    private readonly int _capacity;

    /// <summary>Creates a cache.</summary>
    /// <param name="capacity">The most scripts it keeps.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is not positive.</exception>
    public CodeModeScriptCache(int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    /// <summary>Gets how many scripts the cache holds.</summary>
    public int Count => _scripts.Count;

    /// <summary>Gets a prepared script, preparing it the first time.</summary>
    /// <param name="key">The key: a hash of the code and its name.</param>
    /// <param name="code">The code.</param>
    /// <param name="sourceName">The name of the source in stacks.</param>
    /// <returns>The prepared script.</returns>
    internal Prepared<Script> GetOrPrepare(string key, string code, string sourceName)
    {
        if (_scripts.TryGetValue(key, out var prepared))
        {
            return prepared;
        }

        prepared = Engine.PrepareScript(code, sourceName, strict: true);
        if (_scripts.TryAdd(key, prepared))
        {
            _order.Enqueue(key);
            while (_scripts.Count > _capacity && _order.TryDequeue(out var oldest))
            {
                _scripts.TryRemove(oldest, out _);
            }
        }

        return prepared;
    }
}
