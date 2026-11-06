using CodeAlta.Plugin.Browser;

namespace CodeAlta.Browser.Tests;

[TestClass]
public sealed class PortOwnersTests
{
    // What lsof -nP -iTCP -sTCP:LISTEN -Fpnt writes on macOS; the t and f lines are not asked for but do no harm.
    private const string LsofOutput = """
        p662
        f9
        tIPv4
        n*:59351
        f16
        tIPv6
        n*:59351
        p10196
        f3
        tIPv4
        n127.0.0.1:8780
        p18445
        f61
        tIPv6
        n[::1]:42050
        p25722
        f44
        tIPv4
        n127.0.0.1:9741
        f47
        tIPv6
        n[::1]:9741
        p30228
        f49
        tIPv6
        n*:9500
        p31000
        f12
        tIPv4
        n192.168.1.20:5173
        """;

    [TestMethod]
    public void TheListeningSocketsOfLsof_AreReadWithTheirAddresses()
    {
        var listeners = SystemPortOwners.ParseLsof(LsofOutput.ReplaceLineEndings("\n"));
        CollectionAssert.AreEqual(new[]
        {
            new PortListener(662, "*", 59351), new PortListener(10196, "127.0.0.1", 8780), new PortListener(18445, "[::1]", 42050),
            new PortListener(25722, "127.0.0.1", 9741), new PortListener(25722, "[::1]", 9741), new PortListener(30228, "*", 9500),
            new PortListener(31000, "192.168.1.20", 5173),
        }, listeners.ToArray());
        Assert.IsNull(SystemPortOwners.SplitAddress("*:x"));
        Assert.IsNull(SystemPortOwners.SplitAddress("127.0.0.1:0"));
        Assert.AreEqual(("[::]", 3000), SystemPortOwners.SplitAddress("[::]:3000"));
    }

    [TestMethod]
    public void APageReachesTheSocketsOfAllAddressesAndOfItsOwn()
    {
        foreach (var host in new[] { "localhost", "127.0.0.1", "[::1]", "127.0.0.2" })
        {
            foreach (var any in new[] { "*", "0.0.0.0", "[::]" }) Assert.IsTrue(SystemPortOwners.Reaches(any, host), $"{any} for {host}");
            Assert.IsFalse(SystemPortOwners.Reaches("192.168.1.20", host), $"an address of the network only, for {host}");
            Assert.IsFalse(SystemPortOwners.Reaches("10.0.0.5", host), host);
        }

        Assert.IsTrue(SystemPortOwners.Reaches("127.0.0.1", "localhost"));
        Assert.IsTrue(SystemPortOwners.Reaches("[::1]", "localhost"));
        Assert.IsTrue(SystemPortOwners.Reaches("127.0.0.1", "127.0.0.1"));
        Assert.IsTrue(SystemPortOwners.Reaches("[::1]", "[::1]"));
        Assert.IsTrue(SystemPortOwners.Reaches("[::ffff:127.0.0.1]", "127.0.0.1"));
        Assert.IsFalse(SystemPortOwners.Reaches("127.0.0.1", "[::1]"));
        Assert.IsFalse(SystemPortOwners.Reaches("[::1]", "127.0.0.1"));
        Assert.IsFalse(SystemPortOwners.Reaches("127.0.0.2", "localhost"));
        Assert.IsFalse(SystemPortOwners.Reaches("127.0.0.2", "127.0.0.1"));
    }

