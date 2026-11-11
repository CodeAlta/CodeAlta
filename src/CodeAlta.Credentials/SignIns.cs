using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Catalog.Secrets;

namespace CodeAlta.Credentials;

/// <summary>
/// A sign-in the user shared for websites: the item of a vault whose user name, password and one-time code the browser of a
/// session types on the sites it lists. It holds references, never a value.
/// </summary>
/// <param name="Id">Its id: lowercase letters, digits, <c>-</c> and <c>_</c>.</param>
/// <param name="Label">What the user and the card call it, such as <c>GitHub</c>.</param>
/// <param name="Provider">The id of the credential provider that reads it.</param>
/// <param name="Sites">The origins it is typed on, such as <c>https://github.com</c>: scheme, host and port, nothing else.</param>
/// <param name="Username">The reference of the user name in the vault.</param>
/// <param name="Password">The reference of the password in the vault.</param>
/// <param name="Totp">The reference of the one-time code, or null when the site asks for none.</param>
public sealed record SignIn(string Id, string Label, string Provider, IReadOnlyList<string> Sites, string Username, string Password, string? Totp)
{
    /// <summary>Gets who saved it.</summary>
    public string SetBy { get; init; } = string.Empty;

    /// <summary>Gets when it was saved.</summary>
    public DateTimeOffset SetAt { get; init; }
}

/// <summary>A site the user allowed a sign-in on without being asked again, until a time.</summary>
/// <param name="SignIn">The id of the sign-in.</param>
/// <param name="Origin">The origin of the site.</param>
/// <param name="Until">When the grant ends (UTC).</param>
public sealed record SiteGrant(string SignIn, string Origin, DateTimeOffset Until);

/// <summary>
/// The values of a sign-in, read from its vault for one fill of a page, and already told to the masking of the host. Dispose
/// it as soon as they are typed: it lets go of them, and nothing else keeps them.
/// </summary>
public sealed class SignInValues : IDisposable
{
    private string? _username;
    private string? _password;
    private string? _totp;
    private bool _disposed;

    internal SignInValues(string username, string password, string? totp, DateTimeOffset? totpExpiresAt)
    {
        _username = username;
        _password = password;
        _totp = totp;
        TotpExpiresAt = totpExpiresAt;
    }

    /// <summary>Gets the user name.</summary>
    /// <exception cref="ObjectDisposedException">The values were let go.</exception>
    public string Username => Live(_username);

    /// <summary>Gets the password.</summary>
    /// <exception cref="ObjectDisposedException">The values were let go.</exception>
    public string Password => Live(_password);

    /// <summary>Gets the one-time code, or null when the sign-in has none.</summary>
    /// <exception cref="ObjectDisposedException">The values were let go.</exception>
    public string? Totp
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _totp;
        }
    }

    /// <summary>Gets when the one-time code stops being valid, when the vault says.</summary>
    public DateTimeOffset? TotpExpiresAt { get; }

    /// <summary>Lets go of the values.</summary>
    public void Dispose()
    {
        _disposed = true;
        _username = null;
        _password = null;
        _totp = null;
    }

    /// <inheritdoc />
    public override string ToString() => "SignInValues(***)";

    private string Live(string? value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return value!;
    }
}

/// <summary>
/// The sign-ins of websites of this machine and the sites the user allowed them on, in <c>bindings.json</c> beside the store
/// (its members <c>sign_ins</c> and <c>site_grants</c>). Only the user's own store says which vault item is typed on which
/// site; the file holds no value.
/// </summary>
/// <remarks>
/// A site is an origin: <c>https</c> and a host, with a port when it is not the default one; <c>http</c> only for the loopback.
/// A page matches a sign-in only when its origin is one of the sites exactly: no subdomain, no other scheme or port.
/// </remarks>
public sealed class SignIns
{
    /// <summary>The member of <c>bindings.json</c> that holds the sign-ins.</summary>
    public const string SignInsMember = "sign_ins";

    /// <summary>The member of <c>bindings.json</c> that holds the sites the user allowed.</summary>
    public const string GrantsMember = "site_grants";

    /// <summary>The most sign-ins a machine keeps.</summary>
    public const int MaximumSignIns = 500;

    /// <summary>The most sites of one sign-in.</summary>
    public const int MaximumSites = 20;

    /// <summary>How long a site the user allowed stays allowed.</summary>
    public static readonly TimeSpan GrantDuration = TimeSpan.FromDays(30);

