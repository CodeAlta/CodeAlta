using CodeAlta.Agent;
using CodeAlta.Agent.Runtime.Tools;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AgentApplyPatchTests
{
    [TestMethod]
    public async Task Apply_UpdatesFileAndPreservesTrailingNewline()
    {
        using var temp = TestTempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "sample.txt");
        await File.WriteAllTextAsync(filePath, "alpha\r\nbeta\r\n").ConfigureAwait(false);

        var result = AgentApplyPatch.Apply(
            """
            *** Begin Patch
            *** Update File: sample.txt
            @@
             alpha
            -beta
            +gamma
            *** End Patch
            """,
            temp.Path);

        Assert.IsTrue(result.Success);
        Assert.AreEqual("alpha\r\ngamma\r\n", await File.ReadAllTextAsync(filePath).ConfigureAwait(false));
        StringAssert.Contains(AssertTextResult(result), "M sample.txt");
    }

    [TestMethod]
    public async Task Apply_ReturnsFailureWhenHunkContextDoesNotMatch()
    {
        using var temp = TestTempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "sample.txt");
        await File.WriteAllTextAsync(filePath, "alpha" + Environment.NewLine).ConfigureAwait(false);

        var result = AgentApplyPatch.Apply(
            """
            *** Begin Patch
            *** Update File: sample.txt
            @@
            -beta
            +gamma
            *** End Patch
            """,
            temp.Path);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "The hunk context was not found");
        Assert.AreEqual("alpha" + Environment.NewLine, await File.ReadAllTextAsync(filePath).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task Apply_UsesAnchorTextToTargetTheIntendedRegion()
    {
        using var temp = TestTempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "sample.cs");
        await File.WriteAllTextAsync(
                filePath,
                string.Join(
                    Environment.NewLine,
                    [
                        "public int First()",
                        "{",
                        "    return 1;",
                        "}",
                        string.Empty,
                        "public int Second()",
                        "{",
                        "    return 1;",
                        "}",
                    ]) + Environment.NewLine)
            .ConfigureAwait(false);

        var result = AgentApplyPatch.Apply(
            """
            *** Begin Patch
            *** Update File: sample.cs
            @@ public int Second()
             {
            -    return 1;
            +    return 2;
             }
            *** End Patch
            """,
            temp.Path);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(
            string.Join(
                Environment.NewLine,
                [
                    "public int First()",
                    "{",
                    "    return 1;",
                    "}",
                    string.Empty,
                    "public int Second()",
                    "{",
                    "    return 2;",
                    "}",
                ]) + Environment.NewLine,
            await File.ReadAllTextAsync(filePath).ConfigureAwait(false));
    }

    [TestMethod]
    public void Apply_RejectsSingleAtAnchorHeaderWithHelpfulMessage()
    {
        var result = AgentApplyPatch.Apply(
            """
            *** Begin Patch
            *** Update File: sample.txt
            @ partial void OnDocumentChanged(object? value)
            *** End Patch
            """,
            Path.GetTempPath());

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "Expected a hunk header");
        StringAssert.Contains(result.Error, "Use '@@' before each changed region");
    }

    [TestMethod]
    public async Task Apply_UsesWhitespaceTolerantMatchingWithoutRewritingContext()
    {
        using var temp = TestTempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "sample.txt");
        await File.WriteAllTextAsync(
                filePath,
                $"alpha  {Environment.NewLine}beta{Environment.NewLine}")
            .ConfigureAwait(false);

        var result = AgentApplyPatch.Apply(
            """
            *** Begin Patch
            *** Update File: sample.txt
            @@
             alpha
            -beta
            +gamma
            *** End Patch
            """,
            temp.Path);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(
            $"alpha  {Environment.NewLine}gamma{Environment.NewLine}",
            await File.ReadAllTextAsync(filePath).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task Apply_ToleratesWhitespacePaddedDirectiveLines()
    {
        using var temp = TestTempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "sample.txt");
        await File.WriteAllTextAsync(filePath, "old" + Environment.NewLine).ConfigureAwait(false);

        var result = AgentApplyPatch.Apply(
            """
             *** Begin Patch
              *** Update File: sample.txt
            @@
            -old
            +new
             *** End Patch
            """,
            temp.Path);

        Assert.IsTrue(result.Success);
        Assert.AreEqual("new" + Environment.NewLine, await File.ReadAllTextAsync(filePath).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task Apply_TreatsBlankLinesInsideHunksAsBlankContext()
    {
        using var temp = TestTempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "sample.txt");
        await File.WriteAllTextAsync(
                filePath,
                $"alpha{Environment.NewLine}{Environment.NewLine}beta{Environment.NewLine}")
            .ConfigureAwait(false);

        var result = AgentApplyPatch.Apply(
            """
            *** Begin Patch
            *** Update File: sample.txt
            @@
             alpha

            -beta
            +gamma
            *** End Patch
            """,
            temp.Path);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(
            $"alpha{Environment.NewLine}{Environment.NewLine}gamma{Environment.NewLine}",
            await File.ReadAllTextAsync(filePath).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task Apply_AllowsMultipleAnchorHeadersBeforeOneHunk()
    {
        using var temp = TestTempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "sample.cs");
        await File.WriteAllTextAsync(
                filePath,
                string.Join(
                    Environment.NewLine,
                    [
                        "class Example",
                        "{",
                        "    void First()",
                        "    {",
                        "        var value = 1;",
                        "    }",
                        string.Empty,
                        "    void Second()",
                        "    {",
                        "        var value = 1;",
                        "    }",
                        "}",
                    ]) + Environment.NewLine)
            .ConfigureAwait(false);

        var result = AgentApplyPatch.Apply(
            """
            *** Begin Patch
            *** Update File: sample.cs
            @@ class Example
            @@ void Second()
             {
            -        var value = 1;
            +        var value = 2;
             }
            *** End Patch
            """,
            temp.Path);

        Assert.IsTrue(result.Success);
        StringAssert.Contains(await File.ReadAllTextAsync(filePath).ConfigureAwait(false), "var value = 2;");
    }

    [TestMethod]
    public async Task Apply_PrefersEndOfFileWhenRequested()
    {
        using var temp = TestTempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "sample.txt");
        await File.WriteAllTextAsync(
                filePath,
                string.Join(
                    Environment.NewLine,
                    [
                        "marker",
                        "tail",
                        "marker",
                        "tail",
                    ]) + Environment.NewLine)
            .ConfigureAwait(false);

        var result = AgentApplyPatch.Apply(
            """
            *** Begin Patch
            *** Update File: sample.txt
            @@
             marker
            +done
             tail
            *** End of File
            *** End Patch
            """,
            temp.Path);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(
            string.Join(
                Environment.NewLine,
                [
                    "marker",
                    "tail",
                    "marker",
                    "done",
                    "tail",
                ]) + Environment.NewLine,
            await File.ReadAllTextAsync(filePath).ConfigureAwait(false));
    }

    [TestMethod]
    public void Apply_ReturnsFailureForInvalidPatchEnvelope()
    {
        var result = AgentApplyPatch.Apply(
            """
            *** Update File: sample.txt
            @@
            -alpha
            +beta
            *** End Patch
            """,
            Path.GetTempPath());

        Assert.IsFalse(result.Success);
        Assert.AreEqual("Patch input must start with '*** Begin Patch'.", result.Error);
    }

    [TestMethod]
    public async Task Apply_AppendsTrailingNewlineWhenUpdatingAFileWithoutOne()
    {
        using var temp = TestTempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "sample.txt");
        await File.WriteAllTextAsync(filePath, "no newline").ConfigureAwait(false);

        var result = AgentApplyPatch.Apply(
            """
            *** Begin Patch
            *** Update File: sample.txt
            @@
            -no newline
            +first
            +second
            *** End Patch
            """,
            temp.Path);

        Assert.IsTrue(result.Success);
        Assert.AreEqual("first\nsecond\n", await File.ReadAllTextAsync(filePath).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task GetTouchedPaths_ReturnsSourceAndDestinationPathsForMove()
    {
        using var temp = TestTempDirectory.Create();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "src.txt"), "alpha" + Environment.NewLine).ConfigureAwait(false);

        var paths = AgentApplyPatch.GetTouchedPaths(
            """
            *** Begin Patch
            *** Update File: src.txt
            *** Move to: nested/dst.txt
            @@
            -alpha
            +beta
            *** End Patch
            """,
            temp.Path);

        CollectionAssert.AreEquivalent(
            new[]
            {
                Path.Combine(temp.Path, "src.txt"),
                Path.Combine(temp.Path, "nested", "dst.txt"),
            },
            paths.ToArray());
    }

    [TestMethod]
    public async Task Apply_AllowsMoveOnlyRenames()
    {
        using var temp = TestTempDirectory.Create();
        var sourcePath = Path.Combine(temp.Path, "src.txt");
        var destinationPath = Path.Combine(temp.Path, "nested", "dst.txt");
        await File.WriteAllTextAsync(sourcePath, "alpha" + Environment.NewLine).ConfigureAwait(false);

        var result = AgentApplyPatch.Apply(
            """
            *** Begin Patch
            *** Update File: src.txt
            *** Move to: nested/dst.txt
            *** End Patch
            """,
            temp.Path);

        Assert.IsTrue(result.Success);
        Assert.IsFalse(File.Exists(sourcePath));
        Assert.AreEqual("alpha" + Environment.NewLine, await File.ReadAllTextAsync(destinationPath).ConfigureAwait(false));
        StringAssert.Contains(AssertTextResult(result), "R src.txt -> nested/dst.txt");
    }

    [TestMethod]
    public async Task Apply_RejectsBinaryFiles()
    {
        using var temp = TestTempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "sample.bin");
        await File.WriteAllBytesAsync(filePath, [0x41, 0x00, 0x42]).ConfigureAwait(false);

        var result = AgentApplyPatch.Apply(
            """
            *** Begin Patch
            *** Update File: sample.bin
            @@
            -A
            +B
            *** End Patch
            """,
            temp.Path);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "binary file");
        CollectionAssert.AreEqual(
            new byte[] { 0x41, 0x00, 0x42 },
            await File.ReadAllBytesAsync(filePath).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task Apply_AllowsRelativeParentPathsOutsideWorkingDirectory()
    {
        using var temp = TestTempDirectory.Create();
        var workingDirectory = Path.Combine(temp.Path, "repo");
        Directory.CreateDirectory(workingDirectory);
        var outsidePath = Path.Combine(temp.Path, "outside.txt");

        var result = AgentApplyPatch.Apply(
            """
            *** Begin Patch
            *** Add File: ../outside.txt
            +hello
            *** End Patch
            """,
            workingDirectory);

        Assert.IsTrue(result.Success);
        Assert.AreEqual("hello" + Environment.NewLine, await File.ReadAllTextAsync(outsidePath).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task Apply_AllowsSiblingPrefixOutsideWorkingDirectory()
    {
        using var temp = TestTempDirectory.Create();
        var workingDirectory = Path.Combine(temp.Path, "repo");
        Directory.CreateDirectory(workingDirectory);
        var outsidePath = Path.Combine(temp.Path, "repo2", "outside.txt");

        var result = AgentApplyPatch.Apply(
            """
            *** Begin Patch
            *** Add File: ../repo2/outside.txt
            +hello
            *** End Patch
            """,
            workingDirectory);

        Assert.IsTrue(result.Success);
        Assert.AreEqual("hello" + Environment.NewLine, await File.ReadAllTextAsync(outsidePath).ConfigureAwait(false));
    }

    private static string AssertTextResult(AgentToolResult result)
        => Assert.IsInstanceOfType<AgentToolResultItem.Text>(result.Items.Single()).Value;

    private sealed class TestTempDirectory(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TestTempDirectory Create()
        {
            var path = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "local-agent-apply-patch-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TestTempDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
