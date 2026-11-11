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
    private const string Password = "hunter2-github";
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
    private sealed class SignIns(string site, LoginFillStatus answer = LoginFillStatus.Granted)
    {
        public List<string> Asked { get; } = [];

        public Task<LoginFillGrant> AnswerAsync(string origin, string? signInId, CancellationToken cancellationToken)
        {
            Asked.Add(origin);
            return Task.FromResult(origin != site ? LoginFillGrant.NoSignIn() : answer switch
            {
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
        AssertHoldsNoValue(await session.OkAsync("list_network_requests"));
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
