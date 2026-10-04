using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CodeAlta.Catalog;
using CodeAlta.Hosting;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// Account sign-in for the configured Codex, Copilot and xAI providers of an owned host: stored sign-in state,
/// one sign-in at a time streamed as events, and sign-out.
/// </summary>
/// <remarks>
/// Failures cross the bridge as codes; exception messages, which can carry provider responses, never do.
/// </remarks>
[NeoRpcService("providerLogin", Version = 1)]
internal sealed class ProviderLoginService
{
    /// <summary>Largest address sent to the page, in UTF-16 units; a longer one is opened but not sent.</summary>
    internal const int MaximumUrlLength = 8 * 1024;

    /// <summary>Most prompts forwarded for one sign-in.</summary>
    internal const int MaximumPrompts = 8;

    private const int MaximumTextLength = 256;
    private readonly ProviderLoginOperations? _operations;
    private readonly string? _epoch;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _closing = new();
    private Task? _active; // The running sign-in, if any; guarded by the gate.
    private bool _closed;

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal ProviderLoginService()
    {
    }

    /// <summary>Creates the service over literal operations.</summary>
    /// <param name="operations">The configuration, sign-in and browser operations.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="operations"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    internal ProviderLoginService(ProviderLoginOperations operations, string epoch)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        _operations = operations;
        _epoch = epoch;
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <param name="store">The configuration store of the owned global root.</param>
    /// <param name="configuration">The configuration service that enables a provider and applies the providers.</param>
    /// <param name="stateRoot">The global root that holds provider credentials, as the TUI passes it.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stateRoot"/> or <paramref name="epoch"/> is blank.</exception>
    internal ProviderLoginService(CodeAltaConfigStore store, GlobalConfigService configuration, string stateRoot, string epoch)
        : this(Owned(store, configuration, stateRoot), epoch)
    {
    }

    /// <summary>Reads the stored sign-in state of one configured provider, without network authentication.</summary>
    [NeoRpcMethod("status")]
    public async Task<ProviderLoginStatusResponse> StatusAsync(ProviderLoginStatusRequest request, CancellationToken cancellationToken)
    {
        var (code, key, definition) = Resolve(request?.ExpectedEpoch, request?.Key);
        if (definition is null) return new(code, key, false, [], false, null, null, null);
        var modes = ConfiguredProviderLogin.GetLoginModes(definition.ProviderType).ToArray();
        if (modes.Length == 0) return new("ok", key, false, [], false, null, null, null);
        try
        {
            var status = await _operations!.Status(definition, cancellationToken).ConfigureAwait(false);
            return new("ok", key, true, modes, status.SignedIn, Bound(status.Account), Bound(status.Detail), status.ExpiresAt);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new("read_failed", key, true, modes, false, null, null, null);
        }
    }

    /// <summary>
    /// Signs in to the account of one configured provider. The channel carries <c>prompt</c> events and then one
    /// <c>completed</c> or <c>failed</c> event; closing it cancels the sign-in.
    /// </summary>
    [NeoRpcMethod("login")]
    public NeoRpcChannel<ProviderLoginEvent> Login(ProviderLoginRequest request, CancellationToken cancellationToken)
        => new(LoginAsync(request, cancellationToken), DesktopJsonContext.Default.ProviderLoginEvent);

