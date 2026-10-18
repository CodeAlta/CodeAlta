using System.Diagnostics;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class AttachedDocumentConverterTests
{
    [TestMethod]
    public void TheConverterIsThePinnedPackageRunByDnx()
    {
        var start = AttachedDocumentConverter.CreateStartInfo("/sdk/dotnet", "/attachments/plan.docx", "/attachments/plan.docx.md.tmp");
        Assert.AreEqual("/sdk/dotnet", start.FileName);
        CollectionAssert.AreEqual(new[] { "dnx", "ElBruno.MarkItDotNet.Cli@0.9.2", "--yes", "--", "/attachments/plan.docx", "--quiet", "--output", "/attachments/plan.docx.md.tmp" },
            start.ArgumentList.ToArray());
        Assert.IsFalse(start.UseShellExecute);
        Assert.IsTrue(start.CreateNoWindow);
        Assert.AreEqual("1", start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"]);
    }

    [TestMethod]
    public async Task ConvertAsync_WritesTheMarkdownBesideTheDocumentOnce()
    {
        using var folder = new Folder();
        var document = folder.File("plan.docx");
        var runs = 0;
        var converter = new AttachedDocumentConverter("dotnet", (start, timeout, _) =>
        {
            runs++;
            Assert.AreEqual(AttachedDocumentConverter.Timeout, timeout);
            File.WriteAllText(Output(start), "# Plan");
            return Task.FromResult(0);
        });

        Assert.AreEqual(document + ".md", await converter.ConvertAsync(document, CancellationToken.None));
        Assert.AreEqual("# Plan", await File.ReadAllTextAsync(document + ".md"));
        Assert.AreEqual(document + ".md", await converter.ConvertAsync(document, CancellationToken.None));
        Assert.AreEqual(1, runs, "A converted document is not converted again.");
        CollectionAssert.AreEquivalent(new[] { document, document + ".md" }, Directory.GetFiles(folder.Root));
    }

    [TestMethod]
    public async Task ConvertAsync_LeavesNothingWhenTheConversionFailsOrCannotRun()
    {
        using var folder = new Folder();
        var document = folder.File("report.pdf");
        var failed = new AttachedDocumentConverter("dotnet", (start, _, _) => { File.WriteAllText(Output(start), "partial"); return Task.FromResult(1); });
        Assert.IsNull(await failed.ConvertAsync(document, CancellationToken.None));
        var missing = new AttachedDocumentConverter("dotnet", (_, _, _) => throw new System.ComponentModel.Win32Exception("not found"));
        Assert.IsNull(await missing.ConvertAsync(document, CancellationToken.None));
        var noDotnet = new AttachedDocumentConverter(null, (_, _, _) => throw new AssertFailedException("Nothing runs without a dotnet."));
        Assert.IsNull(await noDotnet.ConvertAsync(document, CancellationToken.None));
        CollectionAssert.AreEqual(new[] { document }, Directory.GetFiles(folder.Root));

        var canceled = new AttachedDocumentConverter("dotnet", (_, _, token) => Task.FromCanceled<int>(token));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => canceled.ConvertAsync(document, cancellation.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => failed.ConvertAsync("relative.pdf", CancellationToken.None));
    }

    private static string Output(ProcessStartInfo start)
    {
        var arguments = start.ArgumentList.ToArray();
        return arguments[Array.IndexOf(arguments, "--output") + 1];
    }

    private sealed class Folder : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("codealta-converter-").FullName;

        public string File(string name)
        {
            var path = Path.Combine(Root, name);
            System.IO.File.WriteAllBytes(path, [1, 2, 3]);
            return path;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
