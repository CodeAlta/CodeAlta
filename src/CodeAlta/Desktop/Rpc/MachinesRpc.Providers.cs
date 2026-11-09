using System.Text.Json.Serialization;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The providers of a machine this computer manages (<c>doc/fleet/node.md</c>, credentials on a node): each provider with
/// whether it is ready and why not, a provider added or changed with its key typed here and sent over the end-to-end channel
/// to the secrets store of the machine, and the device-code sign-in of the providers that have one, run on the machine while
/// the page shows the code. Plain calls: the page asks again while a sign-in waits, with no channel of its own.
/// </summary>
internal sealed partial class MachinesService
{
    private const int MaximumProviderKeyLength = 16 * 1024;

    /// <summary>Reads the providers of a machine and how each stands.</summary>
    [NeoRpcMethod("providers")]
    public async Task<MachineProvidersView> ProvidersAsync(MachineRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return MachineProvidersView.Refused(refused);
        if (request!.MachineId is not { Length: > 0 and <= 64 } id) return MachineProvidersView.Refused("invalid_request");
        if (_fleet!.Find(id) is not { } host) return MachineProvidersView.Refused("not_found");
        try
        {
            var list = await host.ManageAsync((client, token) => client.ListProvidersAsync(token), changes: false, cancellationToken).ConfigureAwait(false);
            return new MachineProvidersView("ok", null, [.. list.Providers.Select(View)],
                [.. list.Types.Select(static type => new MachineProviderTypeView(type.Type, type.Name, type.Credential, type.SignIn))], list.SecretProtection);
        }
        catch (ObjectDisposedException) { return MachineProvidersView.Refused(RemoteSessionHost.Describe(host.State)); }
        catch (FleetException exception) { return MachineProvidersView.Refused(Code(exception)) with { Message = exception.Error.Message }; }
        catch (Exception exception) when (exception is IOException or TimeoutException) { return MachineProvidersView.Refused(RemoteSessionHost.MachineOfflineCode) with { Message = exception.Message }; }
    }

    /// <summary>Adds or changes a provider of a machine; its key, when typed, goes to the secrets store of the machine, never into its configuration.</summary>
    [NeoRpcMethod("setProvider")]
    public async Task<MachineProviderResult> SetProviderAsync(MachineProviderRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (request!.MachineId is not { Length: > 0 and <= 64 } id || !FleetNodeService.IsProviderKey(request.Key) || request.Type is { Length: > 64 }
            || request.DisplayName is { Length: > MaximumNameLength } || request.Model is { Length: > MaximumNameLength } || request.ApiUrl is { Length: > MaximumPathLength }
            || request.ApiKey is { Length: > MaximumProviderKeyLength } || request.MaxConcurrentRequests is < 0)
            return new("invalid_request", null, null);
        if (_fleet!.Find(id) is not { } host) return new("not_found", null, null);
        try
        {
            var status = await host.ManageAsync((client, token) => client.SetProviderAsync(new FleetProviderSetParams(request.Key, request.Type, request.DisplayName, request.Model,
                request.ApiUrl, string.IsNullOrEmpty(request.ApiKey) ? null : request.ApiKey, request.Enabled, request.MakeDefault)
                { MaxConcurrentRequests = request.MaxConcurrentRequests }, token), changes: true, cancellationToken).ConfigureAwait(false);
            Changed();
            return new("ok", View(status), null);
        }
        catch (ObjectDisposedException) { return new(RemoteSessionHost.Describe(host.State), null, null); }
        catch (FleetException exception) { return new(Code(exception), null, exception.Error.Message); }
        catch (Exception exception) when (exception is IOException or TimeoutException) { return new(RemoteSessionHost.MachineOfflineCode, null, exception.Message); }
    }

