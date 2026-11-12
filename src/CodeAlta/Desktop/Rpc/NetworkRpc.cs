using System.Diagnostics;
using System.Net;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Network;
using CodeAlta.Catalog.Secrets;
using CodeAlta.Hosting;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The page's side of the proxy of CodeAlta (<c>[network.proxy]</c>): the settings, their save, which applies them at once
/// to the next requests and to the programs started from then on, and a check of the connection through settings not saved.
/// The password is kept in a secret of CodeAlta, never in the configuration, and never returned to the page.
/// </summary>
[NeoRpcService("network", Version = 1)]
internal sealed class NetworkService
{
    /// <summary>The address a check requests when the page names none.</summary>
    internal const string DefaultCheckTarget = "https://www.codealta.io";

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);

    private readonly CodeAltaConfigStore? _config;
    private readonly LocalSecretStore? _secrets;
    private readonly Func<string?>? _apply;
    private readonly Func<ProxySettings>? _applied;
    private readonly IWebProxy? _system;
    private readonly Func<IWebProxy?, HttpMessageHandler>? _handler;
    private readonly string? _epoch;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal NetworkService()
    {
    }

    /// <summary>Creates the service of an owned host.</summary>
    /// <param name="catalog">The catalog: the configuration and the store of the secrets of the profile.</param>
    /// <param name="network">The proxy of the process.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    internal NetworkService(CatalogOptions catalog, CodeAltaNetwork network, string epoch)
        : this(new CodeAltaConfigStore(catalog), new LocalSecretStore(catalog.GlobalRoot), network.Refresh, () => network.Proxy.Settings, network.Proxy.SystemProxy, epoch,
            static proxy => new SocketsHttpHandler { Proxy = proxy, UseProxy = proxy is not null, AllowAutoRedirect = false, ConnectTimeout = CheckTimeout })
    {
    }

    /// <summary>Creates the service on given parts, for tests.</summary>
    /// <param name="config">The configuration.</param>
    /// <param name="secrets">The store of the password.</param>
    /// <param name="apply">Applies the saved settings; returns why they could not be, or null.</param>
    /// <param name="applied">Gives the settings applied now.</param>
    /// <param name="system">The proxy of the system mode; null for none.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <param name="handler">Creates the handler of a check over a proxy, or null to reach the network directly.</param>
    internal NetworkService(CodeAltaConfigStore config, LocalSecretStore secrets, Func<string?> apply, Func<ProxySettings> applied, IWebProxy? system, string epoch,
        Func<IWebProxy?, HttpMessageHandler> handler)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(applied);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        ArgumentNullException.ThrowIfNull(handler);
        (_config, _secrets, _apply, _applied, _system, _epoch, _handler) = (config, secrets, apply, applied, system, epoch, handler);
    }

    /// <summary>
    /// Gets whether clients of CodeAlta may still use the proxy of the system after the settings left the system mode, until a
    /// restart (<see cref="NetworkProxy.RestartAdvised"/>); null for never.
    /// </summary>
    internal Func<bool>? RestartAdvised { get; init; }

    /// <summary>Gets what is told once the password secret changed: the masking of the host.</summary>
    internal Action? SecretChanged { get; init; }

    /// <summary>Reads the settings saved and the ones applied.</summary>
    [NeoRpcMethod("read")]
    public async Task<NetworkProxyResponse> ReadAsync(NetworkRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return NetworkProxyResponse.Refused(refused);
        return await Task.Run(() => Listing("ok", null, null), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Saves the settings and applies them: the next requests of CodeAlta and the programs it starts from now on use them. A
    /// password given is kept in the secret of the settings; none keeps the one kept.
    /// </summary>
    [NeoRpcMethod("save")]
    public async Task<NetworkProxyResponse> SaveAsync(NetworkProxySaveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return NetworkProxyResponse.Refused(refused);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                ProxySettings saved;
                try { saved = _config!.LoadGlobalProxySettings(); }
                catch (Exception exception) when (IsConfigurationFailure(exception)) { saved = ProxySettings.Default; }
                if (SettingsOf(request.Mode, request.Url, request.Bypass, request.Authentication, request.User, saved.PasswordSecret, out var settings) is { } invalid)
                    return Listing("invalid", invalid, null);
                var password = string.IsNullOrEmpty(request.Password) ? null : request.Password;
                if (settings is { Mode: ProxyMode.Manual, Authentication: ProxyAuthentication.Password } && password is null && !HasSecret(settings.PasswordSecret!))
                    return Listing("invalid", "The proxy needs a password.", null);
                try
                {
                    if (password is not null)
                    {
                        try { _secrets!.SetSecret(settings.PasswordSecret!, password, SetBy); }
                        catch (Exception exception) when (CodeAltaNetwork.IsSecretFailure(exception))
                        {
                            // Nothing is saved: settings that name a password nobody could keep would only fail later.
                            return Listing("write_failed", $"The password could not be kept as the secret {settings.PasswordSecret}: {exception.Message} "
                                + "Check the store of the secrets in Settings > Secrets.", null);
                        }

                        SecretChanged?.Invoke();
                    }

                    _config!.SaveGlobalProxySettings(settings);
                }
                catch (Exception exception) when (IsConfigurationFailure(exception) || exception is ArgumentException)
                {
                    return Listing("write_failed", exception.Message, null);
                }

                return Listing("ok", null, _apply!());
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Requests an address through settings that may not be saved, as the page shows them, and says what happened: the
    /// status of the answer, the proxy it went through, how long it took, or why it failed.
    /// </summary>
    [NeoRpcMethod("check", TimeoutMilliseconds = 30_000)]
    public async Task<NetworkCheckResponse> CheckAsync(NetworkCheckRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, false, null, null, 0, null);
        var text = string.IsNullOrWhiteSpace(request.Target) ? DefaultCheckTarget : request.Target.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https") || target.UserInfo.Length > 0)
            return new("invalid", "The address to check must be http:// or https://.", false, null, null, 0, text);
        var secret = ProxySettings.DefaultPasswordSecret;
        try { secret = _config!.LoadGlobalProxySettings().PasswordSecret ?? secret; }
        catch (Exception exception) when (IsConfigurationFailure(exception)) { }
        if (SettingsOf(request.Mode, request.Url, request.Bypass, request.Authentication, request.User, secret, out var settings) is { } invalid)
            return new("invalid", invalid, false, null, null, 0, target.AbsoluteUri);
        string? password = request.Password;
        if (string.IsNullOrEmpty(password))
        {
            password = null;
            try
            {
                if (settings.Authentication == ProxyAuthentication.Password && _secrets!.TryGetSecret(settings.PasswordSecret!, out var kept)) password = kept;
            }
            catch (Exception exception) when (CodeAltaNetwork.IsSecretFailure(exception))
            {
                return new("ok", $"The proxy password could not be read from the secret {settings.PasswordSecret}: {exception.Message} Type it to check the connection.",
                    false, null, null, 0, target.AbsoluteUri);
            }
        }
        var proxy = NetworkProxy.Create(settings, password, _system);
        var via = proxy is null || proxy.IsBypassed(target) ? null : proxy.GetProxy(target);
        using var client = new HttpClient(_handler!(proxy), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CheckTimeout);
        var watch = Stopwatch.StartNew();
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, target);
            message.Headers.UserAgent.ParseAdd("CodeAlta-Network-Check");
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            // A proxy that refuses or cannot reach the host answers itself (407, 502, 504): the host was not reached.
            var reached = response.StatusCode is not (HttpStatusCode.ProxyAuthenticationRequired or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout);
            return new("ok", reached ? null : NetworkProxy.RedactCredentials(response.ReasonPhrase, password), reached, status, ViaOf(via), (int)watch.ElapsedMilliseconds, target.AbsoluteUri);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new("ok", $"No answer within {CheckTimeout.TotalSeconds:0} seconds.", false, null, ViaOf(via), (int)watch.ElapsedMilliseconds, target.AbsoluteUri);
        }
        catch (HttpRequestException exception)
        {
            return new("ok", NetworkProxy.RedactCredentials(MessageOf(exception), password), false, exception.StatusCode is { } code ? (int)code : null, ViaOf(via), (int)watch.ElapsedMilliseconds,
                target.AbsoluteUri);
        }
    }

    /// <summary>Builds the settings of a request; returns why they are not valid, or null.</summary>
    internal static string? SettingsOf(string? mode, string? url, string[]? bypass, string? authentication, string? user, string? passwordSecret, out ProxySettings settings)
    {
        settings = ProxySettings.Default;
        if (ProxySettings.ParseMode(mode) is not { } parsedMode) return "Unknown proxy mode.";
        if (ProxySettings.ParseAuthentication(authentication) is not { } parsedAuthentication) return "Unknown proxy authentication.";
        var entries = (bypass ?? []).SelectMany(static entry => ProxySettings.ParseBypass(entry)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        settings = new ProxySettings(parsedMode, string.IsNullOrWhiteSpace(url) ? null : url.Trim(), [.. entries], parsedAuthentication,
            string.IsNullOrWhiteSpace(user) ? null : user.Trim(),
            parsedAuthentication == ProxyAuthentication.Password ? passwordSecret ?? ProxySettings.DefaultPasswordSecret : passwordSecret);
        return settings.GetError();
    }

    private NetworkProxyResponse Listing(string status, string? message, string? applyError)
    {
        ProxySettings settings;
        try { settings = _config!.LoadGlobalProxySettings(); }
        catch (Exception exception) when (IsConfigurationFailure(exception))
        {
            return NetworkProxyResponse.Refused("config_invalid") with { Message = NetworkProxy.RedactCredentials(exception.GetBaseException().Message) };
        }

        var applied = _applied!();
        var probe = new Uri(DefaultCheckTarget);
        var system = _system is null || _system.IsBypassed(probe) ? null : ViaOf(_system.GetProxy(probe));
        return new NetworkProxyResponse(status, message, ProxySettings.NameOf(settings.Mode), settings.Url ?? "", [.. settings.Bypass], ProxySettings.NameOf(settings.Authentication),
            settings.User ?? "", settings.PasswordSecret is { } secret && HasSecret(secret), ProxySettings.NameOf(applied.Mode), NetworkProxy.RedactCredentials(applyError), system,
            // The settings of the file are not those that run: another program changed the file since the start or the last save.
            !SameSettings(applied, settings))
        {
            RestartAdvised = RestartAdvised?.Invoke() ?? false,
        };
    }

    private static bool SameSettings(ProxySettings left, ProxySettings right)
        => left.Mode == right.Mode && left.Url == right.Url && left.Bypass.SequenceEqual(right.Bypass) && left.Authentication == right.Authentication
           && left.User == right.User && left.PasswordSecret == right.PasswordSecret;

    private bool HasSecret(string name)
    {
        try { return _secrets!.ListSecrets().Any(secret => string.Equals(secret.Name, name, StringComparison.Ordinal)); }
        catch (Exception exception) when (CodeAltaNetwork.IsSecretFailure(exception)) { return false; }
    }

    // The proxy a request went through, as the page may show it: without the user name and password its address may hold.
    private static string? ViaOf(Uri? proxy) => proxy is null ? null : NetworkProxy.DisplayAddress(proxy);

    // The message of the innermost failure: the refusal of a proxy, a name that does not resolve, a refused connection.
    private static string MessageOf(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message) && !messages.Contains(current.Message)) messages.Add(current.Message.Trim());
        }

        return string.Join(" ", messages);
    }

    private static string SetBy => $"{Environment.UserName} (CodeAlta Desktop)";

    private static bool IsConfigurationFailure(Exception exception)
        => exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or FormatException
           || exception is Tomlyn.TomlException;

    private string? Refuse(string? expectedEpoch)
        => _config is null ? "unavailable" : !string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal) ? "stale_epoch" : null;
}

