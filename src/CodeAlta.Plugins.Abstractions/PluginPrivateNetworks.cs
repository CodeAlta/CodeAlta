using System.Net;

namespace CodeAlta.Plugins.Abstractions;

/// <summary>What a private network does for CodeAlta.</summary>
[Flags]
public enum PrivateNetworkCapabilities
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>
    /// It says where the other devices of the network reach this computer directly, by a name and addresses
    /// (<see cref="IPrivateNetworkProvider.FindThisComputerAsync"/>): the fleet names them in its pairing links. Tailscale does.
    /// </summary>
    Presence = 1,

    /// <summary>
    /// It publishes a page this computer serves on the loopback address at an HTTPS address the person's devices open
    /// (<see cref="IPrivateNetworkProvider.CreatePublisher"/>): the Mobile plugin serves the phones that way. Tailscale Serve and
    /// a Dev Tunnel do.
    /// </summary>
    Publish = 2,

    /// <summary>A publication takes the port of the network it is published at (<see cref="PrivateNetworkPublication.PublicPort"/>).</summary>
    PublicPort = 4,

    /// <summary>
    /// A device signs in to the network in its browser before it reaches a page the network publishes, as with a Dev Tunnel. That
    /// sign-in keeps the path and the query of the address the device opened and loses its fragment (what follows <c>#</c>): a link
    /// that must carry something to the page through it, such as the pairing token of the Mobile plugin, carries it in the query.
    /// </summary>
    BrowserSignIn = 8,
}

/// <summary>
/// A private network: a way the person's own devices reach this computer without opening it to the Internet, such as Tailscale, a Dev
/// Tunnel or another VPN. CodeAlta has Tailscale and Dev Tunnels; a plugin adds another one by implementing
/// <see cref="IPrivateNetworkSource"/>. The fleet and the Mobile plugin use what each one says it can do.
/// </summary>
public interface IPrivateNetworkProvider
{
    /// <summary>Gets the id of the provider, as settings keep it: letters, digits and <c>-</c>, such as <c>tailscale</c>.</summary>
    string Id { get; }

    /// <summary>Gets the name people read, such as <c>Tailscale</c>.</summary>
    string DisplayName { get; }

    /// <summary>Gets a line for people that says what it needs, as a settings page shows it beside the name.</summary>
    string Description => "";

    /// <summary>Gets what it does.</summary>
    PrivateNetworkCapabilities Capabilities { get; }

    /// <summary>
    /// Finds this computer on the network: its name there and the addresses the other devices reach it at, the most useful first.
    /// Asked again at each use: the network may have started or stopped since. Only with <see cref="PrivateNetworkCapabilities.Presence"/>.
    /// </summary>
    /// <param name="cancellationToken">Stops the search.</param>
    /// <returns>This computer on the network, or null when it is not on it now.</returns>
    ValueTask<PrivateNetworkPresence?> FindThisComputerAsync(CancellationToken cancellationToken) => ValueTask.FromResult<PrivateNetworkPresence?>(null);

    /// <summary>
    /// Makes what publishes a page through the network, for one user of it (the Mobile plugin); the caller disposes it. Only with
    /// <see cref="PrivateNetworkCapabilities.Publish"/>.
    /// </summary>
    /// <param name="host">What the user gives the publisher: its state, its log, its background, and a way to say what changed.</param>
    /// <returns>The publisher, or null when the provider publishes nothing.</returns>
    IPrivateNetworkPublisher? CreatePublisher(IPrivateNetworkHost host) => null;
}

/// <summary>Gives the private networks a plugin brings, beside those of CodeAlta: implemented by the plugin class itself.</summary>
public interface IPrivateNetworkSource
{
    /// <summary>Gets the providers, read again at each use: a plugin may add one once it is configured.</summary>
    IReadOnlyList<IPrivateNetworkProvider> PrivateNetworks { get; }
}

/// <summary>Gives the private networks of this computer to a plugin: those of CodeAlta first, then those of the active plugins.</summary>
public interface IPluginPrivateNetworkService
{
    /// <summary>Gets the providers, read again at each call.</summary>
    IReadOnlyList<IPrivateNetworkProvider> Providers { get; }
}

/// <summary>A host that has no private network for plugins.</summary>
public sealed class NoopPluginPrivateNetworkService : IPluginPrivateNetworkService
{
    /// <summary>Gets the one instance.</summary>
    public static NoopPluginPrivateNetworkService Instance { get; } = new();

    /// <inheritdoc />
    public IReadOnlyList<IPrivateNetworkProvider> Providers => [];
}

/// <summary>This computer on a private network.</summary>
/// <param name="Name">Its name there, such as <c>desk.tail1234.ts.net</c>, which reaches it at home and away.</param>
/// <param name="Addresses">Its addresses there, the most useful first.</param>
public sealed record PrivateNetworkPresence(string Name, IReadOnlyList<IPAddress> Addresses);

