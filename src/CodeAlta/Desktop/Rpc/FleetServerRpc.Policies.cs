using System.Globalization;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

// The signed policy of a server and its kill switch (doc/fleet/security-and-privacy.md, policies and the kill switch), for
// Settings > Fleet > Policies: the rules and the stop this computer verified, the versions, and what a signer's computer signs:
// the next version, only with the changes the person read as this computer reads them.
internal sealed partial class FleetServerService
{
    private const int MaxListedVersions = 20;

    /// <summary>The policy of a server as this computer verified it, with what the page needs to change it.</summary>
    [NeoRpcMethod("policy")]
    public async Task<FleetPolicyOverview> PolicyAsync(FleetServerLinkRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return FleetPolicyOverview.Failed(refused, null);
        if (!FleetServerLinkStore.IsValidId(request!.LinkId)) return FleetPolicyOverview.Failed("invalid_request", null);
        try
        {
            var policy = _servers!.PolicyOf(request.LinkId);
            string? problem = null;
            FleetPolicyState state;
            try
            {
                state = await policy.RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (FleetPolicyDocumentException exception)
            {
                // A chain that does not verify changes nothing: the page shows the version verified before, and says so.
                state = policy.Current ?? FleetPolicyState.Empty;
                problem = exception.Message;
            }

            var read = await _servers.ReadAccessAsync(request.LinkId, cancellationToken).ConfigureAwait(false);
            var names = Names(read);
            var versions = await VersionsAsync(request.LinkId, names, cancellationToken).ConfigureAwait(false);
            var signer = state.SignerDeviceId is { } device && read.Devices.TryGetValue(device, out var signed) ? names.GetValueOrDefault(signed.OwnerId ?? string.Empty) ?? signed.Name : null;
            return new FleetPolicyOverview("ok", problem, read.Self.Signer && read.Self.Person.Status == FleetPersonStatuses.Active, Small(state.Number), signer,
                state.Time is { } time ? Iso(time) : null, ToView(state.Rules), state.Stop is { } stop ? ToView(stop, names) : null,
                [.. read.Devices.Values.Where(static device => device.Status == "approved" && device.Kind != FleetDeviceKinds.Recovery)
                    .OrderBy(static device => device.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(device => new FleetPolicyMachineView(device.Id, device.Name, read.Teams.FirstOrDefault(team => team.Machines.Contains(device.Id, StringComparer.Ordinal))?.Name))],
                [.. read.Teams.Select(static team => new FleetPolicyTeamView(team.Id, team.Name))], versions);
        }
        catch (Exception exception) when (Failure(exception) is { } failure)
        {
            return FleetPolicyOverview.Failed(failure.Status, failure.Message);
        }
        catch (Exception exception) when (exception is FleetAccessDocumentException)
        {
            return FleetPolicyOverview.Failed("access_document.invalid", exception.Message);
        }
    }

    /// <summary>What the next version would change, as this computer reads it: the lines the person reads before signing.</summary>
    [NeoRpcMethod("preparePolicy")]
    public async Task<FleetPolicyPreview> PreparePolicyAsync(FleetPolicyChangeRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, []);
        if (!FleetServerLinkStore.IsValidId(request!.LinkId)) return new("invalid_request", null, []);
        try
        {
            var draft = await DraftAsync(request, cancellationToken).ConfigureAwait(false);
            return new("ok", null, [.. draft.Changes]);
        }
        catch (Exception exception) when (Failure(exception) is { } failure)
        {
            return new(failure.Status, failure.Message, []);
        }
        catch (FleetPolicyDocumentException exception)
        {
            return new(PolicyInvalid, exception.Message, []);
        }
    }

    /// <summary>
    /// Signs the next version with the key of this computer, when it changes exactly what the person read: the rules, or the
    /// kill switch set or lifted. <c>changed</c> when the policy changed meanwhile: the page reads it again.
    /// </summary>
    [NeoRpcMethod("signPolicy")]
    public Task<FleetSecretActionResult> SignPolicyAsync(FleetPolicySignRequest request, CancellationToken cancellationToken)
        => SecretActionAsync(request?.ExpectedEpoch, request?.LinkId, async () =>
        {
            if (request!.Changes is null || request.Changes.Count == 0) return new FleetSecretActionResult("invalid_request", null);
            FleetPolicyDraft draft;
            try
            {
                draft = await DraftAsync(new FleetPolicyChangeRequest(request.ExpectedEpoch, request.LinkId, request.Rules, request.Stop), cancellationToken).ConfigureAwait(false);
            }
            catch (FleetPolicyDocumentException exception)
            {
                return new FleetSecretActionResult(PolicyInvalid, exception.Message);
            }

            if (draft.Changes.Count == 0) return new FleetSecretActionResult("ok", null);
            if (!draft.Changes.SequenceEqual(request.Changes, StringComparer.Ordinal)) return new FleetSecretActionResult("changed", null);
            await _servers!.PolicyOf(request.LinkId).SignAsync(draft, cancellationToken).ConfigureAwait(false);
            ChangedLocally?.Invoke();
            return new FleetSecretActionResult("ok", null);
        });

    private const string PolicyInvalid = "policy.invalid";

    private async Task<FleetPolicyDraft> DraftAsync(FleetPolicyChangeRequest request, CancellationToken cancellationToken)
    {
        var read = await _servers!.ReadAccessAsync(request.LinkId, cancellationToken).ConfigureAwait(false);
        var names = Names(read);
        return await _servers.PolicyOf(request.LinkId).PrepareAsync(FromView(request.Rules), request.Stop is { } stop ? FromView(stop) : null, id => names.GetValueOrDefault(id),
            cancellationToken).ConfigureAwait(false);
    }

    // The versions the server lists, the newest first, each with what it changed as this computer describes it.
    private async Task<IReadOnlyList<FleetPolicyVersionView>> VersionsAsync(string linkId, IReadOnlyDictionary<string, string> names, CancellationToken cancellationToken)
    {
        var listed = await _servers!.AccessCallAsync(linkId, static (client, token) => client.ListPolicyDocumentsAsync(0, token), cancellationToken).ConfigureAwait(false);
        var versions = new List<FleetPolicyVersionView>();
        var before = FleetPolicyState.Empty;
        foreach (var item in listed.Items)
        {
            if (!FleetPolicyDocuments.TryParse(item.Document.Payload, out var document)) break;
            var lines = FleetPolicyDocuments.Describe(before, document, id => names.GetValueOrDefault(id));
            versions.Add(new FleetPolicyVersionView(Small(document.Number), names.GetValueOrDefault(document.SignerDeviceId), Iso(document.Time),
                lines.Count == 0 ? ["No rule"] : [.. lines], document.Stop is not null));
            before = before with { Number = document.Number, Rules = document.Rules, Stop = document.Stop };
        }

        versions.Reverse();
        return [.. versions.Take(MaxListedVersions)];
    }

    // The names of the people, devices (as their person, for a signer) and teams the page shows.
    private static Dictionary<string, string> Names(Fleet.DesktopFleetAccess read)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var person in read.People) names[person.Id] = person.DisplayName;
        foreach (var team in read.Teams) names[team.Id] = team.Name;
        foreach (var device in read.Devices.Values)
            names.TryAdd(device.Id, device.Kind == FleetDeviceKinds.Controller && device.OwnerId is { } owner && names.TryGetValue(owner, out var person) ? person : device.Name);
        return names;
    }

    private static FleetPolicyRulesView ToView(FleetPolicyRules rules)
        => new(rules.Providers, rules.Models, rules.Sandbox, rules.ToolsOff, rules.Network, rules.RemoteStarts, rules.AgentStarts, rules.MaxSessionMinutes, rules.PermissionMode,
            rules.PublishFacts, rules.AccountCredentials);

    private static FleetPolicyRules FromView(FleetPolicyRulesView? view)
    {
        if (view is null) return FleetPolicyRules.None;
        static IReadOnlyList<string>? List(IReadOnlyList<string>? values)
            => values?.Select(static value => value.Trim()).Where(static value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() is { Length: > 0 } list ? list : null;
        static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return new FleetPolicyRules(List(view.Providers), List(view.Models), Text(view.Sandbox), List(view.ToolsOff), Text(view.Network), view.RemoteStarts, view.AgentStarts,
            view.MaxSessionMinutes, Text(view.PermissionMode), view.PublishFacts, view.AccountCredentials);
    }

    private static FleetPolicyStopView ToView(FleetPolicyStop stop, IReadOnlyDictionary<string, string> names)
        => new(stop.Scope, [.. stop.Machines], [.. stop.Teams], stop.Reason, [.. stop.Machines.Concat(stop.Teams).Select(id => names.GetValueOrDefault(id, id))]);

    private static FleetPolicyStop FromView(FleetPolicyStopView view)
        => new(view.Scope, view.Scope == FleetPolicyStopScopes.Machines ? [.. view.Machines.Distinct(StringComparer.Ordinal)] : [],
            view.Scope == FleetPolicyStopScopes.Teams ? [.. view.Teams.Distinct(StringComparer.Ordinal)] : [],
            string.IsNullOrWhiteSpace(view.Reason) ? null : view.Reason.Trim());

    // The stop that reaches the machines of a link, as this computer verified it: what the banner of the window says.
    internal static FleetPolicyBanner? Banner(FleetServerConnection connection)
        => connection.Connector?.Policy.Current is { Stop: { } stop } policy
            ? new FleetPolicyBanner(stop.Scope, stop.Reason, stop.Machines.Count, stop.Teams.Count, policy.Time is { } time ? Iso(time) : null,
                (int)Math.Min(policy.Number, int.MaxValue))
            : null;
}