    /// <summary>Signs one configured provider out of its account.</summary>
    [NeoRpcMethod("logout")]
    public async Task<ProviderLogoutResponse> LogoutAsync(ProviderLoginStatusRequest request, CancellationToken cancellationToken)
    {
        var (code, key, definition) = Resolve(request?.ExpectedEpoch, request?.Key);
        if (definition is null) return new(code, key, false);
        if (!ConfiguredProviderLogin.SupportsLogin(definition.ProviderType)) return new("unsupported", key, false);
        lock (_gate)
        {
            // A sign-in writes the credential that this would remove.
            if (_active is { IsCompleted: false }) return new("busy", key, false);
        }

        try
        {
            return new("ok", key, await _operations!.SignOut(definition, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new("logout_failed", key, false);
        }
    }

    /// <summary>Cancels a running sign-in, refuses new ones and waits for the canceled one to end.</summary>
    internal async Task CloseAsync()
    {
        Task? active;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            active = _active;
        }

        _closing.Cancel();
        if (active is not null) await active.ConfigureAwait(false); // A sign-in settles as an event; it never throws.
    }

    internal async IAsyncEnumerable<ProviderLoginEvent> LoginAsync(ProviderLoginRequest? request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (code, _, definition) = Resolve(request?.ExpectedEpoch, request?.Key);
        if (definition is null)
        {
            // A configuration that cannot be read is a failed sign-in, named by its status code.
            yield return code is "config_invalid" or "read_failed" ? Failed("login_failed", code) : Failed(code, null);
            yield break;
        }

        var modes = ConfiguredProviderLogin.GetLoginModes(definition.ProviderType);
        var mode = string.IsNullOrWhiteSpace(request!.Mode) ? modes.FirstOrDefault() : request.Mode.Trim().ToLowerInvariant();
        if (mode is null || !modes.Contains(mode, StringComparer.Ordinal))
        {
            yield return Failed("unsupported", null);
            yield break;
        }

        var prompts = Channel.CreateUnbounded<ProviderLoginEvent>(new UnboundedChannelOptions { SingleReader = true });
        CancellationTokenSource? cancellation = null;
        Task<ProviderLoginEvent>? login = null;
        string? refusal = null;
        lock (_gate)
        {
            if (_closed) refusal = "unavailable";
            else if (_active is { IsCompleted: false }) refusal = "busy";
            else
            {
                cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closing.Token);
                _active = login = RunAsync(_operations!, definition, mode, prompts.Writer, cancellation.Token);
            }
        }

        if (login is null || cancellation is null)
        {
            yield return Failed(refusal ?? "unavailable", null);
            yield break;
        }

        try
        {
            // The reader ends when the sign-in settles, also after cancellation: no token is needed here.
            await foreach (var prompt in prompts.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false)) yield return prompt;
            yield return await login.ConfigureAwait(false);
        }
        finally
        {
            // Closing the channel disposes this enumeration: the sign-in is canceled and joined before the next one starts.
            cancellation.Cancel();
            await login.ConfigureAwait(false);
            cancellation.Dispose();
        }
    }

    // Never throws: every outcome is the terminal event. Runs off the caller's thread; provider sign-in starts synchronously.
    private static async Task<ProviderLoginEvent> RunAsync(ProviderLoginOperations operations, CodeAltaProviderDocument definition,
        string mode, ChannelWriter<ProviderLoginEvent> prompts, CancellationToken cancellationToken)
    {
        try
        {
            var count = 0;
            var status = await Task.Run(() => operations.Login(definition, mode, (prompt, _) =>
            {
                if (Interlocked.Increment(ref count) > MaximumPrompts) return ValueTask.CompletedTask;
                var address = prompt.Uri.IsAbsoluteUri ? prompt.Uri.AbsoluteUri : null;
                var opened = address is not null && prompt.Uri.Scheme == Uri.UriSchemeHttps && operations.OpenBrowser(prompt.Uri);
                prompts.TryWrite(new("prompt", address?.Length <= MaximumUrlLength ? address : null, Bound(prompt.UserCode),
                    prompt.ExpiresAt, opened, false, null, null, null));
                return ValueTask.CompletedTask;
            }, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (status.Usable && definition.Enabled == false)
            {
                // The sign-in stands even when enabling fails: the page reads the providers again and shows the result.
                try { operations.Enable(definition.ProviderKey); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { }
            }

            return new("completed", null, null, status.ExpiresAt, false, status.SignedIn, Bound(status.Account), Bound(status.Detail), null);
        }
        catch (OperationCanceledException)
        {
            return Failed("canceled", null);
        }
        catch (TimeoutException)
        {
            return Failed("timeout", null);
        }
        catch (Exception exception)
        {
            // Only the exception type: its message can quote a provider response.
            return Failed("login_failed", exception.GetType().Name);
        }
        finally
        {
            prompts.TryComplete();
        }
    }

    private (string Code, string? Key, CodeAltaProviderDocument? Definition) Resolve(string? expectedEpoch, string? requestedKey)
    {
        if (_operations is null) return ("unavailable", null, null);
        if (!string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal)) return ("stale_epoch", null, null);
        var key = requestedKey?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(key) || key.Length > 64 || !key.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            return ("invalid", null, null);
        try
        {
            var definition = _operations.LoadDefinitions().FirstOrDefault(value => string.Equals(value.ProviderKey, key, StringComparison.OrdinalIgnoreCase));
            return definition is null ? ("unknown_provider", key, null) : ("ok", key, definition);
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
        {
            return ("config_invalid", key, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ("read_failed", key, null);
        }
    }

    private static ProviderLoginOperations Owned(CodeAltaConfigStore store, GlobalConfigService configuration, string stateRoot)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        return new(
            () => store.LoadGlobalProviderDefinitions(includeDisabled: true),
            (definition, mode, onPrompt, cancellationToken) => ConfiguredProviderLogin.LoginAsync(definition, stateRoot, mode, onPrompt, cancellationToken),
            (definition, cancellationToken) => ConfiguredProviderLogin.GetStatusAsync(definition, stateRoot, cancellationToken),
            (definition, cancellationToken) => ConfiguredProviderLogin.SignOutAsync(definition, stateRoot, cancellationToken),
            key => configuration.EnableProvider(key).Status == "ok",
            OpenBrowser);
    }

    // Hands an https address to the system browser, as the TUI does; a headless or locked-down host just reports false.
    private static bool OpenBrowser(Uri uri)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
            return true;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or PlatformNotSupportedException or IOException)
        {
            return false;
        }
    }

    private static ProviderLoginEvent Failed(string code, string? detail) => new("failed", null, null, null, false, false, null, detail, code);

    private static string? Bound(string? value)
        => value is null || value.Length <= MaximumTextLength ? value : value[..MaximumTextLength];
}