/// <summary>What to publish.</summary>
/// <param name="LocalPort">The port this computer serves the page on, on the loopback address.</param>
/// <param name="PublicPort">The port of the network to publish it at, for a provider with <see cref="PrivateNetworkCapabilities.PublicPort"/>; null for its own.</param>
public sealed record PrivateNetworkPublication(int LocalPort, int? PublicPort = null);

/// <summary>An action a state of a publisher offers, such as a sign-in, which a page shows as a button.</summary>
/// <param name="Id">What <see cref="IPrivateNetworkPublisher.ActAsync"/> is given, such as <c>signin-github</c>.</param>
/// <param name="Label">The label of the button, for people.</param>
public sealed record PrivateNetworkAction(string Id, string Label);

/// <summary>What a publisher says it does.</summary>
/// <param name="State">
/// <c>pending</c> while it publishes, <c>serving</c> once the page is reached at <paramref name="Url"/>, <c>stopped</c>, <c>failed</c>,
/// or a state of its own that says what keeps the devices out (Tailscale: <c>not-installed</c>…; Dev Tunnels: <c>not-signed-in</c>…).
/// </param>
/// <param name="Url">The HTTPS address the devices open, while it serves.</param>
/// <param name="Detail">A line for people: what it does, or what to do.</param>
/// <param name="FixUrl">The page that fixes the state, when there is one.</param>
public sealed record PrivateNetworkStatus(string State, string? Url, string Detail, string? FixUrl = null)
{
    /// <summary>Gets a value indicating whether it is at a step that runs (publishing, a sign-in): no problem, only a wait.</summary>
    public bool Working { get; init; }

    /// <summary>Gets the actions this state offers.</summary>
    public IReadOnlyList<PrivateNetworkAction> Actions { get; init; } = [];

    /// <summary>A publisher at work, with what it does now.</summary>
    /// <param name="detail">What it does.</param>
    /// <returns>The status.</returns>
    public static PrivateNetworkStatus Pending(string detail) => new("pending", null, detail) { Working = true };
}

/// <summary>
/// Publishes a page through a private network for one user, such as the Mobile plugin: <see cref="PublishAsync"/> again replaces what it
/// published before, <see cref="UnpublishAsync"/> takes it off before the user changes its settings or stops.
/// </summary>
public interface IPrivateNetworkPublisher : IAsyncDisposable
{
    /// <summary>Publishes the page, or says why it cannot.</summary>
    /// <param name="publication">What to publish.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <returns>What it does now.</returns>
    Task<PrivateNetworkStatus> PublishAsync(PrivateNetworkPublication publication, CancellationToken cancellationToken);

    /// <summary>Takes off what <see cref="PublishAsync"/> published.</summary>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <returns>A task that completes once it is taken off.</returns>
    Task UnpublishAsync(CancellationToken cancellationToken);

    /// <summary>Runs an action of a state (<see cref="PrivateNetworkStatus.Actions"/>): it starts, and its outcome is reported.</summary>
    /// <param name="action">The id of the action.</param>
    /// <param name="cancellationToken">Stops the start.</param>
    /// <returns>What it does now that it runs; null when it does not know the action.</returns>
    Task<PrivateNetworkStatus?> ActAsync(string action, CancellationToken cancellationToken) => Task.FromResult<PrivateNetworkStatus?>(null);
}

/// <summary>What the user of a publisher gives it. A publisher that was replaced (the settings changed, the user stopped) says nothing any more.</summary>
public interface IPrivateNetworkHost
{
    /// <summary>Reads a value the publisher kept between starts, such as the tunnel it made.</summary>
    /// <param name="key">The name of the value.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The value, or null when none was kept.</returns>
    Task<string?> ReadStateAsync(string key, CancellationToken cancellationToken);

    /// <summary>Keeps a value between starts; null forgets it.</summary>
    /// <param name="key">The name of the value.</param>
    /// <param name="value">The value.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    /// <returns>A task that completes once it is kept.</returns>
    Task WriteStateAsync(string key, string? value, CancellationToken cancellationToken);

    /// <summary>
    /// Says a new status from the background, outside of a publication (a host that stopped, a sign-in that ended). Never awaited from
    /// <see cref="IPrivateNetworkPublisher.PublishAsync"/> or <see cref="IPrivateNetworkPublisher.ActAsync"/>: it waits for them to end.
    /// </summary>
    /// <param name="status">The status.</param>
    /// <returns>A task that completes once it is shown.</returns>
    Task ReportAsync(PrivateNetworkStatus status);

    /// <summary>Has the user publish again, as Try again does: once a sign-in ended, for example.</summary>
    /// <returns>A task that completes once it published.</returns>
    Task RepublishAsync();

    /// <summary>Runs work in the background of the user, stopped with it.</summary>
    /// <param name="name">The name of the work.</param>
    /// <param name="description">What it does, for people.</param>
    /// <param name="work">The work.</param>
    void Run(string name, string description, Func<CancellationToken, ValueTask> work);

    /// <summary>Writes a line to the log of the user.</summary>
    /// <param name="line">The line.</param>
    void Log(string line);
}
