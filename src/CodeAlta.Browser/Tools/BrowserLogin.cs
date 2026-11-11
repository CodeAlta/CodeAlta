using System.Text.Json;
using CodeAlta.Browser.Drivers;

namespace CodeAlta.Browser.Tools;

/// <summary>What the host answers to a sign-in a page asks for.</summary>
public enum LoginFillStatus
{
    /// <summary>The user agreed: the values are given, for this fill only.</summary>
    Granted,

    /// <summary>No sign-in of the user is for that site, or none has the id the agent gave.</summary>
    NoSignIn,

    /// <summary>Several sign-ins are for that site: the agent names one of <see cref="LoginFillGrant.Choices"/>.</summary>
    Choose,

    /// <summary>The user, or a host without one, denied it.</summary>
    Denied,

    /// <summary>The vault of the sign-in is locked: the user unlocks it in Settings › Secrets.</summary>
    Locked,

    /// <summary>The browser of the session cannot sign in: the user's own browser, a sandboxed session, a host without sign-ins.</summary>
    Unsupported,
}

/// <summary>A sign-in of a site the agent can choose, by its id.</summary>
/// <param name="Id">The id of the sign-in.</param>
/// <param name="Label">Its label, as the user named it.</param>
public sealed record LoginFillChoice(string Id, string Label);

/// <summary>
/// The answer of the host to the sign-in of a page: the values to type, when the user agreed, or why not. The values are told
/// to the masking of the host before they are given; the browser types them and disposes this at once, which lets go of them.
/// </summary>
public sealed class LoginFillGrant : IDisposable
{
    private string? _username;
    private string? _password;
    private string? _totp;

    private LoginFillGrant(LoginFillStatus status, string? message, IReadOnlyList<LoginFillChoice> choices)
    {
        Status = status;
        Message = message;
        Choices = choices;
    }

    /// <summary>Gets the answer.</summary>
    public LoginFillStatus Status { get; }

    /// <summary>Gets what the agent is told, in a sentence; never a value.</summary>
    public string? Message { get; }

    /// <summary>Gets the sign-ins of the site the agent can choose from, for <see cref="LoginFillStatus.Choose"/>.</summary>
    public IReadOnlyList<LoginFillChoice> Choices { get; }

    /// <summary>Gets the label of the sign-in that was granted, for the answer of the tool.</summary>
    public string? Label { get; private init; }

    internal string? Username => _username;

    internal string? Password => _password;

    internal string? Totp => _totp;

    /// <summary>Creates the grant of a sign-in the user agreed to.</summary>
    /// <param name="label">The label of the sign-in.</param>
    /// <param name="username">The user name, masked already.</param>
    /// <param name="password">The password, masked already.</param>
    /// <param name="totp">The one-time code, masked already, or null.</param>
    /// <returns>The grant.</returns>
    /// <exception cref="ArgumentException">The user name or the password is empty.</exception>
    public static LoginFillGrant Granted(string label, string username, string password, string? totp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrEmpty(username);
        ArgumentException.ThrowIfNullOrEmpty(password);
        return new LoginFillGrant(LoginFillStatus.Granted, null, []) { Label = label, _username = username, _password = password, _totp = string.IsNullOrEmpty(totp) ? null : totp };
    }

    /// <summary>Creates the answer that no sign-in is for the site.</summary>
    /// <param name="message">What the agent is told, or null for the default.</param>
    /// <returns>The answer.</returns>
    public static LoginFillGrant NoSignIn(string? message = null) => new(LoginFillStatus.NoSignIn, message, []);

    /// <summary>Creates the answer that several sign-ins are for the site.</summary>
    /// <param name="choices">The sign-ins the agent chooses from.</param>
    /// <returns>The answer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="choices"/> is null.</exception>
    public static LoginFillGrant Choose(IReadOnlyList<LoginFillChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        return new(LoginFillStatus.Choose, null, [.. choices]);
    }

    /// <summary>Creates a denial.</summary>
    /// <param name="message">Why, or null for the default.</param>
    /// <returns>The answer.</returns>
    public static LoginFillGrant Denied(string? message = null) => new(LoginFillStatus.Denied, message, []);

    /// <summary>Creates the answer that the vault of the sign-in is locked.</summary>
    /// <param name="message">Which vault, or null for the default.</param>
    /// <returns>The answer.</returns>
    public static LoginFillGrant Locked(string? message = null) => new(LoginFillStatus.Locked, message, []);