/// <summary>A request about the proxy.</summary>
/// <param name="ExpectedEpoch">The host epoch the page was given.</param>
internal sealed record NetworkRequest(string? ExpectedEpoch);

/// <summary>The settings of the proxy to save.</summary>
/// <param name="ExpectedEpoch">The host epoch the page was given.</param>
/// <param name="Mode"><c>system</c>, <c>none</c> or <c>manual</c>.</param>
/// <param name="Url">The address of the manual proxy.</param>
/// <param name="Bypass">The hosts reached without it; an entry may hold several, separated by commas or spaces.</param>
/// <param name="Authentication"><c>none</c>, <c>account</c> or <c>password</c>.</param>
/// <param name="User">The user name of <c>password</c>.</param>
/// <param name="Password">A new password, kept in the secret of the settings; null or empty keeps the one kept.</param>
internal sealed record NetworkProxySaveRequest(string? ExpectedEpoch, string? Mode, string? Url, string[]? Bypass, string? Authentication, string? User, string? Password);

/// <summary>A check of the connection through settings as the page shows them.</summary>
/// <param name="ExpectedEpoch">The host epoch the page was given.</param>
/// <param name="Target">The address to request; null for <c>https://www.codealta.io</c>.</param>
/// <param name="Mode"><c>system</c>, <c>none</c> or <c>manual</c>.</param>
/// <param name="Url">The address of the manual proxy.</param>
/// <param name="Bypass">The hosts reached without it.</param>
/// <param name="Authentication"><c>none</c>, <c>account</c> or <c>password</c>.</param>
/// <param name="User">The user name of <c>password</c>.</param>
/// <param name="Password">The password to try; null or empty for the one kept.</param>
internal sealed record NetworkCheckRequest(string? ExpectedEpoch, string? Target, string? Mode, string? Url, string[]? Bypass, string? Authentication, string? User, string? Password);

