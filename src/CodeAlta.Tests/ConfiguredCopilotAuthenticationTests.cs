using System.Globalization;
using CodeAlta.Agent.Copilot;
using CodeAlta.Catalog;
using Coordinator = CodeAlta.Tui.App.ProviderFrontendCoordinator;

namespace CodeAlta.Tests;

// Original pre-move presentation cases: static helpers only, never a coordinator or real browser.
[TestClass]
public sealed class ConfiguredCopilotAuthenticationTests
{
    [TestMethod]
    public async Task BrowserPrompt_ReportsLocalizedMessageBeforeFakeBrowserAttempt()
    {
        var deviceCode = CreateDeviceCode();
        var events = new List<string>();
        var expected = SR.T("Opening Copilot login in your browser. Enter code {0} at {1}. Waiting for authorization...", deviceCode.UserCode, deviceCode.VerificationUri);

        await Coordinator.ReportCopilotDirectBrowserDeviceCode(
            deviceCode,
            message =>
            {
                Assert.AreEqual(expected, message);
                events.Add("report");
            },
            uri =>
            {
                Assert.AreSame(deviceCode.VerificationUri, uri);
                events.Add("browser");
            });

        CollectionAssert.AreEqual(new[] { "report", "browser" }, events);
    }

    [TestMethod]
    public void BrowserPrompt_ReportFailurePreventsBrowserAttempt()
    {
        var failure = new InvalidOperationException("fixture report failure");
        var events = new List<string>();

        var actual = Assert.ThrowsExactly<InvalidOperationException>(() => Coordinator.ReportCopilotDirectBrowserDeviceCode(
            CreateDeviceCode(),
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

        await Coordinator.ReportCopilotDirectDeviceCode(deviceCode, messages.Add);

        CollectionAssert.AreEqual(new[]
        {
            SR.T("Open {0} and enter code {1}. Waiting for Copilot authorization...", deviceCode.VerificationUri, deviceCode.UserCode),
        }, messages);
    }

    [TestMethod]
    public void DevicePrompt_ReportFailurePropagatesUnchanged()
    {
        var failure = new InvalidOperationException("fixture report failure");

        var actual = Assert.ThrowsExactly<InvalidOperationException>(
            () => Coordinator.ReportCopilotDirectDeviceCode(CreateDeviceCode(), _ => throw failure));

        Assert.AreSame(failure, actual);
    }

    [TestMethod]
    [DataRow(null, false)]
    [DataRow("", false)]
    [DataRow(" ", false)]
    [DataRow(" enterprise.invalid ", false)]
    [DataRow(null, true)]
    [DataRow(" enterprise.invalid ", true)]
    public void ResultMessage_PreservesEnterpriseAndCurrentCultureExpiryFormatting(string? enterprise, bool hasExpiry)
    {
        var result = new CopilotDirectLoginResult(
            new Uri("https://endpoint.invalid"),
            hasExpiry ? new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero) : null,
            enterprise);
        var expectedExpiry = result.ExpiresAt is null ? SR.T("expiry unknown")
            : SR.T("expires {0}", result.ExpiresAt.Value.LocalDateTime.ToString("g", CultureInfo.CurrentCulture));
        var expectedEnterprise = string.IsNullOrWhiteSpace(enterprise) ? "GitHub.com" : enterprise.Trim();

        foreach (var key in new[] { "Copilot login completed", "Copilot device login completed", "Authenticated with cached Copilot credentials" })
        {
            var prefix = SR.T(key);

            var actual = Coordinator.FormatCopilotDirectLoginMessage(prefix, result);

            Assert.AreEqual(SR.T("{0} · {1} · API {2} · {3}.", prefix, expectedEnterprise, result.BaseUri, expectedExpiry), actual);
        }
    }

    private static CopilotDirectDeviceCode CreateDeviceCode()
        => new(new Uri("https://verification.invalid/device"), "FAKE-CODE", new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));
}