    /// <summary>Starts the device-code sign-in of a provider on a machine, and gives the page and the code the person enters there.</summary>
    [NeoRpcMethod("providerSignIn")]
    public async Task<MachineProviderSignInView> ProviderSignInAsync(MachineProviderKeyRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return MachineProviderSignInView.Refused(refused);
        if (request!.MachineId is not { Length: > 0 and <= 64 } id || !FleetNodeService.IsProviderKey(request.Key)) return MachineProviderSignInView.Refused("invalid_request");
        if (_fleet!.Find(id) is not { } host) return MachineProviderSignInView.Refused("not_found");
        try
        {
            var prompt = await host.ManageAsync((client, token) => client.StartProviderSignInAsync(request.Key, token), changes: false, cancellationToken).ConfigureAwait(false);
            // Only an https page of the provider is given to the page, as the sign-ins of this computer do.
            var url = Uri.TryCreate(prompt.Url, UriKind.Absolute, out var page) && page.Scheme == Uri.UriSchemeHttps && prompt.Url.Length <= ProviderLoginService.MaximumUrlLength ? prompt.Url : null;
            return new MachineProviderSignInView("ok", null, url, prompt.UserCode is { Length: <= 64 } code ? code : null, prompt.ExpiresAt);
        }
        catch (ObjectDisposedException) { return MachineProviderSignInView.Refused(RemoteSessionHost.Describe(host.State)); }
        catch (FleetException exception) { return MachineProviderSignInView.Refused(Code(exception)) with { Message = exception.Error.Message }; }
        catch (Exception exception) when (exception is IOException or TimeoutException) { return MachineProviderSignInView.Refused(RemoteSessionHost.MachineOfflineCode) with { Message = exception.Message }; }
    }

    /// <summary>Waits a while for the sign-in of a provider of a machine to end: the page asks again while it waits.</summary>
    [NeoRpcMethod("providerSignInWait")]
    public async Task<MachineProviderSignInState> ProviderSignInWaitAsync(MachineProviderKeyRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (request!.MachineId is not { Length: > 0 and <= 64 } id || !FleetNodeService.IsProviderKey(request.Key)) return new("invalid_request", null, null);
        if (_fleet!.Find(id) is not { } host) return new("not_found", null, null);
        try
        {
            var state = await host.ManageAsync((client, token) => client.WaitProviderSignInAsync(request.Key, 15, token), changes: false, cancellationToken).ConfigureAwait(false);
            if (state.Status == "completed") Changed();
            return new(state.Status, state.Account is { Length: <= MaximumNameLength } account ? account : null, state.Code);
        }
        catch (ObjectDisposedException) { return new(RemoteSessionHost.Describe(host.State), null, null); }
        catch (FleetException exception) { return new(Code(exception), null, null); }
        catch (Exception exception) when (exception is IOException or TimeoutException) { return new(RemoteSessionHost.MachineOfflineCode, null, null); }
    }

    private static MachineProviderStatusView View(FleetProviderStatus status)
        => new(status.Key, status.Name, status.Type, status.Enabled, status.State, status.Reason, status.Detail, status.Credential, status.KeySecret, status.SignIn, status.IsDefault,
            status.Model, status.ApiUrl) { MaxConcurrentRequests = status.MaxConcurrentRequests };
}

/// <summary>A provider of a machine as the person who manages it sees it: never its key, only the secret that holds it.</summary>
/// <param name="Key">Its key.</param>
/// <param name="Name">Its name.</param>
/// <param name="Type">Its type.</param>
/// <param name="Enabled">Whether it is enabled.</param>
/// <param name="State"><c>ready</c>, <c>preparing</c>, <c>failed</c>, <c>not_ready</c> or <c>disabled</c>.</param>
/// <param name="Reason">Why it is not ready: <c>key_missing</c>, <c>secret_missing</c>, <c>sign_in_needed</c>, <c>start_failed</c>, <c>skipped</c>; null when ready.</param>
/// <param name="Detail">What more the machine says: the secret or the variable it waits for, the error of its start.</param>
/// <param name="Credential"><c>key</c>, <c>sign_in</c>, <c>cli</c> or <c>none</c>.</param>
/// <param name="KeySecret">The secret of the machine that holds its key; null for none.</param>
/// <param name="SignIn">Whether it signs in with a device code shown here.</param>
/// <param name="IsDefault">Whether it is the default one.</param>
/// <param name="Model">Its default model; null for the one of its type.</param>
/// <param name="ApiUrl">Its endpoint, when written.</param>
internal sealed record MachineProviderStatusView(string Key, string Name, string Type, bool Enabled, string State, string? Reason, string? Detail, string Credential,
    string? KeySecret, bool SignIn, bool IsDefault, string? Model, string? ApiUrl)
{
    /// <summary>The most requests it takes at once; null for no limit, or the default of its type.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxConcurrentRequests { get; init; }
}