    private readonly SecretBindings _bindings;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new instance of the <see cref="SignIns"/> class.</summary>
    /// <param name="bindings">The file of the bindings of the store.</param>
    /// <param name="time">The clock of the grants; the system's when null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="bindings"/> is null.</exception>
    public SignIns(SecretBindings bindings, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        _bindings = bindings;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Lists the sign-ins.</summary>
    /// <returns>The sign-ins, in the order of their labels.</returns>
    /// <exception cref="InvalidDataException">The file is damaged.</exception>
    public IReadOnlyList<SignIn> List()
    {
        if (_bindings.ReadMember(SignInsMember) is not JsonObject entries) return [];
        var list = new List<SignIn>(entries.Count);
        foreach (var (id, node) in entries)
        {
            if (ToSignIn(id, node) is { } signIn) list.Add(signIn);
        }

        return [.. list.OrderBy(static signIn => signIn.Label, StringComparer.CurrentCultureIgnoreCase).ThenBy(static signIn => signIn.Id, StringComparer.Ordinal)];
    }

    /// <summary>Gets a sign-in.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="signIn">The sign-in, when there is one.</param>
    /// <returns>True when it exists.</returns>
    public bool TryGet(string id, [NotNullWhen(true)] out SignIn? signIn)
    {
        ArgumentNullException.ThrowIfNull(id);
        signIn = List().FirstOrDefault(candidate => candidate.Id == id);
        return signIn is not null;
    }

    /// <summary>Finds the sign-ins of a page: those that list its origin exactly.</summary>
    /// <param name="url">The address of the page, or its origin.</param>
    /// <returns>The sign-ins of that origin; none for an address that is not of a site (<see cref="OriginOf"/>).</returns>
    public IReadOnlyList<SignIn> FindByOrigin(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (OriginOf(url) is not { } origin) return [];
        return [.. List().Where(signIn => signIn.Sites.Contains(origin, StringComparer.Ordinal))];
    }

    /// <summary>Adds a sign-in, or replaces the one of the same id; the grants of the sites it no longer lists end.</summary>
    /// <param name="signIn">The sign-in; its sites are normalized.</param>
    /// <param name="setBy">Who saves it.</param>
    /// <returns>The sign-in as saved.</returns>
    /// <exception cref="ArgumentException">A value is not valid.</exception>
    /// <exception cref="InvalidOperationException">The machine holds the most sign-ins already.</exception>
    public SignIn Save(SignIn signIn, string setBy)
    {
        ArgumentNullException.ThrowIfNull(signIn);
        ArgumentNullException.ThrowIfNull(setBy);
        if (!SecretBindings.IsProviderId(signIn.Id)) throw new ArgumentException($"'{signIn.Id}' is not the id of a sign-in: lowercase letters, digits, - and _.", nameof(signIn));
        if (!SecretBindings.IsProviderId(signIn.Provider)) throw new ArgumentException($"'{signIn.Provider}' is not the id of a credential provider.", nameof(signIn));
        var label = signIn.Label?.Trim() ?? string.Empty;
        if (label.Length is 0 or > SecretBindings.MaximumTextLength) throw new ArgumentException("The label is empty or too long.", nameof(signIn));
        Reference(signIn.Username, "user name");
        Reference(signIn.Password, "password");
        if (signIn.Totp is not null && signIn.Totp.Trim().Length == 0) throw new ArgumentException("The reference of the one-time code is blank: give none.", nameof(signIn));
        if (signIn.Totp is not null) Reference(signIn.Totp, "one-time code");
        if (signIn.Sites is null || signIn.Sites.Count is 0 or > MaximumSites) throw new ArgumentException($"A sign-in lists from 1 to {MaximumSites} sites.", nameof(signIn));
        var sites = new List<string>(signIn.Sites.Count);
        foreach (var site in signIn.Sites)
        {
            var origin = NormalizeSite(site) ?? throw new ArgumentException($"'{site}' is not a site: an origin such as https://github.com, without a path (http only for this computer).", nameof(signIn));
            if (!sites.Contains(origin, StringComparer.Ordinal)) sites.Add(origin);
        }

        var saved = signIn with
        {
            Label = label,
            Sites = sites,
            Username = signIn.Username.Trim(),
            Password = signIn.Password.Trim(),
            Totp = signIn.Totp?.Trim(),
            SetBy = setBy,
            SetAt = _time.GetUtcNow(),
        };
        _bindings.ChangeMember(SignInsMember, current =>
        {
            var entries = current as JsonObject ?? [];
            if (!entries.ContainsKey(saved.Id) && entries.Count >= MaximumSignIns) throw new InvalidOperationException($"This machine keeps at most {MaximumSignIns} sign-ins.");
            entries.Remove(saved.Id);
            var sitesNode = new JsonArray();
            foreach (var site in saved.Sites) sitesNode.Add(site);
            var entry = new JsonObject
            {
                ["label"] = saved.Label,
                ["provider"] = saved.Provider,
                ["sites"] = sitesNode,
                ["username"] = saved.Username,
                ["password"] = saved.Password,
                ["set_by"] = saved.SetBy,
                ["set_at"] = saved.SetAt.ToString("O", CultureInfo.InvariantCulture),
            };
            if (saved.Totp is not null) entry["totp"] = saved.Totp;
            entries[saved.Id] = entry;
            return entries;
        });
        ChangeGrants(grants => grants.RemoveAll(grant => grant.SignIn == saved.Id && !saved.Sites.Contains(grant.Origin, StringComparer.Ordinal)) > 0);
        return saved;
    }

    /// <summary>Removes a sign-in and the sites the user allowed it on.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>True when it existed.</returns>
    public bool Remove(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var removed = _bindings.ChangeMember(SignInsMember, current =>
        {
            if (current is not JsonObject entries || !entries.Remove(id)) return current;
            return entries.Count == 0 ? null : entries;
        });
        ChangeGrants(grants => grants.RemoveAll(grant => grant.SignIn == id) > 0);
        return removed;
    }

    /// <summary>Removes the sign-ins of a credential provider, which is removed.</summary>
    /// <param name="provider">The id of the provider.</param>
    /// <returns>The ids of the sign-ins removed.</returns>
    public IReadOnlyList<string> RemoveProvider(string provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var ids = List().Where(signIn => signIn.Provider == provider).Select(static signIn => signIn.Id).ToArray();
        foreach (var id in ids) Remove(id);
        return ids;
    }

    /// <summary>Lists the sites the user allowed, the ones that ended left out.</summary>
    /// <returns>The grants, by sign-in and origin.</returns>
    public IReadOnlyList<SiteGrant> ListGrants()
    {
        var now = _time.GetUtcNow();
        return [.. ReadGrants(_bindings.ReadMember(GrantsMember)).Where(grant => grant.Until > now).OrderBy(static grant => grant.SignIn, StringComparer.Ordinal).ThenBy(static grant => grant.Origin, StringComparer.Ordinal)];
    }

    /// <summary>Tells whether the user allowed a sign-in on a site, and the grant has not ended.</summary>
    /// <param name="id">The id of the sign-in.</param>
    /// <param name="origin">The origin of the site.</param>
    /// <returns>True when it is typed there without asking.</returns>
    public bool IsGranted(string id, string origin)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(origin);
        if (OriginOf(origin) is not { } site || !TryGet(id, out var signIn) || !signIn.Sites.Contains(site, StringComparer.Ordinal)) return false;
        return ListGrants().Any(grant => grant.SignIn == id && grant.Origin == site);
    }