/// <summary>The rules of a policy, as the page edits them; an absent rule says nothing.</summary>
/// <param name="Providers">The kinds of providers allowed.</param>
/// <param name="Models">The models allowed, a trailing <c>*</c> naming a prefix.</param>
/// <param name="Sandbox">The sandbox profile at least.</param>
/// <param name="ToolsOff">The tools turned off.</param>
/// <param name="Network">The widest network: <c>off</c>, <c>hosts</c> or <c>on</c>.</param>
/// <param name="RemoteStarts">Whether people start sessions on other machines.</param>
/// <param name="AgentStarts">Whether agents start work on other machines.</param>
/// <param name="MaxSessionMinutes">The longest session, in minutes.</param>
/// <param name="PermissionMode">The widest permission mode.</param>
/// <param name="PublishFacts">Whether the sessions of the organization's spaces publish their facts.</param>
/// <param name="AccountCredentials">Whether every automation runs with account credentials.</param>
internal sealed record FleetPolicyRulesView(IReadOnlyList<string>? Providers, IReadOnlyList<string>? Models, string? Sandbox, IReadOnlyList<string>? ToolsOff, string? Network,
    bool? RemoteStarts, bool? AgentStarts, int? MaxSessionMinutes, string? PermissionMode, bool? PublishFacts, bool? AccountCredentials);

