using CodeAlta.Desktop;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopKeepAwakeTests
{
    [TestMethod]
    public void Preferences_KeepTheComputerAwake_UntilItIsTurnedOff()
    {
        var root = Directory.CreateTempSubdirectory("codealta-awake-").FullName;
        try
        {
            var file = Path.Combine(root, "preferences.json");
            Assert.IsTrue(DesktopPreferences.Default.KeepAwake);
            Assert.IsTrue(DesktopPreferences.Load(root).KeepAwake);

            // Only the choice to turn it off is written.
            Assert.IsTrue(new DesktopPreferences(DesktopCloseBehavior.Ask, KeepAwake: false).Save(root));
            Assert.AreEqual("""{"onClose":"ask","keepAwake":false}""", File.ReadAllText(file));
            Assert.AreEqual(new DesktopPreferences(DesktopCloseBehavior.Ask, KeepAwake: false), DesktopPreferences.Load(root));
            Assert.IsTrue(new DesktopPreferences(DesktopCloseBehavior.Exit).Save(root));
            Assert.AreEqual("""{"onClose":"exit"}""", File.ReadAllText(file));
            Assert.IsTrue(DesktopPreferences.Load(root).KeepAwake);

            // A file written before the setting existed says nothing about it: the computer is kept awake.
            foreach (var earlier in new[] { """{"onClose":"keep","mcpServer":false}""", """{"closeToTray":false}""", """{"keepAwake":"no"}""", """{}""" })
            {
                File.WriteAllText(file, earlier);
                Assert.IsTrue(DesktopPreferences.Load(root).KeepAwake, earlier);
            }

            // Turned off, it stays off whichever way the file says what closing does.
            foreach (var off in new[] { """{"onClose":"keep","keepAwake":false}""", """{"closeToTray":true,"keepAwake":false}""", """{"keepAwake":false}""" })
            {
                File.WriteAllText(file, off);
                Assert.IsFalse(DesktopPreferences.Load(root).KeepAwake, off);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void Set_AcquiresOnce_AndReleasesWhenTurnedOff()
    {
        var backend = new FakeBackend();
        var warnings = new List<string>();
        using var keepAwake = new DesktopKeepAwake(backend, warnings.Add, "testing");
        Assert.IsFalse(keepAwake.IsActive);

        keepAwake.Set(true);
        keepAwake.Set(true);
        Assert.IsTrue(keepAwake.IsActive);
        Assert.AreEqual(1, backend.Acquired.Count, "Turned on twice, the computer holds one assertion.");
        Assert.AreEqual("testing", backend.Reasons.Single());

        keepAwake.Set(false);
        Assert.IsFalse(keepAwake.IsActive);
        Assert.IsTrue(backend.Acquired[0].Released);

        // Turned on again, a new assertion is taken.
        keepAwake.Set(true);
        Assert.AreEqual(2, backend.Acquired.Count);
        Assert.IsFalse(backend.Acquired[1].Released);
    }

    [TestMethod]
    public void Dispose_Releases_AndNothingIsAcquiredAfterwards()
    {
        var backend = new FakeBackend();
        var warnings = new List<string>();
        var keepAwake = new DesktopKeepAwake(backend, warnings.Add, "testing");
        keepAwake.Set(true);
        keepAwake.Dispose();
        Assert.IsTrue(backend.Acquired.Single().Released);
        Assert.IsFalse(keepAwake.IsActive);

        keepAwake.Set(true);
        keepAwake.Dispose();
        Assert.AreEqual(1, backend.Acquired.Count, "An application that exits keeps the computer awake no more.");
    }

    [TestMethod]
    public void Set_SurvivesASystemThatRefuses_AndTriesAgainWhenTurnedOnAgain()
    {
        var backend = new FakeBackend { Failures = 2 };
        var warnings = new List<string>();
        using var keepAwake = new DesktopKeepAwake(backend, warnings.Add, "testing");
        keepAwake.Set(true);
        Assert.IsFalse(keepAwake.IsActive);
        keepAwake.Set(true);
        Assert.IsFalse(keepAwake.IsActive);
        Assert.AreEqual("The computer cannot be kept awake: Refused.", warnings.Single(), "A refusal is logged once.");

        keepAwake.Set(false);
        keepAwake.Set(true);
        Assert.IsTrue(keepAwake.IsActive);

        // A system without assertions gives none: nothing is held, nothing fails.
        using var none = new DesktopKeepAwake(new FakeBackend { Unavailable = true }, warnings.Add, "testing");
        none.Set(true);
        Assert.IsFalse(none.IsActive);
    }

    [TestMethod]
    public void PlatformBackend_TakesAndReleasesAnAssertion_OnMacOSAndWindows()
    {
        // Linux depends on systemd-inhibit and a logind session, which a test machine may not have.
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows()) return;
        var warnings = new List<string>();
        using var keepAwake = new DesktopKeepAwake(DesktopKeepAwake.PlatformBackend(), warnings.Add, "CodeAlta test assertion");
        keepAwake.Set(true);
        Assert.IsTrue(keepAwake.IsActive, string.Join(Environment.NewLine, warnings));
        keepAwake.Set(false);
        Assert.IsFalse(keepAwake.IsActive);
        Assert.AreEqual(0, warnings.Count, string.Join(Environment.NewLine, warnings));
    }

    private sealed class FakeBackend : IDesktopKeepAwakeBackend
    {
        internal List<Assertion> Acquired { get; } = [];

        internal List<string> Reasons { get; } = [];

        internal int Failures { get; set; }

        internal bool Unavailable { get; init; }

        public IDisposable? Acquire(string reason)
        {
            Reasons.Add(reason);
            if (Failures > 0)
            {
                Failures--;
                throw new InvalidOperationException("Refused.");
            }

            if (Unavailable) return null;
            var assertion = new Assertion();
            Acquired.Add(assertion);
            return assertion;
        }
    }

    private sealed class Assertion : IDisposable
    {
        internal bool Released { get; private set; }

        public void Dispose() => Released = true;
    }
}
