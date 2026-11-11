using CodeAlta.Agent;
using CodeAlta.LiveTool;

namespace CodeAlta.Node;

/// <summary>
/// The configuration of the node, which <c>alta provider refresh</c> applies again before it tests the providers: the
/// providers changed in <c>config.toml</c> since they were last applied start again at once, as the watch of the file
/// would start them once the file settled, so that the providers tested are those the file now gives.
/// </summary>
/// <param name="registry">The providers of the node.</param>
internal sealed class NodeAltaProviderConfiguration(IModelProviderRegistry registry) : IAltaProviderConfiguration
{
    private NodeProviders? _providers;

    /// <summary>Gets or sets the providers of the fleet part of the node, which apply its configuration; null until it starts.</summary>
    internal NodeProviders? Providers
    {
        get => Volatile.Read(ref _providers);
        set => Volatile.Write(ref _providers, value);
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The providers of the node are not started yet.</exception>
    /// <exception cref="InvalidDataException">The configuration file does not read.</exception>
    public Task<int> ApplyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var providers = Providers ?? throw new InvalidOperationException("The providers of this node are not started yet.");
        providers.ApplyConfigurationChanges();
        // A file that does not read leaves the providers as they run: the command says where it is wrong, never what it holds.
        if (providers.ConfigurationError is { } error) throw new InvalidDataException(error);
        return Task.FromResult(registry.ListProviders().Count);
    }
}