/// <summary>The kill switch as the page shows and sets it.</summary>
/// <param name="Scope"><c>fleet</c>, <c>machines</c> or <c>teams</c>.</param>
/// <param name="Machines">The devices of the machines it stops.</param>
/// <param name="Teams">The teams whose machines it stops.</param>
/// <param name="Reason">Why, for the people of the machines.</param>
/// <param name="Names">The names of what it names, for display; empty when the page sets it.</param>
internal sealed record FleetPolicyStopView(string Scope, IReadOnlyList<string> Machines, IReadOnlyList<string> Teams, string? Reason, IReadOnlyList<string> Names);

/// <summary>A machine the kill switch can name.</summary>
/// <param name="Id">Its device.</param>
/// <param name="Name">Its name.</param>
/// <param name="Team">The team it belongs to, or null for a personal machine.</param>
internal sealed record FleetPolicyMachineView(string Id, string Name, string? Team);

/// <summary>A team the kill switch can name.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Name">Its name.</param>
internal sealed record FleetPolicyTeamView(string Id, string Name);

/// <summary>A version of the policy, as the page lists it.</summary>
/// <param name="Number">Its number.</param>
/// <param name="SignedBy">Who signed it, by name when known.</param>
/// <param name="SignedAt">When its signer made it, in ISO 8601.</param>
/// <param name="Changes">What it changed, for people.</param>
/// <param name="Stops">Whether it holds a stop.</param>
internal sealed record FleetPolicyVersionView(int Number, string? SignedBy, string SignedAt, IReadOnlyList<string> Changes, bool Stops);

