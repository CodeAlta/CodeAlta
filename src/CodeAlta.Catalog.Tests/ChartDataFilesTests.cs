using System.Diagnostics;
using System.Text;
using CodeAlta.Catalog;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class ChartDataFilesTests
{
    [TestMethod]
    [DataRow("out/results.csv", "ok", "out/results.csv")]
    [DataRow("./out/results.tsv", "ok", "out/results.tsv")]
    [DataRow("out\\data\\points.json", "ok", "out/data/points.json")]
    [DataRow("world.TOPOJSON", "ok", "world.TOPOJSON")]
    [DataRow("shapes.geojson", "ok", "shapes.geojson")]
    [DataRow("../secret.csv", "outside", null)]
    [DataRow("out/../../secret.csv", "outside", null)]
    [DataRow("out/./a.csv", "outside", null)]
    [DataRow("/etc/data.csv", "outside", null)]
    [DataRow("\\\\server\\share\\a.csv", "outside", null)]
    [DataRow("C:/data/a.csv", "outside", null)]
    [DataRow("~/a.csv", "outside", null)]
    [DataRow("https://example.com/a.csv", "outside", null)]
    [DataRow("a.csv:stream", "outside", null)]
    [DataRow("a.csv?x=1", "invalid_request", null)]
    [DataRow("*.csv", "invalid_request", null)]
    [DataRow("out./a.csv", "invalid_request", null)]
    [DataRow(" a.csv", "invalid_request", null)]
    [DataRow("", "invalid_request", null)]
    [DataRow("notes.txt", "not_data", null)]
    [DataRow(".env", "not_data", null)]
    public void APath_IsReadOnlyBelowTheFolder_AndOnlyForData(string path, string status, string? parts)
    {
        Assert.AreEqual(status, ChartDataFiles.Parse(path, out var found));
        Assert.AreEqual(parts, found.Length == 0 ? null : string.Join('/', found));
    }

    [TestMethod]
    public async Task AFile_IsReadBelowTheFolder_FromTheFolderOfADocument()
    {
        using var folder = new TempFolder();
        folder.Write("out/results.csv", "name,value\na,1\n");
        folder.Write("docs/data/points.json", "[1,2]");
        var bom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("x\n1")).ToArray();
        File.WriteAllBytes(Path.Combine(folder.Root, "out", "bom.csv"), bom);

        Assert.AreEqual(new ChartDataRead("ok", "name,value\na,1\n"), await ChartDataFiles.ReadAsync(folder.Root, null, "out/results.csv", default));
        Assert.AreEqual(new ChartDataRead("ok", "name,value\na,1\n"), await ChartDataFiles.ReadAsync(folder.Root, null, "out\\results.csv", default));
        Assert.AreEqual(new ChartDataRead("ok", "[1,2]"), await ChartDataFiles.ReadAsync(folder.Root, "docs", "data/points.json", default));
        Assert.AreEqual(new ChartDataRead("ok", "x\n1"), await ChartDataFiles.ReadAsync(folder.Root, null, "out/bom.csv", default), "a byte order mark is no data");
        Assert.AreEqual("not_found", (await ChartDataFiles.ReadAsync(folder.Root, null, "out/missing.csv", default)).Status);
        Assert.AreEqual("not_found", (await ChartDataFiles.ReadAsync(folder.Root, null, "missing/results.csv", default)).Status);
        // A document reads nothing above its project, even through its own folder.
        Assert.AreEqual("outside", (await ChartDataFiles.ReadAsync(folder.Root, "docs", "../out/results.csv", default)).Status);
        Assert.AreEqual("outside", (await ChartDataFiles.ReadAsync(folder.Root, "../elsewhere", "results.csv", default)).Status);
        Assert.AreEqual("no_project", (await ChartDataFiles.ReadAsync(null, null, "out/results.csv", default)).Status);
        Assert.AreEqual("no_project", (await ChartDataFiles.ReadAsync("relative", null, "out/results.csv", default)).Status);
        Assert.AreEqual("no_project", (await ChartDataFiles.ReadAsync(Path.Combine(folder.Root, "gone"), null, "out/results.csv", default)).Status);
    }

    [TestMethod]
    public async Task AFile_TooLargeOrNotText_IsNotRead()
    {
        using var folder = new TempFolder();
        File.WriteAllBytes(folder.Write("big.csv", ""), new byte[ChartDataFiles.MaximumBytes + 1]);
        File.WriteAllBytes(folder.Write("limit.csv", ""), Enumerable.Repeat((byte)'a', ChartDataFiles.MaximumBytes).ToArray());
        File.WriteAllBytes(folder.Write("binary.json", ""), [0x7B, 0x00, 0x7D]);
        Directory.CreateDirectory(Path.Combine(folder.Root, "folder.csv"));

        Assert.AreEqual("too_large", (await ChartDataFiles.ReadAsync(folder.Root, null, "big.csv", default)).Status);
        Assert.AreEqual(ChartDataFiles.MaximumBytes, (await ChartDataFiles.ReadAsync(folder.Root, null, "limit.csv", default)).Text!.Length);
        Assert.AreEqual("not_data", (await ChartDataFiles.ReadAsync(folder.Root, null, "binary.json", default)).Status);
        Assert.AreEqual("not_found", (await ChartDataFiles.ReadAsync(folder.Root, null, "folder.csv", default)).Status);
    }

    [TestMethod]
    public async Task ALinkOrAJunction_IsFollowedOnlyToAPlaceInsideTheFolder()
    {
        using var folder = new TempFolder();
        var project = Directory.CreateDirectory(Path.Combine(folder.Root, "project")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(folder.Root, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "secret.csv"), "secret\n1");
        File.WriteAllText(folder.Write("project/real/data.csv", ""), "a\n1");
        if (!TryLinkFolder(Path.Combine(project, "escape"), outside) || !TryLinkFolder(Path.Combine(project, "inside"), Path.Combine(project, "real")))
        {
            Assert.Inconclusive("Folder links cannot be made here.");
        }

        Assert.AreEqual("outside", (await ChartDataFiles.ReadAsync(project, null, "escape/secret.csv", default)).Status);
        Assert.AreEqual(new ChartDataRead("ok", "a\n1"), await ChartDataFiles.ReadAsync(project, null, "inside/data.csv", default));
        // The folder a document is in is checked as well.
        Assert.AreEqual("outside", (await ChartDataFiles.ReadAsync(project, "escape", "secret.csv", default)).Status);
    }

    [TestMethod]
    public void TheFilesOfAMessage_AreTheDataItsChartBlocksName()
    {
        var text = """
            A chart:

            ```vega-lite
            { "data": { "url": "out/a.csv" }, "layer": [ { "mark": "bar" }, { "data": { "url": "out/b.tsv" }, "mark": "rule" } ],
              "transform": [ { "lookup": "id", "from": { "data": { "url": "names.json" }, "key": "id" } } ] }
            ```

            ~~~~VegaLite
            { "data": { "url": "out/a.csv" }, "mark": { "type": "image", "url": "image.png" } }
            ~~~~

            ```json
            { "data": { "url": "not-a-chart.csv" } }
            ```

            ```vega-lite
            { not json "data": { "url": "broken.csv" } }
            ```
            """;
        // Whatever ends its lines: a message written on Windows names the same files.
        foreach (var ending in new[] { "\n", "\r\n" })
            CollectionAssert.AreEqual(new[] { "out/a.csv", "out/b.tsv", "names.json" }, ChartDataFiles.NamedIn(text.ReplaceLineEndings(ending)).ToArray(), ending == "\n" ? "LF" : "CRLF");
        Assert.AreEqual(0, ChartDataFiles.NamedIn(null).Count);
        Assert.AreEqual(0, ChartDataFiles.NamedIn("out/a.csv").Count);
    }

    private static bool TryLinkFolder(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }

        // A junction needs no privilege on Windows, where a symbolic link may.
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target]) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    private sealed class TempFolder : IDisposable
    {
        public string Root { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CodeAlta-chart-data-" + Guid.NewGuid().ToString("N"))).FullName;

        public string Write(string relative, string text)
        {
            var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            return path;
        }

        public void Dispose()
        {
            // The links go first, alone: a recursive delete must never reach through one.
            foreach (var entry in Directory.EnumerateFileSystemEntries(Root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }).ToList())
            {
                if (new DirectoryInfo(entry) is { LinkTarget: not null } link) link.Delete();
            }

            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
