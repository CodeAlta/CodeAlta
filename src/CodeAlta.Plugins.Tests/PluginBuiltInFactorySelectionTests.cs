using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.Tests;

/// <summary>Construction selection only: literal callbacks and an unattached, acquisition-free fake.</summary>
[TestClass]
public sealed class PluginBuiltInFactorySelectionTests
{
    [TestMethod]
    public void SuppliedFactory_ReturnsExactInstanceWithoutFallback()
    {
        var expected = new InertPlugin();
        Assert.AreSame(expected, PluginRuntimeActivator.CreateInstance(() => expected, ForbiddenFallback));
    }

    [TestMethod]
    public void SuppliedFactory_IsInvokedOncePerAttempt()
    {
        var calls = 0;
        PluginBase Create()
        {
            calls++;
            return new InertPlugin();
        }

        var first = PluginRuntimeActivator.CreateInstance(Create, ForbiddenFallback);
        Assert.AreEqual(1, calls);
        var second = PluginRuntimeActivator.CreateInstance(Create, ForbiddenFallback);
        Assert.AreEqual(2, calls);
        Assert.AreNotSame(first, second);
    }

    [TestMethod]
    public void MissingFactory_InvokesFallbackOnce()
    {
        var calls = 0;
        var expected = new InertPlugin();
        var result = PluginRuntimeActivator.CreateInstance(null, () =>
        {
            calls++;
            return expected;
        });
        Assert.AreSame(expected, result);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public void NullFactoryResult_DoesNotInvokeFallback()
    {
        var calls = 0;
        var result = PluginRuntimeActivator.CreateInstance(() =>
        {
            calls++;
            // Deliberately violate the factory contract; activation owns the null-result diagnostic.
            return null!;
        }, ForbiddenFallback);
        Assert.IsNull(result);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public void FactoryException_PropagatesWithoutFallback()
    {
        var expected = new InvalidOperationException("literal factory failure");
        var calls = 0;
        var actual = Assert.ThrowsExactly<InvalidOperationException>(() => PluginRuntimeActivator.CreateInstance(() =>
        {
            calls++;
            throw expected;
        }, ForbiddenFallback));
        Assert.AreSame(expected, actual);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public void FactoryCancellation_PropagatesWithoutFallback()
    {
        var expected = new OperationCanceledException("literal factory cancellation");
        var calls = 0;
        var actual = Assert.ThrowsExactly<OperationCanceledException>(() => PluginRuntimeActivator.CreateInstance(() =>
        {
            calls++;
            throw expected;
        }, ForbiddenFallback));
        Assert.AreSame(expected, actual);
        Assert.AreEqual(1, calls);
    }

    private static PluginBase? ForbiddenFallback() => throw new AssertFailedException("Fallback invoked.");

    private sealed class InertPlugin : PluginBase
    {
        public override ValueTask InitializeAsync(CancellationToken cancellationToken = default) => throw new AssertFailedException("Initialized.");
        public override ValueTask OnActivatedAsync(CancellationToken cancellationToken = default) => throw new AssertFailedException("Activated.");
        public override ValueTask OnDeactivatingAsync(CancellationToken cancellationToken = default) => throw new AssertFailedException("Deactivated.");
        public override IEnumerable<PluginPromptEditorContribution> GetPromptEditorContributions() => throw new AssertFailedException("Contributions read.");
        public override ValueTask DisposeAsync() => throw new AssertFailedException("Disposed.");
    }
}
