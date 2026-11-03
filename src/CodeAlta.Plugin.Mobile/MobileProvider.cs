namespace CodeAlta.Plugin.Mobile;

/// <summary>What a provider of the connection says of itself, as the tab and the settings page show it.</summary>
/// <param name="State">
/// <c>pending</c> while it publishes, <c>serving</c> once phones reach the view at <paramref name="Url"/>, <c>stopped</c>, or a state of
/// the provider that says what keeps phones out (Tailscale: <c>not-installed</c>, <c>serve-disabled</c>…; Dev Tunnels:
/// <c>not-signed-in</c>…). The pages find the words and the buttons of each state of each provider.
/// </param>
/// <param name="Url">The address phones open, while the view is served.</param>
/// <param name="Detail">A line for people: the words of the provider, or of the tool it uses.</param>
/// <param name="FixUrl">The page that fixes the state, when there is one.</param>
internal sealed record MobileProviderStatus(string State, string? Url, string Detail, string? FixUrl = null)
{
    /// <summary>Gets a value indicating whether the provider is at a step that runs (publishing, a sign-in): no problem, only a wait.</summary>
    public bool Working { get; init; }

    /// <summary>A provider that publishes the view, with what it does now.</summary>
    internal static MobileProviderStatus Pending(string detail) => new("pending", null, detail) { Working = true };

    /// <summary>A provider that serves nothing: the plugin is off.</summary>
    internal static MobileProviderStatus Stopped { get; } = new("stopped", null, "The mobile view is not served.");
}

/// <summary>How the server of the view listens for a provider: on the loopback address, for a provider that publishes it.</summary>
/// <param name="Port">The port of this computer.</param>
internal sealed record MobileListen(int Port);

/// <summary>What the plugin gives a provider.</summary>
internal interface IMobileProviderHost
{
    /// <summary>Gets the settings of now.</summary>
    MobileSettings Settings { get; }

    /// <summary>Changes and saves the settings: what the provider keeps between starts (a tunnel it made).</summary>
    Task SaveAsync(Func<MobileSettings, MobileSettings> change);

    /// <summary>
    /// Says a new status of the provider from its background, outside of a publication (a host that stopped, a sign-in that ended);
    /// ignored once the provider was replaced. Never awaited from <see cref="IMobileProvider.PublishAsync"/> or
    /// <see cref="IMobileProvider.ActAsync"/>: it waits for them to end.
    /// </summary>
    Task ReportAsync(MobileProviderStatus status);

    /// <summary>Publishes the view again, as Try again does: once a sign-in ended, for example.</summary>
    Task RepublishAsync();

    /// <summary>Runs work in the background of the plugin, stopped with it.</summary>
    void Run(string name, string description, Func<CancellationToken, ValueTask> work);

    /// <summary>Writes a line to the log of the plugin.</summary>
    void Log(string line);
}

/// <summary>
/// A way phones reach the view: Tailscale Serve, a Dev Tunnel. The plugin knows only the provider of the settings: it
/// starts the server as the provider says, asks it to publish the view, and shows what it says.
/// </summary>
internal interface IMobileProvider : IAsyncDisposable
{
    /// <summary>Gets the id of the provider, as the settings keep it: <c>tailscale</c>, <c>devtunnel</c>.</summary>
    string Id { get; }

    /// <summary>How the server listens for the settings.</summary>
    MobileListen Listen(MobileSettings settings);

    /// <summary>Why settings cannot work with this provider, or null: checked before they are saved.</summary>
    string? Refusal(MobileSettings settings) => null;

    /// <summary>Publishes the view the server serves; called with the server listening as <see cref="Listen"/> says.</summary>
    /// <returns>What the provider does now.</returns>
    Task<MobileProviderStatus> PublishAsync(CancellationToken cancellationToken);

    /// <summary>Takes off what <see cref="PublishAsync"/> published, before the settings change or the plugin stops.</summary>
    Task UnpublishAsync(CancellationToken cancellationToken);

    /// <summary>Runs an action the tab offers for a state of the provider (<c>signin-github</c>…): it starts, and its outcome is reported.</summary>
    /// <returns>What the provider does now that it runs; null when the provider does not know the action.</returns>
    Task<MobileProviderStatus?> ActAsync(string action, CancellationToken cancellationToken) => Task.FromResult<MobileProviderStatus?>(null);
}
