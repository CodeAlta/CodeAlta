using System.Globalization;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Services;

// The on-call rotations of teams (doc/fleet/orchestration.md, "Autonomy and approvals"): the people of a team in turn, each
// for a shift of hours; who is on call answers the approvals of the team's automations that ask the person on call.
internal sealed partial class AccessService
{
    /// <summary>The longest turn of a rotation, in hours: a week.</summary>
    internal const int MaximumShiftHours = 168;

    /// <summary>The most people of a rotation.</summary>
    internal const int MaximumRotationPeople = 50;

    /// <summary>Gets or sets the store of the rotations; null on a server without them.</summary>
    internal IOnCallStore? OnCall { get; init; }

    /// <summary>Gets or sets the sign-ins of the people, whose emails match whom an on-call tool names; null to match their names only.</summary>
    internal ISignInStore? Logins { get; init; }

    /// <summary>Sets the on-call rotation of a team: its people in turn, members of the team; for its maintainers and admins.</summary>
    /// <param name="actor">The calling controller device.</param>
    /// <param name="teamId">The team.</param>
    /// <param name="request">The people and the length of a turn.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The team with its rotation.</returns>
    /// <exception cref="CoordinatorException">The caller may not, the team is unknown, or the rotation does not hold.</exception>
    public async Task<TeamDto> SetOnCallAsync(DeviceRecord actor, string teamId, PutOnCallRotationRequest? request, RequestOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var (snapshot, person) = await ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        var team = snapshot.Team(teamId) ?? throw NotFound("No team has this id.");
        Require(snapshot, person, "access.manage", AccessTarget.Team(teamId));
        var store = OnCall ?? throw Invalid("This server keeps no on-call rotation.");
        var people = request?.People?.Select(static id => id?.Trim() ?? string.Empty).ToList() ?? [];
        if (people.Count is 0 or > MaximumRotationPeople || people.Distinct(StringComparer.Ordinal).Count() != people.Count)
            throw Invalid($"A rotation names 1 to {MaximumRotationPeople} people of the team, each once.");
        if (people.FirstOrDefault(id => snapshot.TeamRole(team.Id, id) is null) is { } outside) throw Invalid($"{snapshot.Person(outside)?.DisplayName ?? outside} is not of the team.");
        if (request!.ShiftHours is < 1 or > MaximumShiftHours) throw Invalid($"A turn lasts 1 to {MaximumShiftHours} hours.");
        var now = _time.GetUtcNow();
        var rotation = new OnCallRotationRecord(actor.OrganizationId, team.Id, people, request.StartsAt ?? now, request.ShiftHours, now, person.Id);
        await store.PutOnCallRotationAsync(rotation, origin.Audit("team.oncall", actor, team.Id,
            details: Json(("people", people.Count.ToString(CultureInfo.InvariantCulture)), ("shiftHours", request.ShiftHours.ToString(CultureInfo.InvariantCulture)))), cancellationToken)
            .ConfigureAwait(false);
        Changed(actor.OrganizationId);
        return ToDto(snapshot, team) with { OnCall = Rotation(rotation, snapshot, now) };
    }

    /// <summary>Takes the on-call rotation of a team away: its approvals go to its people again; for its maintainers and admins.</summary>
    /// <param name="actor">The calling controller device.</param>
    /// <param name="teamId">The team.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The team.</returns>
    /// <exception cref="CoordinatorException">The caller may not, or the team is unknown.</exception>
    public async Task<TeamDto> ClearOnCallAsync(DeviceRecord actor, string teamId, RequestOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var (snapshot, person) = await ReadAsync(actor, cancellationToken).ConfigureAwait(false);
        var team = snapshot.Team(teamId) ?? throw NotFound("No team has this id.");
        Require(snapshot, person, "access.manage", AccessTarget.Team(teamId));
        if (OnCall is { } store && await store.DeleteOnCallRotationAsync(actor.OrganizationId, team.Id, origin.Audit("team.oncall.clear", actor, team.Id), cancellationToken)
                .ConfigureAwait(false))
            Changed(actor.OrganizationId);
        return ToDto(snapshot, team);
    }

    /// <summary>
    /// Who of a rotation is on call at a time, and until when: the turn of the time from its start, or, when that person is
    /// no longer an active member of the team, the next one in turn who is. Before its start, its first turn.
    /// </summary>
    /// <param name="rotation">The rotation.</param>
    /// <param name="snapshot">The access of the organization.</param>
    /// <param name="now">The time.</param>
    /// <returns>The person on call and the end of the turn; a null person when no one of it is a member of the team now.</returns>
    internal static (string? PersonId, DateTimeOffset? Until) OnCallNow(OnCallRotationRecord rotation, AccessSnapshot snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rotation);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (rotation.People.Count == 0 || rotation.ShiftHours < 1) return (null, null);
        var shift = TimeSpan.FromHours(rotation.ShiftHours);
        var turn = now <= rotation.StartsAt ? 0 : (long)((now - rotation.StartsAt).Ticks / shift.Ticks);
        var until = rotation.StartsAt + TimeSpan.FromTicks(shift.Ticks * (turn + 1));
        for (var step = 0; step < rotation.People.Count; step++)
        {
            var id = rotation.People[(int)((turn + step) % rotation.People.Count)];
            if (snapshot.Person(id) is { IsActive: true } && snapshot.TeamRole(rotation.TeamId, id) is not null) return (id, until);
        }

        return (null, null);
    }

    /// <summary>Who is on call now for a team, when it has a rotation and one of its people is a member of the team.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="teamId">The team.</param>
    /// <param name="snapshot">The access of the organization.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The person; null for none.</returns>
    internal async Task<string?> PersonOnCallAsync(string organizationId, string teamId, AccessSnapshot snapshot, CancellationToken cancellationToken)
        => OnCall is { } store && await store.GetOnCallRotationAsync(organizationId, teamId, cancellationToken).ConfigureAwait(false) is { } rotation
            ? OnCallNow(rotation, snapshot, _time.GetUtcNow()).PersonId : null;

    // The teams with their rotations, for the list of teams.
    private async Task<IReadOnlyList<TeamDto>> WithRotationsAsync(string organizationId, AccessSnapshot snapshot, IReadOnlyList<TeamDto> teams, CancellationToken cancellationToken)
    {
        if (OnCall is not { } store) return teams;
        var rotations = (await store.ListOnCallRotationsAsync(organizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(static rotation => rotation.TeamId, StringComparer.Ordinal);
        var now = _time.GetUtcNow();
        return [.. teams.Select(team => rotations.TryGetValue(team.Id, out var rotation) ? team with { OnCall = Rotation(rotation, snapshot, now) } : team)];
    }

    private static OnCallRotationDto Rotation(OnCallRotationRecord rotation, AccessSnapshot snapshot, DateTimeOffset now)
    {
        var (person, until) = OnCallNow(rotation, snapshot, now);
        return new OnCallRotationDto(rotation.People, rotation.StartsAt, rotation.ShiftHours, person, until);
    }
}
