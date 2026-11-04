using CodeAlta.Desktop.Fleet;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The people of the organization a machine this computer manages lets in, on its page (<c>doc/fleet/access-control.md</c>, what the
/// node decides): the people it names with what they may do there (their own sessions), and who asked to use it and waits. The
/// machine keeps the list itself; only a device paired to manage it changes it.
/// </summary>
internal sealed partial class MachinesService
{
    /// <summary>Gets the names of the people of the organizations this computer is linked to, by id; null names nobody.</summary>
    internal Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>>? PersonNames { get; set; }

    /// <summary>Reads the people a machine names, who asks to use it, and the people of the organization it could name.</summary>
    [NeoRpcMethod("people")]
    public async Task<MachinePeopleView> PeopleAsync(MachineRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return MachinePeopleView.Refused(refused);
        if (request!.MachineId is not { Length: > 0 and <= 64 } id) return MachinePeopleView.Refused("invalid_request");
        if (_fleet!.Find(id) is not { } host) return MachinePeopleView.Refused("not_found");
        try
        {
            var info = await host.ManageAsync(static (client, token) => client.GetPeopleAsync(token), changes: false, cancellationToken).ConfigureAwait(false);
            IReadOnlyDictionary<string, string> names = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                if (PersonNames is { } read) names = await read(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or TimeoutException or CodeAlta.Fleet.Server.FleetServerException)
            {
                // The names are the servers' to give: without them, people are shown by their ids.
            }

            string Name(string person) => names.TryGetValue(person, out var name) ? name : person;
            return new MachinePeopleView("ok", null,
                [.. info.People.Select(person => new MachinePersonView(person.PersonId, Name(person.PersonId), [.. person.Permissions]))],
                [.. info.Asks.Select(ask => new MachineAskView(ask.PersonId, Name(ask.PersonId), ask.AskedAt))],
                [.. names.Where(pair => info.People.All(person => person.PersonId != pair.Key)).OrderBy(static pair => pair.Value, StringComparer.CurrentCultureIgnoreCase)
                    .Select(static pair => new MachinePersonView(pair.Key, pair.Value, []))]);
        }
        catch (ObjectDisposedException) { return MachinePeopleView.Refused(RemoteSessionHost.Describe(host.State)); }
        catch (FleetException exception) { return MachinePeopleView.Refused(Code(exception)) with { Message = exception.Error.Message }; }
        catch (Exception exception) when (exception is IOException or TimeoutException) { return MachinePeopleView.Refused(RemoteSessionHost.MachineOfflineCode) with { Message = exception.Message }; }
    }

    /// <summary>Lets a person use a machine with permissions (view, drive, start: their own sessions), takes them out, or refuses their request.</summary>
    [NeoRpcMethod("setMachinePerson")]
    public Task<MachineManageResult> SetMachinePersonAsync(MachinePersonRequest request, CancellationToken cancellationToken)
    {
        if (request?.PersonId is not { Length: > 0 and <= 128 } person) return Invalid(request?.ExpectedEpoch, "A person is named by their id.");
        if (request.Permissions is { } permissions && (permissions.Length > 3 || permissions.Any(static name => name is not ("view" or "drive" or "start"))))
            return Invalid(request.ExpectedEpoch, "A person may view, drive and start their own sessions.");
        return ManageAsync(request.ExpectedEpoch, request.MachineId, (client, token) => client.SetPeopleAsync(new FleetNodePeopleChange(person, request.Permissions, request.Refuse), token),
            cancellationToken);
    }
}

/// <summary>The people of a machine, as the person who manages it sees them.</summary>
/// <param name="Status"><c>ok</c>, <c>not_manager</c>, <c>offline</c>, or the code the machine answered.</param>
/// <param name="Message">What the machine said, when it refused or failed.</param>
/// <param name="People">The people it names.</param>
/// <param name="Asks">The people who asked to use it and wait.</param>
/// <param name="Others">The people of the organization it does not name, which the page offers to add.</param>
internal sealed record MachinePeopleView(string Status, string? Message, MachinePersonView[] People, MachineAskView[] Asks, MachinePersonView[] Others)
{
    internal static MachinePeopleView Refused(string status) => new(status, null, [], [], []);
}

/// <summary>A person and what they may do on a machine.</summary>
/// <param name="PersonId">The person.</param>
/// <param name="Name">Their name, as their server gives it.</param>
/// <param name="Permissions"><c>view</c>, <c>drive</c>, <c>start</c>.</param>
internal sealed record MachinePersonView(string PersonId, string Name, string[] Permissions);

/// <summary>A person who asked to use a machine.</summary>
/// <param name="PersonId">The person.</param>
/// <param name="Name">Their name.</param>
/// <param name="AskedAt">When they first asked.</param>
internal sealed record MachineAskView(string PersonId, string Name, DateTimeOffset AskedAt);

/// <summary>A person let in, changed, taken out or refused on a machine.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="MachineId">The machine.</param>
/// <param name="PersonId">The person.</param>
/// <param name="Permissions">What they may do; null or empty takes them out.</param>
/// <param name="Refuse">Whether to refuse their request.</param>
internal sealed record MachinePersonRequest(string ExpectedEpoch, string MachineId, string PersonId, string[]? Permissions, bool Refuse);
