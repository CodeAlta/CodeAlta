using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Fleet.Identity;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Fleet.NodeApi;

// Subscriptions are personal (doc/fleet/security-and-privacy.md, credentials): a provider that runs with a person's plan serves
// the owner's own work on their own machine only. A session another person started here (a member of a team on a team machine,
// a person a personal machine lets in), or a run nobody attends that is not the owner's, and every session it starts, keeps to
// account credentials: the providers of the machine it is offered leave the personal plans out, and the runtime refuses one at
// every attachment (a change of provider, a model tier, a fallback, a child).
public sealed partial class FleetNodeService
{
    // Whether a device starts the work of another person than the owner: the one whose session it records (RecordPrincipal).
    private static bool StartsOthersWork(TrustedDevice? device) => device is { OwnSessionsOnly: true, PersonId: not null };

    // Whether a provider is one a session of this device may run with.
    private bool AllowsProvider(TrustedDevice? device, ModelProviderDescriptor provider)
        => !StartsOthersWork(device) || _options.IsPersonalProvider?.Invoke(provider) != true;

    // The check of the runtime at every attachment: a session of another person's work, or one a session of it started.
    private async ValueTask<string?> CheckOthersWorkProviderAsync(SessionViewDescriptor session, string providerKey, CancellationToken cancellationToken)
    {
        if (_options.IsPersonalProvider is not { } isPersonal) return null;
        if (!_host.ModelProviderRegistry.TryGetProvider(new ModelProviderId(providerKey), out var provider) || !isPersonal(provider)) return null;
        bool othersWork;
        try
        {
            othersWork = await _host.RuntimeService.AnyInLineageAsync(session, current => Principals.IsAttributed(current.SessionId), cancellationToken).ConfigureAwait(false);
        }
        catch (SessionLineageException)
        {
            // Whose work it is cannot be told: it is not taken for the owner's.
            othersWork = true;
        }

        return othersWork ? PersonalPlanRefusal(provider) : null;
    }

    // Why a personal plan is refused, with the providers that would run the session.
    private string PersonalPlanRefusal(ModelProviderDescriptor provider)
    {
        var accounts = _host.ModelProviderRegistry.ListProviders()
            .Where(candidate => candidate.IsEnabled && _options.IsPersonalProvider?.Invoke(candidate) != true)
            .Select(static candidate => candidate.ProviderId.Value).Take(8).ToArray();
        var choose = accounts.Length > 0
            ? $" Choose one that runs with the key of an account: {string.Join(", ", accounts)}."
            : " This machine has no provider that runs with the key of an account: its owner adds one.";
        return $"The provider '{provider.ProviderId.Value}' does not run with the key of an account (a personal plan, or credentials CodeAlta cannot tell), " +
            "which serves only the work of the owner of this machine: a session someone else started here keeps to account credentials." + choose;
    }
}