/// <summary>The settings of the proxy.</summary>
/// <param name="Status"><c>ok</c>, <c>unavailable</c>, <c>stale_epoch</c>, <c>invalid</c>, <c>write_failed</c> or <c>config_invalid</c>.</param>
/// <param name="Message">What was wrong, for a status that is not <c>ok</c>.</param>
/// <param name="Mode">The mode saved.</param>
/// <param name="Url">The address of the manual proxy saved, or empty.</param>
/// <param name="Bypass">The hosts reached without it.</param>
/// <param name="Authentication">The authentication saved.</param>
/// <param name="User">The user name saved, or empty.</param>
/// <param name="PasswordSet">Whether the secret of the password is set; its value is never returned.</param>
/// <param name="AppliedMode">The mode the process runs with now.</param>
/// <param name="ApplyError">Why the settings saved could not be applied, or null.</param>
/// <param name="SystemProxy">The proxy the system mode uses for <c>https://www.codealta.io</c>, or null when it reaches it directly.</param>
/// <param name="Pending">Whether the settings of the file differ from those that run: they apply at the next save or start.</param>
internal sealed record NetworkProxyResponse(string Status, string? Message, string Mode, string Url, string[] Bypass, string Authentication, string User, bool PasswordSet,
    string AppliedMode, string? ApplyError, string? SystemProxy, bool Pending)
{
    /// <summary>
    /// Gets whether connections CodeAlta made with the system settings keep them until it restarts: the settings left the
    /// system mode while it ran.
    /// </summary>
    public bool RestartAdvised { get; init; }

    internal static NetworkProxyResponse Refused(string status)
        => new(status, null, ProxySettings.SystemName, "", [], ProxySettings.NoneName, "", false, ProxySettings.SystemName, null, null, false);
}

/// <summary>What a check of the connection found.</summary>
/// <param name="Status"><c>ok</c> once the request was tried, else <c>unavailable</c>, <c>stale_epoch</c> or <c>invalid</c>.</param>
/// <param name="Message">Why the address was not reached, or what is wrong with the request.</param>
/// <param name="Reached">Whether the address answered.</param>
/// <param name="HttpStatus">The status of the answer, when there was one.</param>
/// <param name="Via">The proxy the request went through, or null when it went directly.</param>
/// <param name="ElapsedMilliseconds">How long the request took.</param>
/// <param name="Target">The address requested.</param>
internal sealed record NetworkCheckResponse(string Status, string? Message, bool Reached, int? HttpStatus, string? Via, int ElapsedMilliseconds, string? Target);
