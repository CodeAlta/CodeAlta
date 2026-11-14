using CodeAlta.Browser.Discovery;
using CodeAlta.Browser.Links;

namespace CodeAlta.Browser.Tests;

[TestClass]
public sealed class LinkBrowsersTests
{
    private const string Address = "https://example.invalid/page";

    private static readonly InstalledBrowser Chrome = new("chrome", "Google Chrome", BrowserEngine.Chromium,
        "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome", "156.0", "/data/chrome",
        [new BrowserProfile("Privat", "/data/chrome/Default", true), new BrowserProfile("Work", "/data/chrome/Profile 1", false)]);

    private static readonly InstalledBrowser Firefox = new("firefox", "Firefox", BrowserEngine.Firefox,
        "/Applications/Firefox.app/Contents/MacOS/firefox", "150.0", "/data/firefox", [new BrowserProfile("default-release", "/data/firefox/Profiles/a.default-release", true),
            new BrowserProfile("Work", "/data/firefox/Profiles/b.work", false)]);

    private static readonly InstalledBrowser Safari = new("safari", "Safari", BrowserEngine.Safari, "/usr/bin/safaridriver", "27.0", null, []);

    private static readonly InstalledBrowser[] Installed = [Chrome, Firefox, Safari];

    private static LinkLaunch? Plan(string? browser, string? profile, InstalledBrowser? defaultBrowser = null, BrowserPlatform platform = BrowserPlatform.MacOS)
        => LinkBrowsers.Plan(platform, Installed, defaultBrowser ?? Chrome, Address, browser, profile);

    [TestMethod]
    public void NothingChosen_OrTheDefaultBrowserWithoutProfile_IsLeftToTheSystem()
    {
        Assert.IsNull(Plan(null, null));
        Assert.IsNull(Plan(null, " "));
        Assert.IsNull(Plan("brave", "Work"), "A browser that is not installed: the system opens the link.");
        Assert.IsNull(LinkBrowsers.Plan(BrowserPlatform.MacOS, Installed, null, Address, null, "Work"), "A default browser that cannot be told: the system opens the link.");
    }

    [TestMethod]
    public void AChromiumProfile_IsGivenByItsFolder_FoundByTheNameTheBrowserShows()
    {
        var launch = Plan(null, "work")!;
        Assert.AreEqual("/usr/bin/open", launch.FileName, "On macOS the system starts the browser: it is never a process of CodeAlta.");
        CollectionAssert.AreEqual(new[] { "-n", "-a", "/Applications/Google Chrome.app", "--args", "--profile-directory=Profile 1", Address }, launch.Arguments.ToArray());
        Assert.IsTrue(launch.ProfileFound);
        CollectionAssert.AreEqual(new[] { "-n", "-a", "/Applications/Google Chrome.app", "--args", "--profile-directory=Default", Address }, Plan("chrome", "Default")!.Arguments.ToArray(),
            "The folder names a profile too.");
        var windows = LinkBrowsers.Plan(BrowserPlatform.Windows, [Chrome with { ExecutablePath = @"C:\Chrome\chrome.exe" }], null, Address, "chrome", "Work")!;
        Assert.AreEqual(@"C:\Chrome\chrome.exe", windows.FileName);
        CollectionAssert.AreEqual(new[] { "--profile-directory=Profile 1", Address }, windows.Arguments.ToArray());
    }

    [TestMethod]
    public void AFirefoxProfile_IsGivenByItsName()
    {
        var launch = Plan("firefox", "Work")!;
        Assert.AreEqual("/usr/bin/open", launch.FileName);
        CollectionAssert.AreEqual(new[] { "-n", "-a", "/Applications/Firefox.app", "--args", "-P", "Work", Address }, launch.Arguments.ToArray());
        // The list shows a name without the spaces around it: the profile is found by it, and given as Firefox wrote it.
        var spaced = LinkBrowsers.Plan(BrowserPlatform.Windows, [Firefox with { Profiles = [new BrowserProfile(" Work ", "/data/firefox/Profiles/b.work", false)] }], null, Address, "firefox", "Work")!;
        Assert.IsTrue(spaced.ProfileFound);
        CollectionAssert.AreEqual(new[] { "-P", " Work ", Address }, spaced.Arguments.ToArray());
    }

