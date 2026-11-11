using System.Text.Json;
using CodeAlta.Agent.Copilot;
using GitHub.Copilot.Rpc;

// The managed-settings surface of the Copilot SDK is marked experimental; these tests build its answers.
#pragma warning disable GHCP001

namespace CodeAlta.Tests;

[TestClass]
public sealed class CopilotManagedSettingsTests
{
    [TestMethod]
    public void From_ReadsWhatCodeAltaEnforces_NestedOrFlat()
    {
        var result = new ManagedSettingsResolveResult
        {
            Account = " account-1 ",
            Resolved = new ManagedSettingsResolvedData
            {
                ServerManaged = true,
                BypassPermissionsDisabled = true,
                ManagedKeys = ["permissions", "sandbox", "forceRemoteSettingsRefresh"],
                Settings = JsonElement.Parse("""
                    {
                      "permissions": { "disableAssistedPermissionsMode": true, "defaultMode": { "overridable": "manual" } },
                      "sandbox.enabled": true,
                      "sandbox": { "allowBypass": false },
                      "forceRemoteSettingsRefresh": true
                    }
                    """),
            },
            Diagnostics = [new ManagedSettingsDiagnostic { Message = "Served from cache." }],
        };

        var settings = CopilotManagedSettings.From(result);

        Assert.AreEqual("account-1", settings.Account);
        Assert.IsTrue(settings.ServerManaged);
        Assert.IsTrue(settings.BypassPermissionsDisabled);
        Assert.IsTrue(settings.AssistedPermissionsDisabled);
        Assert.AreEqual("manual", settings.DefaultMode, "a default a team may override still applies");
        Assert.IsTrue(settings.SandboxEnabled, "a dotted key, as an MDM layer carries it");
        Assert.IsFalse(settings.SandboxBypassAllowed);
        Assert.IsTrue(settings.ForceRemoteSettingsRefresh);
        CollectionAssert.AreEqual(new[] { "Served from cache." }, settings.Diagnostics.ToArray());
    }

    [TestMethod]
    public void From_AnEmptyAnswer_NarrowsNothing()
    {
        var settings = CopilotManagedSettings.From(new ManagedSettingsResolveResult());

        Assert.IsFalse(settings.BypassPermissionsDisabled);
        Assert.IsFalse(settings.SandboxEnabled);
        Assert.IsTrue(settings.SandboxBypassAllowed);
        Assert.IsNull(settings.DefaultMode);
        Assert.IsEmpty(settings.ManagedKeys);
    }

    [TestMethod]
    public void From_TheSandboxOfAnUndeterminedPolicy_IsRequired()
    {
        var settings = CopilotManagedSettings.From(new ManagedSettingsResolveResult
        {
            Resolved = new ManagedSettingsResolvedData { FailClosed = true, SandboxEnabledByUndeterminedPolicy = true },
        });

        Assert.IsTrue(settings.FailClosed);
        Assert.IsTrue(settings.SandboxEnabled);
    }

    /// <summary>
    /// Downloads the pinned Copilot runtime and asks it for the managed settings of a GitHub account, when
    /// <c>CODEALTA_COPILOT_POLICY_LIVE_TOKEN</c> holds a token of one; skipped otherwise. The runtime goes to a temporary
    /// folder, so the run downloads it each time (about 100 MB).
    /// </summary>
    [TestMethod]
    [Timeout(600_000, CooperativeCancellation = true)]
    public async Task Live_TheRuntimeResolvesTheSettingsOfAnAccount()
    {
        var token = Environment.GetEnvironmentVariable("CODEALTA_COPILOT_POLICY_LIVE_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            Assert.Inconclusive("Set CODEALTA_COPILOT_POLICY_LIVE_TOKEN to a GitHub token to run the Copilot runtime.");
        }

        using var temp = TestTempDirectory.Create();
        using var installer = new CopilotRuntimeInstaller(Path.Combine(temp.Path, "copilot-runtime"));
        var installation = await installer.EnsureInstalledAsync(null, TestContext.CancellationToken);
        var settings = await new CopilotManagedSettingsReader(installation.Executable, Path.Combine(temp.Path, "home"))
            .ReadAsync([token], TestContext.CancellationToken);

        Assert.HasCount(1, settings);
        Assert.IsNotNull(settings[0].Account, "the account of the token was resolved");
        TestContext.WriteLine($"server={settings[0].ServerManaged} device={settings[0].DeviceManaged} bypassDisabled={settings[0].BypassPermissionsDisabled} keys={string.Join(",", settings[0].ManagedKeys)}");
    }

    public TestContext TestContext { get; set; } = null!;
}