/// <summary>The policy of a server, for Settings > Fleet > Policies.</summary>
/// <param name="Status"><c>ok</c>, or why not.</param>
/// <param name="Message">What was said of a failure, or why the chain the server answers does not verify.</param>
/// <param name="CanSign">Whether this computer's person signs the policy.</param>
/// <param name="Number">The number of the version verified; 0 when none was signed.</param>
/// <param name="SignedBy">Who signed it.</param>
/// <param name="SignedAt">When, in ISO 8601.</param>
/// <param name="Rules">Its rules.</param>
/// <param name="Stop">Its kill switch, or null while the work of the fleet goes on.</param>
/// <param name="Machines">The machines a stop can name.</param>
/// <param name="Teams">The teams a stop can name.</param>
/// <param name="Versions">The versions, the newest first.</param>
internal sealed record FleetPolicyOverview(string Status, string? Message, bool CanSign, int Number, string? SignedBy, string? SignedAt, FleetPolicyRulesView? Rules,
    FleetPolicyStopView? Stop, IReadOnlyList<FleetPolicyMachineView> Machines, IReadOnlyList<FleetPolicyTeamView> Teams, IReadOnlyList<FleetPolicyVersionView> Versions)
{
    /// <summary>A policy that could not be read.</summary>
    public static FleetPolicyOverview Failed(string status, string? message) => new(status, message, false, 0, null, null, null, null, [], [], []);
}

/// <summary>A change of the policy the page asks to read or sign.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The link of the server.</param>
/// <param name="Rules">The rules of the next version.</param>
/// <param name="Stop">Its kill switch, or null to let the work of the fleet go on.</param>
internal sealed record FleetPolicyChangeRequest(string ExpectedEpoch, string LinkId, FleetPolicyRulesView? Rules, FleetPolicyStopView? Stop);

/// <summary>What the next version changes.</summary>
/// <param name="Status"><c>ok</c>, or why not.</param>
/// <param name="Message">What was said of a failure.</param>
/// <param name="Changes">The changes, for people; empty when it changes nothing.</param>
internal sealed record FleetPolicyPreview(string Status, string? Message, IReadOnlyList<string> Changes);

/// <summary>A version the page asks this computer to sign.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The link of the server.</param>
/// <param name="Rules">The rules of the next version.</param>
/// <param name="Stop">Its kill switch, or null.</param>
/// <param name="Changes">The changes the person read.</param>
internal sealed record FleetPolicySignRequest(string ExpectedEpoch, string LinkId, FleetPolicyRulesView? Rules, FleetPolicyStopView? Stop, IReadOnlyList<string> Changes);

/// <summary>A stop of the kill switch that a server's policy holds, for the banner of the window.</summary>
/// <param name="Scope"><c>fleet</c>, <c>machines</c> or <c>teams</c>.</param>
/// <param name="Reason">Why.</param>
/// <param name="Machines">How many machines it names.</param>
/// <param name="Teams">How many teams it names.</param>
/// <param name="Since">When its signer made it, in ISO 8601.</param>
/// <param name="Number">The version of the policy that holds it.</param>
internal sealed record FleetPolicyBanner(string Scope, string? Reason, int Machines, int Teams, string? Since, int Number);
