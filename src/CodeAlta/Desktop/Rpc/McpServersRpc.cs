using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Catalog;
using CodeAlta.Plugin.Mcp;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// Lists and edits MCP server definitions (<c>mcp.json</c>) and their enablement policy for the desktop
/// Settings page, and authorizes an HTTP server in the browser (OAuth) as the terminal's MCP dialog does.
/// Editing never connects to a server: sessions reach the servers through the MCP plugin (<c>alta mcp</c>
/// and activated tools). Only an authorization connects, to the one server it is asked for.
/// </summary>
/// <remarks>
/// Environment and header values never leave the host. Arguments and URLs are returned with the same
/// redaction the terminal uses for display; a redacted value the form sends back unchanged is replaced
/// by the stored one, so editing one field does not require re-entering a secret. Tokens stay in the MCP
/// plugin's own store: the page learns only whether one is stored and when it expires.
/// </remarks>
[NeoRpcService("mcpServers", Version = 1)]
internal sealed class McpServersService
{
    /// <summary>Largest number of server definitions returned by one listing.</summary>
    internal const int MaximumServers = 128;

    private const int MaximumKeyLength = 128;
    private const int MaximumFieldLength = 4096;
    private const int MaximumArguments = 128;
    private const int MaximumNamedValues = 64;
    private const int MaximumNameLength = 128;
    private const int MaximumValueLength = 8192;
    private const int MaximumDisabledTools = 128;

    /// <summary>Largest authorization address sent to the page, in UTF-16 units; a longer one is opened but not sent.</summary>
    internal const int MaximumUrlLength = 8 * 1024;

    /// <summary>Most addresses forwarded for one authorization.</summary>
    internal const int MaximumPrompts = 8;

    private const int MaximumDetailLength = 512;
    private readonly ProjectCatalog? _projects;
    private readonly string? _epoch;
    private readonly string? _home;
    private readonly McpManagementService _management = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly McpServerLogin _login = static (key, paths, report, cancellationToken)
        // Its own service: an authorization runs for minutes, beside the listing and the edits of the other one.
        => new McpManagementService().LoginOAuthAsync(key, report, paths, cancellationToken);
    private readonly Lock _loginGate = new();
    private readonly CancellationTokenSource _closing = new();
    private Task? _activeLogin; // The running authorization, if any; guarded by the login gate.
    private bool _closed;

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal McpServersService()
    {
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <param name="projects">The host's project catalog, used to resolve a project id to its root.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <param name="home">The user home that holds the global <c>.alta</c> folder, or null for the profile.</param>
    /// <exception cref="ArgumentNullException"><paramref name="projects"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    internal McpServersService(ProjectCatalog projects, string epoch, string? home)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        _projects = projects;
        _epoch = epoch;
        _home = home;
    }

    /// <summary>Creates the service for an owned host over a literal authorization.</summary>
    /// <param name="projects">The host's project catalog, used to resolve a project id to its root.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <param name="home">The user home that holds the global <c>.alta</c> folder, or null for the profile.</param>
    /// <param name="login">The browser authorization of one server.</param>
    /// <exception cref="ArgumentNullException"><paramref name="projects"/> or <paramref name="login"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    internal McpServersService(ProjectCatalog projects, string epoch, string? home, McpServerLogin login)
        : this(projects, epoch, home)
    {
        ArgumentNullException.ThrowIfNull(login);
        _login = login;
    }

