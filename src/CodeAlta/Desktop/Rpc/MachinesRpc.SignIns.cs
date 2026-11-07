using System.Collections.Concurrent;
using System.Net.Sockets;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The sign-ins a machine this computer manages keeps for itself (<c>doc/fleet/node.md</c>, credentials on a node): its providers,
/// its MCP servers, the tools of its spaces. The machine starts each sign-in and keeps its secrets; this computer shows the device
/// code, or opens the page in its own browser, listens on the loopback address the machine names, and forwards the callback the
/// browser brings back. A token never comes here. Plain calls: the page asks again while a sign-in waits.
/// </summary>
internal sealed partial class MachinesService
{
    // The browser sign-ins whose callback this computer waits for, by attempt: the listener goes with the attempt.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _forwardedSignIns = new(StringComparer.Ordinal);

    /// <summary>Lists what a machine signs in, and how each stands.</summary>
    [NeoRpcMethod("signIns")]
    public async Task<MachineSignInsView> SignInsAsync(MachineRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return MachineSignInsView.Refused(refused);
        if (request!.MachineId is not { Length: > 0 and <= 64 } id) return MachineSignInsView.Refused("invalid_request");
        if (_fleet!.Find(id) is not { } host) return MachineSignInsView.Refused("not_found");
        try
        {
            var list = await host.ManageAsync((client, token) => client.ListSignInsAsync(token), changes: false, cancellationToken).ConfigureAwait(false);
            return new MachineSignInsView("ok", null, [.. list.Items.Take(128).Select(View)]);
        }
        catch (ObjectDisposedException) { return MachineSignInsView.Refused(RemoteSessionHost.Describe(host.State)); }
        catch (FleetException exception) { return MachineSignInsView.Refused(Code(exception)) with { Message = exception.Error.Message }; }
        catch (Exception exception) when (exception is IOException or TimeoutException) { return MachineSignInsView.Refused(RemoteSessionHost.MachineOfflineCode) with { Message = exception.Message }; }
    }