/// <summary>A type a provider added to a machine can have.</summary>
/// <param name="Type">The type.</param>
/// <param name="Name">Its name.</param>
/// <param name="Credential"><c>key</c> or <c>sign_in</c>.</param>
/// <param name="SignIn">Whether it signs in with a device code shown here.</param>
internal sealed record MachineProviderTypeView(string Type, string Name, string Credential, bool SignIn);

/// <summary>The providers of a machine.</summary>
/// <param name="Status"><c>ok</c>, <c>not_manager</c>, <c>not_found</c>, <c>offline</c>, <c>stale_epoch</c>, or the code the machine answered.</param>
/// <param name="Message">What the machine said, when it refused.</param>
/// <param name="Providers">Its providers.</param>
/// <param name="Types">The types a provider added here can have.</param>
/// <param name="SecretProtection">How the machine protects its secrets.</param>
internal sealed record MachineProvidersView(string Status, string? Message, MachineProviderStatusView[] Providers, MachineProviderTypeView[] Types, string? SecretProtection)
{
    internal static MachineProvidersView Refused(string status) => new(status, null, [], [], null);
}

/// <summary>A provider of a machine to add or change.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="MachineId">The machine.</param>
/// <param name="Key">The key of the provider.</param>
/// <param name="Type">Its type; null keeps the type of a provider that exists.</param>
/// <param name="DisplayName">Its name; null keeps it, empty clears it.</param>
/// <param name="Model">Its default model; null keeps it, empty clears it.</param>
/// <param name="ApiUrl">Its endpoint; null keeps it, empty clears it.</param>
/// <param name="ApiKey">Its key, typed by the person; null or empty keeps the key it has.</param>
/// <param name="Enabled">Whether it is enabled.</param>
/// <param name="MakeDefault">Whether sessions that name no provider run on it.</param>
internal sealed record MachineProviderRequest(string ExpectedEpoch, string MachineId, string Key, string? Type, string? DisplayName, string? Model, string? ApiUrl, string? ApiKey,
    bool Enabled, bool MakeDefault)
{
    /// <summary>The most requests it takes at once: null keeps the limit it has, 0 removes it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxConcurrentRequests { get; init; }
}

/// <summary>A provider of a machine, by its key.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="MachineId">The machine.</param>
/// <param name="Key">The key of the provider.</param>
internal sealed record MachineProviderKeyRequest(string ExpectedEpoch, string MachineId, string Key);

/// <summary>The outcome of a change of a provider of a machine.</summary>
/// <param name="Status"><c>ok</c>, <c>not_manager</c>, <c>offline</c>, <c>invalid_request</c>, or the code the machine answered.</param>
/// <param name="Provider">The provider as it stands now.</param>
/// <param name="Message">Why it was refused.</param>
internal sealed record MachineProviderResult(string Status, MachineProviderStatusView? Provider, string? Message);

/// <summary>What the person opens to sign a provider of a machine in.</summary>
/// <param name="Status"><c>ok</c>, <c>provider.sign_in_unsupported</c>, <c>provider.sign_in_failed</c>, <c>not_manager</c>, <c>offline</c>...</param>
/// <param name="Message">Why it could not start.</param>
/// <param name="Url">The https page of the provider; null when it gave none.</param>
/// <param name="UserCode">The code to enter there.</param>
/// <param name="ExpiresAt">When the code expires.</param>
internal sealed record MachineProviderSignInView(string Status, string? Message, string? Url, string? UserCode, DateTimeOffset? ExpiresAt)
{
    internal static MachineProviderSignInView Refused(string status) => new(status, null, null, null, null);
}

/// <summary>Where a sign-in of a provider of a machine stands.</summary>
/// <param name="Status"><c>waiting</c>, <c>completed</c>, <c>failed</c>, <c>none</c>, or why the machine could not say.</param>
/// <param name="Account">The account signed in, once it is.</param>
/// <param name="Code">Why it failed.</param>
internal sealed record MachineProviderSignInState(string Status, string? Account, string? Code);