    /// <summary>Creates the answer that this browser cannot sign in.</summary>
    /// <param name="message">Why.</param>
    /// <returns>The answer.</returns>
    /// <exception cref="ArgumentException"><paramref name="message"/> is blank.</exception>
    public static LoginFillGrant Unsupported(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new(LoginFillStatus.Unsupported, message, []);
    }

    /// <summary>Lets go of the values.</summary>
    public void Dispose()
    {
        _username = null;
        _password = null;
        _totp = null;
    }

    /// <inheritdoc />
    public override string ToString() => $"LoginFillGrant({Status})";
}

/// <summary>
/// Asks the host for the sign-in of a site: it finds the sign-ins of that origin, asks the user unless a grant holds, and
/// reads the values from the vault.
/// </summary>
/// <param name="origin">The origin of the top frame of the tab, read from the browser.</param>
/// <param name="signInId">The sign-in the agent chose among several, or null.</param>
/// <param name="cancellationToken">Ends the wait.</param>
/// <returns>The answer, which the caller disposes.</returns>
internal delegate Task<LoginFillGrant> BrowserSignIn(string origin, string? signInId, CancellationToken cancellationToken);

/// <summary>
/// The tool <c>fill_login</c>: types the sign-in the user shared for the site of the selected tab into its fields, submits
/// them, and answers a status, never a value. The tool holds the calls of the session from the first key to the end of the
/// navigation: the other tools of the session wait for it.
/// </summary>
internal static class BrowserLogin
{
    /// <summary>The statuses the tool answers.</summary>
    public const string SignedIn = "signed_in", Submitted = "submitted", Failed = "failed", NoSignIn = "no_sign_in", Choose = "choose_sign_in",
        Denied = "denied", NoField = "no_field", Unsupported = "unsupported", Locked = "locked";

    /// <summary>The description of the tool.</summary>
    public const string Description =
        "Signs in to the site of the selected page with a sign-in the user shared in CodeAlta (Settings > Secrets): CodeAlta reads the site from the browser, " +
        "asks the user, types the user name, the password and the one-time code into the fields of the page, and submits them. You never get or give a value, " +
        "and you give no site. Call it on the page that shows the sign-in form. It answers a status: signed_in, submitted (check the page with take_snapshot), " +
        "failed (the page did not take it; the fields were cleared), no_sign_in, choose_sign_in (call again with one signInId of the list), denied, no_field, locked, unsupported.";

    private const int MaximumSteps = 3;

    /// <summary>Runs the tool on a page.</summary>
    public static async Task FillAsync(BrowserToolCall call, BrowserPageState state)
    {
        call.Page = state;
        var page = state.Page;
        var cancellationToken = call.CancellationToken;
        if (call.Session.Options.SignIn is not { } signIn)
        {
            Answer(call, Unsupported, "This browser cannot sign in to websites with the sign-ins of the user.");
            return;
        }

        if (page is not IChromiumPage)
        {
            Answer(call, Unsupported, "Signing in is done in Chromium browsers (Edge, Chrome) for now: sign in another way, or ask the user.");
            return;
        }

        if (OriginOf(page.Url) is not { } origin)
        {
            Answer(call, NoSignIn, $"The page {page.Url} is not of a website: a sign-in is typed on https sites only.");
            return;
        }

        // The fields are checked before the user is asked: nobody is asked for a page where nothing would be typed.
        var fields = await FieldsAsync(page, origin, cancellationToken).ConfigureAwait(false);
        if (!fields.Any)
        {
            Answer(call, NoField, NoFieldMessage(fields.Reason));
            return;
        }

        using var grant = await signIn(origin, call.OptionalString("signInId"), cancellationToken).ConfigureAwait(false);
        switch (grant.Status)
        {
            case LoginFillStatus.NoSignIn:
                Answer(call, NoSignIn, grant.Message ?? $"The user shared no sign-in for {origin}. Ask the user to sign in, or to add a sign-in for this site in Settings > Secrets of CodeAlta.");
                return;
            case LoginFillStatus.Choose:
                Answer(call, Choose, $"Several sign-ins of the user are for {origin}. Call fill_login again with the signInId of one of them:");
                foreach (var choice in grant.Choices) call.Lines.Add($"- signInId: {choice.Id} ({choice.Label})");
                return;
            case LoginFillStatus.Denied:
                Answer(call, Denied, grant.Message ?? "The user did not let this session sign in to this site. Do not try to sign in another way unless the user asks.");
                return;
            case LoginFillStatus.Locked:
                Answer(call, Locked, grant.Message ?? "The vault of this sign-in is locked. Ask the user to unlock it in Settings > Secrets of CodeAlta, then call fill_login again.");
                return;
            case LoginFillStatus.Unsupported:
                Answer(call, Unsupported, grant.Message ?? "This session cannot sign in to websites.");
                return;
        }

        // The user may have taken a while: the page is the same site still, or nothing is typed.
        if (OriginOf(page.Url) != origin)
        {
            Answer(call, NoField, "The page left the site while the user was asked. Call fill_login again on the sign-in page.");
            return;
        }

        var (status, note) = await TypeAsync(call, state, origin, grant, cancellationToken).ConfigureAwait(false);
        Answer(call, status, note.Replace("{label}", grant.Label, StringComparison.Ordinal));
        if (status is SignedIn or Submitted) call.Lines.Add($"The page is {page.Url}.");
    }