    /// <summary>Lists the global definitions and, when a project is named, that project's definitions.</summary>
    [NeoRpcMethod("list")]
    public async Task<McpServersListResponse> ListAsync(McpServersListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        McpServersListResponse Failed(string status) => new(status, request.ProjectId, [], null, null, false, false, 0);
        if (_projects is null) return Failed("unavailable");
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return Failed("stale_epoch");
        var project = await SettingsProjectScope.ResolveAsync(_projects, request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project.Status != "ok") return Failed(project.Status);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = _management.RefreshSnapshot(Paths(project.Root));
            var servers = new List<McpServerEntry>();
            var omitted = 0;
            foreach (var row in snapshot.Servers.Where(IsDefinition)
                         .OrderBy(static row => row.Key, StringComparer.OrdinalIgnoreCase).ThenBy(static row => row.SourceScope))
            {
                // A definition the editor cannot represent exactly is counted, never shortened into another one.
                if (servers.Count < MaximumServers && Entry(row) is { } entry) servers.Add(entry);
                else omitted++;
            }

            return new("ok", request.ProjectId, servers, SourceState(snapshot, McpManagementScope.Global),
                project.Root is null ? null : SourceState(snapshot, McpManagementScope.Project),
                snapshot.Policy.Enabled, snapshot.Policy.Diagnostic is not null, omitted);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failed("read_failed"); // Never serialize exceptions, file paths or parser diagnostics.
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Adds a server definition or updates the one named by the original key and scope, then applies the
    /// requested enablement when it differs from the effective policy.
    /// </summary>
    [NeoRpcMethod("save")]
    public async Task<McpServersMutationResponse> SaveAsync(McpServersSaveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_projects is null) return new("unavailable", null);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", null);
        if (!TryScope(request.Scope, out var scope)) return Invalid("The scope must be Global or Project.");
        var originalKey = string.IsNullOrWhiteSpace(request.OriginalKey) ? null : request.OriginalKey.Trim();
        var originalScope = scope;
        if (originalKey is not null && request.OriginalScope is not null && !TryScope(request.OriginalScope, out originalScope))
            return Invalid("The original scope must be Global or Project.");
        if (Validate(request.Server, out var transport) is { } refusal) return Invalid(refusal);
        var edit = request.Server!;
        var project = await SettingsProjectScope.ResolveAsync(_projects, request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project.Status != "ok") return new(project.Status, null);
        if (project.Root is null && (scope == McpManagementScope.Project || originalScope == McpManagementScope.Project))
            return Invalid("The project scope requires a project.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var paths = Paths(project.Root);
            var snapshot = _management.RefreshSnapshot(paths);
            if (SourceState(snapshot, scope) == "invalid") return new("config_invalid", null);
            var previous = originalKey is null ? null : Find(snapshot, originalKey, originalScope);
            if (originalKey is not null && previous is null) return new("not_found", null);
            var key = edit.Key!.Trim();
            // Saving must never replace another definition that happens to use the requested key.
            if (Find(snapshot, key, scope) is { } occupant && !ReferenceEquals(occupant, previous)) return new("conflict", null);

            var stdio = transport == McpManagementTransport.Stdio;
            IReadOnlyList<string> arguments = [];
            if (stdio && RestoreArguments(edit.Arguments ?? [], previous, out arguments) is { } hidden) return Invalid(hidden);
            if (Values(stdio ? edit.Environment : null, previous?.EditableEnv, "environment variable", out var environment) is { } missing) return Invalid(missing);
            if (Values(stdio ? null : edit.Headers, previous?.EditableHeaders, "header", out var headers) is { } absent) return Invalid(absent);
            var url = edit.Url?.Trim();
            // The redacted URL the listing returned stands for the stored one.
            if (previous?.EditableUrl is { } stored && string.Equals(url, previous.Url, StringComparison.Ordinal)) url = stored;

            await _management.AddOrUpdateServerAsync(new McpManagementServerEdit
            {
                Key = key, Transport = transport, Command = edit.Command, Args = arguments, Cwd = edit.WorkingDirectory,
                Env = environment, Url = url, Headers = headers,
            }, scope, originalKey, originalKey is null ? null : originalScope, paths, CancellationToken.None).ConfigureAwait(false);

            var saved = _management.CachedSnapshot is { } refreshed ? Find(refreshed, key, scope) : null;
            if (saved is null || (saved.PolicyEnabled != false) == edit.Enabled) return new("ok", null);
            if (_management.CachedSnapshot?.Policy.Diagnostic is not null) return new("policy_failed", null);
            try
            {
                await _management.SetServerEnabledAsync(key, edit.Enabled, scope, paths, CancellationToken.None).ConfigureAwait(false);
                return new("ok", null);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new("policy_failed", null); // The definition is saved; only its enablement was not written.
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return new("config_invalid", null);
        }
        catch (ArgumentException)
        {
            return Invalid("The server definition is not valid.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new("write_failed", null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes one server definition from the named scope; its policy entry is left untouched.</summary>
    [NeoRpcMethod("remove")]
    public Task<McpServersMutationResponse> RemoveAsync(McpServersRemoveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return MutateExistingAsync(request.ExpectedEpoch, request.ProjectId, request.Scope, request.Key, policy: false,
            async (key, scope, paths) =>
                (await _management.RemoveServerAsync(key, scope, paths, CancellationToken.None).ConfigureAwait(false)).Changed,
            cancellationToken);
    }

    /// <summary>Enables or disables one configured server in the policy file of the named scope.</summary>
    [NeoRpcMethod("setEnabled")]
    public Task<McpServersMutationResponse> SetEnabledAsync(McpServersSetEnabledRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return MutateExistingAsync(request.ExpectedEpoch, request.ProjectId, request.Scope, request.Key, policy: true,
            async (key, scope, paths) =>
            {
                await _management.SetServerEnabledAsync(key, request.Enabled, scope, paths, CancellationToken.None).ConfigureAwait(false);
                return true;
            }, cancellationToken);
    }

    /// <summary>
    /// Authorizes one HTTP server in the browser. The channel carries <c>prompt</c> events and then one
    /// <c>completed</c> or <c>failed</c> event; closing it cancels the authorization.
    /// </summary>
    [NeoRpcMethod("login")]
    public NeoRpcChannel<McpServerLoginEvent> Login(McpServerLoginRequest request, CancellationToken cancellationToken)
        => new(LoginAsync(request, cancellationToken), DesktopJsonContext.Default.McpServerLoginEvent);

    /// <summary>Removes the tokens stored for one HTTP server.</summary>
    [NeoRpcMethod("logout")]
    public async Task<McpServerLogoutResponse> LogoutAsync(McpServerLoginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var removed = false;
        var (code, _, _) = await ResolveHttpServerAsync(request, (key, paths) =>
        {
            lock (_loginGate)
            {
                // An authorization writes the tokens that this would remove.
                if (_activeLogin is { IsCompleted: false }) return "busy";
            }

            try
            {
                removed = _management.LogoutOAuth(key, paths);
                return "ok";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return "logout_failed";
            }
        }, cancellationToken).ConfigureAwait(false);
        return new(code, removed);
    }

    /// <summary>Cancels a running authorization, refuses new ones and waits for the canceled one to end.</summary>
    internal async Task CloseAsync()
    {
        Task? active;
        lock (_loginGate)
        {
            if (_closed) return;
            _closed = true;
            active = _activeLogin;
        }

        _closing.Cancel();
        if (active is not null) await active.ConfigureAwait(false); // An authorization settles as an event; it never throws.
    }

    internal async IAsyncEnumerable<McpServerLoginEvent> LoginAsync(McpServerLoginRequest? request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (code, key, paths) = request is null ? ("invalid", null, null)
            : await ResolveHttpServerAsync(request, null, cancellationToken).ConfigureAwait(false);
        if (key is null || paths is null)
        {
            yield return LoginFailed(code, null);
            yield break;
        }

        var prompts = Channel.CreateUnbounded<McpServerLoginEvent>(new UnboundedChannelOptions { SingleReader = true });
        CancellationTokenSource? cancellation = null;
        Task<McpServerLoginEvent>? login = null;
        string? refusal = null;
        lock (_loginGate)
        {
            if (_closed) refusal = "unavailable";
            else if (_activeLogin is { IsCompleted: false }) refusal = "busy";
            else
            {
                cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closing.Token);
                _activeLogin = login = RunLoginAsync(_login, key, paths, prompts.Writer, cancellation.Token);
            }
        }

        if (login is null || cancellation is null)
        {
            yield return LoginFailed(refusal ?? "unavailable", null);
            yield break;
        }

        try
        {
            // The reader ends when the authorization settles, also after cancellation: no token is needed here.
            await foreach (var prompt in prompts.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false)) yield return prompt;
            yield return await login.ConfigureAwait(false);
        }
        finally
        {
            // Closing the channel disposes this enumeration: the authorization is canceled and joined before the next one starts.
            cancellation.Cancel();
            await login.ConfigureAwait(false);
            cancellation.Dispose();
        }
    }

    // Never throws: every outcome is the terminal event. Runs off the caller's thread.
    private static async Task<McpServerLoginEvent> RunLoginAsync(McpServerLogin login, string key, McpManagementRequest paths,
        ChannelWriter<McpServerLoginEvent> prompts, CancellationToken cancellationToken)
    {
        try
        {
            var count = 0;
            string? refused = null;
            var result = await Task.Run(() => login(key, paths, message =>
            {
                // The plugin reports the address to open, and afterwards why the browser's answer was refused.
                if (AuthorizationAddress(message) is not { } address) Volatile.Write(ref refused, message);
                else if (Interlocked.Increment(ref count) <= MaximumPrompts)
                    prompts.TryWrite(new("prompt", address.Length <= MaximumUrlLength ? address : null, 0, null, null));
            }, cancellationToken), cancellationToken).ConfigureAwait(false);
            // The plugin's texts are written for display and redacted by it.
            var detail = Detail(Volatile.Read(ref refused) ?? result.Diagnostics.FirstOrDefault());
            return result.Status switch
            {
                McpManagementTestStatus.Succeeded => new("completed", null, result.Tools.Count, null, null),
                McpManagementTestStatus.Canceled => LoginFailed("canceled", null),
                McpManagementTestStatus.TimedOut => LoginFailed("timeout", detail),
                McpManagementTestStatus.Unsupported => LoginFailed("unsupported", detail),
                _ => LoginFailed("login_failed", detail),
            };
        }
        catch (OperationCanceledException)
        {
            return LoginFailed("canceled", null);
        }
        catch (Exception exception)
        {
            // Only the exception type: its message can quote a server response.
            return LoginFailed("login_failed", exception.GetType().Name);
        }
        finally
        {
            prompts.TryComplete();
        }
    }

    // Resolves a request to a saved HTTP definition that is the one in effect, then runs an optional step while
    // the configuration is held; the step's code replaces "ok".
    private async Task<(string Code, string? Key, McpManagementRequest? Paths)> ResolveHttpServerAsync(McpServerLoginRequest request,
        Func<string, McpManagementRequest, string>? then, CancellationToken cancellationToken)
    {
        if (_projects is null) return ("unavailable", null, null);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return ("stale_epoch", null, null);
        if (!TryScope(request.Scope, out var scope) || !ValidKey(request.Key)) return ("invalid", null, null);
        try
        {
            var project = await SettingsProjectScope.ResolveAsync(_projects, request.ProjectId, cancellationToken).ConfigureAwait(false);
            if (project.Status != "ok") return (project.Status, null, null);
            if (project.Root is null && scope == McpManagementScope.Project) return ("invalid", null, null);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var paths = Paths(project.Root);
                var snapshot = _management.RefreshSnapshot(paths);
                if (SourceState(snapshot, scope) == "invalid") return ("config_invalid", null, null);
                if (Find(snapshot, request.Key!, scope) is not { } row) return ("not_found", null, null);
                if (row.Transport != McpManagementTransport.Http) return ("unsupported", null, null);
                // The plugin authorizes the definition in effect: a global one that the project overrides is not it.
                if (row.State == McpManagementServerState.Shadowed) return ("shadowed", null, null);
                var code = then?.Invoke(row.Key, paths) ?? "ok";
                return code == "ok" ? ("ok", row.Key, paths) : (code, null, null);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return ("canceled", null, null);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return ("config_invalid", null, null);
        }
        catch (Exception)
        {
            return ("read_failed", null, null); // Never serialize exceptions, file paths or parser diagnostics.
        }
    }

    // The address a status message ends with, when it is an absolute http or https one.
    private static string? AuthorizationAddress(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var text = message.TrimEnd();
        var candidate = text[(text.LastIndexOfAny([' ', '\t', '\r', '\n']) + 1)..];
        return Uri.TryCreate(candidate, UriKind.Absolute, out var address) && (address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeHttp)
            ? address.AbsoluteUri : null;
    }

    private static string? Detail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = new string(value.Select(static character => char.IsControl(character) ? ' ' : character).ToArray()).Trim();
        return text.Length <= MaximumDetailLength ? text : text[..MaximumDetailLength];
    }

    private static McpServerLoginEvent LoginFailed(string code, string? detail) => new("failed", null, 0, detail, code);

    // Runs one edit of a definition that the listing for the same scope still contains.
    private async Task<McpServersMutationResponse> MutateExistingAsync(string? expectedEpoch, string? projectId, string? requestedScope,
        string? requestedKey, bool policy, Func<string, McpManagementScope, McpManagementRequest, Task<bool>> edit, CancellationToken cancellationToken)
    {
        if (_projects is null) return new("unavailable", null);
        if (!string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", null);
        if (!TryScope(requestedScope, out var scope)) return Invalid("The scope must be Global or Project.");
        if (!ValidKey(requestedKey)) return Invalid(KeyRule);
        var project = await SettingsProjectScope.ResolveAsync(_projects, projectId, cancellationToken).ConfigureAwait(false);
        if (project.Status != "ok") return new(project.Status, null);
        if (project.Root is null && scope == McpManagementScope.Project) return Invalid("The project scope requires a project.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var paths = Paths(project.Root);
            var snapshot = _management.RefreshSnapshot(paths);
            // A policy file that cannot be read cannot be rewritten without losing what it contains.
            if (policy ? snapshot.Policy.Diagnostic is not null : SourceState(snapshot, scope) == "invalid") return new("config_invalid", null);
            if (Find(snapshot, requestedKey!, scope) is null) return new("not_found", null);
            return new(await edit(requestedKey!, scope, paths).ConfigureAwait(false) ? "ok" : "not_found", null);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return new("config_invalid", null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new("write_failed", null);
        }
        finally
        {
            _gate.Release();
        }
    }

    private McpManagementRequest Paths(string? projectRoot)
        => new() { ProjectDirectory = projectRoot, UserHomeDirectory = _home, ProbeWritability = false };

    private static bool IsDefinition(McpManagementServerSnapshot row)
        => row is { SourceScope: not null, Transport: not null }
           && row.State is not (McpManagementServerState.MissingConfig or McpManagementServerState.InvalidConfig);

    private static McpManagementServerSnapshot? Find(McpManagementSnapshot snapshot, string key, McpManagementScope scope)
        => snapshot.Servers.FirstOrDefault(row => IsDefinition(row) && row.SourceScope == scope && string.Equals(row.Key, key, StringComparison.Ordinal));

    private static string SourceState(McpManagementSnapshot snapshot, McpManagementScope scope)
        => snapshot.Sources.FirstOrDefault(source => source.Scope == scope) switch
        {
            null or { Exists: false } => "missing",
            { IsValid: false } => "invalid",
            _ => "ok",
        };

    private static McpServerEntry? Entry(McpManagementServerSnapshot row)
    {
        var environment = row.EditableEnv ?? row.Env;
        var headers = row.EditableHeaders ?? row.Headers;
        if (!ValidKey(row.Key) || row.Command?.Length > MaximumFieldLength || row.Cwd?.Length > MaximumFieldLength
            || row.Url?.Length > MaximumFieldLength || row.Args.Count > MaximumArguments
            || row.Args.Any(static value => value.Length > MaximumFieldLength)
            || environment.Count > MaximumNamedValues || headers.Count > MaximumNamedValues
            || environment.Keys.Concat(headers.Keys).Any(static name => !ValidName(name)))
            return null;
        return new(row.Key, row.SourceScope.ToString()!, row.Transport.ToString()!, row.Command, row.Args,
            row.EditableArgs is { } arguments && !arguments.SequenceEqual(row.Args, StringComparer.Ordinal),
            row.Cwd, row.Url, row.EditableUrl is { } url && !string.Equals(url, row.Url, StringComparison.Ordinal),
            row.PolicyEnabled != false, row.OverridesGlobal, row.State == McpManagementServerState.Shadowed,
            Names(environment), Names(headers),
            row.DisabledTools.Where(static tool => tool.Length <= MaximumNameLength).Take(MaximumDisabledTools).ToArray(),
            row.OAuthTokenCached, row.OAuthTokenCached ? row.OAuthTokenExpiresAt : null);
    }

    private static McpServerValueName[] Names(IReadOnlyDictionary<string, string> values)
        => values.OrderBy(static value => value.Key, StringComparer.Ordinal)
            .Select(static value => new McpServerValueName(value.Key, !string.IsNullOrEmpty(value.Value))).ToArray();

    // Returns the refusal for a definition the editor cannot save, or null.
    private static string? Validate(McpServerEdit? edit, out McpManagementTransport transport)
    {
        transport = McpManagementTransport.Stdio;
        if (edit is null) return "A server definition is required.";
        if (!ValidKey(edit.Key?.Trim())) return KeyRule;
        if (!TryTransport(edit.Transport, out transport)) return "The transport must be Stdio or Http.";
        if (new[] { edit.Command, edit.WorkingDirectory, edit.Url }.Any(static value => !Text(value, MaximumFieldLength)))
            return "A server field is too long or contains control characters.";
        if (transport == McpManagementTransport.Stdio && string.IsNullOrWhiteSpace(edit.Command)) return "A stdio server requires a command.";
        if (transport == McpManagementTransport.Http && string.IsNullOrWhiteSpace(edit.Url)) return "An HTTP server requires a URL.";
        if (edit.Arguments is { } arguments && (arguments.Count > MaximumArguments || arguments.Any(static value => value is null || !Text(value, MaximumFieldLength))))
            return "The arguments are too many, too long or contain control characters.";
        foreach (var values in new[] { edit.Environment, edit.Headers })
        {
            if (values is null) continue;
            // Environment values may span lines (certificates); header values may not.
            var multiline = ReferenceEquals(values, edit.Environment);
            if (values.Count > MaximumNamedValues || values.Any(value => value is null || !ValidName(value.Name?.Trim()) || !Text(value.Value, MaximumValueLength, multiline)))
                return "An environment variable or header has an invalid name or value.";
            if (values.Select(static value => value.Name!.Trim()).Distinct(StringComparer.Ordinal).Count() != values.Count)
                return "An environment variable or header is listed twice.";
        }

        return null;
    }

    // Resolves submitted name/value pairs; a null value keeps the value stored under that name.
    private static string? Values(IReadOnlyList<McpServerValueEdit>? edits, IReadOnlyDictionary<string, string>? stored, string kind,
        out Dictionary<string, string> values)
    {
        values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var edit in edits ?? [])
        {
            var name = edit.Name!.Trim();
            if (edit.Value is { } value) values[name] = value;
            else if (stored is not null && stored.TryGetValue(name, out var kept)) values[name] = kept;
            else return $"The {kind} '{name}' has no stored value to keep.";
        }

        return null;
    }

    // Replaces each redacted argument the form returned unchanged by the stored argument it was shown for.
    private static string? RestoreArguments(IReadOnlyList<string> submitted, McpManagementServerSnapshot? previous, out IReadOnlyList<string> arguments)
    {
        arguments = submitted;
        if (previous?.EditableArgs is not { } stored || stored.Count != previous.Args.Count) return null;
        var hidden = new List<(string Shown, string Stored)>();
        for (var index = 0; index < stored.Count; index++)
        {
            if (!string.Equals(previous.Args[index], stored[index], StringComparison.Ordinal)) hidden.Add((previous.Args[index], stored[index]));
        }

        if (hidden.Count == 0) return null;
        var placeholders = hidden.Select(static value => value.Shown).ToHashSet(StringComparer.Ordinal);
        var restored = new string[submitted.Count];
        for (var index = 0; index < submitted.Count; index++)
        {
            var match = hidden.FindIndex(value => string.Equals(value.Shown, submitted[index], StringComparison.Ordinal));
            if (match < 0 && placeholders.Contains(submitted[index])) return "An argument still shows a redacted value; enter it again.";
            restored[index] = match < 0 ? submitted[index] : hidden[match].Stored;
            if (match >= 0) hidden.RemoveAt(match);
        }

        arguments = restored;
        return null;
    }

    private const string KeyRule = "A server key uses letters, digits, '-', '_' or '.' (at most 128).";

    // Only opaque configuration identifiers: a URL, path or command used as a key is never listed or written.
    private static bool ValidKey(string? key)
        => key is { Length: > 0 and <= MaximumKeyLength } && key.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool ValidName(string? name)
        => name is { Length: > 0 and <= MaximumNameLength } && !name.Any(static character => char.IsControl(character) || char.IsWhiteSpace(character) || character == '=');

    private static bool Text(string? value, int maximum, bool multiline = false)
        => value is null || (value.Length <= maximum && !value.Any(character => char.IsControl(character) && !(multiline && character is '\n' or '\r' or '\t')));

    private static bool TryScope(string? value, out McpManagementScope scope)
    {
        scope = string.Equals(value, "Project", StringComparison.OrdinalIgnoreCase) ? McpManagementScope.Project : McpManagementScope.Global;
        return scope == McpManagementScope.Project || string.Equals(value, "Global", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryTransport(string? value, out McpManagementTransport transport)
    {
        transport = string.Equals(value, "Http", StringComparison.OrdinalIgnoreCase) ? McpManagementTransport.Http : McpManagementTransport.Stdio;
        return transport == McpManagementTransport.Http || string.Equals(value, "Stdio", StringComparison.OrdinalIgnoreCase);
    }

    private static McpServersMutationResponse Invalid(string message) => new("invalid", message);
}

/// <summary>Resolves the optional project of a Settings request through the host's project catalog.</summary>
internal static class SettingsProjectScope
{
    /// <summary>
    /// Returns <c>ok</c> with the project root (null for the global scope), or the refusal for a project
    /// that is unknown, archived or whose folder is gone. The frontend only ever names a project id.
    /// </summary>
    internal static Task<(string Status, string? Root)> ResolveAsync(ProjectCatalog projects, string? projectId, CancellationToken cancellationToken)
        => ResolveAsync(projects, projectId, allowArchived: false, cancellationToken);

    /// <summary>
    /// Resolves like the overload above; with <paramref name="allowArchived"/> an archived project resolves
    /// to its root too, for requests that only read information about it.
    /// </summary>
    internal static async Task<(string Status, string? Root)> ResolveAsync(ProjectCatalog projects, string? projectId, bool allowArchived,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projects);
        if (projectId is null) return ("ok", null);
        if (projectId.Length is 0 or > 256 || projectId.Trim() != projectId || projectId.Any(char.IsControl)) return ("unknown_project", null);
        ProjectDescriptor? project;
        try
        {
            project = await projects.GetByIdAsync(projectId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ("read_failed", null);
        }

        if (project is null || !string.Equals(project.Id, projectId, StringComparison.Ordinal)) return ("unknown_project", null);
        if (project.Archived && !allowArchived) return ("archived_project", null);
        return Path.IsPathFullyQualified(project.ProjectPath) && Directory.Exists(project.ProjectPath)
            ? ("ok", Path.GetFullPath(project.ProjectPath))
            : ("project_unavailable", null);
    }
}

internal sealed record McpServersListRequest(string? ExpectedEpoch, string? ProjectId);

/// <summary>The listed definitions; a configuration state is <c>missing</c>, <c>ok</c> or <c>invalid</c>.</summary>
internal sealed record McpServersListResponse(string Status, string? ProjectId, IReadOnlyList<McpServerEntry> Servers,
    string? GlobalConfigState, string? ProjectConfigState, bool McpEnabled, bool PolicyReadError, int Omitted);

/// <summary>
/// One definition in one scope; redacted arguments or URL may be sent back unchanged to keep the stored value.
/// <c>Authorized</c> says that tokens of a browser authorization are stored for an HTTP server.
/// </summary>
internal sealed record McpServerEntry(string Key, string Scope, string Transport, string? Command, IReadOnlyList<string> Arguments,
    bool ArgumentsRedacted, string? WorkingDirectory, string? Url, bool UrlRedacted, bool Enabled, bool OverridesGlobal, bool Shadowed,
    IReadOnlyList<McpServerValueName> Environment, IReadOnlyList<McpServerValueName> Headers, IReadOnlyList<string> DisabledTools,
    bool Authorized, DateTimeOffset? AuthorizationExpiresAt);
internal sealed record McpServerValueName(string Name, bool HasValue);

/// <summary>An environment variable or header to save; a null value keeps the stored value.</summary>
internal sealed record McpServerValueEdit(string? Name, string? Value);
internal sealed record McpServerEdit(string? Key, string? Transport, string? Command, IReadOnlyList<string>? Arguments,
    string? WorkingDirectory, string? Url, IReadOnlyList<McpServerValueEdit>? Environment, IReadOnlyList<McpServerValueEdit>? Headers, bool Enabled);
internal sealed record McpServersSaveRequest(string? ExpectedEpoch, string? ProjectId, string? Scope, string? OriginalKey,
    string? OriginalScope, McpServerEdit? Server);
internal sealed record McpServersRemoveRequest(string? ExpectedEpoch, string? ProjectId, string? Scope, string? Key);
internal sealed record McpServersSetEnabledRequest(string? ExpectedEpoch, string? ProjectId, string? Scope, string? Key, bool Enabled);
internal sealed record McpServersMutationResponse(string Status, string? Message);
internal sealed record McpServerLoginRequest(string? ExpectedEpoch, string? ProjectId, string? Scope, string? Key);

/// <summary>
/// One authorization event. <c>prompt</c> carries the address to open in a browser; <c>completed</c> the number
/// of tools the server listed; <c>failed</c> a code and, as detail, the MCP plugin's redacted explanation or an
/// exception type name.
/// </summary>
internal sealed record McpServerLoginEvent(string Kind, string? Url, int Tools, string? Detail, string? Code);
internal sealed record McpServerLogoutResponse(string Status, bool Removed);

/// <summary>The browser authorization of one server: tests supply a literal one.</summary>
/// <param name="key">The server key.</param>
/// <param name="paths">The configuration and token paths.</param>
/// <param name="report">Receives the plugin's redacted progress messages, among them the address to open.</param>
/// <param name="cancellationToken">Cancels the authorization.</param>
internal delegate Task<McpManagementServerTestResult> McpServerLogin(string key, McpManagementRequest paths, Action<string> report,
    CancellationToken cancellationToken);
