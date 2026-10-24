using System.Security.Cryptography;
using System.Text;
using NeoAstra;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopPluginAppFilesTests
{
    [TestMethod]
    public async Task AFileOfTheManifest_IsGiven_AndNothingElse()
    {
        using var temp = new TempFolder();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "assets")).FullName;
        Write(root, "mobile.html", "<!doctype html><title>Phone</title>");
        Write(root, "assets/mobile-1a2b3c4d.js", "export const phone = 1;");
        Write(root, "secret.txt", "not listed");
        var manifest = Manifest(root, ("assets/mobile-1a2b3c4d.js", "text/javascript; charset=utf-8", "public,max-age=31536000,immutable"), ("mobile.html", "text/html; charset=utf-8", "no-cache"));
        var files = new DesktopPluginAppFiles(root, manifest);

        Assert.IsTrue(files.HasFiles);
        var page = await files.ReadAsync("mobile.html");
        Assert.IsNotNull(page);
        Assert.AreEqual(("mobile.html", "text/html; charset=utf-8", false), (page.Path, page.ContentType, page.Immutable));
        Assert.AreEqual("<!doctype html><title>Phone</title>", Encoding.UTF8.GetString(page.Content.Span));
        var script = await files.ReadAsync("assets/mobile-1a2b3c4d.js");
        Assert.IsTrue(script?.Immutable, "A hashed name never changes.");

        Assert.IsNull(await files.ReadAsync("secret.txt"), "A file the manifest does not list is no file of the page.");
        Assert.IsNull(await files.ReadAsync("assets/../secret.txt"));
        Assert.IsNull(await files.ReadAsync("/mobile.html"), "Paths are relative to the root of the page.");

        // A file that changed since the build is not the one the build made.
        Write(root, "mobile.html", "<!doctype html><script>alert(1)</script>");
        Assert.IsNull(await files.ReadAsync("mobile.html"));
        File.Delete(Path.Combine(root, "assets", "mobile-1a2b3c4d.js"));
        Assert.IsNull(await files.ReadAsync("assets/mobile-1a2b3c4d.js"));
    }

    private static void Write(string root, string path, string content)
    {
        var file = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
    }

    private static NeoAssetManifest Manifest(string root, params (string Path, string ContentType, string CacheControl)[] assets)
    {
        var entries = assets.OrderBy(static asset => asset.Path, StringComparer.Ordinal).Select(asset =>
        {
            var bytes = File.ReadAllBytes(Path.Combine(root, asset.Path.Replace('/', Path.DirectorySeparatorChar)));
            return new NeoAssetEntry(asset.Path, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), asset.ContentType, asset.CacheControl);
        }).ToArray();
        return new NeoAssetManifest(1, "mobile.html", "mobile.html", "app://codealta", "default-src 'self'", "no-referrer", [], ["/api", "/_neoastra"], entries);
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codealta-app-files-" + Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }
}
