using System.Text.Json;
using CodeAlta.Browser.Discovery;
using CodeAlta.Browser.Drivers;
using CodeAlta.Browser.Drivers.Cdp;
using CodeAlta.Browser.Launch;
using CodeAlta.Browser.Tools;

namespace CodeAlta.Browser.Tests;

/// <summary>
/// A browser of this computer started headless for the tests of a class, with the pages of <see cref="TestSite"/>.
/// The tests of a browser that is not installed are inconclusive, as the frontend's tests are without Edge.
/// </summary>
internal sealed class BrowserTestHost : IAsyncDisposable
{
    private BrowserTestHost(IBrowserConnection connection, TestSite site, string folder)
    {
        Connection = connection;
        Site = site;
        Folder = folder;
    }

    public IBrowserConnection Connection { get; }

    public TestSite Site { get; }

    /// <summary>The folder the files of the tests go to.</summary>
    public string Folder { get; }

    /// <summary>The first installed browser of an engine: <c>CODEALTA_TEST_CHROMIUM</c> or <c>CODEALTA_TEST_FIREFOX</c> names another program.</summary>
    public static InstalledBrowser? Find(BrowserEngine engine)
    {
        var variable = engine == BrowserEngine.Firefox ? "CODEALTA_TEST_FIREFOX" : "CODEALTA_TEST_CHROMIUM";
        var browsers = BrowserCatalog.Find(BrowserEnvironment.Current());
        if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } program && File.Exists(program))
        {
            return new InstalledBrowser(engine == BrowserEngine.Firefox ? "firefox" : "chromium", Path.GetFileNameWithoutExtension(program), engine, program, null, null, []);
        }

        string[] order = engine == BrowserEngine.Firefox ? ["firefox", "firefox-developer", "firefox-nightly"] : ["edge", "chrome", "chromium", "brave", "chrome-beta", "edge-beta"];
        return order.Select(id => browsers.FirstOrDefault(browser => browser.Id == id)).FirstOrDefault(static browser => browser is not null);
    }

    public static async Task<BrowserTestHost?> StartAsync(BrowserEngine engine)
    {
        if (Find(engine) is not { } browser) return null;
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-browser-tests", Guid.NewGuid().ToString("N")[..8])).FullName;
        var site = await TestSite.StartAsync();
        // A browser that starts cold on a busy CI machine can take more than the 30 seconds a desktop gives it.
        var options = new BrowserLaunchOptions { Executable = browser.ExecutablePath, ProfileDirectory = Path.Combine(folder, "profile"), Headless = true, StartTimeout = TimeSpan.FromMinutes(2) };
        IBrowserConnection connection;
        if (engine == BrowserEngine.Firefox)
        {
            connection = await Drivers.BiDi.BiDiBrowserConnection.LaunchAsync(browser, options, CancellationToken.None);
        }
        else
        {
            var launch = await ChromiumLauncher.LaunchAsync(options, CancellationToken.None);
            connection = await CdpBrowserConnection.ConnectAsync(browser, launch.Endpoint, launch.Process, CancellationToken.None);
        }

        return new BrowserTestHost(connection, site, folder);
    }

    /// <summary>A session of the tools in a context of its own, or in the windows of the browser's profile when not isolated.</summary>
    public BrowserSession NewSession(Func<Uri, CancellationToken, ValueTask<string?>>? checkNavigation = null, bool isolated = true)
    {
        var files = new BrowserFiles(Folder, [Folder], [Folder], Path.Combine(Folder, "tmp"));
        return new BrowserSession(new BrowserSessionOptions
        {
            OpenContext = cancellationToken => Connection.CreateContextAsync(new BrowserContextOptions { Isolated = isolated, DownloadDirectory = Path.Combine(Folder, "downloads") }, cancellationToken),
            Files = _ => ValueTask.FromResult(files),
            CheckNavigation = checkNavigation,
        });
    }

    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync();
        await Site.DisposeAsync();
        try { Directory.Delete(Folder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Calls of tools in tests.</summary>
internal static class BrowserToolTesting
{
    /// <summary>
    /// How long a test waits for a call that may start the browser, or open its first context: the start of the browser,
    /// which may take its two minutes, and a command of the browser after it. Measured 2026-11-06 on Windows: Firefox opens
    /// its BiDi session in about 1 s alone, in 5 to 18 s under a load of a busy pwsh per core, and in more than 60 s in a full
    /// validation, where a deadline of 60 s failed the call. The deadline is only there to end a test that hangs.
    /// </summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromMinutes(3);

    public static async Task<BrowserToolResult> CallAsync(this BrowserSession session, string tool, string arguments = "{}")
    {
        using var document = JsonDocument.Parse(arguments);
        using var timeout = new CancellationTokenSource(Deadline);
        return await session.CallAsync(tool, document.RootElement, timeout.Token);
    }

    public static async Task<string> OkAsync(this BrowserSession session, string tool, string arguments = "{}")
    {
        var result = await session.CallAsync(tool, arguments);
        Assert.IsFalse(result.IsError, $"{tool} failed: {result.Text}");
        return result.Text;
    }

    /// <summary>The uid of the first line of a snapshot that holds a text.</summary>
    public static string Uid(string snapshot, string text)
    {
        foreach (var line in snapshot.Split('\n'))
        {
            if (!line.Contains(text, StringComparison.Ordinal)) continue;
            var start = line.IndexOf("uid=", StringComparison.Ordinal) + 4;
            var end = line.IndexOf(' ', start);
            return end < 0 ? line[start..].Trim() : line[start..end];
        }

        Assert.Fail($"No line of the snapshot holds \"{text}\":\n{snapshot}");
        return string.Empty;
    }
}
