using CodeAlta.Views;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Text;

namespace CodeAlta.Tests;

[TestClass]
public sealed class DirectoryPathCompletionProviderTests
{
    [TestMethod]
    public void Complete_ListsMatchingDirectoriesWithinParent()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-open-folder-tests", Guid.NewGuid().ToString("N"));
        var parent = Path.Combine(root, "Repos");
        Directory.CreateDirectory(Path.Combine(parent, "CodeAlta"));
        Directory.CreateDirectory(Path.Combine(parent, "Codex"));
        Directory.CreateDirectory(Path.Combine(parent, "Other"));

        try
        {
            var provider = new DirectoryPathCompletionProvider(root);
            var input = Path.Combine(parent, "Cod");
            var snapshot = new TextDocument(input).CurrentSnapshot;

            var result = provider.Complete(new PromptEditorCompletionRequest(
                snapshot,
                snapshot.Length,
                SelectionStart: snapshot.Length,
                SelectionLength: 0,
                Modifiers: TerminalModifiers.None));

            Assert.IsTrue(result.Handled);
            CollectionAssert.AreEqual(
                new[]
                {
                    Path.Combine(parent, "CodeAlta") + Path.DirectorySeparatorChar,
                    Path.Combine(parent, "Codex") + Path.DirectorySeparatorChar,
                },
                result.Candidates!.ToArray());
            Assert.AreEqual(0, result.ReplaceStart);
            Assert.AreEqual(snapshot.Length, result.ReplaceLength);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void Complete_BlankInput_ListsRootCandidates()
    {
        var provider = new DirectoryPathCompletionProvider(Environment.CurrentDirectory);
        var snapshot = new TextDocument(string.Empty).CurrentSnapshot;

        var result = provider.Complete(new PromptEditorCompletionRequest(
            snapshot,
            CaretIndex: 0,
            SelectionStart: 0,
            SelectionLength: 0,
            Modifiers: TerminalModifiers.None));

        Assert.IsTrue(result.Handled);
        Assert.IsNotNull(result.Candidates);
        Assert.IsTrue(result.Candidates.Count > 0);
    }
}
