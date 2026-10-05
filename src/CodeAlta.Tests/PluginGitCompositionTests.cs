using CodeAlta.Plugin.Git;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Tests;

/// <summary>Uninitialized backend composition only; never attaches a context or invokes a native attachment.</summary>
[TestClass]
public sealed class PluginGitCompositionTests
{
    [TestMethod]
    public void PlainBackend_HasNoPromptPresentation()
    {
        var plugin = new GitPlugin();
        Assert.AreEqual(0, plugin.GetPromptEditorContributions().Count());
    }

    [TestMethod]
    public void InjectedFactory_IsDeferredUntilEnumeration()
    {
        var calls = 0;
        var moves = 0;
        var first = Literal("first");
        var second = Literal("second");
        IEnumerable<PluginPromptEditorContribution> Sequence()
        {
            moves++;
            yield return first;
            moves++;
            yield return second;
        }

        var plugin = new GitPlugin(_ =>
        {
            calls++;
            return Sequence();
        });
        Assert.AreEqual(0, calls);
        var contributions = plugin.GetPromptEditorContributions();
        Assert.AreEqual(0, calls);
        using var iterator = contributions.GetEnumerator();
        Assert.AreEqual(0, calls);
        Assert.AreEqual(0, moves);
        Assert.IsTrue(iterator.MoveNext());
        Assert.AreSame(first, iterator.Current);
        Assert.AreEqual(1, calls);
        Assert.AreEqual(1, moves);
        Assert.IsTrue(iterator.MoveNext());
        Assert.AreSame(second, iterator.Current);
        Assert.AreEqual(2, moves);
        Assert.IsFalse(iterator.MoveNext());
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public void InjectedFactory_ReceivesSameBackendAndPreservesSequence()
    {
        GitPlugin? received = null;
        var calls = 0;
        var first = Literal("same-name");
        var second = Literal("same-name");
        var plugin = new GitPlugin(owner =>
        {
            received = owner;
            calls++;
            // A null element deliberately tests transparent sequence forwarding, not validation policy.
            return new PluginPromptEditorContribution[] { second, first, first, null! };
        });
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var result = plugin.GetPromptEditorContributions().ToArray();
            Assert.AreSame(plugin, received);
            Assert.AreEqual(attempt, calls);
            Assert.AreEqual(4, result.Length);
            Assert.AreSame(second, result[0]);
            Assert.AreSame(first, result[1]);
            Assert.AreSame(first, result[2]);
            Assert.IsNull(result[3]);
        }
        Assert.AreEqual(0, new GitPlugin(static _ => []).GetPromptEditorContributions().Count());
    }

    [TestMethod]
    public void InjectedFactory_PropagatesFailureWithoutFallback()
    {
        var expected = new InvalidOperationException("literal contribution failure");
        var calls = 0;
        var plugin = new GitPlugin(_ =>
        {
            calls++;
            throw expected;
        });
        using var iterator = plugin.GetPromptEditorContributions().GetEnumerator();
        Assert.AreEqual(0, calls);
        Assert.AreSame(expected, Assert.ThrowsExactly<InvalidOperationException>(() => iterator.MoveNext()));
        Assert.AreEqual(1, calls);

        var first = Literal("before failure");
        IEnumerable<PluginPromptEditorContribution> FailingSequence()
        {
            yield return first;
            throw expected;
        }
        using var sequence = new GitPlugin(_ => FailingSequence()).GetPromptEditorContributions().GetEnumerator();
        Assert.IsTrue(sequence.MoveNext());
        Assert.AreSame(first, sequence.Current);
        Assert.AreSame(expected, Assert.ThrowsExactly<InvalidOperationException>(() => sequence.MoveNext()));

        var cancellation = new OperationCanceledException("literal cancellation");
        using var canceled = new GitPlugin(_ => throw cancellation).GetPromptEditorContributions().GetEnumerator();
        Assert.AreSame(cancellation, Assert.ThrowsExactly<OperationCanceledException>(() => canceled.MoveNext()));

        // A null sequence is an invalid factory result, not an empty-presentation fallback.
        using var missing = new GitPlugin(static _ => null!).GetPromptEditorContributions().GetEnumerator();
        Assert.ThrowsExactly<NullReferenceException>(() => missing.MoveNext());
    }

    [TestMethod]
    public void InjectedConstructor_RejectsNull()
    {
        // Explicitly exercise the non-null delegate constructor contract.
        var exception = Assert.ThrowsExactly<ArgumentNullException>(() => new GitPlugin(null!));
        Assert.AreEqual("createPromptEditorContributions", exception.ParamName);
    }

    [TestMethod]
    public void TerminalFactory_PreservesContributionMetadataWithoutAttachment()
    {
        var plugin = new GitPlugin(GitTerminalContributions.CreatePromptEditorContributions);
        var contribution = plugin.GetPromptEditorContributions().Single();
        Assert.AreEqual("Git issue prompt picker", contribution.Name);
        Assert.AreEqual("[#] to reference an issue", contribution.PlaceholderText);
        Assert.AreEqual(0, contribution.Order);
        Assert.IsNotNull(contribution.Attach);
    }

    private static PluginPromptEditorContribution Literal(string name) => new()
    {
        Name = name,
        Attach = static _ => throw new AssertFailedException("Attachment invoked."),
    };
}
