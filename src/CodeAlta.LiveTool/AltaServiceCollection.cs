namespace CodeAlta.LiveTool;

/// <summary>
/// Small deterministic service provider for in-process <c>alta</c> hosts and tests.
/// </summary>
public sealed class AltaServiceCollection : IServiceProvider
{
    private readonly Dictionary<Type, object> _services = new();

    /// <summary>Adds or replaces a service instance.</summary>
    public AltaServiceCollection Add<T>(T service)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(service);
        _services[typeof(T)] = service;
        return this;
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (_services.TryGetValue(serviceType, out var service))
        {
            return service;
        }

        foreach (var entry in _services)
        {
            if (serviceType.IsAssignableFrom(entry.Key))
            {
                return entry.Value;
            }
        }

        return null;
    }
}
