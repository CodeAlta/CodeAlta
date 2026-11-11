namespace CodeAlta.Credentials.Tests;

[TestClass]
public sealed class VaultCommandGuardTests
{
    [TestMethod]
    public void The_program_of_a_configured_vault_is_found_in_any_command_of_a_line()
    {
        string[] programs = ["op", "bw"];
        Assert.AreEqual("op", VaultCommandGuard.FindProgram("op read op://Private/x/y", programs));
        Assert.AreEqual("op", VaultCommandGuard.FindProgram("echo hi && \"C:/Program Files/1Password CLI/op.exe\" item list", programs));
        Assert.AreEqual("bw", VaultCommandGuard.FindProgram("export X=$(bw get password abc)", programs));
        Assert.IsNull(VaultCommandGuard.FindProgram("git commit -m 'op is fine here'", programs), "a word inside quotes of another program is an argument");
        Assert.IsNull(VaultCommandGuard.FindProgram("dotnet build", programs));
    }

    [TestMethod]
    public void A_vault_guards_its_program_once_configured_unless_the_user_allows_it()
    {
        using var vault = new FakeVault();
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile });
        var guard = new VaultCommandGuard(broker);
        Assert.IsNull(guard.Check("op read op://x/y/z"), "no vault is configured: nothing changes for who does not use the feature");

        vault.WriteConfig("[credentials.personal]\ntype = \"1password\"\n");
        StringAssert.Contains(guard.Check("op read op://x/y/z"), "${secret:NAME}");
        Assert.IsNull(guard.Check("bw get password abc"), "only the programs of the configured vaults");

        vault.WriteConfig("[secrets]\nagent_vault_commands = \"allow\"\n\n[credentials.personal]\ntype = \"1password\"\n");
        Assert.IsNull(guard.Check("op read op://x/y/z"));
    }
}