    private static async Task<(string Status, string Note)> TypeAsync(BrowserToolCall call, BrowserPageState state, string origin, LoginFillGrant grant, CancellationToken cancellationToken)
    {
        var page = state.Page;
        bool typedUser = false, typedPassword = false, typedOtp = false;
        var startWindow = TimeSpan.FromSeconds(2) * state.CpuMultiplier;
        var loadTimeout = call.Session.Options.NavigationTimeout * (state.NetworkMultiplier * state.CpuMultiplier);
        for (var step = 0; step < MaximumSteps && !page.IsClosed && page.Dialog is null; step++)
        {
            if (OriginOf(page.Url) != origin) break;
            var fields = await FieldsAsync(page, origin, cancellationToken).ConfigureAwait(false);
            List<(string Field, string Value)> targets = [];
            if (fields.Password is { } password && !typedPassword)
            {
                if (fields.Username is { } user && !(typedUser && fields.UsernameFilled)) targets.Add((user, grant.Username!));
                targets.Add((password, grant.Password!));
                // A form that asks for the one-time code with the password gets it too.
                if (fields.Otp is { } inForm && grant.Totp is { } sameForm && !typedOtp) targets.Add((inForm, sameForm));
            }
            else if (fields.Passwordless && fields.Username is { } first && !typedUser && !typedPassword)
            {
                targets.Add((first, grant.Username!));
            }
            else if (fields.Otp is { } otp && grant.Totp is { } code && !typedOtp && (typedPassword || fields.Passwordless))
            {
                targets.Add((otp, code));
            }

            if (targets.Count == 0)
            {
                if (step == 0) return (NoField, NoFieldMessage(fields.Reason));
                break;
            }

            var typed = new List<string>(targets.Count);
            foreach (var (field, value) in targets)
            {
                // Checked again before each value: the field, its document and its form, and the site of the tab.
                var focused = await page.CallAgentAsync(page.MainFrame, "loginFocus", $$"""{"field":{{BrowserSession.JsonText(field)}},"origin":{{BrowserSession.JsonText(origin)}}}""", cancellationToken).ConfigureAwait(false);
                if (OriginOf(page.Url) != origin || !(focused.TryGetProperty("focused", out var ok) && ok.ValueKind == JsonValueKind.True))
                {
                    await ClearAsync(page, typed, cancellationToken).ConfigureAwait(false);
                    return (NoField, "A field of the sign-in changed before it was typed in: nothing was submitted. Call fill_login again.");
                }

                await page.InsertTextAsync(value, cancellationToken).ConfigureAwait(false);
                typed.Add(field);
                if (field == fields.Username) typedUser = true;
                else if (field == fields.Password) typedPassword = true;
                else if (field == fields.Otp) typedOtp = true;
            }

            var before = page.Navigations;
            // Enter in the last field submits its form, as a user does: the default button of the form is clicked.
            var enter = BrowserKeys.Resolve("Enter");
            await page.KeyAsync(enter, down: true, 0, cancellationToken).ConfigureAwait(false);
            await page.KeyAsync(enter, down: false, 0, cancellationToken).ConfigureAwait(false);
            bool navigated;
            try
            {
                navigated = await page.WaitForNavigationAsync(before, startWindow, loadTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (BrowserToolException exception) when (exception.Code == "timeout")
            {
                navigated = true;
            }

            await StableAsync(page, state, cancellationToken).ConfigureAwait(false);
            if (navigated || page.IsClosed) continue;
            // The page stayed: a page of scripts may have taken the values and shown the next step, or refused them and
            // left the password or the code in its fields, which are cleared then. A user name the page keeps is no failure.
            var secrets = typed.Where(field => field != fields.Username).ToArray();
            if (secrets.Length == 0) continue;
            var held = await page.CallAgentAsync(page.MainFrame, "loginState", $$"""{"fields":[{{string.Join(",", secrets.Select(BrowserSession.JsonText))}}]}""", cancellationToken).ConfigureAwait(false);
            if (held.GetProperty("fields").EnumerateArray().Any(static item => item.GetProperty("present").GetBoolean() && item.GetProperty("filled").GetBoolean()))
            {
                await ClearAsync(page, typed, cancellationToken).ConfigureAwait(false);
                return (Failed, "The page did not take the sign-in {label}: it was submitted, the page stayed, and its fields were cleared. Take a snapshot to read what the page says.");
            }
        }

        if (page.IsClosed) return (Submitted, "The sign-in {label} was submitted and the tab closed.");
        if (!typedPassword) return (Submitted, "The user name of the sign-in {label} was submitted: the page asks for something else now. Take a snapshot to see what.");
        var after = OriginOf(page.Url) is { } now ? await FieldsAsync(page, now, cancellationToken).ConfigureAwait(false) : default;
        return after.Password is null && after.Otp is null
            ? (SignedIn, "Signed in with {label}.")
            : (Submitted, "The sign-in {label} was submitted, and the page still asks for something. Take a snapshot to read what it says.");
    }

    private static async Task StableAsync(IBrowserPage page, BrowserPageState state, CancellationToken cancellationToken)
    {
        if (page.IsClosed || page.Dialog is not null) return;
        try
        {
            var quiet = (int)(150 * state.CpuMultiplier);
            var limit = (int)(3000 * state.CpuMultiplier);
            await page.CallAgentAsync(page.MainFrame, "waitForStableDom", $$"""{"quiet":{{quiet}},"timeout":{{limit}}}""", cancellationToken).ConfigureAwait(false);
        }
        catch (BrowserToolException)
        {
            // The document went away meanwhile.
        }
    }

    private static async Task ClearAsync(IBrowserPage page, IReadOnlyList<string> fields, CancellationToken cancellationToken)
    {
        if (fields.Count == 0 || page.IsClosed) return;
        try
        {
            await page.CallAgentAsync(page.MainFrame, "loginClear", $$"""{"fields":[{{string.Join(",", fields.Select(BrowserSession.JsonText))}}]}""", cancellationToken).ConfigureAwait(false);
        }
        catch (BrowserToolException)
        {
            // The document went away, and its fields with it.
        }
    }

    private readonly record struct LoginFields(string? Password, string? Username, bool UsernameFilled, string? Otp, bool Passwordless, string? Reason)
    {
        public bool Any => Password is not null || Username is not null || Otp is not null;
    }

    private static async Task<LoginFields> FieldsAsync(IBrowserPage page, string origin, CancellationToken cancellationToken)
    {
        JsonElement value;
        try
        {
            value = await page.CallAgentAsync(page.MainFrame, "loginFields", $$"""{"origin":{{BrowserSession.JsonText(origin)}}}""", cancellationToken).ConfigureAwait(false);
        }
        catch (BrowserToolException)
        {
            return new LoginFields(null, null, false, null, false, "other-origin");
        }

        string? Text(string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
        bool Flag(string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.True;
        return new LoginFields(Text("password"), Text("username"), Flag("usernameFilled"), Text("otp"), Flag("passwordless"), Text("reason"));
    }

    private static string NoFieldMessage(string? reason) => reason switch
    {
        "hidden" => "The password field of this page is not visible or cannot be typed in: nothing was typed. Show the sign-in form first (a button such as \"Sign in\"), then call fill_login again.",
        "form-elsewhere" => "The sign-in form of this page sends to another site: nothing was typed.",
        "several-passwords" => "This form has several password fields (a new password or its confirmation): fill_login types only on a sign-in form.",
        "other-origin" => "The sign-in form is not in a document of this site (a frame of another site): nothing was typed. Open the address of that frame in the tab, then call fill_login again.",
        _ => "This page shows no sign-in field of this site. Open its sign-in page, then call fill_login again.",
    };

    private static void Answer(BrowserToolCall call, string status, string message)
    {
        call.Lines.Add($"fill_login: {status}");
        call.Lines.Add(message);
    }

    /// <summary>The origin of an address of a site: https, or http on the loopback; null otherwise.</summary>
    internal static string? OriginOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrEmpty(uri.Host)) return null;
        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme != Uri.UriSchemeHttps && !(scheme == Uri.UriSchemeHttp && uri.IsLoopback)) return null;
        var host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.IdnHost.ToLowerInvariant();
        return uri.IsDefaultPort ? $"{scheme}://{host}" : $"{scheme}://{host}:{uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }
}