    /// <summary>Allows a sign-in on one of its sites for <see cref="GrantDuration"/>, from now.</summary>
    /// <param name="id">The id of the sign-in.</param>
    /// <param name="origin">The origin of the site: one the sign-in lists.</param>
    /// <returns>The grant.</returns>
    /// <exception cref="KeyNotFoundException">No sign-in has that id.</exception>
    /// <exception cref="ArgumentException">The sign-in does not list that site.</exception>
    public SiteGrant Grant(string id, string origin)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(origin);
        if (!TryGet(id, out var signIn)) throw new KeyNotFoundException($"No sign-in is named '{id}'.");
        var site = OriginOf(origin);
        if (site is null || !signIn.Sites.Contains(site, StringComparer.Ordinal)) throw new ArgumentException($"The sign-in {signIn.Label} is not for {origin}.", nameof(origin));
        var grant = new SiteGrant(id, site, _time.GetUtcNow() + GrantDuration);
        ChangeGrants(grants =>
        {
            grants.RemoveAll(existing => existing.SignIn == id && existing.Origin == site);
            grants.Add(grant);
            return true;
        });
        return grant;
    }

    /// <summary>Ends the grants of a sign-in: on one site, or on all of them.</summary>
    /// <param name="id">The id of the sign-in.</param>
    /// <param name="origin">The origin of the site; null for every site.</param>
    /// <returns>True when a grant ended.</returns>
    public bool Revoke(string id, string? origin = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        var site = origin is null ? null : OriginOf(origin) ?? origin;
        return ChangeGrants(grants => grants.RemoveAll(grant => grant.SignIn == id && (site is null || grant.Origin == site)) > 0);
    }

    /// <summary>
    /// Gets the origin of an address: <c>scheme://host[:port]</c>, lowercase, with the port only when it is not the default
    /// one. Only <c>https</c>, and <c>http</c> for the loopback (<c>localhost</c>, <c>127.0.0.1</c>, <c>[::1]</c>), are sites.
    /// </summary>
    /// <param name="url">The address.</param>
    /// <returns>The origin, or null for an address that is not of a site.</returns>
    public static string? OriginOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        if (!string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrEmpty(uri.Host)) return null;
        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme != Uri.UriSchemeHttps && !(scheme == Uri.UriSchemeHttp && uri.IsLoopback)) return null;
        var host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.IdnHost.ToLowerInvariant();
        return uri.IsDefaultPort ? $"{scheme}://{host}" : $"{scheme}://{host}:{uri.Port.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>Gets the origin of a site as a sign-in lists it: an origin, without a path, a query or a fragment.</summary>
    /// <param name="site">The site, such as <c>https://github.com</c> or <c>github.com</c>.</param>
    /// <returns>The origin, or null for a text that is not a site.</returns>
    public static string? NormalizeSite(string? site)
    {
        if (string.IsNullOrWhiteSpace(site)) return null;
        var text = site.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.AbsolutePath is not ("/" or "") || uri.Query.Length != 0 || uri.Fragment.Length != 0) return null;
        return OriginOf(text);
    }

    private static void Reference(string? reference, string what)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > SecretBindings.MaximumTextLength)
        {
            throw new ArgumentException($"The reference of the {what} is empty or too long.", nameof(reference));
        }
    }

    private static SignIn? ToSignIn(string id, JsonNode? node)
    {
        if (!SecretBindings.IsProviderId(id) || node is not JsonObject entry) return null;
        string? Text(string member) => entry[member]?.GetValueKind() == JsonValueKind.String ? entry[member]!.GetValue<string>() : null;
        if (Text("provider") is not { } provider || !SecretBindings.IsProviderId(provider)
            || Text("username") is not { Length: > 0 } username || Text("password") is not { Length: > 0 } password) return null;
        var sites = new List<string>();
        if (entry["sites"] is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item?.GetValueKind() == JsonValueKind.String && NormalizeSite(item.GetValue<string>()) is { } origin && !sites.Contains(origin, StringComparer.Ordinal)) sites.Add(origin);
            }
        }

        if (sites.Count == 0) return null;
        var at = DateTimeOffset.TryParse(Text("set_at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : DateTimeOffset.MinValue;
        return new SignIn(id, Text("label") is { Length: > 0 } label ? label : id, provider, sites, username, password, Text("totp") is { Length: > 0 } totp ? totp : null)
        {
            SetBy = Text("set_by") ?? string.Empty,
            SetAt = at,
        };
    }

    private static List<SiteGrant> ReadGrants(JsonNode? member)
    {
        var list = new List<SiteGrant>();
        if (member is not JsonArray grants) return list;
        foreach (var node in grants)
        {
            if (node is not JsonObject grant) continue;
            string? Text(string member) => grant[member]?.GetValueKind() == JsonValueKind.String ? grant[member]!.GetValue<string>() : null;
            if (Text("sign_in") is not { } id || !SecretBindings.IsProviderId(id) || OriginOf(Text("origin")) is not { } origin
                || !DateTimeOffset.TryParse(Text("until"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var until)) continue;
            list.Add(new SiteGrant(id, origin, until));
        }

        return list;
    }

    // Changes the grants under the lock of the file; those that ended are dropped at each change.
    private bool ChangeGrants(Func<List<SiteGrant>, bool> change)
    {
        var changed = false;
        _bindings.ChangeMember(GrantsMember, current =>
        {
            var now = _time.GetUtcNow();
            var grants = ReadGrants(current);
            var before = grants.Count;
            grants.RemoveAll(grant => grant.Until <= now);
            changed = change(grants) || grants.Count != before;
            if (grants.Count == 0) return null;
            var array = new JsonArray();
            foreach (var grant in grants)
            {
                array.Add(new JsonObject
                {
                    ["sign_in"] = grant.SignIn,
                    ["origin"] = grant.Origin,
                    ["until"] = grant.Until.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                });
            }

            return array;
        });
        return changed;
    }
}
