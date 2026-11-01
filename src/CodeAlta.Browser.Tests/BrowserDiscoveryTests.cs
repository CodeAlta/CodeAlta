using CodeAlta.Browser.Discovery;

namespace CodeAlta.Browser.Tests;

[TestClass]
public sealed class BrowserDiscoveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static InstalledBrowser Browser(string id, BrowserEngine engine = BrowserEngine.Chromium, string version = "156.0.1.2")
        => new(id, id switch { "chrome" => "Google Chrome", "edge" => "Microsoft Edge", "firefox" => "Firefox", "safari" => "Safari", _ => id }, engine, $"/bin/{id}", version, null, []);

    [TestMethod]
    public void TheBrowserUsedLastWins()
    {
        BrowserUsage[] usages = [new(Browser("chrome"), Now.AddDays(-3), false), new(Browser("firefox", BrowserEngine.Firefox), Now.AddHours(-2), false)];
        var choice = BrowserChoice.Choose(usages, null, null, BrowserProfileKind.Isolated, Now, out _)!;
        Assert.AreEqual("firefox", choice.Browser.Id);
        Assert.AreEqual(BrowserChoiceSource.Auto, choice.Source);
        Assert.AreEqual("used most recently, 2 hours ago", choice.Reason);
    }

    [TestMethod]
    public void TheDefaultBrowserWinsWithinADay()
    {
        // Edge was opened by a link of another program an hour after the user's Chrome.
        BrowserUsage[] usages = [new(Browser("chrome"), Now.AddHours(-3), true), new(Browser("edge"), Now.AddHours(-2), false)];
        var choice = BrowserChoice.Choose(usages, null, "auto", BrowserProfileKind.Isolated, Now, out _)!;
        Assert.AreEqual("chrome", choice.Browser.Id);
        Assert.AreEqual("the default browser, used within a day of Microsoft Edge", choice.Reason);

        BrowserUsage[] old = [new(Browser("chrome"), Now.AddDays(-5), true), new(Browser("edge"), Now.AddHours(-2), false)];
        Assert.AreEqual("edge", BrowserChoice.Choose(old, null, null, BrowserProfileKind.Isolated, Now, out _)!.Browser.Id);
    }

    [TestMethod]
    public void SafariCountsByItsDefaultStatusAndNeverForTheUsersProfile()
    {
        BrowserUsage[] usages = [new(Browser("safari", BrowserEngine.Safari, "27.0"), null, true, Measurable: false), new(Browser("chrome"), Now.AddHours(-1), false)];
        Assert.AreEqual("safari", BrowserChoice.Choose(usages, null, null, BrowserProfileKind.Isolated, Now, out _)!.Browser.Id);
        Assert.AreEqual("chrome", BrowserChoice.Choose(usages, null, null, BrowserProfileKind.User, Now, out _)!.Browser.Id);
        Assert.IsNull(BrowserChoice.Choose(usages, "safari", null, BrowserProfileKind.Persistent, Now, out var refusal));
        StringAssert.Contains(refusal, "private window");
    }

    [TestMethod]
    public void TheSessionsNameThenSettingsComeFirst()
    {
        BrowserUsage[] usages = [new(Browser("chrome"), Now, true), new(Browser("edge"), Now.AddDays(-9), false), new(Browser("firefox", BrowserEngine.Firefox), null, false)];
        var named = BrowserChoice.Choose(usages, "Firefox", "edge", BrowserProfileKind.Isolated, Now, out _)!;
        Assert.AreEqual(("firefox", BrowserChoiceSource.User), (named.Browser.Id, named.Source));
        var preferred = BrowserChoice.Choose(usages, null, "edge", BrowserProfileKind.Isolated, Now, out _)!;
        Assert.AreEqual(("edge", BrowserChoiceSource.Settings), (preferred.Browser.Id, preferred.Source));
        var missing = BrowserChoice.Choose(usages, null, "brave", BrowserProfileKind.Isolated, Now, out _)!;
        Assert.AreEqual("chrome", missing.Browser.Id);
        StringAssert.Contains(missing.Reason, "brave, preferred in Settings, is not installed");
        Assert.IsNull(BrowserChoice.Choose(usages, "opera", null, BrowserProfileKind.Isolated, Now, out var refusal));
        StringAssert.Contains(refusal, "chrome, edge, firefox");
    }

    [TestMethod]
    public void WithoutEvidenceTheDefaultThenTheFirstFoundWins()
    {
        BrowserUsage[] withDefault = [new(Browser("firefox", BrowserEngine.Firefox), null, false), new(Browser("edge"), null, true)];
        Assert.AreEqual("edge", BrowserChoice.Choose(withDefault, null, null, BrowserProfileKind.Isolated, Now, out _)!.Browser.Id);
        BrowserUsage[] none = [new(Browser("firefox", BrowserEngine.Firefox), null, false), new(Browser("edge"), null, false)];
        var first = BrowserChoice.Choose(none, null, null, BrowserProfileKind.Isolated, Now, out _)!;
        Assert.AreEqual("edge", first.Browser.Id, "Chrome, Edge, Firefox, Safari: the first one found in this order.");
        Assert.AreEqual("the first browser found", first.Reason);
    }

    [TestMethod]
    public void TheUsersProfileNeedsChromeOrEdge144OrFirefox()
    {
        Assert.IsNull(Browser("chrome", version: "156.0").Refusal(BrowserProfileKind.User));
        StringAssert.Contains(Browser("edge", version: "140.0.1").Refusal(BrowserProfileKind.User), "version 144 or later");
        StringAssert.Contains(Browser("brave").Refusal(BrowserProfileKind.User), "only Chrome, Edge and Firefox");
        Assert.IsNull(Browser("firefox", BrowserEngine.Firefox, "150.0").Refusal(BrowserProfileKind.User));
    }

    [TestMethod]
    public void TheDefaultOfEachSystemIsMatched()
    {
        var browsers = new[] { Browser("chrome") with { ExecutablePath = Path.Combine(Path.GetTempPath(), "Google", "chrome.exe") }, Browser("firefox", BrowserEngine.Firefox) };
        Assert.AreEqual("chrome", BrowserChoice.MatchDefault(BrowserPlatform.Windows, browsers, Path.Combine(Path.GetTempPath(), "GOOGLE", "Chrome.exe"))?.Id);
        Assert.AreEqual("firefox", BrowserChoice.MatchDefault(BrowserPlatform.Linux, browsers, "firefox_firefox.desktop")?.Id);
        Assert.AreEqual("chrome", BrowserChoice.MatchDefault(BrowserPlatform.Linux, browsers, "google-chrome.desktop")?.Id);
        Assert.IsNull(BrowserChoice.MatchDefault(BrowserPlatform.Linux, browsers, "org.gnome.Epiphany.desktop"));
        Assert.AreEqual(@"C:\Program Files\Mozilla Firefox\firefox.exe", BrowserEnvironment.FirstToken("\"C:\\Program Files\\Mozilla Firefox\\firefox.exe\" -osint -url \"%1\""));
    }

    [TestMethod]
    public void TheCatalogFindsBrowsersTheirVersionsAndProfilesWithoutStartingThem()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-browser-catalog", Guid.NewGuid().ToString("N")[..8])).FullName;
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
            File.WriteAllText(Path.Combine(bin, "google-chrome"), string.Empty);
            var firefoxHome = Directory.CreateDirectory(Path.Combine(root, "firefox-install")).FullName;
            File.WriteAllText(Path.Combine(firefoxHome, "firefox"), string.Empty);
            File.WriteAllText(Path.Combine(firefoxHome, "application.ini"), "[App]\nVendor=Mozilla\nVersion=150.0.3\n");
            File.WriteAllText(Path.Combine(bin, "firefox"), string.Empty);

            var chromeData = Directory.CreateDirectory(Path.Combine(root, "home", ".config", "google-chrome")).FullName;
            File.WriteAllText(Path.Combine(chromeData, "Last Version"), "156.0.8078.80\n");
            var activeTime = Now.AddHours(-5).ToUnixTimeSeconds();
            File.WriteAllText(Path.Combine(chromeData, "Local State"), """{"profile":{"last_used":"Profile 1","info_cache":{"Default":{"name":"Person 1","active_time":""" + activeTime + """.5},"Profile 1":{"name":"Work"}}}}""");
            var work = Directory.CreateDirectory(Path.Combine(chromeData, "Profile 1")).FullName;
            File.WriteAllText(Path.Combine(work, "History"), string.Empty);
            File.SetLastWriteTimeUtc(Path.Combine(work, "History"), Now.AddHours(-1).UtcDateTime);

            var firefoxData = Directory.CreateDirectory(Path.Combine(root, "home", ".mozilla", "firefox")).FullName;
            File.WriteAllText(Path.Combine(firefoxData, "profiles.ini"), "[Profile0]\nName=default-release\nIsRelative=1\nPath=abc.default-release\n\n[Install4F96D1932A9F858E]\nDefault=abc.default-release\n");
            var profile = Directory.CreateDirectory(Path.Combine(firefoxData, "abc.default-release")).FullName;
            File.WriteAllText(Path.Combine(profile, "places.sqlite"), string.Empty);
            File.SetLastWriteTimeUtc(Path.Combine(profile, "places.sqlite"), Now.AddDays(-2).UtcDateTime);

            var environment = new BrowserEnvironment
            {
                Platform = BrowserPlatform.Linux,
                Home = Path.Combine(root, "home"),
                Variable = name => name == "PATH" ? bin : null,
                DefaultBrowser = static () => "google-chrome.desktop",
            };
            var browsers = BrowserCatalog.Find(environment);
            var chrome = browsers.Single(browser => browser.Id == "chrome");
            Assert.AreEqual("156.0.8078.80", chrome.Version);
            CollectionAssert.AreEquivalent(new[] { "Person 1", "Work" }, chrome.Profiles.Select(static item => item.Name).ToArray());
            Assert.IsTrue(chrome.Profiles.Single(static item => item.Name == "Work").IsDefault, "The profile Chrome used last is the one it opens.");
            var firefox = browsers.Single(browser => browser.Id == "firefox");
            Assert.AreEqual("default-release", firefox.Profiles.Single().Name);
            Assert.IsTrue(firefox.Profiles.Single().IsDefault);

            var usage = BrowserChoice.ReadUsage(environment, browsers);
            var chromeUse = usage.Single(item => item.Browser.Id == "chrome");
            Assert.IsTrue(chromeUse.IsDefault);
            Assert.AreEqual(Now.AddHours(-1), chromeUse.LastUsed, "History is newer than active_time, which Chrome writes at most hourly.");
            Assert.AreEqual(Now.AddDays(-2), usage.Single(item => item.Browser.Id == "firefox").LastUsed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void TheDefaultOfLinuxIsReadFromMimeapps()
    {
        var home = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-mimeapps", Guid.NewGuid().ToString("N")[..8])).FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(home, ".config"));
            File.WriteAllText(Path.Combine(home, ".config", "mimeapps.list"), "[Added Associations]\nx-scheme-handler/https=chromium.desktop;\n[Default Applications]\ntext/html=firefox.desktop\nx-scheme-handler/https=firefox.desktop;chromium.desktop;\n");
            Assert.AreEqual("firefox.desktop", BrowserEnvironment.LinuxDefaultBrowser(home, _ => null));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [TestMethod]
    public void AgoReadsAsAFewWords()
    {
        Assert.AreEqual("just now", BrowserChoice.Ago(Now, Now.AddSeconds(-10)));
        Assert.AreEqual("1 minute ago", BrowserChoice.Ago(Now, Now.AddMinutes(-1)));
        Assert.AreEqual("3 hours ago", BrowserChoice.Ago(Now, Now.AddHours(-3.5)));
        Assert.AreEqual("2 days ago", BrowserChoice.Ago(Now, Now.AddDays(-2)));
    }
}