/// <summary>The operations behind <see cref="ProviderLoginService"/>; tests supply literal callbacks.</summary>
/// <param name="LoadDefinitions">Loads the type-completed provider definitions, including disabled ones.</param>
/// <param name="Login">Signs in with a mode, reporting what the user must open.</param>
/// <param name="Status">Reads the stored sign-in state.</param>
/// <param name="SignOut">Signs out; true when a credential was removed.</param>
/// <param name="Enable">Enables a disabled provider and applies the providers; true when it was enabled.</param>
/// <param name="OpenBrowser">Opens an https address in the system browser; false when it could not.</param>
internal sealed record ProviderLoginOperations(
    Func<IReadOnlyList<CodeAltaProviderDocument>> LoadDefinitions,
    Func<CodeAltaProviderDocument, string, Func<ProviderLoginPrompt, CancellationToken, ValueTask>, CancellationToken, Task<ProviderLoginStatus>> Login,
    Func<CodeAltaProviderDocument, CancellationToken, Task<ProviderLoginStatus>> Status,
    Func<CodeAltaProviderDocument, CancellationToken, Task<bool>> SignOut,
    Func<string, bool> Enable,
    Func<Uri, bool> OpenBrowser);

internal sealed record ProviderLoginStatusRequest(string? ExpectedEpoch, string? Key);

/// <summary>Stored sign-in state. <c>Supported</c> is false, with status <c>ok</c>, for a provider type without account sign-in.</summary>
internal sealed record ProviderLoginStatusResponse(string Status, string? Key, bool Supported, string[] Modes, bool SignedIn,
    string? Account, string? Detail, DateTimeOffset? ExpiresAt);
internal sealed record ProviderLoginRequest(string? ExpectedEpoch, string? Key, string? Mode);

/// <summary>
/// One sign-in event. <c>prompt</c> carries the address, a device code and its expiry and whether the browser
/// opened; <c>completed</c> the signed-in state; <c>failed</c> a code and at most an exception type name as detail.
/// </summary>
internal sealed record ProviderLoginEvent(string Kind, string? Url, string? UserCode, DateTimeOffset? ExpiresAt, bool BrowserOpened,
    bool SignedIn, string? Account, string? Detail, string? Code);
internal sealed record ProviderLogoutResponse(string Status, string? Key, bool Removed);
