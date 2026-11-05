using System.Reflection;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Tests;

/// <summary>
/// A store that passes every call to a real one, and lets a test act before a call or answer it instead: what a service does
/// at a moment no endpoint can reach (a request cancelled once a change committed, a check that read the store before a change).
/// </summary>
public class InterceptedStore : DispatchProxy
{
    private ICoordinatorStore _inner = null!;
    private Func<string, object?[]?, object?>? _replace;
    private Action<string, object?[]?>? _before;

    /// <summary>Makes a store over a real one.</summary>
    /// <param name="inner">The real store.</param>
    /// <param name="replace">Gives the result of a call instead of the real store (its method name and arguments); null calls the real store.</param>
    /// <param name="before">Called before each call, with its method name and arguments.</param>
    /// <returns>The store.</returns>
    public static ICoordinatorStore Over(ICoordinatorStore inner, Func<string, object?[]?, object?>? replace = null, Action<string, object?[]?>? before = null)
    {
        var store = Create<ICoordinatorStore, InterceptedStore>();
        var proxy = (InterceptedStore)(object)store;
        proxy._inner = inner;
        proxy._replace = replace;
        proxy._before = before;
        return store;
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        _before?.Invoke(targetMethod.Name, args);
        // A cancelled token stops the call, as the store's own reads and writes would.
        foreach (var arg in args ?? []) if (arg is CancellationToken token) token.ThrowIfCancellationRequested();
        if (_replace?.Invoke(targetMethod.Name, args) is { } replaced) return replaced;
        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }
}
