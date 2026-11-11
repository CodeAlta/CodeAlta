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
    // A file an editor is still writing reads within a moment: it is read again a few times before the refresh fails.
    private const int ReadAttempts = 4;
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(250);

    private NodeProviders? _providers;

    /// <summary>Gets or sets the providers of the fleet part of the node, which apply its configuration; null until it starts.</summary>
    internal NodeProviders? Providers
    {
        get => Volatile.Read(ref _providers);
        set => Volatile.Write(ref _providers, value);
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The providers of the node are not started yet.</exception>
    /// <exception cref="IOException">The configuration file cannot be read now (another program holds it).</exception>
    /// <exception cref="InvalidDataException">The configuration file does not read.</exception>
    public async Task<int> ApplyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var providers = Providers ?? throw new InvalidOperationException("The providers of this node are not started yet.");
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                providers.ApplyConfigurationChanges(requireRead: true);
                break;
            }
            catch (IOException) when (attempt < ReadAttempts)
            {
                await Task.Delay(ReadRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        // A file that does not read leaves the providers as they run: the command says where it is wrong, never what it holds.
        if (providers.ConfigurationError is { } error) throw new InvalidDataException(error);
        return registry.ListProviders().Count;
    }
}
