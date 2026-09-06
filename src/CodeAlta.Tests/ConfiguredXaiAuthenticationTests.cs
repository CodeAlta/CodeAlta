using System.Globalization;
using CodeAlta.Agent.Xai;
using CodeAlta.Catalog;
using Coordinator = CodeAlta.Tui.App.ProviderFrontendCoordinator;

namespace CodeAlta.Tests;

// Original presentation cases only: inert display records and recording browser actions.
// SR may read output-directory localization content; assembly-level writerless logging still applies.
[TestClass]
public sealed class ConfiguredXaiAuthenticationTests
{
    [TestMethod]
    public async Task BrowserPrompt_ReportsLocalizedMessageBeforeFakeBrowserAttempt()
    {
        var authorization = new XaiDirectBrowserAuthorization(new Uri("https://example.invalid/authorize"));
        var events = new List<string>();

        await Coordinator.ReportXaiDirectBrowserAuthorization(
            authorization,
            message =>
            {
                Assert.AreEqual(SR.T("Opening xAI login in your browser: {0}. Waiting for authorization...", authorization.AuthorizeUri), message);
                events.Add("report");
            },
            uri =>
            {
                Assert.AreSame(authorization.AuthorizeUri, uri);
                events.Add("browser");
            });

        CollectionAssert.AreEqual(new[] { "report", "browser" }, events);
    }

    [TestMethod]
    public void BrowserPrompt_ReportFailurePreventsBrowserAttempt()
    {
        var failure = new InvalidOperationException("fixture report failure");
        var events = new List<string>();

        var actual = Assert.ThrowsExactly<InvalidOperationException>(() => Coordinator.ReportXaiDirectBrowserAuthorization(
            new XaiDirectBrowserAuthorization(new Uri("https://example.invalid/authorize")),
            _ =>
            {
                events.Add("report");
                throw failure;
            },
            _ => events.Add("browser")));

        Assert.AreSame(failure, actual);
        CollectionAssert.AreEqual(new[] { "report" }, events);
    }

    [TestMethod]
    public async Task DevicePrompt_ReportsLocalizedMessageWithoutBrowserCapability()
    {
        var deviceCode = CreateDeviceCode();
        var messages = new List<string>();

        await Coordinator.ReportXaiDirectDeviceCode(deviceCode, messages.Add);

        CollectionAssert.AreEqual(new[]
        {
            SR.T("Open {0} and enter code {1}. Waiting for xAI authorization...", deviceCode.VerificationUri, deviceCode.UserCode),
        }, messages);
    }

    [TestMethod]
    public void DevicePrompt_ReportFailurePropagatesUnchanged()
    {
        var failure = new InvalidOperationException("fixture report failure");

        var actual = Assert.ThrowsExactly<InvalidOperationException>(
            () => Coordinator.ReportXaiDirectDeviceCode(CreateDeviceCode(), _ => throw failure));

        Assert.AreSame(failure, actual);
    }

    [TestMethod]
    [DataRow(null, false)]
    [DataRow("", false)]
    [DataRow(" \t ", false)]
    [DataRow(" fixture-scope ", false)]
    [DataRow(null, true)]
    [DataRow("", true)]
    [DataRow(" \t ", true)]
    [DataRow(" fixture-scope ", true)]
    public void ResultMessage_PreservesLocalizedScopeAndCurrentCultureExpiryFormatting(string? scope, bool hasExpiry)
    {
        var result = new XaiDirectLoginResult(
            new Uri("https://example.invalid/api"),
            hasExpiry ? new DateTimeOffset(2000, 1, 2, 3, 4, 5, TimeSpan.Zero) : null,
            scope);
        var expectedExpiry = result.ExpiresAt is null ? SR.T("expiry unknown")
            : SR.T("expires {0}", result.ExpiresAt.Value.LocalDateTime.ToString("g", CultureInfo.CurrentCulture));
        var expectedScope = string.IsNullOrWhiteSpace(scope) ? SR.T("scope unknown") : SR.T("scope {0}", scope.Trim());

        foreach (var key in new[] { "xAI login completed", "xAI device login completed", "Authenticated with cached xAI credentials" })
        {
            var prefix = SR.T(key);

            var actual = Coordinator.FormatXaiDirectLoginMessage(prefix, result);

            Assert.AreEqual(SR.T("{0} · API {1} · {2} · {3}.", prefix, result.BaseUri, expectedExpiry, expectedScope), actual);
        }
    }

    private static XaiDirectDeviceCode CreateDeviceCode()
        => new(new Uri("https://example.invalid/device"), "FAKE-DISPLAY-CODE", new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));
}
