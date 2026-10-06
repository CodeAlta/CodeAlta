using CodeAlta.Desktop;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopTerminalStartTests
{
    private static DesktopLaunchOptions? Parse(params string[] args)
        => DesktopCommandLine.TryParse(args, _ => false, _ => false, out var options, out _) ? options : null;

    [TestMethod]
    public void WaitOption_KeepsTheTerminal_AndStandsAloneOrWithDev()
    {
        Assert.IsTrue(Parse("--wait")!.Wait);
        Assert.IsFalse(Parse("--wait")!.Developer);
        Assert.IsTrue(Parse("--dev", "--wait")!.Wait);
        Assert.IsTrue(Parse("--dev", "--wait")!.Developer);
        Assert.IsTrue(Parse("--wait", "--dev")!.Developer);
        Assert.IsFalse(Parse()!.Wait);
        Assert.IsFalse(Parse("--dev")!.Wait);
        // It starts the application on the same profile as the start without it.
        Assert.AreEqual(Parse()!.DataRoot, Parse("--wait")!.DataRoot);
        Assert.AreEqual(Parse()!.Owned, Parse("--wait")!.Owned);
        Assert.IsNull(Parse("--wait", "--wait"));
        Assert.IsNull(Parse("--wait", "--exit"));
        Assert.IsNull(Parse("--wait", "--data-root", "C:\\data"));
    }

    [TestMethod]
    public void OnlyANormalStart_LeavesItsTerminal()
    {
        Assert.IsTrue(DesktopTerminalStart.Applies(Parse()!));
        Assert.IsTrue(DesktopTerminalStart.Applies(Parse("--dev")!));
        // The terminal is kept on request, and by a start that only talks to the running application.
        Assert.IsFalse(DesktopTerminalStart.Applies(Parse("--wait")!));
        Assert.IsFalse(DesktopTerminalStart.Applies(Parse("--dev", "--wait")!));
        Assert.IsFalse(DesktopTerminalStart.Applies(Parse("--exit")!));
        // The application that such a start runs does not start another one.
        Assert.IsFalse(DesktopTerminalStart.Applies(Parse()! with { StartToken = new string('a', 32) }));
        // Explicit roots are tests and automation, which own the process they start.
        var root = OperatingSystem.IsWindows() ? @"Q:\owned" : "/owned";
        Assert.IsFalse(DesktopTerminalStart.Applies(new DesktopLaunchOptions(Path.Combine(root, "browser"), null)));
        Assert.IsFalse(DesktopTerminalStart.Applies(new DesktopLaunchOptions(Path.Combine(root, "browser"), Path.Combine(root, "copy"))
        {
            Owned = new(Path.Combine(root, "project"), Path.Combine(root, "home"), root, Path.Combine(root, "builtin")),
        }));
    }

    [TestMethod]
    public void OnlyTheApplicationsOwnExecutable_IsStartedAgain()
    {
        Assert.IsTrue(DesktopTerminalStart.IsApplication("/home/me/.dotnet/tools/.store/codealta/1.2.3/codealta.linux-x64/1.2.3/tools/net10.0/linux-x64/alta"));
        // A path of Windows is one name anywhere else.
        if (OperatingSystem.IsWindows())
            Assert.IsTrue(DesktopTerminalStart.IsApplication(@"C:\Users\me\.dotnet\tools\.store\codealta\1.2.3\codealta.win-x64\1.2.3\tools\net10.0\win-x64\ALTA.exe"));
        // The .NET host running the assembly would be started without it.
        Assert.IsFalse(DesktopTerminalStart.IsApplication(@"C:\Program Files\dotnet\dotnet.exe"));
        Assert.IsFalse(DesktopTerminalStart.IsApplication("/usr/lib/dotnet/dotnet"));
        Assert.IsFalse(DesktopTerminalStart.IsApplication(null));

        // Nor is the process running these tests: nothing is started, and nothing is left behind.
        using var error = new StringWriter();
        Assert.IsNull(DesktopTerminalStart.TryHandOver(Parse()!, error));
        Assert.AreEqual(string.Empty, error.ToString());
        Assert.IsNull(Environment.GetEnvironmentVariable(DesktopTerminalStart.Variable));
    }

    [TestMethod]
    public void Token_NamesOneFileOfTheTemporaryFolder()
    {
        var token = Guid.NewGuid().ToString("N");
        Assert.IsTrue(DesktopTerminalStart.IsToken(token));
        Assert.AreEqual(Path.Combine(Path.GetTempPath(), "codealta-start-" + token), DesktopTerminalStart.TokenPath(token));
        // Nothing but a token names a file: the variable that carries it is read from the environment.
        foreach (var other in new[] { null, "", "1", token[..31], token + "0", new string('A', 32), "../" + token[3..], new string('g', 32) })
        {
            Assert.IsFalse(DesktopTerminalStart.IsToken(other), other);
            if (other is not null) Assert.ThrowsExactly<ArgumentException>(() => DesktopTerminalStart.TokenPath(other));
        }
    }

    [TestMethod]
    public void Start_EndsOnceTheWindowIsShown()
    {
        var token = NewToken();
        var path = DesktopTerminalStart.TokenPath(token);
        var steps = 0;

        // The application shows its window while the start waits for it.
        var code = DesktopTerminalStart.Await(token, _ =>
        {
            if (++steps == 3) DesktopTerminalStart.NotifyShown(token);
            return null;
        }, TimeSpan.FromMinutes(1));

        Assert.AreEqual(0, code);
        Assert.AreEqual(3, steps);
        Assert.IsFalse(File.Exists(path));
        // A window shown before the start looks, and an application nothing waits for.
        Assert.AreEqual(0, DesktopTerminalStart.Await(token, _ => throw new AssertFailedException("Nothing is left to wait for."), TimeSpan.FromMinutes(1)));
        DesktopTerminalStart.NotifyShown(token);
        DesktopTerminalStart.NotifyShown(null);
    }

    [TestMethod]
    public void Start_ReportsAnApplicationThatEndedBeforeItsWindow()
    {
        var token = NewToken();
        var steps = 0;

        var code = DesktopTerminalStart.Await(token, _ => ++steps == 2 ? 3 : null, TimeSpan.FromMinutes(1));

        Assert.AreEqual(3, code);
        Assert.AreEqual(2, steps);
        Assert.IsFalse(File.Exists(DesktopTerminalStart.TokenPath(token)), "the token does not outlive its start");

        // One that ended well (it found CodeAlta running, and showed its window) is no failure.
        token = NewToken();
        Assert.AreEqual(0, DesktopTerminalStart.Await(token, _ => 0, TimeSpan.FromMinutes(1)));
        Assert.IsFalse(File.Exists(DesktopTerminalStart.TokenPath(token)));

        var said = DesktopTerminalStart.Failure(3, Path.Combine("data", "logs"), windows: false);
        StringAssert.Contains(said, "exit code 3");
        StringAssert.Contains(said, Path.Combine("data", "logs"));
        StringAssert.Contains(said, "alta --wait");
        // A windowed program writes nothing to a console on Windows: the option would show no more.
        Assert.IsFalse(DesktopTerminalStart.Failure(3, "logs", windows: true).Contains("--wait", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Start_LeavesASlowApplicationToItself()
    {
        var token = NewToken();
        var steps = 0;

        var code = DesktopTerminalStart.Await(token, wait =>
        {
            steps++;
            Thread.Sleep(wait);
            return null;
        }, TimeSpan.FromMilliseconds(120));

        Assert.AreEqual(0, code);
        Assert.IsTrue(steps >= 2, "it looked more than once");
        // The application finds nothing to remove when its window is shown at last.
        Assert.IsFalse(File.Exists(DesktopTerminalStart.TokenPath(token)));
        DesktopTerminalStart.NotifyShown(token);
    }

    // What a start does before it starts the application.
    private static string NewToken()
    {
        var token = Guid.NewGuid().ToString("N");
        File.Open(DesktopTerminalStart.TokenPath(token), FileMode.CreateNew).Dispose();
        return token;
    }
}
