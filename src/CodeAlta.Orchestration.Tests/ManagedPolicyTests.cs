using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class ManagedPolicyTests
{
    [TestMethod]
    public void None_RefusesNothing()
    {
        Assert.IsTrue(ManagedPolicy.None.IsEmpty);
        Assert.IsFalse(ManagedPolicy.None.Refuses(SessionPermissionModes.Bypass));
        Assert.AreEqual(SessionPermissionModes.Bypass, ManagedPolicy.None.Narrow(SessionPermissionModes.Bypass));
        Assert.IsNull(ManagedPolicy.None.Refusal(SessionPermissionModes.Bypass));
    }

    [TestMethod]
    public void BypassDisabled_NarrowsToTheWidestModeTheSessionCanBeGiven()
    {
        var policy = new ManagedPolicy { BypassPermissionsDisabled = true, Source = "GitHub Copilot" };

        Assert.IsTrue(policy.Refuses(SessionPermissionModes.Bypass));
        Assert.IsFalse(policy.Refuses("auto"), "turning bypass off leaves the assisted mode");
        Assert.AreEqual(SessionPermissionModes.AcceptEdits, policy.Narrow(SessionPermissionModes.Bypass), "the modes of the host");
        Assert.AreEqual("auto", policy.Narrow(SessionPermissionModes.Bypass, ["default", "acceptEdits", "plan", "auto", "dontAsk", "bypassPermissions"]), "Claude Code has auto");
        Assert.AreEqual(SessionPermissionModes.Ask, policy.Narrow(SessionPermissionModes.Bypass, ["default", "bypassPermissions"]));
        Assert.AreEqual(SessionPermissionModes.AcceptEdits, policy.Narrow(SessionPermissionModes.AcceptEdits));
        Assert.IsNull(policy.Narrow(null));
        StringAssert.Contains(policy.Refusal(SessionPermissionModes.Bypass), "GitHub Copilot");
    }

    [TestMethod]
    public void AssistedDisabled_RefusesAuto()
    {
        var policy = new ManagedPolicy { BypassPermissionsDisabled = true, AssistedPermissionsDisabled = true };

        Assert.IsTrue(policy.Refuses("auto"));
        Assert.AreEqual(SessionPermissionModes.AcceptEdits, policy.Narrow(SessionPermissionModes.Bypass, ["default", "acceptEdits", "auto", "bypassPermissions"]));
        Assert.AreEqual(SessionPermissionModes.AcceptEdits, policy.Narrow("auto", ["default", "acceptEdits", "auto", "bypassPermissions"]));
    }

    [TestMethod]
    public void Combine_KeepsEachRefusalAndTheNarrowestDefault()
    {
        var first = new ManagedPolicy { Source = "GitHub Copilot", BypassPermissionsDisabled = true, DefaultPermissionMode = SessionPermissionModes.AcceptEdits, ManagedKeys = ["permissions"] };
        var second = new ManagedPolicy { Source = "GitHub Copilot", SandboxRequired = true, DefaultPermissionMode = SessionPermissionModes.Ask, SupportUrl = "https://help.example", ManagedKeys = ["sandbox", "permissions"] };

        var combined = ManagedPolicy.Combine([first, second, ManagedPolicy.None]);

        Assert.IsTrue(combined.BypassPermissionsDisabled);
        Assert.IsTrue(combined.SandboxRequired);
        Assert.AreEqual(SessionPermissionModes.Ask, combined.DefaultPermissionMode);
        Assert.AreEqual("https://help.example", combined.SupportUrl);
        Assert.AreEqual("GitHub Copilot", combined.Source);
        CollectionAssert.AreEqual(new[] { "permissions", "sandbox" }, combined.ManagedKeys.ToArray());
        Assert.AreSame(ManagedPolicy.None, ManagedPolicy.Combine([ManagedPolicy.None]));
        Assert.AreSame(first, ManagedPolicy.Combine([first]));
    }

    [TestMethod]
    public void Equality_ComparesTheManagedKeysByValue()
    {
        Assert.AreEqual(new ManagedPolicy { SandboxRequired = true, ManagedKeys = ["sandbox"] }, new ManagedPolicy { SandboxRequired = true, ManagedKeys = ["sandbox"] });
        Assert.AreNotEqual(new ManagedPolicy { SandboxRequired = true, ManagedKeys = ["sandbox"] }, new ManagedPolicy { SandboxRequired = true, ManagedKeys = ["model"] });
    }

    [TestMethod]
    public void State_CombinesItsSources_AndSaysWhenThePolicyChanges()
    {
        var state = new ManagedPolicyState();
        var changes = new List<ManagedPolicy>();
        state.Changed += changes.Add;

        state.Set("copilot", new ManagedPolicy { BypassPermissionsDisabled = true });
        state.Set("copilot", new ManagedPolicy { BypassPermissionsDisabled = true });
        state.Set("copilot-work", new ManagedPolicy { SandboxRequired = true });

        Assert.IsTrue(state.Current.BypassPermissionsDisabled);
        Assert.IsTrue(state.Current.SandboxRequired);
        Assert.HasCount(2, state.Policies);
        Assert.HasCount(2, changes, "setting the same policy again changes nothing");

        state.Set("copilot", ManagedPolicy.None);
        state.Remove("copilot-work");

        Assert.AreSame(ManagedPolicy.None, state.Current);
        Assert.IsEmpty(state.Policies);
        Assert.HasCount(4, changes);
    }
}
