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
        StringAssert.Contains(result.Error, "Failed to find expected lines in sample.txt (hunk at patch line 3):");
        StringAssert.Contains(result.Error, "No file was changed.");
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

    [TestMethod]
    public void Apply_VerifiesEveryOperationBeforeWritingAny()
    {
        using var temp = TestTempDirectory.Create();
        WriteText(temp, "first.txt", "alpha\n");
        WriteText(temp, "second.txt", "beta\n");

        var result = AgentApplyPatch.Apply(
            Patch(
                "*** Begin Patch",
                "*** Add File: created.txt",
                "+hello",
                "*** Update File: first.txt",
                "@@",
                "-alpha",
                "+ALPHA",
                "*** Update File: second.txt",
                "@@",
                "-missing",
                "+gamma",
                "*** End Patch"),
            temp.Path);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "Failed to find expected lines in second.txt (hunk at patch line 9):");
        StringAssert.Contains(result.Error, "No file was changed.");
        Assert.IsFalse(File.Exists(Path.Combine(temp.Path, "created.txt")));
        Assert.AreEqual("alpha\n", ReadText(temp, "first.txt"));
        Assert.AreEqual("beta\n", ReadText(temp, "second.txt"));
    }

    [TestMethod]
    public void Apply_ListsWhatWasAppliedWhenAWriteFailsPartWay()
    {
        using var temp = TestTempDirectory.Create();
        WriteText(temp, "existing.txt", "alpha\n");
        WriteText(temp, "blocker", "a file where a folder is needed\n");
        WriteText(temp, "last.txt", "last\n");

        var result = AgentApplyPatch.Apply(
            Patch(
                "*** Begin Patch",
                "*** Add File: first.txt",
                "+hello",
                "*** Update File: existing.txt",
                "@@",
                "-alpha",
                "+beta",
                "*** Add File: blocker/inner.txt",
                "+never written",
                "*** Delete File: last.txt",
                "*** End Patch"),
            temp.Path);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "Failed to write 'blocker/inner.txt'");
        StringAssert.Contains(result.Error, "The patch was applied only in part. Applied:\nA first.txt\nM existing.txt\nNot applied:\nA blocker/inner.txt\nD last.txt".Replace("\n", Environment.NewLine, StringComparison.Ordinal));
        Assert.AreEqual("beta\n", ReadText(temp, "existing.txt"));
        Assert.IsTrue(File.Exists(Path.Combine(temp.Path, "last.txt")));
    }

    [TestMethod]
    public void Apply_RejectsAPatchWithoutOperations()
    {
        using var temp = TestTempDirectory.Create();

        var result = AgentApplyPatch.Apply(Patch("*** Begin Patch", "*** End Patch"), temp.Path);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "The patch contains no file operation.");
    }

    [TestMethod]
    public void Apply_RejectsTwoOperationsOnTheSameFile()
    {
        using var temp = TestTempDirectory.Create();
        WriteText(temp, "sample.txt", "one\ntwo\n");

        var result = AgentApplyPatch.Apply(
            Patch(
                "*** Begin Patch",
                "*** Update File: sample.txt",
                "@@",
                "-one",
                "+ONE",
                "*** Update File: sample.txt",
                "@@",
                "-two",
                "+TWO",
                "*** End Patch"),
            temp.Path);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "multiple operations target 'sample.txt'");
        Assert.AreEqual("one\ntwo\n", ReadText(temp, "sample.txt"));
    }

    [TestMethod]
    public void Apply_ReplacesAFileDeletedEarlierInThePatch()
    {
        using var temp = TestTempDirectory.Create();
        WriteText(temp, "sample.txt", "old\n");

        var result = AgentApplyPatch.Apply(
            Patch(
                "*** Begin Patch",
                "*** Delete File: sample.txt",
                "*** Add File: sample.txt",
                "+new",
                "*** End Patch"),
            temp.Path);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("new\n", ReadText(temp, "sample.txt"));
    }

    [TestMethod]
    public void Apply_RefusesToAddAnExistingFileAndSaysWhatToDo()
    {
        using var temp = TestTempDirectory.Create();
        WriteText(temp, "sample.txt", "old\n");

        var result = AgentApplyPatch.Apply(
            Patch("*** Begin Patch", "*** Add File: sample.txt", "+new", "*** End Patch"),
            temp.Path);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "Cannot add 'sample.txt' because it already exists.");
        StringAssert.Contains(result.Error, "'*** Update File: sample.txt'");
        StringAssert.Contains(result.Error, "'*** Delete File: sample.txt' before '*** Add File: sample.txt'");
        Assert.AreEqual("old\n", ReadText(temp, "sample.txt"));
    }

    [TestMethod]
    public void Apply_RefusesToMoveOntoAnExistingFileUnlessItIsDeletedFirst()
    {
        using var temp = TestTempDirectory.Create();
        WriteText(temp, "source.txt", "from\n");
        WriteText(temp, "target.txt", "existing\n");
        const string Move = "*** Update File: source.txt\n*** Move to: target.txt\n@@\n-from\n+new";

        var refused = AgentApplyPatch.Apply(Patch("*** Begin Patch", Move, "*** End Patch"), temp.Path);

        Assert.IsFalse(refused.Success);
        StringAssert.Contains(refused.Error, "because the destination already exists");
        StringAssert.Contains(refused.Error, "put '*** Delete File: target.txt' before '*** Update File: source.txt'");
        Assert.AreEqual("existing\n", ReadText(temp, "target.txt"));

        var replaced = AgentApplyPatch.Apply(
            Patch("*** Begin Patch", "*** Delete File: target.txt", Move, "*** End Patch"),
            temp.Path);

        Assert.IsTrue(replaced.Success, replaced.Error);
        Assert.IsFalse(File.Exists(Path.Combine(temp.Path, "source.txt")));
        Assert.AreEqual("new\n", ReadText(temp, "target.txt"));
    }

    [TestMethod]
    public void Apply_KeepsTheLineEndingOfEachLine()
    {
        using var temp = TestTempDirectory.Create();
        WriteText(temp, "mixed.txt", "one\r\ntwo\rthree\nfour\r\n");

        var result = AgentApplyPatch.Apply(
            Patch(
                "*** Begin Patch",
                "*** Update File: mixed.txt",
                "@@",
                " one",
                " two",
                "-three",
                "+THREE",
                "+inserted",
                " four",
                "*** End Patch"),
            temp.Path);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("one\r\ntwo\rTHREE\r\ninserted\r\nfour\r\n", ReadText(temp, "mixed.txt"));
    }

    [TestMethod]
    public void Apply_InsertsLinesWithTheFirstLineEndingOfTheFile()
    {
        using var temp = TestTempDirectory.Create();
        WriteText(temp, "sample.txt", "alpha\nbeta\r\ngamma\r\n");

        var result = AgentApplyPatch.Apply(
            Patch("*** Begin Patch", "*** Update File: sample.txt", "@@", " beta", "+inserted", " gamma", "*** End Patch"),
            temp.Path);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("alpha\nbeta\r\ninserted\ngamma\r\n", ReadText(temp, "sample.txt"));
    }

    [TestMethod]
    public void Apply_KeepsAUtf8ByteOrderMark()
    {
        using var temp = TestTempDirectory.Create();
        var path = Path.Combine(temp.Path, "bom.txt");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. "alpha\r\nbeta\r\n"u8]);

        var result = AgentApplyPatch.Apply(
            Patch("*** Begin Patch", "*** Update File: bom.txt", "@@", "-beta", "+gamma", "*** End Patch"),
            temp.Path);

        Assert.IsTrue(result.Success, result.Error);
        CollectionAssert.AreEqual(
            (byte[])[0xEF, 0xBB, 0xBF, .. "alpha\r\ngamma\r\n"u8],
            File.ReadAllBytes(path));
    }

    [TestMethod]
    public void Apply_AcceptsAFirstHunkWithoutHeaderAndWhitespaceAroundTheEnvelope()
    {
        using var temp = TestTempDirectory.Create();
        WriteText(temp, "sample.txt", "one\ntwo\n");

        var result = AgentApplyPatch.Apply(
            "\n  \n" + Patch("  *** Begin Patch", "*** Update File: sample.txt", " one", "-two", "+TWO", "*** End Patch  ") + "\n\n",
            temp.Path);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("one\nTWO\n", ReadText(temp, "sample.txt"));
    }

    [TestMethod]
    public void Apply_IgnoresABlankLineBeforeTheFirstHunkHeader()
    {
        using var temp = TestTempDirectory.Create();
        WriteText(temp, "sample.txt", "one\ntwo\n");

        var result = AgentApplyPatch.Apply(
            Patch("*** Begin Patch", "*** Update File: sample.txt", "", "@@", " one", "-two", "+TWO", "*** End Patch"),
            temp.Path);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("one\nTWO\n", ReadText(temp, "sample.txt"));
    }

    [TestMethod]
    [DataRow("<<'EOF'", "EOF")]
    [DataRow("<<EOF", "EOF")]
    [DataRow("<<\"PATCH\"", "PATCH")]
    [DataRow("apply_patch <<'EOF'", "EOF")]
    public void Apply_UnwrapsAHeredoc(string opener, string delimiter)
    {
        using var temp = TestTempDirectory.Create();

        var result = AgentApplyPatch.Apply(
            Patch(opener, "*** Begin Patch", "*** Add File: added.txt", "+hello", "*** End Patch", delimiter),
            temp.Path);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("hello\n", ReadText(temp, "added.txt"));
    }

    [TestMethod]
    public void Apply_RejectsTextAfterTheEndOfThePatch()
    {
        using var temp = TestTempDirectory.Create();

        var result = AgentApplyPatch.Apply(
            Patch(
                "*** Begin Patch",
                "*** Add File: first.txt",
                "+one",
                "*** End Patch",
                "*** Add File: second.txt",
                "+two",
                "*** End Patch"),
            temp.Path);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "Unexpected text after '*** End Patch' at patch line 5");
        Assert.IsFalse(File.Exists(Path.Combine(temp.Path, "first.txt")));
    }

    [TestMethod]
    public void Apply_TreatsAnIndentedHunkHeaderAsContext()
    {
        using var temp = TestTempDirectory.Create();
        WriteText(temp, "notes.diff", "old\n@@ a\nold\n");

        var result = AgentApplyPatch.Apply(
            Patch("*** Begin Patch", "*** Update File: notes.diff", "@@", " @@ a", "-old", "+new", "*** End Patch"),
            temp.Path);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("old\n@@ a\nnew\n", ReadText(temp, "notes.diff"));
    }

    [TestMethod]
    public void Apply_ToleratesAUnifiedDiffRangeInTheHunkHeader()
    {
        using var temp = TestTempDirectory.Create();
        WriteText(temp, "sample.txt", "void First()\n    run();\nvoid Second()\n    run();\n");

        var result = AgentApplyPatch.Apply(
            Patch("*** Begin Patch", "*** Update File: sample.txt", "@@ -3,2 +3,2 @@ void Second()", "-    run();", "+    stop();", "*** End Patch"),
            temp.Path);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("void First()\n    run();\nvoid Second()\n    stop();\n", ReadText(temp, "sample.txt"));
    }

    [TestMethod]
    public void Apply_MatchesTypographicPunctuationWithAscii()
    {
        using var temp = TestTempDirectory.Create();
        const string Title = "title \u201Cquoted\u201D \u2013 it\u2019s\u00A0here";
        WriteText(temp, "anchor.txt", $"body\n{Title}\nbody\n");
        WriteText(temp, "context.txt", $"intro\n{Title}\nbody\n");

        var result = AgentApplyPatch.Apply(
            Patch(
                "*** Begin Patch",
                "*** Update File: anchor.txt",
                "@@ title \"quoted\" - it's here",
                "-body",
                "+BODY",
                "*** Update File: context.txt",
                "@@",
                " title \"quoted\" - it's here",
                "-body",
                "+BODY",
                "*** End Patch"),
            temp.Path);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual($"body\n{Title}\nBODY\n", ReadText(temp, "anchor.txt"));
        Assert.AreEqual($"intro\n{Title}\nBODY\n", ReadText(temp, "context.txt"));
    }

    [TestMethod]
    public void Apply_RetriesWithoutATrailingEmptyContextLine()
    {
        using var temp = TestTempDirectory.Create();
        WriteText(temp, "sample.txt", "one\ntwo");

        var result = AgentApplyPatch.Apply(
            Patch("*** Begin Patch", "*** Update File: sample.txt", "@@", " one", "-two", "+TWO", "", "*** End Patch"),
            temp.Path);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("one\nTWO\n", ReadText(temp, "sample.txt"));
    }

    [TestMethod]
    public void Apply_DoesNotPlaceAHunkOnAnEarlierDuplicate()
    {
        using var temp = TestTempDirectory.Create();
        WriteText(temp, "sample.txt", "value = 1\nvalue = 1\nmarker\ntail\n");

        var result = AgentApplyPatch.Apply(
            Patch(
                "*** Begin Patch",
                "*** Update File: sample.txt",
                "@@",
                "-marker",
                "+MARKER",
                "@@",
                "-value = 1",
                "+value = 2",
                "*** End Patch"),
            temp.Path);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "appear 2 times before it");
        Assert.AreEqual("value = 1\nvalue = 1\nmarker\ntail\n", ReadText(temp, "sample.txt"));
    }

    [TestMethod]
    public void Apply_PlacesAnOutOfOrderHunkOnItsOnlyEarlierMatch()
    {
        using var temp = TestTempDirectory.Create();
        WriteText(temp, "sample.txt", "first\nsecond\nthird\n");

        var result = AgentApplyPatch.Apply(
            Patch(
                "*** Begin Patch",
                "*** Update File: sample.txt",
                "@@",
                "-third",
                "+THIRD",
                "@@",
                "-first",
                "+FIRST",
                "*** End Patch"),
            temp.Path);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("FIRST\nsecond\nTHIRD\n", ReadText(temp, "sample.txt"));
    }

    [TestMethod]
    public void Apply_QuotesEveryExpectedLineWhenTheContextIsMissing()
    {
        using var temp = TestTempDirectory.Create();
        WriteText(temp, "sample.txt", "one\n");

        var result = AgentApplyPatch.Apply(
            Patch(
                "*** Begin Patch",
                "*** Update File: sample.txt",
                "@@ class Missing",
                " first context",
                " second context",
                "-third removed",
                "+added",
                " fourth context",
                "*** End Patch"),
            temp.Path);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(
            result.Error,
            "Failed to find context 'class Missing' in sample.txt (hunk at patch line 3)."
            + " The lines of the hunk do not match a single place without it:\nfirst context\nsecond context\nthird removed\nfourth context"
                .Replace("\n", Environment.NewLine, StringComparison.Ordinal));
    }

    [TestMethod]
    public void Apply_UsesTheContextWhenTheAnchorIsMissingButTheContextIsUnique()
    {
        using var temp = TestTempDirectory.Create();
        WriteText(temp, "sample.py", "def run(self):\n    return 1\n");

        var result = AgentApplyPatch.Apply(
            Patch("*** Begin Patch", "*** Update File: sample.py", "@@ def run():", "-    return 1", "+    return 2", "*** End Patch"),
            temp.Path);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("def run(self):\n    return 2\n", ReadText(temp, "sample.py"));
    }

    [TestMethod]
    public void Apply_InsertsAnAdditionOnlyHunkAfterItsAnchor()
    {
        using var temp = TestTempDirectory.Create();
        WriteText(temp, "sample.txt", "header\nbody\n");

        var inserted = AgentApplyPatch.Apply(
            Patch("*** Begin Patch", "*** Update File: sample.txt", "@@ header", "+inserted", "*** End Patch"),
            temp.Path);
        var missingAnchor = AgentApplyPatch.Apply(
            Patch("*** Begin Patch", "*** Update File: sample.txt", "@@ absent", "+never", "*** End Patch"),
            temp.Path);

        Assert.IsTrue(inserted.Success, inserted.Error);
        Assert.IsFalse(missingAnchor.Success);
        StringAssert.Contains(missingAnchor.Error, "Failed to find context 'absent' in sample.txt");
        Assert.AreEqual("header\ninserted\nbody\n", ReadText(temp, "sample.txt"));
    }

    private static string Patch(params string[] lines)
        => string.Join("\n", lines);

    private static void WriteText(TestTempDirectory temp, string relativePath, string text)
        => File.WriteAllBytes(Path.Combine(temp.Path, relativePath), System.Text.Encoding.UTF8.GetBytes(text));

    private static string ReadText(TestTempDirectory temp, string relativePath)
        => System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(temp.Path, relativePath)));

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
