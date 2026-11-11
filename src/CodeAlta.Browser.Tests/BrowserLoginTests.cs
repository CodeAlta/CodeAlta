using CodeAlta.Browser.Discovery;
using CodeAlta.Browser.Tools;

namespace CodeAlta.Browser.Tests;

/// <summary>
/// The tool <c>fill_login</c> in a Chromium browser, headless, on the sign-in pages of <see cref="TestSite"/>: the host gives
/// the sign-in of <c>http://127.0.0.1:port</c> only, and no answer of the tool may hold one of its values.
/// </summary>
[TestClass]
public sealed class BrowserLoginTests
{
    private const string Username = "octocat";
    private const string Password = TestSite.SignInPassword;
    private const string Code = "123456";

    private BrowserTestHost? _host;

    private BrowserTestHost Host => _host ?? throw new AssertInconclusiveException("No Chromium browser is installed on this computer.");

    [TestInitialize]
    public async Task StartAsync()
    {
        _host = await BrowserTestHost.StartAsync(BrowserEngine.Chromium);
        if (_host is null) Assert.Inconclusive("No Chromium browser is installed on this computer.");
    }

    [TestCleanup]
    public async Task StopAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    // The host of the tests: the sign-in of the site of the test pages, and the origins it was asked for.
    private sealed class SignIns(string site, LoginFillStatus answer = LoginFillStatus.Granted, Func<CancellationToken, Task<string?>>? readCode = null)
    {
        public List<string> Asked { get; } = [];

        public Task<LoginFillGrant> AnswerAsync(string origin, string? signInId, CancellationToken cancellationToken)
        {
            Asked.Add(origin);
            return Task.FromResult(origin != site ? LoginFillGrant.NoSignIn() : answer switch
            {
                LoginFillStatus.Granted when readCode is not null => LoginFillGrant.GrantedReadingCode("Demo", Username, Password, readCode),
                LoginFillStatus.Granted => LoginFillGrant.Granted("Demo", Username, Password, Code),
                LoginFillStatus.Denied => LoginFillGrant.Denied(),
                LoginFillStatus.Locked => LoginFillGrant.Locked(),
                _ => LoginFillGrant.Unsupported("not here"),
            });
        }
    }

    private async Task<(string Text, BrowserSession Session)> FillAsync(string path, SignIns signIns)
    {
        var session = Host.NewSession(signIn: signIns.AnswerAsync);
        await session.OkAsync("navigate_page", $$"""{"type":"url","url":"{{(path.StartsWith("http", StringComparison.Ordinal) ? path : Host.Site.Url(path))}}"}""");
        var result = await session.CallAsync("fill_login");
        Assert.IsFalse(result.IsError, result.Text);
        AssertHoldsNoValue(result.Text);
        return (result.Text, session);
    }

    private static void AssertHoldsNoValue(string text)
    {
        foreach (var value in new[] { Username, Password, Code }) Assert.DoesNotContain(value, text, $"the answer holds a value: {text}");
        // The password as a form, an address or JSON writes it.
        foreach (var encoded in new[] { "hunter+2%26gh%2Bub%21%C3%A9%22x", Uri.EscapeDataString(Password), System.Text.Json.JsonSerializer.Serialize(Password), "hunter" })
        {
            Assert.DoesNotContain(encoded, text, $"the answer holds the password encoded: {text}");
        }
    }

    // The id of the request of the list whose line holds a text.
    private static string RequestId(string requests, string text)
    {
        var line = requests.Split('\n').First(item => item.Contains(text, StringComparison.Ordinal));
        return line["reqid=".Length..line.IndexOf(' ', StringComparison.Ordinal)];
    }

