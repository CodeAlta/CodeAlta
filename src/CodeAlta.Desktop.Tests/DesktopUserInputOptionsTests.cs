using CodeAlta.Desktop;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopUserInputOptionsTests
{
    [TestMethod]
    public void Cli_InputFlagRequiresOwnedRootsAndRejectsDuplicates()
    {
        var root = Path.GetPathRoot(Path.GetFullPath("."))!;
        var data = Path.Combine(root, "input-fixture-browser"); var catalog = Path.Combine(root, "input-fixture-catalog");
        var project = Path.Combine(root, "input-fixture-project"); var home = Path.Combine(root, "input-fixture-home");
        var builtin = Path.Combine(root, "input-fixture-builtin");
        string[] args = ["--data-root", data, "--catalog-root", catalog, "--allow-catalog-cache", "--allow-owned-host", "--project-root", project,
            "--discovery-home", home, "--instruction-root", project, "--builtin-skill-root", builtin];
        bool Exists(string path) => path != data;
        Assert.IsTrue(DesktopCommandLine.TryParse(args, Exists, _ => false, out var off, out _));
        Assert.IsFalse(off!.EnableOwnedUserInput);
        Assert.IsTrue(DesktopCommandLine.TryParse([.. args, "--enable-owned-user-input"], Exists, _ => false, out var on, out _));
        Assert.IsTrue(on!.EnableOwnedUserInput); Assert.IsFalse(on.ReviewOwnedCommandPermissions);
        Assert.IsTrue(DesktopCommandLine.TryParse([.. args, "--review-owned-command-permissions"], Exists, _ => false, out var review, out _));
        Assert.IsTrue(review!.ReviewOwnedCommandPermissions); Assert.IsFalse(review.EnableOwnedUserInput);
        Assert.IsFalse(DesktopCommandLine.TryParse([.. args, "--enable-owned-user-input", "--enable-owned-user-input"], Exists, _ => false, out _, out _));
        Assert.IsFalse(DesktopCommandLine.TryParse(["--data-root", data, "--enable-owned-user-input"], Exists, _ => false, out _, out _));
    }

    [TestMethod]
    public void HostAndBoot_OptionsAreIndependent()
    {
        foreach (var review in new[] { false, true })
        foreach (var asks in new[] { false, true })
        {
            var options = new CodeAlta.Orchestration.Hosting.CodeAltaHostOptions { ReviewOwnedCommandPermissions = review, EnableOwnedAsks = asks };
            Assert.IsFalse(options.EnableOwnedUserInput);
            foreach (var input in new[] { false, true })
            {
                var boot = new CodeAlta.Desktop.Rpc.BootService("11111111-1111-4111-8111-111111111111", review, input).Status(new());
                Assert.AreEqual(review, boot.CommandReviewEnabled); Assert.AreEqual(input, boot.OwnedUserInputEnabled);
            }
        }
    }
}
