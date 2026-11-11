using CodeAlta.Catalog;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Fleet.NodeApi;

/// <summary>The parameters of <c>control.servers.join</c>.</summary>
/// <param name="Url">The address of the server.</param>
/// <param name="Token">The one-time enrollment token a controller device of the server made.</param>
/// <param name="Name">The name of the node at the server; null for the name of the node.</param>
public sealed record FleetServerJoinParams(string Url, string Token, string? Name = null);

/// <summary>The result of <c>control.servers.join</c>: the node is enrolled and waits for its approval.</summary>
/// <param name="DeviceId">The device id of the node at the server, the one to approve.</param>
/// <param name="Name">Its name there.</param>
/// <param name="Words">The six words of its key, which the server shows beside it.</param>
/// <param name="AlreadyLinked">Whether the node was linked to that server already (nothing was enrolled again).</param>
public sealed record FleetServerJoinResult(string DeviceId, string Name, string Words, bool AlreadyLinked);

public sealed partial class FleetNodeService
{
    /// <summary>Gets or sets the HTTP handler of the enrollment of <c>control.servers.join</c>, for tests; null for the usual one.</summary>
    internal HttpMessageHandler? ServerHandler { get; init; }

    // altanode join, from the node itself: it enrolls with the token, records the link of the server as pending (its
    // connections follow the links of its folder, and wait for the approval there), and links its default space.
    private async Task<FleetServerJoinResult> ControlJoinServerAsync(FleetNodeCall call, FleetServerJoinParams parameters, CancellationToken cancellationToken)
    {
        if (!FleetServerAddress.TryParse(parameters.Url, out var address, out var certificatePin))
            throw new FleetException(FleetErrorCodes.InvalidParams, null, "This is not the address of a server (http:// or https://, with the #pin= of its own certificate if it has one).");
        if (!FleetTunnelAccessStore.IsEnrollmentCode(parameters.Token) || parameters.Token is not { } token)
            throw new FleetException(FleetErrorCodes.InvalidParams, null, "An enrollment token is needed.");
        string name;
        try { name = Identity.TrustedDeviceStore.NormalizeName(string.IsNullOrWhiteSpace(parameters.Name) ? _options.Name : parameters.Name); }
        catch (ArgumentException exception) { throw new FleetException(FleetErrorCodes.InvalidParams, null, exception.Message); }

        var store = new FleetServerLinkStore(_host.CatalogOptions.FleetRoot);
        var url = FleetServerAddress.Format(address);
        var words = _options.Identity.Fingerprint.ToShortWords();
        if (store.Find(url) is { State: not FleetServerLinkStates.Revoked } existing && existing.DeviceId == _options.Identity.DeviceId)
        {
            if (FleetTunnelAccessStore.IsInvitation(token)) new FleetTunnelAccessStore(_options.Identity).ImportInvitation(address, token.Trim());
            return new FleetServerJoinResult(existing.DeviceId, name, words, AlreadyLinked: true);
        }

        Protocol.Api.DeviceDto device;
        using (var client = new FleetServerClient(address, _options.Identity, ServerHandler, null, certificatePin))
        {
            try
            {
                device = await client.EnrollAsync(token.Trim(), name, cancellationToken).ConfigureAwait(false);
            }
            catch (FleetServerException exception)
            {
                throw new FleetException(FleetNodeErrorCodes.ServerRefused, null, $"The server refused the enrollment ({exception.Code}): {exception.Message}");
            }
            catch (HttpRequestException exception)
            {
                throw new FleetException(FleetNodeErrorCodes.ServerUnreachable, FleetErrorHints.Retry, $"{url} does not answer from the node: {exception.Message}");
            }
        }

        var now = _options.TimeProvider.GetUtcNow();
        store.Save(new FleetServerLink(store.Find(url)?.Id ?? FleetServerLinkStore.NewId(), url, null, null, _options.Identity.DeviceId, FleetDeviceKinds.Node,
            FleetServerLinkStates.Pending, now, now, certificatePin));
        var spaces = await _host.SpaceCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (spaces.FirstOrDefault(static space => space.IsDefault) is { } space)
        {
            var change = await _host.SpaceCatalog.UpdateSettingsAsync(space.Id, settings => settings with { Fleet = new SpaceFleet(url) }, cancellationToken).ConfigureAwait(false);
            if (!change.Succeeded) LogWarn($"The space {space.Name} could not be linked to {url}: {change.Message}");
        }

        LogInfo($"{Capitalized(Who(call))} enrolled the node in {url} as {name} ({device.Id}); it waits for its approval.");
        return new FleetServerJoinResult(device.Id, name, words, AlreadyLinked: false);
    }
}
