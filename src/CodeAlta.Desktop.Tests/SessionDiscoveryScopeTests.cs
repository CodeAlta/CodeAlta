using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

/// <summary>Only lexical operations on the linked production scope; no filesystem or runtime fixtures.</summary>
[TestClass]
public sealed class SessionDiscoveryScopeTests
{
    private static string Volume => OperatingSystem.IsWindows() ? "C:\\" : "/";
    private static string Root => Path.Combine(Volume, "scope-test");
    private static SessionDiscoveryScope CreateScope() => new(Path.Combine(Volume, "discovery-home"), Root);

    [TestMethod]
    public void Constructor_RejectsMissingOrNonAbsoluteRoots()
    {
        foreach (var invalid in InvalidPaths())
        {
            Reject(() => new SessionDiscoveryScope(invalid!, Root));
            Reject(() => new SessionDiscoveryScope(Root, invalid!));
        }
    }

    [TestMethod]
    public void Constructor_NormalizesLexicalRootsAndPreservesVolumeRoots()
    {
        var scope = new SessionDiscoveryScope(Path.Combine(Root, "a", ".."), Root + Path.DirectorySeparatorChar);
        Assert.AreEqual(Root, scope.UserProfileRoot);
        Assert.AreEqual(Root, scope.InstructionAncestorRoot);
        var volume = new SessionDiscoveryScope(Volume, Volume);
        Assert.AreEqual(Volume, volume.UserProfileRoot);
        Assert.AreEqual(Volume, volume.InstructionAncestorRoot);
    }

    [TestMethod]
    public void ValidateHostRoots_RequiresExplicitAbsoluteGlobalAndProjectRoots()
    {
        var scope = CreateScope();
        foreach (var invalid in InvalidPaths())
        {
            Reject(() => scope.ValidateHostRoots(invalid, Root));
            Reject(() => scope.ValidateHostRoots(Root, invalid));
        }
        Reject(() => scope.ValidateHostRoots(Root, Path.Combine(Volume, "outside")));
    }

    [TestMethod]
    public void ValidateHostRoots_DoesNotDeriveDiscoveryHomeOrBoundary()
    {
        var scope = CreateScope();
        var home = scope.UserProfileRoot;
        scope.ValidateHostRoots(Path.Combine(Volume, "separate-catalog"), Root);
        Assert.AreEqual(home, scope.UserProfileRoot);
        Assert.AreEqual(Root, scope.InstructionAncestorRoot);
    }

    [TestMethod]
    public void ValidateProjectPath_AcceptsBoundaryAndDescendants()
    {
        var scope = CreateScope();
        foreach (var path in new[] { Root, Path.Combine(Root, "a"), Path.Combine(Root, "a", "b") })
            Assert.AreEqual(path, scope.ValidateProjectPath(path, "path"));
        Assert.AreEqual(Root, scope.ValidateProjectPath(Path.Combine(Root, "a", ".."), "path"));
    }

    [TestMethod]
    public void ValidateProjectPath_RejectsSiblingPrefixesAndNormalizedEscapes()
    {
        var scope = CreateScope();
        foreach (var path in InvalidPaths().Concat(new[] { Root + "-sibling", Path.Combine(Root, "..", "outside"), Volume }))
            Reject(() => scope.ValidateProjectPath(path!, "path"));
        if (OperatingSystem.IsWindows())
            Reject(() => scope.ValidateProjectPath("D:\\scope-test", "path"));
    }

    [TestMethod]
    public void ValidateProjectPath_UsesPlatformPathComparison()
    {
        var scope = CreateScope();
        var differentlyCased = Root.ToUpperInvariant();
        if (OperatingSystem.IsWindows())
            Assert.AreEqual(differentlyCased, scope.ValidateProjectPath(differentlyCased, "path"));
        else
            Reject(() => scope.ValidateProjectPath(differentlyCased, "path"));
    }

    [TestMethod]
    public void GetInstructionAncestors_ScopedWalkIncludesBoundaryInRootToLeafOrder()
    {
        var scope = CreateScope();
        CollectionAssert.AreEqual(new[] { Root }, SessionDiscoveryScope.GetInstructionAncestors(Root, scope).ToArray());
        CollectionAssert.AreEqual(new[] { Root, Path.Combine(Root, "a"), Path.Combine(Root, "a", "b") },
            SessionDiscoveryScope.GetInstructionAncestors(Path.Combine(Root, "a", "b"), scope).ToArray());
        var volumeScope = new SessionDiscoveryScope(Volume, Volume);
        CollectionAssert.AreEqual(new[] { Volume, Root }, SessionDiscoveryScope.GetInstructionAncestors(Root, volumeScope).ToArray());
    }

    [TestMethod]
    public void GetInstructionAncestors_RejectsInvalidRootsWithoutExistenceChecks()
    {
        var scope = CreateScope();
        foreach (var path in InvalidPaths().Concat(new[] { Path.Combine(Volume, "outside", "not-created"), Root + "-sibling" }))
            Reject(() => SessionDiscoveryScope.GetInstructionAncestors(path!, scope));
        CollectionAssert.AreEqual(new[] { Root, Path.Combine(Root, "not-created") },
            SessionDiscoveryScope.GetInstructionAncestors(Path.Combine(Root, "not-created"), scope).ToArray());
    }

    [TestMethod]
    public void GetInstructionAncestors_UnscopedWalkPreservesFullAncestry()
    {
        var trailingRoot = Root + Path.DirectorySeparatorChar;
        CollectionAssert.AreEqual(new[] { Volume, Root, trailingRoot },
            SessionDiscoveryScope.GetInstructionAncestors(trailingRoot, null).ToArray());
        CollectionAssert.AreEqual(new[] { Volume, Root, Path.Combine(Root, "a") },
            SessionDiscoveryScope.GetInstructionAncestors(Path.Combine(Root, "a"), null).ToArray());
        CollectionAssert.AreEqual(new[] { Volume }, SessionDiscoveryScope.GetInstructionAncestors(Volume, null).ToArray());
    }

    private static IEnumerable<string?> InvalidPaths()
    {
        yield return null;
        yield return "";
        yield return " ";
        yield return ".";
        yield return "relative";
        yield return "../outside";
        yield return Root + Path.DirectorySeparatorChar + "invalid\0path";
        if (OperatingSystem.IsWindows())
        {
            yield return "C:relative";
            yield return "\\root-relative";
        }
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        Assert.Fail("Expected lexical path rejection.");
    }
}
