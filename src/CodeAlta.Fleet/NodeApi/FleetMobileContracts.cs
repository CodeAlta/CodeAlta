namespace CodeAlta.Fleet.NodeApi;

/// <summary>Whether a node serves the page of the phones (the Mobile plugin), for <c>control.mobile.*</c>.</summary>
/// <param name="Available">Whether this node can serve it: it has the plugin and the files of the page.</param>
/// <param name="Enabled">Whether it is turned on.</param>
/// <param name="Running">Whether the plugin runs now.</param>
/// <param name="Refusal">Why the node does not serve it, for people; null when nothing keeps it from it.</param>
public sealed record FleetMobileState(bool Available, bool Enabled, bool Running, string? Refusal = null);

/// <summary>The parameters of <c>control.mobile.enable</c>.</summary>
/// <param name="Enabled">Whether the page of the phones is turned on.</param>
public sealed record FleetMobileEnableParams(bool Enabled);

/// <summary>The parameters of <c>control.mobile.invoke</c>: one call of the Mobile tab or of its settings page.</summary>
/// <param name="Method">The call: one of <see cref="Methods"/>.</param>
/// <param name="Payload">The JSON of its parameters; null for a call that takes none.</param>
public sealed record FleetMobileInvokeParams(string Method, string? Payload = null)
{
    /// <summary>The longest payload of a call.</summary>
    public const int MaxPayload = 4096;

    /// <summary>The calls a node takes for its Mobile plugin.</summary>
    public static IReadOnlyList<string> Methods { get; } =
        ["mobile.status", "mobile.pair", "mobile.retry", "mobile.act", "mobile.settings.get", "mobile.settings.set", "mobile.reset"];
}

/// <summary>The result of <c>control.mobile.invoke</c>.</summary>
/// <param name="Json">The JSON of the answer, as the Mobile tab and its settings page read it.</param>
public sealed record FleetMobileInvokeResult(string Json);

/// <summary>
/// The page of the phones of a node, which its host serves with the Mobile plugin: what <c>control.mobile.*</c> asks. The
/// node API knows no plugin; the host turns the plugin on and off itself, since a plugin that is off hears no request.
/// </summary>
public interface IFleetNodeMobile
{
    /// <summary>Whether the node serves the page.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The state.</returns>
    Task<FleetMobileState> ReadAsync(CancellationToken cancellationToken);

    /// <summary>Turns the page on or off, and keeps it so across restarts.</summary>
    /// <param name="enabled">Whether it is on.</param>
    /// <param name="cancellationToken">Cancels the wait for another change of the plugins; a change that began runs to its end.</param>
    /// <returns>The state after the change.</returns>
    Task<FleetMobileState> EnableAsync(bool enabled, CancellationToken cancellationToken);

    /// <summary>Runs one call of the Mobile tab or of its settings page.</summary>
    /// <param name="method">The call.</param>
    /// <param name="payload">The JSON of its parameters, or null.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The JSON of the answer; null when the plugin does not run.</returns>
    /// <exception cref="ArgumentException">The parameters of the call do not read.</exception>
    Task<string?> InvokeAsync(string method, string? payload, CancellationToken cancellationToken);
}
