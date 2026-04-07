using CodeAlta.Agent;
using CodeAlta.Agent.LocalRuntime.Tools;

namespace CodeAlta.Tests;

[TestClass]
public sealed class LocalAgentApplyPatchTests
{
    [TestMethod]
    public async Task Apply_UpdatesFileAndPreservesTrailingNewline()
    {
        using var temp = TestTempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "sample.txt");
        await File.WriteAllTextAsync(filePath, "alpha\r\nbeta\r\n").ConfigureAwait(false);

        var result = LocalAgentApplyPatch.Apply(
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

        var result = LocalAgentApplyPatch.Apply(
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
    public void Apply_ReturnsFailureForInvalidPatchEnvelope()
    {
        var result = LocalAgentApplyPatch.Apply(
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
    public async Task GetTouchedPaths_ReturnsSourceAndDestinationPathsForMove()
    {
        using var temp = TestTempDirectory.Create();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "src.txt"), "alpha" + Environment.NewLine).ConfigureAwait(false);

        var paths = LocalAgentApplyPatch.GetTouchedPaths(
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
    public void Apply_ThrowsWhenPatchEscapesWorkingDirectory()
    {
        using var temp = TestTempDirectory.Create();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => LocalAgentApplyPatch.Apply(
            """
            *** Begin Patch
            *** Add File: ../escape.txt
            +nope
            *** End Patch
            """,
            temp.Path));

        StringAssert.Contains(exception.Message, "escapes the working directory");
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
