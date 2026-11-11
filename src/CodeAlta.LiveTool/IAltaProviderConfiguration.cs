namespace CodeAlta.LiveTool;

/// <summary>
/// The providers of the configuration of a host (<c>config.toml</c>), which <c>alta provider refresh</c> reads again
/// before it tests them. A host without it only tests the providers it has.
/// </summary>
public interface IAltaProviderConfiguration
{
    /// <summary>
    /// Registers again the providers the configuration gives, as <b>Save and apply</b> does: a provider added or enabled is
    /// registered, one removed or disabled is unregistered, and each one has a new runtime.
    /// </summary>
    /// <param name="cancellationToken">Ends the wait.</param>
    /// <returns>The number of providers registered.</returns>
    /// <exception cref="InvalidOperationException">A provider is being prepared and cannot be replaced now.</exception>
    /// <exception cref="IOException">The configuration cannot be read.</exception>
    /// <exception cref="InvalidDataException">The configuration is not valid.</exception>
    Task<int> ApplyAsync(CancellationToken cancellationToken);
}
