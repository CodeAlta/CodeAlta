using System.Collections.Concurrent;
using System.Text;

namespace CodeAlta.Catalog.Tests;

/// <summary>The files of the catalog that others read while they are written: a reader finds the old text or the new one.</summary>
[TestClass]
public sealed class SharedTextFileTests
{
    [TestMethod]
    public void AMissingFile_IsNoText_AndAReadOfItSaysSo()
    {
        using var folder = new TempDirectory();
        var path = Path.Combine(folder.Path, "config.toml");

        Assert.IsNull(SharedTextFile.TryReadAllText(path));
        Assert.IsNull(SharedTextFile.OpenRead(path));
        Assert.ThrowsExactly<FileNotFoundException>(() => SharedTextFile.ReadAllText(path));
        Assert.ThrowsExactly<DirectoryNotFoundException>(() => SharedTextFile.ReadAllText(Path.Combine(folder.Path, "missing", "config.toml")));
    }

    [TestMethod]
    public async Task AWrite_ReplacesTheText_InTheEncodingAsked()
    {
        using var folder = new TempDirectory();
        var path = Path.Combine(folder.Path, "task.md");

        SharedTextFile.WriteAllText(path, "first");
        Assert.AreEqual("first", SharedTextFile.ReadAllText(path));
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("first"), await File.ReadAllBytesAsync(path), "no byte order mark by default");

        SharedTextFile.WriteAllText(path, "second", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        CollectionAssert.AreEqual(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("second")).ToArray(), await File.ReadAllBytesAsync(path));
        Assert.AreEqual("second", await SharedTextFile.ReadAllTextAsync(path, default));

        await SharedTextFile.WriteAllTextAsync(path, "third", default);
        Assert.AreEqual("third", await SharedTextFile.TryReadAllTextAsync(path, default));
        Assert.AreEqual(1, Directory.GetFiles(folder.Path).Length, "nothing is left beside the file");
    }

    [TestMethod]
    public void AWriteThroughALink_ReplacesItsTarget_AndKeepsTheLink()
    {
        using var folder = new TempDirectory();
        var target = Path.Combine(folder.Path, "dotfiles", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "kept with the other files of the user");
        var link = Path.Combine(folder.Path, "config.toml");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive("This computer does not let the test make links.");
        }

        SharedTextFile.WriteAllText(link, "written");

        Assert.IsNotNull(new FileInfo(link).LinkTarget, "the link stays a link");
        Assert.AreEqual("written", File.ReadAllText(target));
    }

    [TestMethod]
    public void AWrite_KeepsTheModeOfTheFile()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix modes are not on Windows.");
            return;
        }

        using var folder = new TempDirectory();
        var path = Path.Combine(folder.Path, "config.toml");
        File.WriteAllText(path, "private");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        SharedTextFile.WriteAllText(path, "still private");

        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [TestMethod]
    public async Task AReadDuringWrites_FindsAWholeText()
    {
        using var folder = new TempDirectory();
        var path = Path.Combine(folder.Path, "project.md");
        SharedTextFile.WriteAllText(path, Text(0));
        using var stop = new CancellationTokenSource();
        var failures = new ConcurrentQueue<string>();
        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var text = SharedTextFile.ReadAllText(path);
                    if (!text.StartsWith("---\n", StringComparison.Ordinal) || !text.EndsWith("\n---\n", StringComparison.Ordinal)) failures.Enqueue($"part of a text ({text.Length} characters)");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    failures.Enqueue($"{exception.GetType().Name}: {exception.Message}");
                }
            }
        })).ToArray();

        for (var version = 1; version <= 150; version++) SharedTextFile.WriteAllText(path, Text(version));

        await stop.CancelAsync();
        await Task.WhenAll(readers);
        Assert.IsEmpty(failures, string.Join(" | ", failures.Distinct().Take(3)));
        Assert.AreEqual(Text(150), SharedTextFile.ReadAllText(path));

        static string Text(int version) => "---\n" + new string('x', 1000 + version * 97) + "\n---\n";
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("codealta-shared-text-").FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