    /// <summary>
    /// Starts a sign-in on a machine. A browser sign-in listens on the loopback address the machine names, opens the page in the
    /// browser of this computer, and forwards the callback to the machine; a device sign-in gives the code the person enters.
    /// </summary>
    [NeoRpcMethod("signInStart")]
    public async Task<MachineSignInPromptView> SignInStartAsync(MachineSignInStartRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return MachineSignInPromptView.Refused(refused);
        if (request!.MachineId is not { Length: > 0 and <= 64 } id || request.Kind is not { Length: > 0 and <= 16 } || request.Key is not { Length: > 0 and <= 128 }
            || request.Space is { Length: > 64 } || request.Way is { Length: > 16 })
            return MachineSignInPromptView.Refused("invalid_request");
        if (_fleet!.Find(id) is not { } host) return MachineSignInPromptView.Refused("not_found");
        FleetMachineSignInPrompt prompt;
        try
        {
            prompt = await host.ManageAsync((client, token) => client.StartSignInAsync(new FleetMachineSignInStartParams(request.Kind, request.Key, request.Space, request.Way), token),
                changes: false, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { return MachineSignInPromptView.Refused(RemoteSessionHost.Describe(host.State)); }
        catch (FleetException exception) { return MachineSignInPromptView.Refused(Code(exception)) with { Message = exception.Error.Message }; }
        catch (Exception exception) when (exception is IOException or TimeoutException) { return MachineSignInPromptView.Refused(RemoteSessionHost.MachineOfflineCode) with { Message = exception.Message }; }

        var page = SafePage(prompt.Page);
        if (prompt.Way != FleetMachineSignInWays.Browser)
            return new MachineSignInPromptView("ok", null, prompt.AttemptId, prompt.Way, page?.AbsoluteUri, prompt.UserCode is { Length: <= 64 } code ? code : null,
                prompt.Ask is { Length: <= 256 } ask ? ask : null, prompt.ExpiresAt);

        // A browser sign-in: the page must be one to open, and the redirect a loopback address of this computer.
        if (page is null || !Uri.TryCreate(prompt.Redirect, UriKind.Absolute, out var redirect) || ForwardedState(page) is not { } state)
        {
            await CancelOnMachineAsync(host, prompt.AttemptId).ConfigureAwait(false);
            return MachineSignInPromptView.Refused("signin.failed") with { Message = "The machine gave no page and address this computer can sign it in with." };
        }

        FleetLoopbackRedirect listener;
        try
        {
            listener = FleetLoopbackRedirect.StartAt(redirect);
        }
        catch (Exception exception) when (exception is ArgumentException or SocketException)
        {
            await CancelOnMachineAsync(host, prompt.AttemptId).ConfigureAwait(false);
            return MachineSignInPromptView.Refused("port_busy") with
            {
                Message = exception is SocketException
                    ? $"Another program of this computer uses the port {redirect.Port}, which the sign-in comes back to: close it, or start the sign-in again."
                    : exception.Message,
            };
        }

        var lifetime = new CancellationTokenSource(prompt.ExpiresAt - DateTimeOffset.UtcNow is { Ticks: > 0 } left ? left : TimeSpan.FromMinutes(1));
        _forwardedSignIns[prompt.AttemptId] = lifetime;
        _ = ForwardAsync(host, prompt.AttemptId, listener, state, lifetime);
        if (!ProviderLoginService.OpenBrowser(page))
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            await CancelOnMachineAsync(host, prompt.AttemptId).ConfigureAwait(false);
            return MachineSignInPromptView.Refused("signin.browser") with { Message = "The browser of this computer did not open the sign-in." };
        }

        return new MachineSignInPromptView("ok", null, prompt.AttemptId, prompt.Way, page.AbsoluteUri, null, null, prompt.ExpiresAt);
    }

    /// <summary>Gives a sign-in of a machine the code the person pasted.</summary>
    [NeoRpcMethod("signInCode")]
    public async Task<MachineSignInStateView> SignInCodeAsync(MachineSignInCodeRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return MachineSignInStateView.Refused(refused);
        if (request!.MachineId is not { Length: > 0 and <= 64 } id || request.AttemptId is not { Length: > 0 and <= 64 } attempt || request.Code is not { Length: > 0 and <= 4096 } code)
            return MachineSignInStateView.Refused("invalid_request");
        if (_fleet!.Find(id) is not { } host) return MachineSignInStateView.Refused("not_found");
        return await StateAsync(host, (client, token) => client.CompleteSignInAsync(new FleetMachineSignInCompleteParams(attempt, null, code.Trim()), token), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Waits a while for a sign-in of a machine to end.</summary>
    [NeoRpcMethod("signInWait")]
    public async Task<MachineSignInStateView> SignInWaitAsync(MachineSignInAttemptRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return MachineSignInStateView.Refused(refused);
        if (request!.MachineId is not { Length: > 0 and <= 64 } id || request.AttemptId is not { Length: > 0 and <= 64 } attempt) return MachineSignInStateView.Refused("invalid_request");
        if (_fleet!.Find(id) is not { } host) return MachineSignInStateView.Refused("not_found");
        return await StateAsync(host, (client, token) => client.WaitSignInAsync(attempt, 15, token), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Ends a sign-in of a machine that waits, and stops listening for its browser.</summary>
    [NeoRpcMethod("signInCancel")]
    public async Task<MachineSignInStateView> SignInCancelAsync(MachineSignInAttemptRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return MachineSignInStateView.Refused(refused);
        if (request!.MachineId is not { Length: > 0 and <= 64 } id || request.AttemptId is not { Length: > 0 and <= 64 } attempt) return MachineSignInStateView.Refused("invalid_request");
        if (_forwardedSignIns.TryRemove(attempt, out var lifetime)) await lifetime.CancelAsync().ConfigureAwait(false);
        if (_fleet!.Find(id) is not { } host) return MachineSignInStateView.Refused("not_found");
        return await StateAsync(host, (client, token) => client.CancelSignInAsync(attempt, token), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Signs a provider, an MCP server or a tool of a machine out: its tokens are deleted there.</summary>
    [NeoRpcMethod("signOut")]
    public async Task<MachineSignInItemResult> SignOutAsync(MachineSignInTargetRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (request!.MachineId is not { Length: > 0 and <= 64 } id || request.Kind is not { Length: > 0 and <= 16 } || request.Key is not { Length: > 0 and <= 128 } || request.Space is { Length: > 64 })
            return new("invalid_request", null, null);
        if (_fleet!.Find(id) is not { } host) return new("not_found", null, null);
        try
        {
            var item = await host.ManageAsync((client, token) => client.SignOutAsync(new FleetMachineSignInTarget(request.Kind, request.Key, request.Space), token), changes: true,
                cancellationToken).ConfigureAwait(false);
            Changed();
            return new("ok", View(item), null);
        }
        catch (ObjectDisposedException) { return new(RemoteSessionHost.Describe(host.State), null, null); }
        catch (FleetException exception) { return new(Code(exception), null, exception.Error.Message); }
        catch (Exception exception) when (exception is IOException or TimeoutException) { return new(RemoteSessionHost.MachineOfflineCode, null, exception.Message); }
    }

    // Waits for the browser to come back to this computer, then gives the callback to the machine, which exchanges the code.
    private async Task ForwardAsync(RemoteSessionHost host, string attemptId, FleetLoopbackRedirect listener, string state, CancellationTokenSource lifetime)
    {
        try
        {
            using (listener)
            {
                var (_, callback) = await listener.WaitForCallbackAsync(state, lifetime.Token).ConfigureAwait(false);
                await host.ManageAsync((client, token) => client.CompleteSignInAsync(new FleetMachineSignInCompleteParams(attemptId, callback.AbsoluteUri), token), changes: true,
                    lifetime.Token).ConfigureAwait(false);
                Changed();
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or FleetException or IOException or TimeoutException or SocketException)
        {
            // The person gave up, the attempt expired, or the machine went: the page reads how it ended from the machine.
        }
        finally
        {
            _forwardedSignIns.TryRemove(attemptId, out _);
            lifetime.Dispose();
        }
    }

    private async Task<MachineSignInStateView> StateAsync(RemoteSessionHost host, Func<CodeAlta.Fleet.Client.FleetNodeClient, CancellationToken, Task<FleetMachineSignInState>> call,
        CancellationToken cancellationToken)
    {
        try
        {
            var state = await host.ManageAsync(call, changes: false, cancellationToken).ConfigureAwait(false);
            if (state.Status == FleetMachineSignInStates.Completed) Changed();
            return new MachineSignInStateView(state.Status, state.Account is { Length: <= MaximumNameLength } account ? account : null, state.Code,
                state.Message is { Length: <= 1024 } message ? message : null);
        }
        catch (ObjectDisposedException) { return MachineSignInStateView.Refused(RemoteSessionHost.Describe(host.State)); }
        catch (FleetException exception) { return MachineSignInStateView.Refused(Code(exception)) with { Message = exception.Error.Message }; }
        catch (Exception exception) when (exception is IOException or TimeoutException) { return MachineSignInStateView.Refused(RemoteSessionHost.MachineOfflineCode); }
    }

    private static async Task CancelOnMachineAsync(RemoteSessionHost host, string attemptId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await host.ManageAsync((client, token) => client.CancelSignInAsync(attemptId, token), changes: false, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or FleetException or IOException or TimeoutException)
        {
            // The attempt ends by itself when it expires.
        }
    }

    // A page this computer opens for a machine: https, or http on the loopback (a local authorization server); never anything else.
    private static Uri? SafePage(string? page)
        => Uri.TryCreate(page, UriKind.Absolute, out var address) && page!.Length <= ProviderLoginService.MaximumUrlLength
           && (address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeHttp && address.IsLoopback)
            ? address
            : null;

    // The state the browser brings back: the one the authorization page names.
    private static string? ForwardedState(Uri page)
    {
        foreach (var pair in page.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0 && pair[..separator] == "state") return Uri.UnescapeDataString(pair[(separator + 1)..]);
        }

        return null;
    }

    private static MachineSignInItemView View(FleetMachineSignInItem item)
        => new(item.Kind, item.Key, item.Space, item.Title.Length <= MaximumNameLength ? item.Title : item.Title[..MaximumNameLength], item.State,
            item.Account is { Length: <= MaximumNameLength } account ? account : null, item.ExpiresAt, [.. item.Ways.Take(4)], item.Detail is { Length: <= 1024 } detail ? detail : null);
}

/// <summary>Something a machine signs in, as the person who manages it sees it: never a token.</summary>
/// <param name="Kind"><c>provider</c>, <c>mcp</c> or <c>cli</c>.</param>
/// <param name="Key">Its key.</param>
/// <param name="Space">The space of its profile; null for the machine's own.</param>
/// <param name="Title">Its name.</param>
/// <param name="State"><c>signed_in</c>, <c>signed_out</c> or <c>unknown</c>.</param>
/// <param name="Account">The account it is signed in as, when known.</param>
/// <param name="ExpiresAt">When its sign-in expires, when known.</param>
/// <param name="Ways">How this computer signs it in: <c>browser</c>, <c>device</c>, <c>code</c>; empty when only on the machine.</param>
/// <param name="Detail">What the machine says more of it.</param>
internal sealed record MachineSignInItemView(string Kind, string Key, string? Space, string Title, string State, string? Account, DateTimeOffset? ExpiresAt, string[] Ways,
    string? Detail);

/// <summary>What a machine signs in.</summary>
/// <param name="Status"><c>ok</c>, <c>not_manager</c>, <c>not_found</c>, <c>offline</c>, <c>stale_epoch</c>, or the code the machine answered.</param>
/// <param name="Message">What the machine said, when it refused.</param>
/// <param name="Items">Its providers, MCP servers and tools.</param>
internal sealed record MachineSignInsView(string Status, string? Message, MachineSignInItemView[] Items)
{
    internal static MachineSignInsView Refused(string status) => new(status, null, []);
}

/// <summary>A sign-in to start on a machine.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="MachineId">The machine.</param>
/// <param name="Kind"><c>provider</c>, <c>mcp</c> or <c>cli</c>.</param>
/// <param name="Key">What to sign in.</param>
/// <param name="Space">The space of its profile; null for the machine's own.</param>
/// <param name="Way"><c>browser</c>, <c>device</c> or <c>code</c>; null for the first it offers.</param>
internal sealed record MachineSignInStartRequest(string ExpectedEpoch, string MachineId, string Kind, string Key, string? Space, string? Way);

/// <summary>What the page shows for a sign-in that started on a machine.</summary>
/// <param name="Status"><c>ok</c>, <c>port_busy</c>, <c>signin.browser</c>, <c>signin.unsupported</c>, <c>not_manager</c>, <c>offline</c>...</param>
/// <param name="Message">Why it could not start.</param>
/// <param name="AttemptId">The attempt, for the calls that follow.</param>
/// <param name="Way"><c>browser</c> (the browser was opened here), <c>device</c> or <c>code</c>.</param>
/// <param name="Page">The page: opened already for <c>browser</c>; to open for <c>device</c> and <c>code</c>.</param>
/// <param name="UserCode">For <c>device</c>: the code to enter on the page.</param>
/// <param name="Ask">For <c>code</c>: what the person pastes.</param>
/// <param name="ExpiresAt">When the attempt ends if nothing comes.</param>
internal sealed record MachineSignInPromptView(string Status, string? Message, string? AttemptId, string? Way, string? Page, string? UserCode, string? Ask, DateTimeOffset? ExpiresAt)
{
    internal static MachineSignInPromptView Refused(string status) => new(status, null, null, null, null, null, null, null);
}

/// <summary>An attempt to sign a machine in.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="MachineId">The machine.</param>
/// <param name="AttemptId">The attempt.</param>
internal sealed record MachineSignInAttemptRequest(string ExpectedEpoch, string MachineId, string AttemptId);

/// <summary>The code the person pasted for a sign-in of a machine.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="MachineId">The machine.</param>
/// <param name="AttemptId">The attempt.</param>
/// <param name="Code">The code.</param>
internal sealed record MachineSignInCodeRequest(string ExpectedEpoch, string MachineId, string AttemptId, string Code);

/// <summary>Where a sign-in of a machine stands.</summary>
/// <param name="Status"><c>waiting</c>, <c>completed</c>, <c>failed</c>, <c>none</c>, or why the machine could not say.</param>
/// <param name="Account">The account signed in, once it is.</param>
/// <param name="Code">Why it failed.</param>
/// <param name="Message">What the machine said of a failure.</param>
internal sealed record MachineSignInStateView(string Status, string? Account, string? Code, string? Message)
{
    internal static MachineSignInStateView Refused(string status) => new(status, null, null, null);
}

/// <summary>Something of a machine to sign out.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="MachineId">The machine.</param>
/// <param name="Kind"><c>provider</c>, <c>mcp</c> or <c>cli</c>.</param>
/// <param name="Key">Its key.</param>
/// <param name="Space">The space of its profile; null for the machine's own.</param>
internal sealed record MachineSignInTargetRequest(string ExpectedEpoch, string MachineId, string Kind, string Key, string? Space);

/// <summary>The outcome of a sign-out on a machine.</summary>
/// <param name="Status"><c>ok</c>, or why it was refused.</param>
/// <param name="Item">It as it stands now.</param>
/// <param name="Message">Why it was refused.</param>
internal sealed record MachineSignInItemResult(string Status, MachineSignInItemView? Item, string? Message);
