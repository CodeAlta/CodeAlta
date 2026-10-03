using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using CodeAlta.Tui.Frontend.Commands;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Tui.Views;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ArchitectureGuardrailTests
{
    [TestMethod]
    public void CodeAltaSource_DoesNotUseStaticMutableData()
    {
        var sourceRoot = GetSourceRoot();
        var staticMutableDataPattern = new Regex(
            @"\bstatic\s+(?:readonly\s+)?(?:ConcurrentDictionary|Dictionary|HashSet|List|Queue|Stack|ConcurrentBag|ConcurrentQueue|ConcurrentStack|SemaphoreSlim)<[^>\r\n]+>\s+\w+\s*(?:=|;)",
            RegexOptions.CultureInvariant);
        var violations = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(static file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                                  !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(file => new
            {
                RelativePath = Path.GetRelativePath(sourceRoot, file).Replace('\\', '/'),
                Source = File.ReadAllText(file),
            })
            .SelectMany(file => staticMutableDataPattern
                .Matches(file.Source)
                .Select(match => $"{file.RelativePath}:{GetLineNumber(file.Source, match.Index)}:{match.Value}"))
            .OrderBy(static violation => violation, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(Array.Empty<string>(), violations);
    }

    [TestMethod]
    public void DirectoryPathDialog_UsesNamedServiceInsteadOfDomainCallbackList()
    {
        var constructor = typeof(DirectoryPathDialog)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single();
        var parameters = constructor.GetParameters();

        Assert.IsTrue(parameters.Any(static parameter => parameter.ParameterType == typeof(IDirectoryPathDialogService)));
        Assert.IsFalse(parameters.Any(static parameter => typeof(Delegate).IsAssignableFrom(parameter.ParameterType)));
    }

    [TestMethod]
    public void ProjectDetailsDialog_UsesNamedServiceInsteadOfDomainCallbackList()
    {
        var constructor = typeof(ProjectDetailsDialog)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single();
        var parameters = constructor.GetParameters();

        Assert.IsTrue(parameters.Any(static parameter => parameter.ParameterType == typeof(IProjectDetailsDialogService)));
        Assert.IsFalse(parameters.Any(static parameter => typeof(Delegate).IsAssignableFrom(parameter.ParameterType)));
    }

    [TestMethod]
    public void NavigatorSettingsDialog_UsesNamedServiceInsteadOfDomainCallbackList()
    {
        var constructor = typeof(NavigatorSettingsDialog)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single();
        var parameters = constructor.GetParameters();

        Assert.IsTrue(parameters.Any(static parameter => parameter.ParameterType == typeof(INavigatorSettingsDialogService)));
        Assert.IsFalse(parameters.Any(static parameter => typeof(Delegate).IsAssignableFrom(parameter.ParameterType)));
    }

    [TestMethod]
    public void OrchestrationProject_DoesNotReferenceFrontendOrTerminalUi()
    {
        var orchestrationRoot = Path.Combine(GetSourceRoot(), "CodeAlta.Orchestration");
        var sourceFiles = Directory.EnumerateFiles(orchestrationRoot, "*.cs", SearchOption.AllDirectories)
            .Where(static file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var projectSource = File.ReadAllText(Path.Combine(orchestrationRoot, "CodeAlta.Orchestration.csproj"));

        Assert.IsFalse(projectSource.Contains("..\\CodeAlta\\CodeAlta.csproj", StringComparison.Ordinal));
        Assert.IsFalse(projectSource.Contains("../CodeAlta/CodeAlta.csproj", StringComparison.Ordinal));
        Assert.IsFalse(projectSource.Contains("..\\CodeAlta.Tui\\CodeAlta.Tui.csproj", StringComparison.Ordinal));
        Assert.IsFalse(projectSource.Contains("../CodeAlta.Tui/CodeAlta.Tui.csproj", StringComparison.Ordinal));
        AssertSourceDoesNotContain(sourceFiles, "XenoAtom.Terminal.UI");
        AssertSourceDoesNotContain(sourceFiles, "using CodeAlta.Tui.App");
        AssertSourceDoesNotContain(sourceFiles, "using CodeAlta.Tui.Views");
    }

    [TestMethod]
    public void LowerLayerProjects_DoNotReferenceFrontendProject()
    {
        var sourceRoot = GetSourceRoot();
        var projectNames = new[]
        {
            "CodeAlta.Hosting",
            "CodeAlta.Orchestration",
            "CodeAlta.Plugins",
            "CodeAlta.Catalog",
        };
        var violations = projectNames
            .Select(name => new { Name = name, Directory = Path.Combine(sourceRoot, name) })
            .Where(static project => Directory.Exists(project.Directory))
            .Select(project => Path.Combine(project.Directory, project.Name + ".csproj"))
            .Where(File.Exists)
            .Where(projectFile =>
            {
                var projectSource = File.ReadAllText(projectFile);
                return projectSource.Contains("..\\CodeAlta\\CodeAlta.csproj", StringComparison.Ordinal) ||
                    projectSource.Contains("../CodeAlta/CodeAlta.csproj", StringComparison.Ordinal) ||
                    projectSource.Contains("..\\CodeAlta.Tui\\CodeAlta.Tui.csproj", StringComparison.Ordinal) ||
                    projectSource.Contains("../CodeAlta.Tui/CodeAlta.Tui.csproj", StringComparison.Ordinal);
            })
            .Select(projectFile => Path.GetRelativePath(sourceRoot, projectFile).Replace('\\', '/'))
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(Array.Empty<string>(), violations);
    }

    [TestMethod]
    public void Repository_DoesNotReferenceAkkaNetWithoutDecisionRecord()
    {
        var sourceRoot = GetSourceRoot();
        var packageFiles = Directory
            .EnumerateFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(sourceRoot, "Directory.Packages.props", SearchOption.TopDirectoryOnly))
            .ToArray();

        AssertSourceDoesNotContain(packageFiles, "PackageReference Include=\"Akka");
        AssertSourceDoesNotContain(packageFiles, "<PackageVersion Include=\"Akka");
    }

    [TestMethod]
    public void RuntimeCommands_AreNamedRequestRecordsWithStructuredOutcomes()
    {
        var commandMethods = typeof(ISessionOrchestrator)
            .GetMethods()
            .Where(static method => method.Name.EndsWith("Async", StringComparison.Ordinal) &&
                method.Name is not "StreamEventsAsync" and not "GetSessionSnapshotAsync")
            .ToArray();

        Assert.IsTrue(commandMethods.Length > 0);
        foreach (var method in commandMethods)
        {
            Assert.AreEqual(typeof(ValueTask<SessionCommandResult>), method.ReturnType, method.Name);
            var parameters = method.GetParameters();
            Assert.AreEqual(2, parameters.Length, method.Name);
            Assert.IsTrue(parameters[0].ParameterType.Name.EndsWith("Request", StringComparison.Ordinal), method.Name);
            Assert.AreEqual(typeof(CancellationToken), parameters[1].ParameterType, method.Name);
        }
    }

    [TestMethod]
    public void RuntimeActors_AreInternalImplementationDetails()
    {
        var actorPublicTypes = typeof(ISessionOrchestrator).Assembly
            .GetExportedTypes()
            .Where(static type => type.FullName?.Contains(".Runtime.Actors.", StringComparison.Ordinal) == true)
            .Select(static type => type.FullName)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(Array.Empty<string>(), actorPublicTypes);
    }

    private static void AssertSourceDoesNotContain(IEnumerable<string> sourceFiles, string pattern)
    {
        ArgumentNullException.ThrowIfNull(sourceFiles);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        var matches = sourceFiles
            .Where(file => File.ReadAllText(file).Contains(pattern, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(Array.Empty<string>(), matches, $"Found unexpected pattern '{pattern}'.");
    }

    private static int GetLineNumber(string source, int index)
    {
        var line = 1;
        for (var i = 0; i < index; i++)
        {
            if (source[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static string GetCodeAltaSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (TryGetCodeAltaSourceRoot(directory.FullName, out var candidate) ||
                TryGetCodeAltaSourceRoot(Path.Combine(directory.FullName, "CodeAlta.Tui"), out candidate) ||
                TryGetCodeAltaSourceRoot(Path.Combine(directory.FullName, "src", "CodeAlta.Tui"), out candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        Assert.Fail("Could not locate the CodeAlta.Tui source directory from the test output path.");
        return null!;
    }

    private static bool TryGetCodeAltaSourceRoot(string candidate, [NotNullWhen(true)] out string? sourceRoot)
    {
        if (File.Exists(Path.Combine(candidate, "CodeAlta.Tui.csproj")) &&
            Directory.Exists(Path.Combine(candidate, "App")) &&
            Directory.Exists(Path.Combine(candidate, "Views")))
        {
            sourceRoot = candidate;
            return true;
        }

        sourceRoot = null;
        return false;
    }

    private static string GetSourceRoot()
        => Directory.GetParent(GetCodeAltaSourceRoot())!.FullName;
}
