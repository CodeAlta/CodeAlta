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
    public void A_program_named_as_an_argument_does_not_run()
    {
        string[] programs = ["op", "bw", "pass"];
        Assert.IsNull(VaultCommandGuard.FindProgram("echo op", programs));
        Assert.IsNull(VaultCommandGuard.FindProgram("Get-Command bw", programs));
        Assert.IsNull(VaultCommandGuard.FindProgram("git commit -m pass", programs));
        Assert.IsNull(VaultCommandGuard.FindProgram("grep -r op src | wc -l", programs));
        Assert.IsNull(VaultCommandGuard.FindProgram("command -v op", programs), "command -v says where it is");
        Assert.IsNull(VaultCommandGuard.FindProgram("Test-Path C:/tools/op.exe; where.exe bw", programs));
    }

    [TestMethod]
    public void The_program_of_each_command_is_found_after_assignments_and_wrappers()
    {
        string[] programs = ["op", "bw"];
        Assert.AreEqual("op", VaultCommandGuard.FindProgram("ls && op read x", programs));
        Assert.AreEqual("op", VaultCommandGuard.FindProgram("cat list | op inject", programs));
        Assert.AreEqual("op", VaultCommandGuard.FindProgram("OP_ACCOUNT=me op read x", programs));
        Assert.AreEqual("op", VaultCommandGuard.FindProgram("sudo -u root op read x", programs));
        Assert.AreEqual("op", VaultCommandGuard.FindProgram("env -i A=1 op read x", programs));
        Assert.AreEqual("op", VaultCommandGuard.FindProgram("timeout 10 op read x", programs));
        Assert.AreEqual("op", VaultCommandGuard.FindProgram("& 'C:\\Program Files\\1Password CLI\\op.exe' read x", programs));
        Assert.AreEqual("op", VaultCommandGuard.FindProgram("cmd /c \"op read x\"", programs));
        Assert.AreEqual("bw", VaultCommandGuard.FindProgram("pwsh -NoProfile -Command \"bw get password abc\"", programs));
        Assert.AreEqual("bw", VaultCommandGuard.FindProgram("bash -lc 'bw get password abc'", programs));
        Assert.AreEqual("op", VaultCommandGuard.FindProgram("echo \"$(op read x)\"", programs), "a substitution inside quotes runs");
        Assert.AreEqual("op", VaultCommandGuard.FindProgram("echo `op read x`", programs));
        Assert.AreEqual("op", VaultCommandGuard.FindProgram("Start-Process -FilePath op -ArgumentList read", programs));
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes("op read x"));
        Assert.AreEqual("op", VaultCommandGuard.FindProgram($"powershell -EncodedCommand {encoded}", programs));
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
