using CodeAlta.Agent.Runtime;

namespace CodeAlta.Credentials;

/// <summary>
/// What a run does for a provider whose key is kept in a vault that cannot give it now: it asks the vault to unlock (one prompt
/// for every run that waits for that vault), or the user to confirm a secret that asks before use, then answers whether the
/// secrets can be given. It never asks where the host cannot prompt, nor for a vault that takes a password typed in Settings,
/// nor again soon after the user dismissed the prompt of a vault: the run then fails with what to do.
/// </summary>
public sealed class VaultSecretWaiter : IProviderSecretWaiter
{
    // After a dismissed prompt, the runs that need the vault fail with its reason rather than show the prompt again at once.
    private static readonly TimeSpan PauseAfterDismissal = TimeSpan.FromMinutes(2);

    private readonly CredentialBroker _broker;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Task<CredentialProviderView>> _unlocks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<UserPresenceResult>> _confirmations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _dismissed = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="VaultSecretWaiter"/> class.</summary>
    /// <param name="broker">The broker of the host.</param>
    /// <exception cref="ArgumentNullException"><paramref name="broker"/> is null.</exception>
    public VaultSecretWaiter(CredentialBroker broker)
        : this(broker, TimeProvider.System)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="VaultSecretWaiter"/> class.</summary>
    /// <param name="broker">The broker of the host.</param>
    /// <param name="time">The clock.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public VaultSecretWaiter(CredentialBroker broker, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(time);
        _broker = broker;
        _time = time;
    }

    /// <inheritdoc />
    public string? WaitsFor(IReadOnlyCollection<string> names, string use)
    {
        ArgumentNullException.ThrowIfNull(names);
        foreach (var name in names)
        {
            if (_broker.GetAvailability(name) is not { } availability) continue;
            var vault = availability.Provider is { } id ? _broker.GetProvider(id)?.DisplayName ?? id : "its vault";
            switch (availability.State)
            {
                case SecretAvailabilityState.Locked:
                    return $"its secret {name} is kept in {vault}, which is locked. A session that uses it asks {vault} to unlock; or unlock it in Settings › Secrets.";
                case SecretAvailabilityState.SignInNeeded:
                    return $"its secret {name} is kept in {vault}, which needs a sign-in: sign in from Settings › Secrets.";
                case SecretAvailabilityState.WaitsForYou:
                    return $"its secret {name} asks before use. A session that uses it asks you to confirm; or confirm it in Settings › Secrets.";
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<bool> WaitAsync(IReadOnlyCollection<string> names, string use, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(names);
        foreach (var name in names)
        {
            if (_broker.GetAvailability(name) is not { } availability || availability.State == SecretAvailabilityState.Ready) continue;
            if (!_broker.CanPrompt) return false;
            if (availability.State == SecretAvailabilityState.Locked && availability.Provider is { } id)
            {
                if (!await UnlockAsync(id, cancellationToken).ConfigureAwait(false)) return false;
                // The unlock read the secrets of the vault; one that is still not there (another field, an expired value) is read now.
                availability = _broker.GetAvailability(name) ?? availability;
                if (availability.State is not (SecretAvailabilityState.Ready or SecretAvailabilityState.WaitsForYou))
                    availability = await _broker.ReadSecretAsync(name, cancellationToken).ConfigureAwait(false);
            }

            // A secret that asks before use: this use is confirmed (another use may still wait for its own).
            if (availability.State == SecretAvailabilityState.WaitsForYou
                && await ConfirmAsync(name, use, cancellationToken).ConfigureAwait(false) == UserPresenceResult.Verified)
                continue;
            if (availability.State != SecretAvailabilityState.Ready) return false;
        }

        return true;
    }

    // Asks a vault to unlock, once for every run that waits for it at the same time.
    private async Task<bool> UnlockAsync(string id, CancellationToken cancellationToken)
    {
        if (_broker.GetProvider(id) is not { } provider || provider.UnlockAsksPassword) return false;
        Task<CredentialProviderView>? unlock;
        TaskCompletionSource<CredentialProviderView>? mine = null;
        lock (_gate)
        {
            if (_dismissed.TryGetValue(id, out var at) && _time.GetUtcNow() - at < PauseAfterDismissal) return false;
            if (!_unlocks.TryGetValue(id, out unlock))
            {
                // Added before it starts, so that a prompt that answers at once cannot leave its answer for the next runs.
                mine = new TaskCompletionSource<CredentialProviderView>(TaskCreationOptions.RunContinuationsAsynchronously);
                unlock = mine.Task;
                _unlocks[id] = unlock;
            }
        }

        if (mine is not null) _ = UnlockOnceAsync(id, mine);
        var view = await unlock.WaitAsync(cancellationToken).ConfigureAwait(false);
        return view.Status.State == CodeAlta.Plugins.Abstractions.CredentialProviderState.Ready;
    }

    private async Task UnlockOnceAsync(string id, TaskCompletionSource<CredentialProviderView> outcome)
    {
        try
        {
            // The prompt belongs to the vault: a run that stops waiting does not take it away from the others.
            var view = await _broker.UnlockAsync(id, null, CancellationToken.None).ConfigureAwait(false);
            lock (_gate)
            {
                _unlocks.Remove(id);
                if (view.Status.State != CodeAlta.Plugins.Abstractions.CredentialProviderState.Ready) _dismissed[id] = _time.GetUtcNow();
            }

            outcome.SetResult(view);
        }
        catch (Exception exception)
        {
            lock (_gate) _unlocks.Remove(id);
            outcome.SetException(exception);
        }
    }

    // Asks the user to confirm a use of a secret that asks before use, once for every run that waits for that use.
    private Task<UserPresenceResult> ConfirmAsync(string name, string use, CancellationToken cancellationToken)
    {
        var key = name + "\n" + use;
        Task<UserPresenceResult>? confirmation;
        TaskCompletionSource<UserPresenceResult>? mine = null;
        lock (_gate)
        {
            if (!_confirmations.TryGetValue(key, out confirmation))
            {
                mine = new TaskCompletionSource<UserPresenceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                confirmation = mine.Task;
                _confirmations[key] = confirmation;
            }
        }

        if (mine is not null) _ = ConfirmOnceAsync(name, use, key, mine);
        return confirmation.WaitAsync(cancellationToken);
    }

    private async Task ConfirmOnceAsync(string name, string use, string key, TaskCompletionSource<UserPresenceResult> outcome)
    {
        try
        {
            var result = await _broker.ConfirmAsync(name, use, CancellationToken.None).ConfigureAwait(false);
            lock (_gate) _confirmations.Remove(key);
            outcome.SetResult(result);
        }
        catch (Exception exception)
        {
            lock (_gate) _confirmations.Remove(key);
            outcome.SetException(exception);
        }
    }
}