    [TestMethod]
    public void TheOwnerOfAPage_IsTheOnlyProcessListeningWhereThePageConnects()
    {
        const int Own = 1;
        // A server of the project on an address of the network, and another program on the loopback, on the same port.
        PortListener[] shared = [new(100, "192.168.1.20", 5173), new(200, "127.0.0.1", 5173)];
        Assert.AreEqual(200, SystemPortOwners.OwnerOf(shared, "localhost", 5173, Own));
        Assert.AreEqual(200, SystemPortOwners.OwnerOf(shared, "127.0.0.1", 5173, Own));
        Assert.IsNull(SystemPortOwners.OwnerOf(shared, "[::1]", 5173, Own), "nothing listens on ::1");
        Assert.IsNull(SystemPortOwners.OwnerOf([new PortListener(100, "192.168.1.20", 5173)], "localhost", 5173, Own), "a page of the loopback never reaches it");

        // One process on both loopbacks is the owner; two processes where the page could connect are none.
        Assert.AreEqual(300, SystemPortOwners.OwnerOf([new PortListener(300, "127.0.0.1", 3000), new PortListener(300, "[::1]", 3000)], "localhost", 3000, Own));
        Assert.IsNull(SystemPortOwners.OwnerOf([new PortListener(300, "127.0.0.1", 3000), new PortListener(400, "[::1]", 3000)], "localhost", 3000, Own));
        Assert.AreEqual(400, SystemPortOwners.OwnerOf([new PortListener(300, "127.0.0.1", 3000), new PortListener(400, "[::1]", 3000)], "[::1]", 3000, Own));
        Assert.IsNull(SystemPortOwners.OwnerOf([new PortListener(300, "*", 3000), new PortListener(400, "127.0.0.1", 3000)], "127.0.0.1", 3000, Own));
        Assert.IsNull(SystemPortOwners.OwnerOf([new PortListener(300, "*", 3000)], "localhost", 3001, Own), "another port");

        // CodeAlta itself is never the server of a page of a project, and with it there, nothing is.
        Assert.IsNull(SystemPortOwners.OwnerOf([new PortListener(Own, "127.0.0.1", 3000)], "localhost", 3000, Own));
        Assert.IsNull(SystemPortOwners.OwnerOf([new PortListener(Own, "*", 3000), new PortListener(300, "[::1]", 3000)], "[::1]", 3000, Own));
    }

