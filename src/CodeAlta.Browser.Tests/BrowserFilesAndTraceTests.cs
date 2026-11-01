using System.Text;
using CodeAlta.Browser.Performance;
using CodeAlta.Browser.Tools;

namespace CodeAlta.Browser.Tests;

[TestClass]
public sealed class BrowserFilesAndTraceTests
{
    [TestMethod]
    public void PathsStayInTheirFoldersAndSecretsNeverGoToAPage()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-files", "work");
        var other = Path.Combine(Path.GetTempPath(), "codealta-files", "out");
        var files = new BrowserFiles(root, [root], [root, other], Path.Combine(root, "tmp"));
        Assert.AreEqual(Path.Combine(root, "a", "b.png"), files.ResolveWrite("a/b.png", "filePath"));
        Assert.AreEqual(Path.Combine(other, "c.png"), files.ResolveWrite(Path.Combine(other, "c.png"), "filePath"));
        StringAssert.Contains(Assert.ThrowsExactly<BrowserToolException>(() => files.ResolveRead(Path.Combine(other, "c.png"), "filePaths")).Message, "outside the folders");
        StringAssert.Contains(Assert.ThrowsExactly<BrowserToolException>(() => files.ResolveRead("../work2/x", "filePaths")).Message, "outside the folders");
        foreach (var secret in new[] { ".env", ".env.local", "deploy.pem", "id_ed25519", ".ssh/config", "certs/site.pfx" })
        {
            StringAssert.Contains(Assert.ThrowsExactly<BrowserToolException>(() => files.ResolveRead(secret, "filePaths")).Message, "holds secrets", secret);
        }