    [TestMethod]
    public void ABrowserWithoutProfile_OpensAsTheSystemOpensIt()
    {
        var chrome = Plan("chrome", null)!;
        Assert.AreEqual("/usr/bin/open", chrome.FileName);
        CollectionAssert.AreEqual(new[] { "-a", "/Applications/Google Chrome.app", Address }, chrome.Arguments.ToArray());
        CollectionAssert.AreEqual(new[] { "-a", "/Applications/Safari.app", Address }, Plan("safari", "Work")!.Arguments.ToArray(), "Safari takes no profile.");
        var windows = LinkBrowsers.Plan(BrowserPlatform.Windows, [Chrome with { ExecutablePath = @"C:\Chrome\chrome.exe" }], null, Address, "chrome", null)!;
        Assert.AreEqual(@"C:\Chrome\chrome.exe", windows.FileName);
        CollectionAssert.AreEqual(new[] { Address }, windows.Arguments.ToArray());
    }

    [TestMethod]
    public void AListOfProfilesOutOfReach_TakesTheNameAsItIsWritten_ButNeverMakesANewChromiumProfile()
    {
        InstalledBrowser[] hidden = [Chrome with { Profiles = [] }, Firefox with { Profiles = [] }];
        LinkLaunch? Hidden(string browser, string profile) => LinkBrowsers.Plan(BrowserPlatform.MacOS, hidden, hidden[0], Address, browser, profile);
        CollectionAssert.AreEqual(new[] { "-n", "-a", "/Applications/Google Chrome.app", "--args", "--profile-directory=Profile 1", Address }, Hidden("chrome", "Profile 1")!.Arguments.ToArray());
        CollectionAssert.AreEqual(new[] { "-n", "-a", "/Applications/Google Chrome.app", "--args", "--profile-directory=Default", Address }, Hidden("chrome", "Default")!.Arguments.ToArray());
        CollectionAssert.AreEqual(new[] { "-n", "-a", "/Applications/Firefox.app", "--args", "-P", "Work", Address }, Hidden("firefox", "Work")!.Arguments.ToArray());
        var named = Hidden("chrome", "Work")!;
        Assert.IsFalse(named.ProfileFound, "A name that is not a folder would make Chrome create a profile: it opens without one.");
        CollectionAssert.AreEqual(new[] { "-a", "/Applications/Google Chrome.app", Address }, named.Arguments.ToArray());
        foreach (var flag in new[] { "-headless", "--remote-debugging-port=9222", "/jsconsole" })
        {
            var refused = Hidden("firefox", flag)!;
            Assert.IsFalse(refused.ProfileFound, $"'{flag}' starts like a command-line switch: Firefox must not be given it after -P.");
            CollectionAssert.AreEqual(new[] { "-a", "/Applications/Firefox.app", Address }, refused.Arguments.ToArray());
        }
        Assert.IsFalse(LinkBrowsers.IsChromiumProfileFolder("Profile x"));
        Assert.IsFalse(LinkBrowsers.IsChromiumProfileFolder("../Profile 1"));
    }

    [TestMethod]
    public void AProfileTheBrowserDoesNotHave_OpensWithoutProfile_AndSaysSo()
    {
        var launch = Plan("chrome", "Contoso")!;
        Assert.IsFalse(launch.ProfileFound);
        CollectionAssert.AreEqual(new[] { "-a", "/Applications/Google Chrome.app", Address }, launch.Arguments.ToArray());
        Assert.IsNull(Plan(null, "Contoso"), "The default browser without the profile is what the system does.");
    }
}