    [TestMethod]
    public async Task TheSystemTellsTheProgramOfAPort_ForTheAddressItListensOn()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The sockets of Windows are read from netstat: see the tests of its output.");
            return;
        }

        using var folder = new BrowserPluginTests.TempFolder();
        var port = CodeAlta.Browser.Launch.LaunchHelpers.FreePort();
        var start = new System.Diagnostics.ProcessStartInfo("nc") { WorkingDirectory = folder.Path, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "-l", "127.0.0.1", port.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(argument);
        System.Diagnostics.Process listener;
        try
        {
            listener = System.Diagnostics.Process.Start(start)!;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Inconclusive("nc is not installed.");
            return;
        }

        using (listener)
        {
            try
            {
                var owners = new SystemPortOwners();
                PortOwner? owner = null;
                for (var deadline = DateTime.UtcNow.AddSeconds(10); owner is null && !listener.HasExited && DateTime.UtcNow < deadline; await Task.Delay(100))
                {
                    owner = await owners.FindAsync("localhost", port, CancellationToken.None);
                }

                Assert.IsNotNull(owner, "nc listens on the loopback");
                Assert.AreEqual(listener.Id, owner.ProcessId);
                Assert.AreEqual(CodeAlta.Browser.Tools.BrowserFiles.RealPath(folder.Path), CodeAlta.Browser.Tools.BrowserFiles.RealPath(owner.WorkingDirectory!));
                StringAssert.Contains(owner.Command, "nc");
                Assert.AreEqual(listener.Id, (await owners.FindAsync("127.0.0.1", port, CancellationToken.None))?.ProcessId);
                Assert.IsNull(await owners.FindAsync("[::1]", port, CancellationToken.None), "nothing listens on ::1");
                Assert.AreEqual(listener.Id, (await owners.ListAsync(CancellationToken.None)).Single(item => item.Port == port).Owner.ProcessId);
            }
            finally
            {
                listener.Kill(entireProcessTree: true);
                await listener.WaitForExitAsync();
            }
        }
    }

    // What netstat -ano writes on Windows, in English and in German: the words of the state are translated.
    private const string NetstatOutput = """

        Active Connections

          Proto  Local Address          Foreign Address        State           PID
          TCP    0.0.0.0:135            0.0.0.0:0              LISTENING       1060
          TCP    127.0.0.1:5173         0.0.0.0:0              LISTENING       12345
          TCP    192.168.1.20:5173      0.0.0.0:0              LISTENING       4711
          TCP    127.0.0.1:5173         127.0.0.1:51234        ESTABLISHED     12345
          TCP    127.0.0.1:51234        127.0.0.1:5173         ESTABLISHED     9999
          TCP    [::]:135               [::]:0                 LISTENING       1060
          TCP    [::1]:5173             [::]:0                 LISTENING       12345
          TCP    [fe80::1%12]:7680      [::]:0                 LISTENING       777
          UDP    0.0.0.0:5353           *:*                                    2020
          UDP    [::]:5353              *:*                                    2020

        Aktive Verbindungen

          Proto  Lokale Adresse         Remoteadresse          Status           PID
          TCP    0.0.0.0:3000           0.0.0.0:0              ABHÖREN          5150
          TCP    127.0.0.1:3000         127.0.0.1:60000        HERGESTELLT      5150
        """;

    [TestMethod]
    public void TheListeningSocketsOfNetstat_AreReadWhateverTheLanguageOfWindows()
    {
        var listeners = SystemPortOwners.ParseNetstat(NetstatOutput.ReplaceLineEndings("\r\n"));
        CollectionAssert.AreEqual(new[]
        {
            new PortListener(1060, "0.0.0.0", 135), new PortListener(12345, "127.0.0.1", 5173), new PortListener(4711, "192.168.1.20", 5173),
            new PortListener(1060, "[::]", 135), new PortListener(12345, "[::1]", 5173), new PortListener(777, "[fe80::1%12]", 7680),
            new PortListener(5150, "0.0.0.0", 3000),
        }, listeners.ToArray());
        Assert.AreEqual(12345, SystemPortOwners.OwnerOf(listeners, "localhost", 5173, 1), "the server of both loopbacks, not the one of the network");
        Assert.AreEqual(5150, SystemPortOwners.OwnerOf(listeners, "127.0.0.1", 3000, 1));
    }

    [TestMethod]
    public void TheCommandLineOfAProcess_NamesTheAbsolutePathsOfItsWords()
    {
        CollectionAssert.AreEqual(new[] { "C:\\Program Files\\nodejs\\node.exe", "C:\\app\\node_modules\\.bin\\\\..\\vite\\bin\\vite.js" },
            SystemPortOwners.CommandLinePaths("\"C:\\Program Files\\nodejs\\node.exe\" \"C:\\app\\node_modules\\.bin\\\\..\\vite\\bin\\vite.js\" --port 5173", windows: true).ToArray());
        CollectionAssert.AreEqual(new[] { "C:/app/bin/Debug/net10.0/App.exe", "D:\\data" },
            SystemPortOwners.CommandLinePaths("C:/app/bin/Debug/net10.0/App.exe --urls=http://localhost:5000 --root=D:\\data relative\\file.js", windows: true).ToArray());
        Assert.AreEqual(0, SystemPortOwners.CommandLinePaths("node \\\\server\\share\\vite.js \\\\?\\C:\\x /app/x", windows: true).Count, "a share, a device path and a path of another system are left out");
        CollectionAssert.AreEqual(new[] { "/usr/bin/node", "/app/server.js", "/app/www" },
            SystemPortOwners.CommandLinePaths("/usr/bin/node /app/server.js --dir=/app/www //server/x C:\\x", windows: false).ToArray());
        CollectionAssert.AreEqual(new[] { "a b", "", "c" }, SystemPortOwners.CommandLineWords("  \"a b\" \"\"  c ").ToArray());
    }

    [TestMethod]
    public async Task WhereTheSystemDoesNotTellTheFolderOfAProgram_ItsCommandLineNamesAFileOfTheProject()
    {
        using var folder = new BrowserPluginTests.TempFolder();
        var project = CodeAlta.Browser.Tools.BrowserFiles.RealPath(Directory.CreateDirectory(Path.Combine(folder.Path, "project")).FullName);
        var script = Path.Combine(project, "node_modules", "vite", "bin", "vite.js");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        await File.WriteAllTextAsync(script, "// vite");
        var other = Directory.CreateDirectory(Path.Combine(folder.Path, "other")).FullName;
        await File.WriteAllTextAsync(Path.Combine(other, "server.js"), "// other");

        Assert.AreEqual(project, new PortOwner(1, $"/usr/local/bin/node \"{script}\" --port 5173", null).FolderIn(project, byCommandLine: true));
        Assert.IsNull(new PortOwner(1, $"/usr/local/bin/node \"{script}\"", null).FolderIn(project, byCommandLine: false), "only where the system does not tell the folder");
        Assert.IsNull(new PortOwner(1, $"/usr/local/bin/node {Path.Combine(other, "server.js")}", null).FolderIn(project, byCommandLine: true));
        Assert.IsNull(new PortOwner(1, $"/usr/local/bin/node {Path.Combine(project, "missing.js")}", null).FolderIn(project, byCommandLine: true), "a file that is not there");
        Assert.IsNull(new PortOwner(1, "python manage.py runserver", null).FolderIn(project, byCommandLine: true));

        // The folder a program runs in, when the system tells it, is the only answer.
        Assert.AreEqual(project, new PortOwner(1, "python manage.py runserver", project).FolderIn(project, byCommandLine: true));
        Assert.IsNull(new PortOwner(1, $"/usr/local/bin/node \"{script}\"", other).FolderIn(project, byCommandLine: true));
    }

    [TestMethod]
    public void TheCommandLineWindowsGives_IsReadInsideItsBuffer()
    {
        const string Text = "node C:\\app\\vite.js";
        var size = 2 * IntPtr.Size + Text.Length * 2;
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
        try
        {
            System.Runtime.InteropServices.Marshal.WriteInt16(buffer, (short)(Text.Length * 2));
            System.Runtime.InteropServices.Marshal.WriteInt16(buffer, 2, (short)(Text.Length * 2));
            System.Runtime.InteropServices.Marshal.WriteIntPtr(buffer, IntPtr.Size, buffer + 2 * IntPtr.Size);
            System.Runtime.InteropServices.Marshal.Copy(Text.ToCharArray(), 0, buffer + 2 * IntPtr.Size, Text.Length);
            Assert.AreEqual(Text, WindowsProcesses.ReadUnicodeString(buffer, size));

            // A pointer or a length that leads out of the buffer is never followed.
            System.Runtime.InteropServices.Marshal.WriteIntPtr(buffer, IntPtr.Size, buffer + size);
            Assert.IsNull(WindowsProcesses.ReadUnicodeString(buffer, size));
            System.Runtime.InteropServices.Marshal.WriteIntPtr(buffer, IntPtr.Size, buffer + 2 * IntPtr.Size);
            System.Runtime.InteropServices.Marshal.WriteInt16(buffer, (short)(Text.Length * 2 + 2));
            Assert.IsNull(WindowsProcesses.ReadUnicodeString(buffer, size));
            System.Runtime.InteropServices.Marshal.WriteInt16(buffer, 0);
            Assert.AreEqual(string.Empty, WindowsProcesses.ReadUnicodeString(buffer, size));
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }

    [TestMethod]
    public async Task APortThatChangesHands_IsAskedAgain()
    {
        using var folder = new BrowserPluginTests.TempFolder();
        var project = Directory.CreateDirectory(Path.Combine(folder.Path, "project")).FullName;
        var owners = new ChangingPortOwners { Owner = new PortOwner(100, "node vite", project) };
        await using var files = new ProjectFileServer();
        var pages = new ProjectPages(files, owners);
        var url = new Uri("http://localhost:5173/");
        Assert.IsNotNull(await pages.FindAsync(url, project, CancellationToken.None));
        Assert.AreEqual(("localhost", 5173), owners.Asked[^1]);

        // The server of the project stops; another program takes its port.
        owners.Owner = new PortOwner(200, "python -m http.server", folder.Path);
        Assert.IsNull(await pages.FindAsync(url, project, CancellationToken.None), "the page is no longer one of the project");
        Assert.IsNull(await pages.FindAsync(new Uri("http://[::1]:5173/"), project, CancellationToken.None));
        Assert.AreEqual(("[::1]", 5173), owners.Asked[^1], "the host of the page is asked for");
    }

    private sealed class ChangingPortOwners : IPortOwners
    {
        public PortOwner? Owner { get; set; }

        public List<(string Host, int Port)> Asked { get; } = [];

        public Task<PortOwner?> FindAsync(string host, int port, CancellationToken cancellationToken)
        {
            Asked.Add((host, port));
            return Task.FromResult(Owner);
        }

        public Task<IReadOnlyList<ListeningPort>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ListeningPort>>([]);
    }
}