    [TestMethod]
    public async Task A_sign_in_is_typed_submitted_and_its_one_time_code_too()
    {
        var signIns = new SignIns(Host.Site.Origin);
        var (text, session) = await FillAsync("/login", signIns);
        await using var _ = session;
        StringAssert.Contains(text, "fill_login: signed_in");
        StringAssert.Contains(text, "/welcome");
        CollectionAssert.AreEqual(new[] { Host.Site.Origin }, signIns.Asked, "the site is read from the browser, once");
        AssertHoldsNoValue(await session.OkAsync("take_snapshot"));
        var requests = await session.OkAsync("list_network_requests", """{"includePreservedRequests":true}""");
        AssertHoldsNoValue(requests);

        // The bodies of the requests that carried the sign-in, a form that encodes the password, are neither shown nor saved.
        foreach (var posted in new[] { "POST " + Host.Site.Url("/session"), "POST " + Host.Site.Url("/two-factor") })
        {
            var reqid = RequestId(requests, posted);
            var detail = await session.OkAsync("get_network_request", $$"""{"reqid":{{reqid}}}""");
            AssertHoldsNoValue(detail);
            StringAssert.Contains(detail, "fill_login typed a sign-in");
            var requestFile = Path.Combine(Host.Folder, $"sign-in-{reqid}.network-request");
            var responseFile = Path.Combine(Host.Folder, $"sign-in-{reqid}.network-response");
            var saved = await session.OkAsync("get_network_request", $$"""{"reqid":{{reqid}},"requestFilePath":{{System.Text.Json.JsonSerializer.Serialize(requestFile)}},"responseFilePath":{{System.Text.Json.JsonSerializer.Serialize(responseFile)}}}""");
            AssertHoldsNoValue(saved);
            StringAssert.Contains(saved, "Nothing was saved.");
            Assert.IsFalse(File.Exists(requestFile), "the body of a sign-in is never written to a file");
            Assert.IsFalse(File.Exists(responseFile), "nor what the server answered to it");
        }

        // A request of the page after the sign-in is shown as any other.
        await session.OkAsync("evaluate_script", """{"function":"async () => (await fetch('/api/echo', { method: 'POST', body: 'after the sign-in' })).text()"}""");
        var later = await session.OkAsync("list_network_requests");
        StringAssert.Contains(await session.OkAsync("get_network_request", $$"""{"reqid":{{RequestId(later, "/api/echo")}}}"""), "after the sign-in");
    }

    [TestMethod]
    public async Task A_form_whose_button_outside_it_sends_to_another_site_is_not_filled()
    {
        var signIns = new SignIns(Host.Site.Origin);
        var (text, session) = await FillAsync("/login-external-button", signIns);
        await using var _ = session;
        StringAssert.Contains(text, "fill_login: no_field");
        StringAssert.Contains(text, "another site");
        Assert.IsEmpty(signIns.Asked, "nobody is asked for a form that may send elsewhere");
        StringAssert.Contains(await session.OkAsync("evaluate_script", """{"function":"() => document.getElementById('password').value.length + document.getElementById('login').value.length"}"""), "0");
    }

    [TestMethod]
    public async Task A_timer_that_moves_the_focus_into_a_frame_of_another_site_never_gets_a_value()
    {
        var signIns = new SignIns(Host.Site.Origin);
        var (text, session) = await FillAsync("/login-focus-thief", signIns);
        await using var _ = session;
        StringAssert.Contains(text, "fill_login: submitted");
        var state = await session.OkAsync("evaluate_script", """{"function":"async () => { await new Promise(done => setTimeout(done, 300)); return JSON.stringify({ typed: window.__typed, leaked: window.__leaked }); }"}""");
        StringAssert.Contains(state.Replace("\\", string.Empty, StringComparison.Ordinal), $"\"typed\":[{Username.Length},{Password.Length}]", state);
        StringAssert.Contains(state.Replace("\\", string.Empty, StringComparison.Ordinal), "\"leaked\":0", "the frame of the other site got nothing");
    }

    [TestMethod]
    public async Task The_one_time_code_is_read_when_its_field_is_reached_after_the_password_was_sent()
    {
        var reads = new List<int>();
        Task<string?> ReadCode(CancellationToken cancellationToken)
        {
            // The code is the right one only once the password reached the site: one read with the password would be too early.
            reads.Add(Host.Site.SignInsPosted);
            return Task.FromResult<string?>(Host.Site.SignInsPosted > 0 ? Code : "000000");
        }

        var signIns = new SignIns(Host.Site.Origin, readCode: ReadCode);
        var (text, session) = await FillAsync("/login", signIns);
        await using var _ = session;
        StringAssert.Contains(text, "fill_login: signed_in");
        CollectionAssert.AreEqual(new[] { 1 }, reads, "read once, as the field of the code was reached");
    }