        Assert.AreEqual(Path.Combine(root, "photo.jpg"), files.ResolveRead("photo.jpg", "filePaths"));
        Assert.IsTrue(files.CanRead(Path.Combine(root, "photo.jpg")));
        Assert.IsFalse(files.CanRead(Path.Combine(root, ".env")));
        if (OperatingSystem.IsWindows())
        {
            // Refused by its text alone, before anything asks the network for it.
            var started = System.Diagnostics.Stopwatch.StartNew();
            StringAssert.Contains(Assert.ThrowsExactly<BrowserToolException>(() => files.ResolveRead(@"\\codealta-test.invalid\share\x.txt", "filePaths")).Message, "outside the folders");
            Assert.IsFalse(files.CanRead(@"\\codealta-test.invalid\share\x.txt"));
            Assert.IsTrue(started.Elapsed < TimeSpan.FromSeconds(1), "no lookup of the server");
        }
    }

    [TestMethod]
    public async Task AFolderReplacedByALinkAfterTheCheck_IsRefusedWhenTheFileIsOpened()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("The system does not tell the path of an opened file here: the path is resolved again instead.");
            return;
        }

        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-files", Guid.NewGuid().ToString("N")[..8])).FullName;
        var work = Directory.CreateDirectory(Path.Combine(folder, "work")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(folder, "outside")).FullName;
        var sub = Path.Combine(work, "sub");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "outside");
            await File.WriteAllTextAsync(Path.Combine(outside, "keep.txt"), "kept");
            Directory.CreateDirectory(sub);
            await File.WriteAllTextAsync(Path.Combine(sub, "secret.txt"), "inside");
            var files = new BrowserFiles(work, [work], [work], Path.Combine(folder, "tmp"));
            var written = files.ResolveWrite("sub/keep.txt", "filePath");
            files.ResolveRead("sub/secret.txt", "filePaths");

            // Between the check and the use, the folder becomes a link out of the folders.
            Directory.Delete(sub, recursive: true);
            if (OperatingSystem.IsWindows())
            {
                using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{sub}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false })!;
                await mklink.WaitForExitAsync();
            }
            else
            {
                Directory.CreateSymbolicLink(sub, outside);
            }

            StringAssert.Contains((await Assert.ThrowsExactlyAsync<BrowserToolException>(() => files.WriteAsync(written, "replaced", CancellationToken.None))).Message, "moved outside");
            Assert.AreEqual("kept", await File.ReadAllTextAsync(Path.Combine(outside, "keep.txt")), "nothing was truncated or written outside");
            await Assert.ThrowsExactlyAsync<BrowserToolException>(() => files.StageReadAsync("sub/secret.txt", "filePaths", CancellationToken.None));
        }
        finally
        {
            try
            {
                if (Directory.Exists(sub)) Directory.Delete(sub);
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public async Task AnUploadIsACopyThePageGets_AndAScriptIsReadAsItWasChecked()
    {
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-files", Guid.NewGuid().ToString("N")[..8])).FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "photo.txt"), "photo");
            await File.WriteAllTextAsync(Path.Combine(folder, "script.js"), "() => 1");
            var temporary = Path.Combine(Path.GetTempPath(), "codealta-files-tmp", Guid.NewGuid().ToString("N")[..8]);
            var files = new BrowserFiles(folder, [folder], [folder], temporary);
            var staged = await files.StageReadAsync("photo.txt", "filePaths", CancellationToken.None);
            Assert.IsTrue(BrowserFiles.IsWithin(staged, temporary), staged);
            Assert.AreEqual("photo.txt", Path.GetFileName(staged));
            Assert.AreEqual("photo", await File.ReadAllTextAsync(staged));
            Assert.AreEqual("() => 1", await files.ReadTextAsync("script.js", "sourcePath", CancellationToken.None));
            await files.WriteAsync(Path.Combine(folder, "out.txt"), "longer text", CancellationToken.None);
            await files.WriteAsync(Path.Combine(folder, "out.txt"), "short", CancellationToken.None);
            Assert.AreEqual("short", await File.ReadAllTextAsync(Path.Combine(folder, "out.txt")), "a write replaces what was there");
            Directory.Delete(temporary, recursive: true);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void SafariBeforeVersion27_IsNotChosen()
    {
        var old = new CodeAlta.Browser.Discovery.InstalledBrowser("safari", "Safari", CodeAlta.Browser.Discovery.BrowserEngine.Safari, "/usr/bin/safaridriver", "26.2", null, []);
        StringAssert.Contains(old.Refusal(CodeAlta.Browser.Discovery.BrowserProfileKind.Isolated), "Safari 27");
        Assert.IsNull((old with { Version = "27.0" }).Refusal(CodeAlta.Browser.Discovery.BrowserProfileKind.Isolated));
    }

    [TestMethod]
    public void ALinkedFolderLeadsNowhereOutsideTheFolders_AndASandboxKeepsItsDeniedAndProtectedPaths()
    {
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-files", Guid.NewGuid().ToString("N")[..8])).FullName;
        try
        {
            var work = Directory.CreateDirectory(Path.Combine(folder, "work")).FullName;
            var outside = Directory.CreateDirectory(Path.Combine(folder, "outside")).FullName;
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "outside");
            var files = new BrowserFiles(work, [work], [work], Path.Combine(work, "tmp"))
            {
                Denied = [Path.Combine(work, "private")],
                ReadOnly = [Path.Combine(work, ".git", "hooks")],
            };
            // A junction on Windows needs no privilege; a symbolic link elsewhere.
            var link = Path.Combine(work, "link");
            if (OperatingSystem.IsWindows())
            {
                using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false })!;
                mklink.WaitForExit();
            }
            else
            {
                Directory.CreateSymbolicLink(link, outside);
            }

            Assert.IsTrue(Directory.Exists(link), "the link was made");
            StringAssert.Contains(Assert.ThrowsExactly<BrowserToolException>(() => files.ResolveRead("link/secret.txt", "filePaths")).Message, "outside the folders");
            StringAssert.Contains(Assert.ThrowsExactly<BrowserToolException>(() => files.ResolveWrite("link/new.png", "filePath")).Message, "outside the folders");
            Assert.IsFalse(files.CanRead(Path.Combine(link, "secret.txt")), "a file: address follows the link too");
            Assert.IsTrue(files.CanRead(Path.Combine(work, "page.html")));

            StringAssert.Contains(Assert.ThrowsExactly<BrowserToolException>(() => files.ResolveRead("private/a.txt", "filePaths")).Message, "outside the folders");
            Assert.IsFalse(files.CanRead(Path.Combine(work, "private", "a.txt")));
            StringAssert.Contains(Assert.ThrowsExactly<BrowserToolException>(() => files.ResolveWrite(".git/hooks/pre-commit", "filePath")).Message, "read-only");
            Assert.AreEqual(Path.Combine(work, ".git", "hooks", "pre-commit"), files.ResolveRead(".git/hooks/pre-commit", "filePaths"), "a protected path is read");
        }
        finally
        {
            // The link first, alone: its target is not deleted through it.
            try
            {
                var link = Path.Combine(folder, "work", "link");
                if (Directory.Exists(link)) Directory.Delete(link);
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public void TheTraceSummaryReadsPaintsShiftsTasksAndRequests()
    {
        var trace = """
            {"traceEvents":[
              {"name":"thread_name","ph":"M","pid":1,"tid":7,"ts":0,"args":{"name":"CrRendererMain"}},
              {"name":"navigationStart","ph":"R","pid":1,"tid":7,"ts":1000000,"args":{"data":{"isLoadingMainFrame":true,"documentLoaderURL":"https://example.com/"}}},
              {"name":"ResourceSendRequest","ph":"I","pid":1,"tid":7,"ts":1000100,"args":{"data":{"requestId":"r1","url":"https://example.com/app.js"}}},
              {"name":"RunTask","ph":"X","pid":1,"tid":7,"ts":1010000,"dur":120000},
              {"name":"EvaluateScript","ph":"X","pid":1,"tid":7,"ts":1010000,"dur":100000},
              {"name":"FunctionCall","ph":"X","pid":1,"tid":7,"ts":1020000,"dur":50000},
              {"name":"Layout","ph":"X","pid":1,"tid":7,"ts":1110000,"dur":10000},
              {"name":"RunTask","ph":"X","pid":1,"tid":99,"ts":1010000,"dur":900000},
              {"name":"firstContentfulPaint","ph":"R","pid":1,"tid":7,"ts":1300000},
              {"name":"largestContentfulPaint::Candidate","ph":"R","pid":1,"tid":7,"ts":1400000},
              {"name":"largestContentfulPaint::Candidate","ph":"R","pid":1,"tid":7,"ts":1800000},
              {"name":"LayoutShift","ph":"I","pid":1,"tid":7,"ts":1500000,"args":{"data":{"weighted_score_delta":0.05,"had_recent_input":false}}},
              {"name":"LayoutShift","ph":"I","pid":1,"tid":7,"ts":1600000,"args":{"data":{"weighted_score_delta":0.5,"had_recent_input":true}}},
              {"name":"ResourceFinish","ph":"I","pid":1,"tid":7,"ts":1250100,"args":{"data":{"requestId":"r1","encodedDataLength":20480}}},
              {"name":"MarkLoad","ph":"I","pid":1,"tid":7,"ts":1900000,"args":{"data":{"isMainFrame":true}}}
            ]}
            """;
        var summary = TraceSummary.Summarize(new MemoryStream(Encoding.UTF8.GetBytes(trace)), "about:blank");
        Assert.AreEqual("https://example.com/", summary.Url);
        Assert.AreEqual(300, summary.FirstContentfulPaintMs);
        Assert.AreEqual(800, summary.LargestContentfulPaintMs, "The last candidate is the largest paint.");
        Assert.AreEqual(0.05, summary.LayoutShift, 1e-9, "A shift after an input does not count.");
        Assert.AreEqual(900, summary.LoadMs);
        Assert.AreEqual(910, summary.DurationMs, "from the first timed event to the end of the last one: the metadata events at 0 have no time");
        Assert.AreEqual(1, summary.LongTasks.Count, "Only the tasks of the main thread of the renderer.");
        Assert.AreEqual(120, summary.LongTasks[0].DurationMs);
        Assert.AreEqual(100, summary.MainThreadMs["scripting"], "A call inside a script is part of it, not counted twice.");
        Assert.AreEqual(10, summary.MainThreadMs["rendering"]);
        Assert.AreEqual(("https://example.com/app.js", 250.0, 20480L), summary.SlowestRequests.Single());
        var text = summary.ToText();
        StringAssert.Contains(text, "Largest Contentful Paint: 800 ms");
        StringAssert.Contains(text, "Long tasks (over 50 ms): 1");
        StringAssert.Contains(text, "- 250 ms 20 kB https://example.com/app.js");
    }

    [TestMethod]
    public void KeysParseAsAKeyboardGivesThem()
    {
        var (key, modifiers) = BrowserKeys.ParseCombination("Control+A");
        Assert.AreEqual(("a", 1), (key.Key, modifiers.Count));
        var (shifted, _) = BrowserKeys.ParseCombination("Control+Shift+a");
        Assert.AreEqual("A", shifted.Key);
        var (plus, held) = BrowserKeys.ParseCombination("Control++");
        Assert.AreEqual(("+", "Control"), (plus.Key, held.Single().Key));
        Assert.AreEqual(2, BrowserKeys.Resolve("Control").ModifierBit);
        Assert.ThrowsExactly<BrowserToolException>(() => BrowserKeys.ParseCombination("Control+Nope"));
    }

    [TestMethod]
    public void ViewportsAndLocationsParse()
    {
        Assert.AreEqual((400, 700, 2.0, true, false, true), BrowserTools.ParseViewport("400x700x2,mobile,landscape"));
        Assert.AreEqual((1280, 720, 1.0, false, false, false), BrowserTools.ParseViewport("1280x720"));
        Assert.ThrowsExactly<BrowserToolException>(() => BrowserTools.ParseViewport("wide"));
        Assert.AreEqual((48.85, 2.35), BrowserTools.ParseGeolocation("48.85, 2.35"));
        Assert.ThrowsExactly<BrowserToolException>(() => BrowserTools.ParseGeolocation("91,0"));
    }
}