    [TestMethod]
    public async Task A_one_time_code_the_vault_no_longer_gives_is_not_typed()
    {
        var signIns = new SignIns(Host.Site.Origin, readCode: static _ => Task.FromResult<string?>(null));
        var (text, session) = await FillAsync("/login", signIns);
        await using var _ = session;
        StringAssert.Contains(text, "fill_login: failed");
        StringAssert.Contains(text, "one-time code");
        StringAssert.Contains(await session.OkAsync("evaluate_script", """{"function":"() => location.pathname + ':' + document.getElementById('otp').value.length"}"""), "/two-factor:0");
    }

    [TestMethod]
    public async Task A_look_alike_site_gets_no_sign_in()
    {
        var signIns = new SignIns(Host.Site.Origin);
        var (text, session) = await FillAsync(Host.Site.OtherOrigin + "/login", signIns);
        await using var _ = session;
        StringAssert.Contains(text, "fill_login: no_sign_in");
        CollectionAssert.AreEqual(new[] { Host.Site.OtherOrigin }, signIns.Asked);
        StringAssert.Contains(await session.OkAsync("evaluate_script", """{"function":"() => document.getElementById('password').value.length"}"""), "0");
    }

    [TestMethod]
    public async Task A_hidden_password_field_is_never_typed_in_and_the_user_is_not_asked()
    {
        var signIns = new SignIns(Host.Site.Origin);
        var (text, session) = await FillAsync("/login-hidden", signIns);
        await using var _ = session;
        StringAssert.Contains(text, "fill_login: no_field");
        Assert.IsEmpty(signIns.Asked, "nobody is asked for a page where nothing would be typed");
        StringAssert.Contains(await session.OkAsync("evaluate_script", """{"function":"() => document.getElementById('password').value.length + document.getElementById('login').value.length"}"""), "0");
    }

    [TestMethod]
    public async Task A_form_in_a_frame_of_another_site_is_not_filled()
    {
        var signIns = new SignIns(Host.Site.Origin);
        var (text, session) = await FillAsync("/login-frame", signIns);
        await using var _ = session;
        StringAssert.Contains(text, "fill_login: no_field");
        Assert.IsEmpty(signIns.Asked);
    }

    [TestMethod]
    public async Task A_form_that_sends_to_another_site_is_not_filled()
    {
        var signIns = new SignIns(Host.Site.Origin);
        var (text, session) = await FillAsync("/login-elsewhere", signIns);
        await using var _ = session;
        StringAssert.Contains(text, "fill_login: no_field");
        StringAssert.Contains(text, "another site");
        Assert.IsEmpty(signIns.Asked);
    }

    [TestMethod]
    public async Task A_sign_in_the_page_refuses_is_cleared_from_its_fields()
    {
        var signIns = new SignIns(Host.Site.Origin);
        var (text, session) = await FillAsync("/login-script", signIns);
        await using var _ = session;
        StringAssert.Contains(text, "fill_login: failed");
        var lengths = await session.OkAsync("evaluate_script", """{"function":"() => [document.getElementById('login').value.length, document.getElementById('password').value.length, document.getElementById('error').textContent]"}""");
        StringAssert.Contains(lengths, "Incorrect username or password.", "the page refused it");
        StringAssert.Contains(lengths.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal), "[0,0,", "both fields were emptied");
    }

    [TestMethod]
    public async Task A_denial_or_a_locked_vault_types_nothing()
    {
        foreach (var (answer, status) in new[] { (LoginFillStatus.Denied, "denied"), (LoginFillStatus.Locked, "locked") })
        {
            var (text, session) = await FillAsync("/login", new SignIns(Host.Site.Origin, answer));
            await using var _ = session;
            StringAssert.Contains(text, "fill_login: " + status);
            if (answer == LoginFillStatus.Locked) StringAssert.Contains(text, "Settings > Secrets");
            StringAssert.Contains(await session.OkAsync("evaluate_script", """{"function":"() => document.getElementById('password').value.length + document.getElementById('login').value.length"}"""), "0");
        }
    }

    [TestMethod]
    public async Task A_browser_without_sign_ins_answers_unsupported()
    {
        await using var session = Host.NewSession();
        await session.OkAsync("navigate_page", $$"""{"type":"url","url":"{{Host.Site.Url("/login")}}"}""");
        StringAssert.Contains(await session.OkAsync("fill_login"), "fill_login: unsupported");
    }
}
