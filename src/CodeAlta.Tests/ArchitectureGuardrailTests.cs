using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using CodeAlta.Frontend.Commands;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Views;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ArchitectureGuardrailTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void CodeAltaSource_DoesNotContainLegacyUiThreadHelpersOrBroadRefreshView()
    {
        var codeAltaRoot = GetCodeAltaSourceRoot();
        var sourceFiles = Directory.EnumerateFiles(codeAltaRoot, "*.cs", SearchOption.AllDirectories).ToArray();

        AssertSourceDoesNotContain(sourceFiles, "PostToUi");
        AssertSourceDoesNotContain(sourceFiles, "ReadUiValue");
        AssertSourceDoesNotContain(sourceFiles, "RunOnUiThread");
        AssertSourceDoesNotContain(sourceFiles, "RefreshView(");
        AssertSourceDoesNotContain(sourceFiles, "ThreadTabState");
    }

    [TestMethod]
    public void RuntimeEventPump_IsOnlyCodeAltaConsumerOfRuntimeEventStream()
    {
        var codeAltaRoot = GetCodeAltaSourceRoot();
        var streamEventMatches = Directory
            .EnumerateFiles(codeAltaRoot, "*.cs", SearchOption.AllDirectories)
            .Select(file => new
            {
                File = file,
                Content = File.ReadAllText(file),
            })
            .Where(static entry => entry.Content.Contains("StreamEventsAsync(", StringComparison.Ordinal))
            .Select(static entry => Path.GetFileName(entry.File))
            .Where(static fileName => fileName is not "RuntimeWorkThreadOrchestratorAdapter.cs")
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { "RuntimeEventPump.cs" },
            streamEventMatches);
    }

    [TestMethod]
    public void FrontendViews_DoNotCallRuntimeDirectly()
    {
        var viewsRoot = Path.Combine(GetCodeAltaSourceRoot(), "Views");
        var forbiddenNames = new[]
        {
            "WorkThreadRuntimeService",
            "AgentHub",
            "PluginHostBridge",
        };
        var violations = Directory
            .EnumerateFiles(viewsRoot, "*.cs", SearchOption.AllDirectories)
            .SelectMany(file =>
            {
                var relativePath = Path.GetRelativePath(GetCodeAltaSourceRoot(), file).Replace('\\', '/');
                var content = File.ReadAllText(file);
                return forbiddenNames
                    .Where(name => content.Contains(name, StringComparison.Ordinal))
                    .Select(name => $"{relativePath}:{name}");
            })
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(Array.Empty<string>(), violations);
    }

    [TestMethod]
    public void OrchestrationRuntimeEventStreams_AreBounded()
    {
        var runtimeRoot = Path.Combine(GetSourceRoot(), "CodeAlta.Orchestration", "Runtime");
        var matches = Directory
            .EnumerateFiles(runtimeRoot, "*.cs", SearchOption.AllDirectories)
            .Where(static file => File.ReadAllText(file).Contains("Channel.CreateUnbounded<WorkThreadRuntimeEvent>", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(runtimeRoot, file).Replace('\\', '/'))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(Array.Empty<string>(), matches);
    }

    [TestMethod]
    public void Solution_IncludesOrchestrationTestsAsFirstClassProject()
    {
        var solutionSource = File.ReadAllText(Path.Combine(GetSourceRoot(), "CodeAlta.slnx"));

        Assert.IsTrue(solutionSource.Contains("CodeAlta.Orchestration.Tests/CodeAlta.Orchestration.Tests.csproj", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DevelopmentGuide_DocumentsArchitectureBoundariesAndActorDecision()
    {
        var guideSource = File.ReadAllText(Path.GetFullPath(Path.Combine(GetSourceRoot(), "..", "doc", "development-guide.md")));

        Assert.IsTrue(guideSource.Contains("## Architecture Boundaries", StringComparison.Ordinal));
        Assert.IsTrue(guideSource.Contains("## Runtime Orchestration Concurrency", StringComparison.Ordinal));
        Assert.IsTrue(guideSource.Contains("per-thread mailbox/actor-style command processors", StringComparison.Ordinal));
        Assert.IsTrue(guideSource.Contains("Do not add Akka.NET", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodeAltaSource_DoesNotIntroduceUnexpectedCallbackAggregates()
    {
        var codeAltaRoot = GetCodeAltaSourceRoot();
        var allowedCallbackFiles = new[]
        {
            "Presentation/Prompting/PromptImageWorkspaceCallbacks.cs",
        };

        var callbackFiles = Directory
            .EnumerateFiles(codeAltaRoot, "*Callbacks.cs", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(codeAltaRoot, file).Replace('\\', '/'))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(allowedCallbackFiles, callbackFiles);
    }

    [TestMethod]
    public void AppContextClasses_DoNotWrapLargeDelegateSets()
    {
        var contextRoot = Path.Combine(GetCodeAltaSourceRoot(), "App", "Context");
        var oversizedContexts = Directory
            .EnumerateFiles(contextRoot, "*.cs", SearchOption.TopDirectoryOnly)
            .Select(file => new
            {
                Path = Path.GetRelativePath(GetCodeAltaSourceRoot(), file).Replace('\\', '/'),
                DelegateFieldCount = File.ReadLines(file)
                    .Count(static line =>
                        line.Contains("private readonly Action", StringComparison.Ordinal) ||
                        line.Contains("private readonly Func", StringComparison.Ordinal)),
            })
            .Where(static context => context.DelegateFieldCount > 2)
            .Select(static context => $"{context.Path}:{context.DelegateFieldCount}")
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(Array.Empty<string>(), oversizedContexts);
    }

    [TestMethod]
    public void PromptSessionContracts_RequireProjectThreadRefAndModelProvider()
    {
        var source = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Models", "PromptSessionBinding.cs"));

        Assert.IsTrue(source.Contains("ProjectId projectId", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("ShellThreadRef thread", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("ModelProviderId modelProviderId", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("projectId == default", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("ArgumentNullException.ThrowIfNull(thread)", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("modelProviderId.IsEmpty", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("ProjectId? projectId", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("ModelProviderId? modelProviderId", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ArchitectureInventory_ReportsRemainingDelegateBasedFrontendSeams()
    {
        var codeAltaRoot = GetCodeAltaSourceRoot();
        var seams = Directory
            .EnumerateFiles(codeAltaRoot, "*.cs", SearchOption.AllDirectories)
            .Select(file => new
            {
                RelativePath = Path.GetRelativePath(codeAltaRoot, file).Replace('\\', '/'),
                Lines = File.ReadLines(file)
                    .Select((line, index) => new { Number = index + 1, Text = line })
                    .Where(static line =>
                        line.Text.Contains("Action<", StringComparison.Ordinal) ||
                        line.Text.Contains("Func<", StringComparison.Ordinal) ||
                        line.Text.Contains("Action ", StringComparison.Ordinal))
                    .Select(line => $"{line.Number}:{line.Text.Trim()}")
                    .ToArray(),
            })
            .Where(static file =>
                file.Lines.Length > 0 &&
                (file.RelativePath.StartsWith("App/", StringComparison.Ordinal) ||
                 file.RelativePath.StartsWith("Views/", StringComparison.Ordinal)))
            .Select(file => $"{file.RelativePath} => {string.Join(" | ", file.Lines)}")
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        TestContext.WriteLine("Remaining delegate seams in App/ and Views/:\n{0}", string.Join(Environment.NewLine, seams));
        Assert.IsNotNull(seams);
    }

    [TestMethod]
    public void FrontendRefactorV2Inventory_ReportsCurrentSimplificationBudget()
    {
        var codeAltaRoot = GetCodeAltaSourceRoot();
        var sourceFiles = Directory
            .EnumerateFiles(codeAltaRoot, "*.cs", SearchOption.AllDirectories)
            .ToArray();
        var frontendRoots = new[] { "App", "Frontend", "Views" };
        var delegateReferenceCounts = frontendRoots
            .Select(root => new
            {
                Root = root,
                Count = Directory
                    .EnumerateFiles(Path.Combine(codeAltaRoot, root), "*.cs", SearchOption.AllDirectories)
                    .Sum(file => File.ReadLines(file).Count(static line =>
                        line.Contains("Action<", StringComparison.Ordinal) ||
                        line.Contains("Func<", StringComparison.Ordinal) ||
                        line.Contains("Action ", StringComparison.Ordinal))),
            })
            .ToArray();
        var oversizedConstructors = sourceFiles
            .SelectMany(file => FindConstructorsWithTooManyDelegates(codeAltaRoot, file, maxDelegateParameters: 3))
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        var transitionalNames = new[]
        {
            "ICodeAltaFrontendServices",
            "CodeAltaFrontendServicesAdapter",
            "IProjectionInvalidator",
            "RefreshCatalogAndThreadWorkspace",
            "RefreshSelectionAndThreadWorkspace",
            "RefreshShellChrome",
        };
        var transitionalReferences = sourceFiles
            .Select(file => new
            {
                RelativePath = Path.GetRelativePath(codeAltaRoot, file).Replace('\\', '/'),
                Content = File.ReadAllText(file),
            })
            .SelectMany(entry => transitionalNames
                .Where(name => entry.Content.Contains(name, StringComparison.Ordinal))
                .Select(name => $"{entry.RelativePath}:{name}"))
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        TestContext.WriteLine("Frontend v2 simplification inventory:");
        TestContext.WriteLine("Total production CodeAlta C# LOC: {0}", sourceFiles.Sum(static file => File.ReadLines(file).Count()));
        TestContext.WriteLine(
            "Delegate references: {0}",
            string.Join(", ", delegateReferenceCounts.Select(static item => $"{item.Root}={item.Count}")));
        TestContext.WriteLine(
            "Constructors with more than 3 delegate parameters:\n{0}",
            string.Join(Environment.NewLine, oversizedConstructors));
        TestContext.WriteLine(
            "Transitional facade/projection references:\n{0}",
            string.Join(Environment.NewLine, transitionalReferences));
        Assert.IsTrue(sourceFiles.Length > 0);
        Assert.IsTrue(delegateReferenceCounts.Length == frontendRoots.Length);
        Assert.IsNotNull(oversizedConstructors);
        Assert.IsNotNull(transitionalReferences);
    }

    [TestMethod]
    public void CodeAltaFrontendCallbacks_IsDeletedAfterPortMigration()
    {
        Assert.IsFalse(File.Exists(Path.Combine(GetCodeAltaSourceRoot(), "App", "CodeAltaFrontendCallbacks.cs")));
    }

    [TestMethod]
    public void FrontendShellContractInventory_ReportsLegacyBackendNamedApis()
    {
        var codeAltaRoot = GetCodeAltaSourceRoot();
        var legacyNames = new[]
        {
            "BackendId",
            "SelectedBackend",
            "ChatBackendState",
        };
        var matches = Directory
            .EnumerateFiles(codeAltaRoot, "*.cs", SearchOption.AllDirectories)
            .Select(file => new
            {
                RelativePath = Path.GetRelativePath(codeAltaRoot, file).Replace('\\', '/'),
                Content = File.ReadAllText(file),
            })
            .Where(static entry =>
                entry.RelativePath.StartsWith("App/", StringComparison.Ordinal) ||
                entry.RelativePath.StartsWith("Frontend/", StringComparison.Ordinal) ||
                entry.RelativePath.StartsWith("Models/", StringComparison.Ordinal) ||
                entry.RelativePath.StartsWith("Presentation/", StringComparison.Ordinal) ||
                entry.RelativePath.StartsWith("ViewModels/", StringComparison.Ordinal) ||
                entry.RelativePath.StartsWith("Views/", StringComparison.Ordinal))
            .SelectMany(entry => legacyNames
                .Where(name => entry.Content.Contains(name, StringComparison.Ordinal))
                .Select(name => $"{entry.RelativePath}:{name}"))
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        TestContext.WriteLine("Legacy frontend backend-named contract/API references:\n{0}", string.Join(Environment.NewLine, matches));
        Assert.IsNotNull(matches);
    }

    [TestMethod]
    public void AppCoordinatorConstructors_DoNotAddLargeDelegateBags()
    {
        var codeAltaRoot = GetCodeAltaSourceRoot();
        var allowedLegacyConstructors = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["App/AcpManagementCoordinator.cs:AcpManagementCoordinator"] = "Legacy coordinator pending provider-management port extraction.",
            ["App/ChatBackendInitializationCoordinator.cs:ChatBackendInitializationCoordinator"] = "Legacy backend initialization seam pending model-provider port migration.",
            ["App/IFrontendPersistencePort.cs:FrontendPersistencePort"] = "Transitional port adapter wrapping persistence callbacks.",
            ["App/IModelProviderPreferencePort.cs:DelegatingModelProviderPreferencePort"] = "Transitional adapter preserving frontend preference callbacks.",
            ["App/IModelProviderPreferencePort.cs:FrontendModelProviderPreferencePort"] = "Transitional adapter preserving frontend preference callbacks.",
            ["App/IPromptSessionPort.cs:PromptSessionPort"] = "Transitional prompt-session port before orchestration facade migration.",
            ["App/IShellSelectionPort.cs:DelegatingShellSelectionPort"] = "Transitional selection port adapter.",
            ["App/IShellStatusPort.cs:ShellStatusPort"] = "Transitional status port adapter.",
            ["App/IWorkspaceSurface.cs:ShellWorkspaceSurfacePort"] = "Transitional workspace surface port adapter.",
            ["App/LegacyPromptSessionPort.cs:LegacyPromptSessionPort"] = "Legacy prompt-session bridge kept during facade migration.",
            ["App/NavigatorActionCoordinator.cs:NavigatorActionCoordinator"] = "Legacy navigator coordinator pending port extraction.",
            ["App/NavigatorSettingsCoordinator.cs:NavigatorSettingsCoordinator"] = "Legacy navigator settings coordinator pending service split.",
            ["App/OpenThreadStateStore.cs:OpenThreadStateStore"] = "Legacy open-thread registry pending state-store migration.",
            ["App/ShellThreadStateCoordinator.cs:ShellThreadStateCoordinator"] = "Legacy shell state coordinator pending tab service migration.",
            ["App/ShellWorkspacePorts.cs:DelegatingShellWorkspaceProjectionPort"] = "Transitional workspace projection port adapter.",
            ["App/SidebarCoordinator.cs:SidebarCoordinator"] = "Legacy sidebar coordinator pending projection-only migration.",
            ["App/SkillsManagementCoordinator.cs:SkillsManagementCoordinator"] = "Legacy skills coordinator pending service/view split.",
            ["App/ThreadCommandPorts.cs:DelegatingThreadLifecycleCommandPort"] = "Transitional command port adapter.",
            ["App/ThreadCommandPorts.cs:ThreadCommandUiPort"] = "Transitional command UI port adapter.",
            ["App/ThreadCreationCoordinator.cs:ThreadCreationCoordinator"] = "Legacy thread creation coordinator pending orchestration facade migration.",
            ["App/ThreadHistoryCoordinator.cs:ThreadHistoryCoordinator"] = "Legacy history coordinator pending runtime event projection migration.",
            ["App/ThreadPromptQueueCoordinator.cs:ThreadPromptQueueCoordinator"] = "Legacy queue coordinator pending prompt facade migration.",
            ["App/ThreadProviderSwitchCoordinator.cs:ThreadProviderSwitchCoordinator"] = "Legacy provider switch coordinator pending model-provider port migration.",
            ["App/ThreadRuntimeEventCoordinator.cs:ThreadRuntimeEventCoordinator"] = "Legacy runtime event coordinator pending centralized event projection.",
            ["App/ThreadTabPorts.cs:DelegatingThreadTabSurfacePort"] = "Transitional tab surface port adapter.",
            ["App/ThreadTabPorts.cs:DelegatingThreadTabLifecyclePort"] = "Transitional tab lifecycle port adapter.",
        };
        var violations = Directory
            .EnumerateFiles(Path.Combine(codeAltaRoot, "App"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => FindConstructorsWithTooManyDelegates(codeAltaRoot, file, maxDelegateParameters: 3))
            .Where(match => !allowedLegacyConstructors.ContainsKey(match))
            .OrderBy(static match => match, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(Array.Empty<string>(), violations);
    }

    [TestMethod]
    public void ShellInputCoordinator_UsesCommandDispatcherInsteadOfCallbackFanOut()
    {
        var constructor = typeof(ShellInputCoordinator)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single();
        var parameters = constructor.GetParameters();
        var delegateParameters = parameters.Count(static parameter => typeof(Delegate).IsAssignableFrom(parameter.ParameterType));

        Assert.AreEqual(4, parameters.Length);
        Assert.AreEqual(2, delegateParameters);
        Assert.IsTrue(parameters.Any(static parameter => parameter.ParameterType == typeof(IShellCommandDispatcher)));

        var source = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Frontend", "Commands", "ShellInputCoordinator.cs"));
        Assert.IsFalse(source.Contains("PluginHostBridge", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("ThreadCommandCoordinator", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ShellCommandSurfaceCoordinator_UsesNamedCommandServicesInsteadOfCallbackFanOut()
    {
        var constructor = typeof(ShellCommandSurfaceCoordinator)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single();
        var parameters = constructor.GetParameters();
        var delegateParameters = parameters.Count(static parameter => typeof(Delegate).IsAssignableFrom(parameter.ParameterType));

        Assert.IsTrue(delegateParameters <= 1, $"ShellCommandSurfaceCoordinator has {delegateParameters} delegate parameters.");
        Assert.IsTrue(parameters.Any(static parameter => parameter.Name == "toggleCommandBarMultiLine" && typeof(Delegate).IsAssignableFrom(parameter.ParameterType)));
        Assert.IsTrue(parameters.Any(static parameter => parameter.ParameterType == typeof(IShellPromptInputService)));
        Assert.IsTrue(parameters.Any(static parameter => parameter.ParameterType == typeof(IShellCommandDispatcher)));
        Assert.IsTrue(parameters.Any(static parameter => parameter.ParameterType == typeof(ShellCommandBindingProjector)));
        Assert.IsTrue(parameters.Any(static parameter => parameter.ParameterType == typeof(IShellCommandSurfacePresenter)));

        var compositionSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Frontend", "Commands", "ShellCommandSurfaceComposition.cs"));
        var source = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Frontend", "Commands", "ShellCommandSurfaceCoordinator.cs"));
        var factorySource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Frontend", "Commands", "ShellCommandRegistryFactory.cs"));
        var projectorSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Frontend", "Commands", "ShellCommandBindingProjector.cs"));
        var paletteSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Frontend", "Commands", "ShellCommandPalettePresenter.cs"));
        var serviceSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Frontend", "Commands", "ShellCommandServices.cs"));
        var viewFactorySource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "CodeAltaShellViewFactory.cs"));
        Assert.IsFalse(source.Contains("new ShellCommandRegistryFactory(", StringComparison.Ordinal));
        Assert.IsTrue(compositionSource.Contains("new ShellCommandRegistryFactory(", StringComparison.Ordinal));
        Assert.IsTrue(compositionSource.Contains("new ShellCommandDispatcher(", StringComparison.Ordinal));
        Assert.IsTrue(compositionSource.Contains("new ShellCommandBindingProjector(", StringComparison.Ordinal));
        Assert.IsTrue(compositionSource.Contains("new ShellCommandPalettePresenter(", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("registry.RegisterFactory(", StringComparison.Ordinal));
        Assert.IsTrue(factorySource.Contains("registry.RegisterFactory(", StringComparison.Ordinal));
        Assert.IsTrue(projectorSource.Contains("ShellCommandCatalog.Get(", StringComparison.Ordinal));
        Assert.IsTrue(paletteSource.Contains("new CommandPalette()", StringComparison.Ordinal));
        Assert.IsTrue(serviceSource.Contains("internal interface IPluginCommandService", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("Func<Task> scrollToPreviousMessageAsync", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("Func<Task> openModelProvidersAsync", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("Func<WorkThreadDescriptor?> getSelectedThread", StringComparison.Ordinal));
        Assert.IsFalse(viewFactorySource.Contains("Action focusSidebar", StringComparison.Ordinal));
        Assert.IsFalse(viewFactorySource.Contains("Action focusPromptEditor", StringComparison.Ordinal));
        Assert.IsTrue(viewFactorySource.Contains("shellCommandSurfaceCoordinator.FocusSidebarAsync", StringComparison.Ordinal));
        Assert.IsTrue(viewFactorySource.Contains("shellCommandSurfaceCoordinator.FocusPromptAsync", StringComparison.Ordinal));
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
    public void FrontendShellContracts_DoNotAddBackendTerminologyOutsideLegacyAdapters()
    {
        var codeAltaRoot = GetCodeAltaSourceRoot();
        var allowedLegacyFiles = new HashSet<string>(StringComparer.Ordinal)
        {
            "App/CodeAltaShellBridge.cs",
            "App/CodeAltaShellController.cs",
            "App/Context/ShellWorkspaceContext.cs",
            "App/ICodeAltaShell.cs",
            "App/IModelProviderPreferencePort.cs",
            "App/PluginHostBridge.cs",
            "App/PromptImageCapabilityContext.cs",
            "App/ShellWorkspacePorts.cs",
            "App/State/ModelProviderSelectorStateStore.cs",
            "App/State/OpenThreadState.cs",
            "Models/ChatTimelineModels.cs",
            "Models/ThreadSessionState.cs",
        };
        var contractRoots = new[]
        {
            Path.Combine(codeAltaRoot, "App"),
            Path.Combine(codeAltaRoot, "Models"),
        };
        var violations = contractRoots
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Select(file => new
            {
                RelativePath = Path.GetRelativePath(codeAltaRoot, file).Replace('\\', '/'),
                Content = File.ReadAllText(file),
            })
            .Where(static entry =>
                entry.RelativePath.EndsWith("Port.cs", StringComparison.Ordinal) ||
                entry.RelativePath.EndsWith("Ports.cs", StringComparison.Ordinal) ||
                entry.RelativePath.EndsWith("Context.cs", StringComparison.Ordinal) ||
                entry.RelativePath.EndsWith("Bridge.cs", StringComparison.Ordinal) ||
                entry.RelativePath.EndsWith("State.cs", StringComparison.Ordinal) ||
                entry.RelativePath.StartsWith("Models/", StringComparison.Ordinal) ||
                string.Equals(entry.RelativePath, "App/ICodeAltaShell.cs", StringComparison.Ordinal))
            .Where(static entry => Regex.IsMatch(entry.Content, @"\bBackend(?:Id)?\b|ChatBackendState|SelectedBackend"))
            .Select(static entry => entry.RelativePath)
            .Where(file => !allowedLegacyFiles.Contains(file))
            .OrderBy(static file => file, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(Array.Empty<string>(), violations);
    }

    [TestMethod]
    public void FrontendCoordinators_DoNotAddUntrackedFireAndForgetTasks()
    {
        var codeAltaRoot = GetCodeAltaSourceRoot();
        var allowedLegacySites = new HashSet<string>(StringComparer.Ordinal)
        {
            "App/CodeAltaShellController.cs:71:_initializationTask = Task.Run(",
            "App/CodeAltaShellController.cs:356:var startupProviderLoadTask = Task.Run(",
            "App/CodeAltaApp.cs:343:_ = PersistViewStateAsync();",
            "App/CodeAltaApp.cs:416:_ = OpenModelProvidersAsync();",
            "App/RuntimeEventPump.cs:34:_pumpTask = Task.Run(",
            "App/ShellThreadStateCoordinator.cs:318:_ = RestoreStartupThreadHistoryAsync(threadId, cancellationToken);",
            "App/ShellThreadStateCoordinator.cs:327:_ = PersistViewStateAsync();",
            "App/ShellThreadStateCoordinator.cs:340:_ = PersistViewStateAsync();",
            "App/ShellThreadStateCoordinator.cs:399:_ = PersistViewStateAsync();",
            "App/ShellThreadStateCoordinator.cs:401:_ = _historyLoader.EnsureThreadHistoryLoadedAsync(thread, CancellationToken.None);",
            "App/ShellThreadStateCoordinator.cs:482:_ = PersistViewStateAsync();",
            "App/ShellThreadStateCoordinator.cs:519:_ = PersistViewStateAsync();",
            "App/SidebarCoordinator.cs:297:_ = CommitInlineRenameAsync(row, projectId, displayName, previousTitle);",
            "App/ThreadPromptDispatchCoordinator.cs:177:_ = RecordResolvedReferenceUsageAsync(promptInput.ResolvedReferences);",
            "App/ThreadPromptDraftPersistenceCoordinator.cs:83:_ = PersistPromptDraftAsync(threadId, normalizedPrompt, cancellationSource);",
            "App/ThreadRuntimeEventCoordinator.cs:232:Task.Run(async () =>",
            "App/ThreadRuntimeEventCoordinator.cs:461:_ = InvalidateProjectFileSearchAsync(thread.WorkingDirectory);",
            "Presentation/Editing/FileEditorTab.cs:213:_ = RefreshExternalStateAsync();",
            "Presentation/Editing/ProjectFileOpenDialogController.cs:217:_ = AcceptSelectedAsync(selected);",
            "Presentation/Prompting/ProjectFileReferencePopupController.cs:153:var sessionCreateTask = Task.Run(",
            "Presentation/Prompting/ProjectFileReferencePopupController.cs:164:_ = sessionCreateTask.ContinueWith(",
            "Presentation/Prompting/ProjectFileReferencePopupController.cs:377:_ = CloseAsync();",
            "Presentation/Prompting/ProjectFileReferencePopupController.cs:378:_ = RecordUsageAsync(selected);",
            "Presentation/Tabs/ThreadTabStripCoordinator.cs:469:_ = CloseTabFromViewAsync(currentThreadId, ShellTabCloseReason.UserDetached);",
            "Presentation/Tabs/ThreadTabStripCoordinator.cs:675:_ = CloseTabFromViewAsync(CodeAltaApp.DraftTabId, ShellTabCloseReason.UserDetached);",
            "Presentation/Tabs/ThreadTabStripCoordinator.cs:710:_ = CloseTabFromViewAsync(currentTabId, ShellTabCloseReason.FileEditorClosed);",
            "Presentation/Tabs/ThreadTabStripCoordinator.cs:751:_ = CloseTabFromViewAsync(currentTabId, ShellTabCloseReason.UserDetached);",
            "Presentation/Threads/ThreadInfoPresenter.cs:96:_ = LoadAsync(cancellationTokenSource.Token);",
        };
        var violations = new[]
            {
                Path.Combine(codeAltaRoot, "App"),
                Path.Combine(codeAltaRoot, "Presentation"),
            }
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .SelectMany(file =>
            {
                var relativePath = Path.GetRelativePath(codeAltaRoot, file).Replace('\\', '/');
                return File.ReadLines(file)
                    .Select((line, index) => new { Number = index + 1, Text = line.Trim() })
                    .Where(static line =>
                        line.Text.Contains("Task.Run(", StringComparison.Ordinal) ||
                        line.Text.Contains("ContinueWith(", StringComparison.Ordinal) ||
                        Regex.IsMatch(line.Text, @"^_\s*=\s*\w+Async\("))
                    .Select(line => $"{relativePath}:{line.Number}:{line.Text}");
            })
            .Where(site => !allowedLegacySites.Contains(site))
            .OrderBy(static site => site, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(Array.Empty<string>(), violations);
    }

    [TestMethod]
    public void RuntimeEventPump_TargetsRuntimeEventProjectorFacade()
    {
        var pumpSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "RuntimeEventPump.cs"));

        Assert.IsTrue(pumpSource.Contains("IThreadRuntimeEventProjector", StringComparison.Ordinal));
        Assert.IsFalse(pumpSource.Contains("CodeAltaShellController", StringComparison.Ordinal));
        Assert.IsTrue(pumpSource.Contains("_runtimeEventProjector.QueueRuntimeEvent(runtimeEvent, cancellationToken);", StringComparison.Ordinal));
    }

    [TestMethod]
    public void StateMutationCoordinators_DoNotCallBroadProjectionRefreshMethods()
    {
        var codeAltaRoot = GetCodeAltaSourceRoot();
        var mutationCoordinatorFiles = new[]
        {
            Path.Combine(codeAltaRoot, "App", "ShellThreadStateCoordinator.cs"),
            Path.Combine(codeAltaRoot, "App", "ThreadRuntimeEventCoordinator.cs"),
            Path.Combine(codeAltaRoot, "App", "ChatBackendInitializationCoordinator.cs"),
            Path.Combine(codeAltaRoot, "App", "AcpFrontendCoordinator.cs"),
            Path.Combine(codeAltaRoot, "App", "ProviderFrontendCoordinator.cs"),
            Path.Combine(codeAltaRoot, "Views", "InitialCatalogStateCoordinator.cs"),
        };
        var forbiddenCalls = new[]
        {
            "ApplyCatalogProjection(",
            "ApplySelectionProjection(",
            "ApplyHeaderProjection(",
            "ApplyPromptAvailabilityProjection(",
        };

        var violations = mutationCoordinatorFiles
            .SelectMany(file => File.ReadLines(file).Select((line, index) => new { File = file, Line = line, Number = index + 1 }))
            .Where(entry => forbiddenCalls.Any(call => entry.Line.Contains(call, StringComparison.Ordinal)))
            .Select(entry => $"{Path.GetRelativePath(codeAltaRoot, entry.File).Replace('\\', '/')}:{entry.Number}:{entry.Line.Trim()}")
            .OrderBy(static violation => violation, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(Array.Empty<string>(), violations);
    }

    [TestMethod]
    public void FrontendComposition_DoesNotWireBroadProjectionRefreshCallbacks()
    {
        var compositionSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "CodeAltaFrontendComposition.cs"));
        var forbiddenCallbacks = new[]
        {
            "frontend.ApplyCatalogProjection",
            "frontend.ApplyHeaderProjection",
            "frontend.ApplySelectionProjection",
            "frontend.ApplyShellChromeProjection",
        };

        foreach (var callback in forbiddenCallbacks)
        {
            Assert.IsFalse(compositionSource.Contains(callback, StringComparison.Ordinal), callback);
        }
    }

    [TestMethod]
    public void ShellProjectionCoordinator_UsesTypedProjectionControllers()
    {
        var codeAltaRoot = GetCodeAltaSourceRoot();
        Assert.IsFalse(File.Exists(Path.Combine(codeAltaRoot, "App", "CodeAltaProjectionInvalidator.cs")));

        var coordinatorSource = File.ReadAllText(Path.Combine(codeAltaRoot, "App", "Events", "ShellProjectionCoordinator.cs"));
        Assert.IsFalse(coordinatorSource.Contains("IProjectionInvalidator", StringComparison.Ordinal));
        Assert.IsFalse(coordinatorSource.Contains("RefreshCatalogAndThreadWorkspace", StringComparison.Ordinal));
        StringAssert.Contains(coordinatorSource, "StartupCatalogProjectionReadyEvent");
        StringAssert.Contains(coordinatorSource, "IWorkspaceProjectionController");
        StringAssert.Contains(coordinatorSource, "IPromptAvailabilityProjectionController");
        StringAssert.Contains(coordinatorSource, "IQueuedPromptProjectionController");
        StringAssert.Contains(coordinatorSource, "ApplyCatalogProjection");
        StringAssert.Contains(coordinatorSource, "ApplySelectionProjection");
        StringAssert.Contains(coordinatorSource, "ApplyThreadStatusProjection");
        StringAssert.Contains(coordinatorSource, "ApplyPromptAvailabilityProjection");
        StringAssert.Contains(coordinatorSource, "ApplyQueuedPromptProjection");
        StringAssert.Contains(coordinatorSource, "ApplySessionUsageProjection");
        StringAssert.Contains(coordinatorSource, "ApplyTabProjection");

        var shellBridgeSource = File.ReadAllText(Path.Combine(codeAltaRoot, "App", "ICodeAltaShell.cs"));
        Assert.IsFalse(shellBridgeSource.Contains("RefreshCatalogAndThreadWorkspace", StringComparison.Ordinal));
        StringAssert.Contains(shellBridgeSource, "PublishStartupCatalogProjectionReady");
    }

    [TestMethod]
    public void FrontendRefactorV2_CleanupGuardrailsPreventLegacyFacadeShims()
    {
        var codeAltaRoot = GetCodeAltaSourceRoot();
        var allProductionSource = Directory.EnumerateFiles(codeAltaRoot, "*.cs", SearchOption.AllDirectories)
            .Select(file => new
            {
                RelativePath = Path.GetRelativePath(codeAltaRoot, file).Replace('\\', '/'),
                Content = File.ReadAllText(file),
            })
            .ToArray();
        var forbiddenNames = new[]
        {
            "ICodeAltaFrontendServices",
            "CodeAltaFrontendServicesAdapter",
            "IProjectionInvalidator",
            "CodeAltaProjectionInvalidator",
            "RefreshCatalogAndThreadWorkspace",
            "RefreshSelectionAndThreadWorkspace",
            "RefreshHeaderAndThreadWorkspace",
            "RefreshShellChrome",
        };
        var violations = allProductionSource
            .SelectMany(entry => forbiddenNames
                .Where(name => entry.Content.Contains(name, StringComparison.Ordinal))
                .Select(name => $"{entry.RelativePath}:{name}"))
            .OrderBy(static violation => violation, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(Array.Empty<string>(), violations);
    }

    [TestMethod]
    public void FrontendRefactorV2_DoesNotAddLargeDelegatingPorts()
    {
        var codeAltaRoot = GetCodeAltaSourceRoot();
        var approvedLegacyPorts = new[]
        {
            "App/IModelProviderPreferencePort.cs:DelegatingModelProviderPreferencePort:9",
            "App/IShellSelectionPort.cs:DelegatingShellSelectionPort:6",
            "App/ShellWorkspacePorts.cs:DelegatingShellWorkspaceProjectionPort:10",
            "App/ThreadCommandPorts.cs:DelegatingThreadLifecycleCommandPort:4",
            "App/ThreadTabPorts.cs:DelegatingThreadTabLifecyclePort:5",
        };
        var largeDelegatingPorts = Directory.EnumerateFiles(Path.Combine(codeAltaRoot, "App"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => FindLargeDelegatingPorts(codeAltaRoot, file, maxDelegateFields: 3))
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(approvedLegacyPorts, largeDelegatingPorts);
    }

    [TestMethod]
    public void FrontendRefactorV2_PresentationViewsDependenciesAreExplicitlyApproved()
    {
        var codeAltaRoot = GetCodeAltaSourceRoot();
        var presentationRoot = Path.Combine(codeAltaRoot, "Presentation");
        var approvedViewDependencies = new[]
        {
            "Presentation/Editing/FileEditorTab.cs",
            "Presentation/Tabs/ThreadTabStripCoordinator.cs",
            "Presentation/Timeline/ChatTimelineVisualFactory.cs",
            "Presentation/Timeline/FileChangePresenter.cs",
            "Presentation/Timeline/ToolCallPresenter.cs",
            "Presentation/Usage/SessionUsagePresenter.cs",
        };
        var viewDependencies = Directory.EnumerateFiles(presentationRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("using CodeAlta.Views;", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(codeAltaRoot, file).Replace('\\', '/'))
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(approvedViewDependencies, viewDependencies);
    }

    [TestMethod]
    public void TabContentMigrationInventory_ReportsLegacyContentPlacementApis()
    {
        var codeAltaRoot = GetCodeAltaSourceRoot();
        var legacyApis = new[]
        {
            "SetThreadPaneContent",
            "SetActiveTabContent",
            "CreateThreadTabPageContentPlaceholder",
        };
        var matches = Directory
            .EnumerateFiles(codeAltaRoot, "*.cs", SearchOption.AllDirectories)
            .Select(file => new
            {
                RelativePath = Path.GetRelativePath(codeAltaRoot, file).Replace('\\', '/'),
                Content = File.ReadAllText(file),
            })
            .SelectMany(entry => legacyApis
                .Where(api => entry.Content.Contains(api, StringComparison.Ordinal))
                .Select(api => $"{entry.RelativePath}:{api}"))
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        TestContext.WriteLine("Legacy tab content placement references:\n{0}", string.Join(Environment.NewLine, matches));
        Assert.IsNotNull(matches);
    }

    [TestMethod]
    public void ShellTabs_AssociatedViewModelsUseTabPageData()
    {
        var coordinatorSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Presentation", "Tabs", "ThreadTabStripCoordinator.cs"));

        Assert.IsTrue(coordinatorSource.Contains("private sealed record ThreadTabPageData(string TabId, ShellTabKind Kind, object ViewModel)", StringComparison.Ordinal));
        Assert.IsTrue(coordinatorSource.Contains("Data = new ThreadTabPageData(thread.ThreadId, shellTab.Kind, shellTab.ViewModel)", StringComparison.Ordinal));
        Assert.IsTrue(coordinatorSource.Contains("Data = new ThreadTabPageData(tabId, shellTab.Kind, shellTab.ViewModel)", StringComparison.Ordinal));
        Assert.IsTrue(coordinatorSource.Contains("Data = new ThreadTabPageData(CodeAltaApp.DraftTabId, shellTab.Kind, shellTab.ViewModel)", StringComparison.Ordinal));
        Assert.IsFalse(coordinatorSource.Contains("Data = thread.ThreadId", StringComparison.Ordinal));
        Assert.IsFalse(coordinatorSource.Contains("Data = tabId,", StringComparison.Ordinal));
        Assert.IsFalse(coordinatorSource.Contains("Data = CodeAltaApp.DraftTabId", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ThreadTabStripCoordinator_UsesShellTabsAsLogicalTabSource()
    {
        var coordinatorSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Presentation", "Tabs", "ThreadTabStripCoordinator.cs"));

        StringAssert.Contains(coordinatorSource, "ThreadTabStripProjectionBuilder.Build(_shellTabs.GetTabs())");
        StringAssert.Contains(coordinatorSource, "RestoreThreadTabsFromViewState(workspaceView);");
        StringAssert.Contains(coordinatorSource, "_restoredThreadTabsFromViewState");
        Assert.IsFalse(coordinatorSource.Contains("_getOpenFileTabIds", StringComparison.Ordinal));
        Assert.IsFalse(coordinatorSource.Contains("_getSelectedTabIdOverride", StringComparison.Ordinal));
        Assert.IsFalse(coordinatorSource.Contains("private void EnsureThreadSurfaceShellTabs", StringComparison.Ordinal));

        var buildProjectionStart = coordinatorSource.IndexOf("private ThreadTabStripProjection BuildProjection()", StringComparison.Ordinal);
        var restoreStart = coordinatorSource.IndexOf("private void RestoreThreadTabsFromViewState", StringComparison.Ordinal);
        Assert.IsTrue(buildProjectionStart >= 0);
        Assert.IsTrue(restoreStart > buildProjectionStart);
        var buildProjectionSource = coordinatorSource[buildProjectionStart..restoreStart];
        Assert.IsFalse(buildProjectionSource.Contains("OpenThreadIds", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ThreadTabCloseSemantics_AreExplicitlySeparated()
    {
        var codeAltaRoot = GetCodeAltaSourceRoot();
        var shellTabsSource = File.ReadAllText(Path.Combine(codeAltaRoot, "App", "IShellTabService.cs"));
        var threadStateSource = File.ReadAllText(Path.Combine(codeAltaRoot, "App", "ShellThreadStateCoordinator.cs"));
        var fileEditorSource = File.ReadAllText(Path.Combine(codeAltaRoot, "Views", "FileEditorWorkspaceCoordinator.cs"));
        var threadCommandsSource = File.ReadAllText(Path.Combine(codeAltaRoot, "App", "ThreadCommandCoordinator.cs"));
        var navigatorSource = File.ReadAllText(Path.Combine(codeAltaRoot, "App", "NavigatorActionCoordinator.cs"));
        var appSource = File.ReadAllText(Path.Combine(codeAltaRoot, "App", "CodeAltaApp.cs"));

        StringAssert.Contains(shellTabsSource, "UserDetached");
        StringAssert.Contains(shellTabsSource, "FileEditorClosed");
        StringAssert.Contains(shellTabsSource, "ThreadDeleted");
        StringAssert.Contains(shellTabsSource, "ProjectClosed");
        Assert.IsFalse(shellTabsSource.Contains("User,", StringComparison.Ordinal));

        StringAssert.Contains(threadStateSource, "ShellTabCloseReason.UserDetached");
        StringAssert.Contains(threadStateSource, "ShellTabCloseReason.ThreadDeleted");
        StringAssert.Contains(threadStateSource, "ShellTabCloseReason.ProjectClosed");
        StringAssert.Contains(fileEditorSource, "ShellTabCloseReason.FileEditorClosed");
        StringAssert.Contains(appSource, "new DelegatingShellTabCommandService(() => _threadTabStripCoordinator.CloseSelectedTabAsync())");
        Assert.IsFalse(appSource.Contains("CloseSelectedThreadAsync", StringComparison.Ordinal));

        StringAssert.Contains(threadCommandsSource, "AbortSelectedThreadAsync");
        StringAssert.Contains(threadCommandsSource, "_runtimeService.AbortAsync(thread.ThreadId)");
        StringAssert.Contains(navigatorSource, "DeleteThreadAsync");
        StringAssert.Contains(navigatorSource, "DeleteProjectAsync");
    }

    [TestMethod]
    public void OrchestrationStateOwnershipInventory_ReportsMutablePerThreadState()
    {
        var sourceRoot = GetSourceRoot();
        var orchestrationRoot = Path.Combine(sourceRoot, "CodeAlta.Orchestration");
        var statePatterns = new[]
        {
            "Dictionary<",
            "ConcurrentDictionary<",
            "Channel<",
            "CancellationTokenSource",
            "WorkThreadDescriptor",
            "ThreadSessionEntry",
            "Queue<",
        };
        var matches = Directory
            .EnumerateFiles(orchestrationRoot, "*.cs", SearchOption.AllDirectories)
            .Select(file => new
            {
                RelativePath = Path.GetRelativePath(orchestrationRoot, file).Replace('\\', '/'),
                Lines = File.ReadLines(file)
                    .Select((line, index) => new { Number = index + 1, Text = line })
                    .Where(line => statePatterns.Any(pattern => line.Text.Contains(pattern, StringComparison.Ordinal)))
                    .Select(line => $"{line.Number}:{line.Text.Trim()}")
                    .ToArray(),
            })
            .Where(static file => file.Lines.Length > 0)
            .Select(file => $"{file.RelativePath} => {string.Join(" | ", file.Lines)}")
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        TestContext.WriteLine("Mutable orchestration state inventory:\n{0}", string.Join(Environment.NewLine, matches));
        Assert.IsNotNull(matches);
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
        AssertSourceDoesNotContain(sourceFiles, "XenoAtom.Terminal.UI");
        AssertSourceDoesNotContain(sourceFiles, "using CodeAlta.App");
        AssertSourceDoesNotContain(sourceFiles, "using CodeAlta.Views");
    }

    [TestMethod]
    public void LowerLayerProjects_DoNotReferenceFrontendProject()
    {
        var sourceRoot = GetSourceRoot();
        var projectNames = new[]
        {
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
                    projectSource.Contains("../CodeAlta/CodeAlta.csproj", StringComparison.Ordinal);
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
        var commandMethods = typeof(IWorkThreadOrchestrator)
            .GetMethods()
            .Where(static method => method.Name.EndsWith("Async", StringComparison.Ordinal) &&
                method.Name is not "StreamEventsAsync" and not "GetThreadSnapshotAsync")
            .ToArray();

        Assert.IsTrue(commandMethods.Length > 0);
        foreach (var method in commandMethods)
        {
            Assert.AreEqual(typeof(ValueTask<WorkThreadCommandResult>), method.ReturnType, method.Name);
            var parameters = method.GetParameters();
            Assert.AreEqual(2, parameters.Length, method.Name);
            Assert.IsTrue(parameters[0].ParameterType.Name.EndsWith("Request", StringComparison.Ordinal), method.Name);
            Assert.AreEqual(typeof(CancellationToken), parameters[1].ParameterType, method.Name);
        }
    }

    [TestMethod]
    public void RuntimeActors_AreInternalImplementationDetails()
    {
        var actorPublicTypes = typeof(IWorkThreadOrchestrator).Assembly
            .GetExportedTypes()
            .Where(static type => type.FullName?.Contains(".Runtime.Actors.", StringComparison.Ordinal) == true)
            .Select(static type => type.FullName)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(Array.Empty<string>(), actorPublicTypes);
    }

    [TestMethod]
    public void WorkThreadRuntimeService_AbortRoutesThroughPerThreadActor()
    {
        var runtimeSource = File.ReadAllText(Path.Combine(GetSourceRoot(), "CodeAlta.Orchestration", "Runtime", "WorkThreadRuntimeService.cs"));

        StringAssert.Contains(runtimeSource, "private readonly WorkThreadActorRegistry _threadActors");
        StringAssert.Contains(runtimeSource, "var actor = _threadActors.GetOrCreate(threadId);");
        StringAssert.Contains(runtimeSource, "await actor.ExecuteReservedAsync(");
        StringAssert.Contains(runtimeSource, "await _threadActors.DisposeAsync()");
    }

    [TestMethod]
    public void WorkThreadRuntimeState_IsMailboxOwned()
    {
        var runtimeSource = File.ReadAllText(Path.Combine(GetSourceRoot(), "CodeAlta.Orchestration", "Runtime", "WorkThreadRuntimeService.cs"));

        StringAssert.Contains(runtimeSource, "private readonly WorkThreadActorRegistry _threadActors");
        StringAssert.Contains(runtimeSource, "EnsureCoordinatorSessionCoreAsync(thread, options, actorCancellationToken)");
        Assert.IsFalse(runtimeSource.Contains("SemaphoreSlim _gate", StringComparison.Ordinal));
        Assert.IsFalse(runtimeSource.Contains("_gate.WaitAsync", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WorkThreadRuntimeService_SendSteerAndEventsRouteThroughPerThreadActor()
    {
        var runtimeSource = File.ReadAllText(Path.Combine(GetSourceRoot(), "CodeAlta.Orchestration", "Runtime", "WorkThreadRuntimeService.cs"));

        StringAssert.Contains(runtimeSource, "var agentId = await _threadActors.GetOrCreate(thread.ThreadId).QueryAsync(");
        StringAssert.Contains(runtimeSource, "var runId = await _agentHub.RunAsync(agentId, sendOptions, cancellationToken)");
        StringAssert.Contains(runtimeSource, "await MarkActiveRunIfStillInFlightAsync(thread.ThreadId, runId, runStartedAt, cancellationToken)");
        StringAssert.Contains(runtimeSource, "var entry = await GetActiveCoordinatorSessionForSteeringAsync(thread, options, actorCancellationToken)");
        StringAssert.Contains(runtimeSource, "return await _agentHub.SteerAsync(agentId, steerOptions, cancellationToken)");
        StringAssert.Contains(runtimeSource, "@event => _ = PostAgentEventToActorAsync(actor, thread.ThreadId, projector, @event)");
        StringAssert.Contains(runtimeSource, "projector.Project(@event);");
        StringAssert.Contains(runtimeSource, "var actor = _threadActors.GetOrCreate(threadId);");
        StringAssert.Contains(runtimeSource, "var result = await _threadActors.GetOrCreate(thread.ThreadId).ExecuteAsync(");
        StringAssert.Contains(runtimeSource, "return entry.Projector.ProjectHistory(history);");
    }

    [TestMethod]
    public void PluginOrchestrationHooks_AreNotFrontendOwned()
    {
        var sourceRoot = GetSourceRoot();
        var matches = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Select(file => new
            {
                RelativePath = Path.GetRelativePath(sourceRoot, file).Replace('\\', '/'),
                Content = File.ReadAllText(file),
            })
            .Where(static entry => entry.Content.Contains("PluginOrchestrationBridge", StringComparison.Ordinal))
            .Where(static entry => entry.RelativePath.StartsWith("CodeAlta/", StringComparison.Ordinal))
            .Select(static entry => entry.RelativePath)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(Array.Empty<string>(), matches);
    }

    [TestMethod]
    public void PluginDerivedEvents_DoNotBecomeCanonicalTranscript()
    {
        var sourceRoot = GetSourceRoot();
        var canonicalRoots = new[]
        {
            Path.Combine(sourceRoot, "CodeAlta.Agent"),
            Path.Combine(sourceRoot, "CodeAlta.Catalog"),
        };
        var matches = canonicalRoots
            .Where(Directory.Exists)
            .SelectMany(static root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(static file => File.ReadAllText(file).Contains("PluginDerivedThreadEvent", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(sourceRoot, file).Replace('\\', '/'))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(Array.Empty<string>(), matches);
    }

    [TestMethod]
    public void PluginShellTabs_AreFrontendOnlyAndStable()
    {
        var sourceRoot = GetSourceRoot();
        var nonFrontendMatches = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Select(file => new
            {
                RelativePath = Path.GetRelativePath(sourceRoot, file).Replace('\\', '/'),
                Content = File.ReadAllText(file),
            })
            .Where(static entry => entry.Content.Contains("PluginShellTabService", StringComparison.Ordinal) || entry.Content.Contains("PluginShellTabRequest", StringComparison.Ordinal))
            .Where(static entry =>
                !entry.RelativePath.StartsWith("CodeAlta/", StringComparison.Ordinal) &&
                !entry.RelativePath.StartsWith("CodeAlta.Tests/", StringComparison.Ordinal))
            .Select(static entry => entry.RelativePath)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        var serviceSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "PluginShellTabService.cs"));
        CollectionAssert.AreEqual(Array.Empty<string>(), nonFrontendMatches);
        StringAssert.Contains(serviceSource, "IShellTabService");
        StringAssert.Contains(serviceSource, "new ShellTabAssociation.Plugin");
        StringAssert.Contains(serviceSource, "return null;");
    }

    [TestMethod]
    public void FrontendProject_DoesNotContainReusableAgentConnection()
    {
        var frontendRoot = GetCodeAltaSourceRoot();
        var servicePath = Path.Combine(frontendRoot, "Services", "ChatAgentConnection.cs");
        Assert.IsFalse(File.Exists(servicePath));

        var matches = Directory
            .EnumerateFiles(frontendRoot, "*.cs", SearchOption.AllDirectories)
            .Where(static file => File.ReadAllText(file).Contains("AgentSessionConnection", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(frontendRoot, file).Replace('\\', '/'))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(Array.Empty<string>(), matches);
    }

    [TestMethod]
    public void ShellTabs_AreNotDomainModelSource()
    {
        var sourceRoot = GetSourceRoot();
        var lowerLayerRoots = new[]
        {
            Path.Combine(sourceRoot, "CodeAlta.Agent"),
            Path.Combine(sourceRoot, "CodeAlta.Catalog"),
            Path.Combine(sourceRoot, "CodeAlta.Orchestration"),
        };
        var matches = lowerLayerRoots
            .Where(Directory.Exists)
            .SelectMany(static root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(static file => File.ReadAllText(file).Contains("TabPage", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(sourceRoot, file).Replace('\\', '/'))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(Array.Empty<string>(), matches);
    }

    [TestMethod]
    public void FrontendUiFlows_DoNotUseConfigureAwaitFalseOutsideExplicitBackgroundBoundaries()
    {
        var codeAltaRoot = GetCodeAltaSourceRoot();
        var disallowedMatches = Directory
            .EnumerateFiles(codeAltaRoot, "*.cs", SearchOption.AllDirectories)
            .Select(file => new
            {
                File = file,
                RelativePath = Path.GetRelativePath(codeAltaRoot, file).Replace('\\', '/'),
                Content = File.ReadAllText(file),
            })
            .Where(static entry => entry.Content.Contains("ConfigureAwait(false)", StringComparison.Ordinal))
            .Where(static entry =>
                entry.RelativePath.StartsWith("Views/", StringComparison.Ordinal) ||
                entry.RelativePath.StartsWith("Frontend/", StringComparison.Ordinal) ||
                entry.RelativePath.StartsWith("Presentation/", StringComparison.Ordinal) ||
                entry.RelativePath.StartsWith("App/", StringComparison.Ordinal) &&
                entry.RelativePath is not
                    "App/AcpAgentRegistryService.cs" and
                    not
                    "App/ChatBackendInitializationCoordinator.cs" and
                    not "App/CodeAltaOwnedServices.cs" and
                    not "App/CodeAltaShellController.cs" and
                    not "App/KnownProjectImporter.cs" and
                    not "App/RuntimeEventPump.cs" and
                    not "App/ShellCatalogStateCoordinator.cs" and
                    not "App/ThreadPromptDispatchCoordinator.cs" and
                    not "App/ThreadPromptDraftPersistenceCoordinator.cs" and
                    not "App/ThreadViewStateCoordinator.cs" and
                    not "App/UiTaskDiagnostics.cs")
            .Select(static entry => entry.RelativePath)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(Array.Empty<string>(), disallowedMatches);
    }

    [TestMethod]
    public void ShellController_DoesNotReferenceTimelineOrDialogPresentationTypes()
    {
        var controllerSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "CodeAltaShellController.cs"));

        Assert.IsFalse(controllerSource.Contains("ThreadTimelinePresenter", StringComparison.Ordinal));
        Assert.IsFalse(controllerSource.Contains("ToolCallPresenter", StringComparison.Ordinal));
        Assert.IsFalse(controllerSource.Contains("SessionUsagePresenter", StringComparison.Ordinal));
        Assert.IsFalse(controllerSource.Contains("DocumentFlow", StringComparison.Ordinal));
        Assert.IsFalse(controllerSource.Contains("Dialog", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodeAltaApp_PartialBucketsDoNotReintroduceBridgeOrSettingsSlices()
    {
        var viewsRoot = Path.Combine(GetCodeAltaSourceRoot(), "Views");

        Assert.IsFalse(File.Exists(Path.Combine(viewsRoot, "CodeAltaApp.ControllerBridge.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(viewsRoot, "CodeAltaApp.Settings.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(viewsRoot, "CodeAltaApp.Usage.cs")));
    }

    [TestMethod]
    public void CodeAltaApp_DoesNotOwnDirectTabOrCommandControlFields()
    {
        var appSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "CodeAltaApp.cs"));

        Assert.IsFalse(appSource.Contains("Dictionary<string, TabPage>", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("_threadTabControl", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("_sendPromptButton", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("_chatBackendSelect", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("_chatModelSelect", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("_chatReasoningSelect", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("_statusSpinner", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SidebarDraftTitleEditor_IsRemoved()
    {
        var codeAltaRoot = GetCodeAltaSourceRoot();
        var sourceFiles = Directory.EnumerateFiles(codeAltaRoot, "*.cs", SearchOption.AllDirectories).ToArray();

        AssertSourceDoesNotContain(sourceFiles, "DraftThreadTitle");
        AssertSourceDoesNotContain(sourceFiles, "Thread Title (optional)");
    }

    [TestMethod]
    public void UiProjectionAndUsageFiles_KeepExplicitBindableAccessGuards()
    {
        var sidebarSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "SidebarCoordinator.cs"));
        var appSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "CodeAltaApp.cs"));

        Assert.IsTrue(sidebarSource.Contains("verifyBindableAccess();", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("private T ReadBindableState<T>(Func<T> read)", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ThreadingAbstractions_MoveOutsideAppNamespace()
    {
        var codeAltaRoot = GetCodeAltaSourceRoot();

        Assert.IsTrue(File.Exists(Path.Combine(codeAltaRoot, "Threading", "IUiDispatcher.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(codeAltaRoot, "Threading", "IFrontendUiScheduler.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(codeAltaRoot, "Threading", "FrontendUiScheduler.cs")));
        Assert.IsTrue(File.Exists(Path.Combine(codeAltaRoot, "Threading", "UiDispatch.cs")));
        Assert.IsTrue(File.Exists(Path.Combine(codeAltaRoot, "Threading", "TerminalUiDispatcher.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(codeAltaRoot, "App", "IUiDispatcher.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(codeAltaRoot, "App", "UiDispatch.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(codeAltaRoot, "App", "TerminalUiDispatcher.cs")));
    }

    [TestMethod]
    public void AppOwnedStateAndSharedViews_MoveOutOfLegacyLocations()
    {
        var codeAltaRoot = GetCodeAltaSourceRoot();

        Assert.IsTrue(File.Exists(Path.Combine(codeAltaRoot, "App", "State", "OpenThreadState.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(codeAltaRoot, "Models", "OpenThreadState.cs")));
        Assert.IsTrue(File.Exists(Path.Combine(codeAltaRoot, "App", "SidebarCoordinator.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(codeAltaRoot, "Views", "SidebarCoordinator.cs")));
        Assert.IsTrue(File.Exists(Path.Combine(codeAltaRoot, "Presentation", "Controls", "AnchoredPopupView.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(codeAltaRoot, "Views", "AnchoredPopupView.cs")));
    }

    [TestMethod]
    public void CodeAltaAppPresentationSlice_IsDeletedAndShellHelpersStayExtracted()
    {
        var viewsRoot = Path.Combine(GetCodeAltaSourceRoot(), "Views");
        var appSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "CodeAltaApp.cs"));

        Assert.IsFalse(File.Exists(Path.Combine(viewsRoot, "CodeAltaApp.Presentation.cs")));
        Assert.IsFalse(appSource.Contains("internal static string BuildHeaderText(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static string BuildDraftPromptMessage(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static string BuildDraftTabTitle(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static string BuildWelcomeSubtitle(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static IReadOnlyList<string> BuildWelcomeGuidanceLines(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static Visual BuildWelcomePane(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static string BuildReadyStatusText(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static string BuildThinkingStatusText(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static string BuildStatusIconMarkup(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static TextBlockStyle BuildStatusTextStyle(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static string CompactTabTitle(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static OpenTabIndicatorKind ResolveOpenTabIndicatorKind(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal enum StatusTone", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal readonly record struct StatusSnapshot", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal enum OpenTabIndicatorKind", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal sealed record InitialThreadSelection", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static StatusSnapshot ResolveSelectionStatus(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static InitialThreadSelection ResolveInitialSelection(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static string BuildThreadScopeSummary(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static IReadOnlyList<WorkThreadDescriptor> FilterThreadsForProject(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodeAltaAppPresentationSlice_DelegatesSelectorAndPromptAvailabilityWorkflow()
    {
        var appSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "CodeAltaApp.cs"));

        Assert.IsTrue(appSource.Contains("_modelProviderSelectorCoordinator.RefreshForDraftScope", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("_modelProviderSelectorCoordinator.OnModelProviderSelectionChanged", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("_modelProviderSelectorCoordinator.GetPreferredModelProviderId()", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private PromptComposerProjection BuildPromptComposerProjection(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodeAltaAppPresentationSlice_DelegatesTabStripWorkflow()
    {
        var appSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "CodeAltaApp.cs"));

        Assert.IsTrue(appSource.Contains("_threadTabStripCoordinator.SyncControl()", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("_threadTabStripCoordinator.OnSelectionChanged", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private ThreadTabStripProjection BuildThreadTabStripProjection(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private List<TabPage> BuildDesiredThreadTabPages(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private TabPage EnsureThreadTabPage(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private TabPage EnsureDraftTabPage(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodeAltaApp_DelegatesThreadHistoryWorkflow()
    {
        var appSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "CodeAltaApp.cs"));

        Assert.IsTrue(appSource.Contains("_threadHistoryCoordinator.EnsureLoadedAsync", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static ThreadHistoryLoadPlan CreateInitialThreadHistoryLoadPlan(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static int FindInitialThreadHistoryStartIndex(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static int CountRenderableHistoryMessages(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private async Task LoadEarlierThreadHistoryAsync(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodeAltaApp_DelegatesRuntimeEventWorkflow()
    {
        var appSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "CodeAltaApp.cs"));
        var compositionSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "CodeAltaFrontendComposition.cs"));
        var runtimeSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "ThreadRuntimeEventCoordinator.cs"));

        Assert.IsTrue(appSource.Contains("_threadRuntimeEventCoordinator.ApplyRuntimeEvent", StringComparison.Ordinal));
        Assert.IsTrue(compositionSource.Contains("new ThreadRuntimeEventCoordinator(", StringComparison.Ordinal));
        Assert.IsTrue(runtimeSource.Contains("new ThreadRuntimeStateReducer()", StringComparison.Ordinal));
        Assert.IsTrue(runtimeSource.Contains("new ThreadRuntimeTimelineRenderer(", StringComparison.Ordinal));
        Assert.IsFalse(runtimeSource.Contains("SessionUsageAggregator.Merge(", StringComparison.Ordinal));
        Assert.IsFalse(runtimeSource.Contains("tab.Timeline.UpsertInteraction(", StringComparison.Ordinal));
        Assert.IsFalse(runtimeSource.Contains("tab.Timeline.AddStatus(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static bool ShouldPromoteAgentEventToThinking(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static bool ShouldApplyShellChromeProjectionAfterRuntimeEvent(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private void UpdateThreadFromAgentEvent(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private void UpdateThreadSummary(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private static string SummarizeThreadContent(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodeAltaApp_UsesThreadCommandWorkflowWithoutLegacyDelegation()
    {
        var appSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "CodeAltaApp.cs"));
        var compositionSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "CodeAltaFrontendComposition.cs"));
        var creationSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "ThreadCreationCoordinator.cs"));
        var shellCommandSurfaceSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Frontend", "Commands", "ShellCommandSurfaceCoordinator.cs"));
        var threadCommandSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "ThreadCommandCoordinator.cs"));
        var executionOptionsSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "ThreadExecutionOptionsFactory.cs"));

        Assert.IsTrue(appSource.Contains("CodeAltaFrontendComposition.Create(", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("_shellCommandSurfaceCoordinator.SubmitCurrentPromptAsync", StringComparison.Ordinal));
        Assert.IsTrue(compositionSource.Contains("new ThreadCommandCoordinator(", StringComparison.Ordinal));
        Assert.IsTrue(shellCommandSurfaceSource.Contains("new ShellInputCoordinator(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("SubmitCurrentDelegationAsync", StringComparison.Ordinal));
        Assert.IsFalse(shellCommandSurfaceSource.Contains("CodeAlta.Thread.Delegate", StringComparison.Ordinal));
        Assert.IsFalse(threadCommandSource.Contains("GetThreadInput()", StringComparison.Ordinal));
        Assert.IsFalse(threadCommandSource.Contains("/help", StringComparison.Ordinal));
        Assert.IsFalse(threadCommandSource.Contains("AgentPermissionRequest", StringComparison.Ordinal));
        Assert.IsFalse(threadCommandSource.Contains("AgentUserInputRequest", StringComparison.Ordinal));
        Assert.IsFalse(threadCommandSource.Contains("new WorkThreadExecutionOptions", StringComparison.Ordinal));
        Assert.IsTrue(creationSource.Contains("_buildPreferredExecutionOptions(", StringComparison.Ordinal));
        Assert.IsFalse(threadCommandSource.Contains("DelegateThreadAsync", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private async Task<AgentPermissionDecision> HandleThreadPermissionRequestAsync(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private async Task<AgentUserInputResponse> HandleThreadUserInputRequestAsync(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private WorkThreadExecutionOptions BuildExecutionOptions(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private WorkThreadExecutionOptions BuildPreferredExecutionOptions(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private async Task<WorkThreadDescriptor?> CreateGlobalThreadAsync(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private async Task<WorkThreadDescriptor?> CreateProjectThreadAsync(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private static string CreateTransientThreadKey(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private string ResolveWorkingDirectory(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private IReadOnlyList<string> ResolveProjectRoots(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodeAltaApp_DelegatesThreadStateWorkflow()
    {
        var appSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "CodeAltaApp.cs"));
        var compositionSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "CodeAltaFrontendComposition.cs"));
        var threadStateSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "ShellThreadStateCoordinator.cs"));

        Assert.IsTrue(appSource.Contains("CodeAltaFrontendComposition.Create(", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("_threadStateCoordinator.LoadInitialCatalogStateAsync", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("TryResolveInitialCatalogState(cancellationToken)", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("_threadStateCoordinator.ApplyRecoveredCatalogState", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("_threadStateCoordinator.EnsureThreadTab", StringComparison.Ordinal));
        Assert.IsTrue(compositionSource.Contains("new ShellThreadStateCoordinator(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("new ShellThreadStateCoordinator(", StringComparison.Ordinal));
        Assert.IsTrue(threadStateSource.Contains("new ShellCatalogStateCoordinator(", StringComparison.Ordinal));
        Assert.IsFalse(threadStateSource.Contains("_projectCatalog.LoadAsync", StringComparison.Ordinal));
        Assert.IsFalse(threadStateSource.Contains("_threadCatalog.LoadInternalAsync", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private readonly Dictionary<string, OpenThreadState>", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private readonly ShellSelectionState", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private IReadOnlyList<ProjectDescriptor>", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private IReadOnlyList<WorkThreadDescriptor>", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FrontendComposition_DoesNotUseBroadFrontendServiceAdapter()
    {
        var appRoot = Path.Combine(GetCodeAltaSourceRoot(), "App");
        var compositionSource = File.ReadAllText(Path.Combine(appRoot, "CodeAltaFrontendComposition.cs"));
        var frontendServiceInterfaces = Directory.EnumerateFiles(GetCodeAltaSourceRoot(), "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"interface\s+\w*FrontendServices\b", RegexOptions.CultureInvariant))
            .ToArray();

        Assert.IsFalse(File.Exists(Path.Combine(appRoot, "ICodeAltaFrontendServices.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(appRoot, "CodeAltaFrontendServicesAdapter.cs")));
        Assert.IsFalse(compositionSource.Contains("ICodeAltaFrontendServices", StringComparison.Ordinal));
        Assert.IsFalse(compositionSource.Contains("CodeAltaFrontendServicesAdapter", StringComparison.Ordinal));
        Assert.AreEqual(0, frontendServiceInterfaces.Length);
    }

    [TestMethod]
    public void ShellStateStore_DocumentsBoundedSnapshotOwnership()
    {
        var stateRoot = Path.Combine(GetCodeAltaSourceRoot(), "App", "State");
        var source = File.ReadAllText(Path.Combine(stateRoot, "ShellStateStore.cs"));

        Assert.IsFalse(File.Exists(Path.Combine(stateRoot, "ShellFrontendStateStore.cs")));
        Assert.IsFalse(source.Contains("class ShellFrontendStateStore", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("Frontend state ownership remains split by domain owner", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("Catalog and selection restore", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("Live logical tabs", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("Prompt draft text and images", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("Model-provider selection and runtime state", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("Shell and thread status", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("File editor tabs", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("Plugin projections", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodeAltaApp_DocumentsCompositionRootBoundary()
    {
        var appSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "CodeAltaApp.cs"));
        var guideSource = File.ReadAllText(Path.GetFullPath(Path.Combine(GetCodeAltaSourceRoot(), "..", "..", "doc", "development-guide.md")));

        Assert.IsTrue(appSource.Contains("CodeAltaApp intentionally remains the TUI shell composition root", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("Add behavior to named owners first", StringComparison.Ordinal));
        Assert.IsTrue(guideSource.Contains("`CodeAltaApp` is the TUI shell composition root", StringComparison.Ordinal));
        Assert.IsTrue(guideSource.Contains("ShellFrontendHost` is only the run/tick/dispose lifecycle wrapper", StringComparison.Ordinal));
        Assert.IsTrue(guideSource.Contains("Remaining `CodeAltaApp` internal methods are grouped by owner", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodeAltaApp_DelegatesWorkspaceRefreshWorkflow()
    {
        var appSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "CodeAltaApp.cs"));

        Assert.IsTrue(appSource.Contains("_workspaceCoordinator.ApplyShellChromeProjection", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("_workspaceCoordinator.SetThreadStatus", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("_workspaceCoordinator.CreateComputedVisual", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private readonly State<int> _viewRefreshState", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private readonly State<int> _usageRefreshState", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private void RefreshThreadPaneContent(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private void SyncSelectedSessionUsageViewModel(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ShellWorkspaceCoordinator_RefreshSelectionRebuildsSidebarProjection()
    {
        var workspaceSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "WorkspaceProjectionController.cs"));

        Assert.IsTrue(workspaceSource.Contains("_workspaceContext.RefreshSidebarProjection();", StringComparison.Ordinal));
        Assert.IsFalse(workspaceSource.Contains("_workspaceContext.SyncSidebarSelectionToCurrentState();", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodeAltaApp_DelegatesBackendInitializationWorkflow()
    {
        var appSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "CodeAltaApp.cs"));

        Assert.IsTrue(appSource.Contains("_chatBackendInitializationCoordinator.InitializeAsync", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private async Task RefreshChatBackendStateAsync(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static (ChatBackendAvailability Availability, string StatusMessage) ClassifyBackendInitializationFailure(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodeAltaApp_DelegatesTerminalLoopLifecycle()
    {
        var appSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "CodeAltaApp.cs"));
        var loopSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "TerminalLoopCoordinator.cs"));
        var deferredAppSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "DeferredCodeAltaApp.cs"));

        Assert.IsTrue(appSource.Contains("public TerminalLoopResult Tick(CancellationToken cancellationToken)", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("_terminalLoopCoordinator.OnIteration(cancellationToken)", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("ToggleTerminalLoopCallback()", StringComparison.Ordinal));
        Assert.IsTrue(deferredAppSource.Contains("Terminal.RunAsync(", StringComparison.Ordinal));
        Assert.IsTrue(deferredAppSource.Contains("_app.Tick(cancellationToken)", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("_terminalLoopStarted", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("_shellController.AttachUiDispatcher(_uiDispatcher)", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("_runtimeEventPump.Start(cancellationToken)", StringComparison.Ordinal));
        Assert.IsFalse(loopSource.Contains("DrainPendingRuntimeEvents()", StringComparison.Ordinal));
        Assert.IsFalse(loopSource.Contains("_syncSidebarSelection();", StringComparison.Ordinal));
        Assert.IsTrue(loopSource.Contains("TerminalLoopResult.Continue", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WelcomePaneFactory_KeepsAnimatedAltaGradientStyle()
    {
        var welcomeSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Presentation", "Shell", "WelcomePaneFactory.cs"));

        Assert.IsTrue(welcomeSource.Contains(".Style(() => BuildWelcomeAltaFigletStyle(welcomeAnimationPhase01.Value))", StringComparison.Ordinal));
        Assert.IsFalse(welcomeSource.Contains("DateTime.UtcNow.Ticks", StringComparison.Ordinal));
        Assert.IsFalse(welcomeSource.Contains("private static readonly TextFigletStyle WelcomeAltaFigletStyle", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodeAltaApp_DoesNotConstructPromptEditorControls()
    {
        var appSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "CodeAltaApp.cs"));
        var workspaceSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "ThreadWorkspaceView.cs"));
        var promptComposerSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "PromptComposerView.cs"));

        Assert.IsFalse(appSource.Contains("private ChatPromptEditor CreatePromptEditor(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("new ChatPromptEditor(", StringComparison.Ordinal));
        Assert.IsFalse(workspaceSource.Contains("new ChatPromptEditor(", StringComparison.Ordinal));
        Assert.IsTrue(promptComposerSource.Contains("private static ChatPromptEditor CreatePromptEditor(", StringComparison.Ordinal));
        Assert.IsTrue(promptComposerSource.Contains("new ChatPromptEditor(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ThreadWorkspaceView_ExpandedPromptDialog_UsesCloseCommandsWithoutDirectKeyHandler()
    {
        var workspaceSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "ThreadWorkspaceView.cs"));
        var promptComposerSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "PromptComposerView.cs"));

        Assert.IsTrue(promptComposerSource.Contains("editor.AddCommand(CreateExpandedPromptDialogCloseCommand(\"CodeAlta.Thread.ExpandPrompt.Close\", new KeyGesture(TerminalKey.Escape)))", StringComparison.Ordinal));
        Assert.IsTrue(promptComposerSource.Contains("dialog.AddCommand(CreateExpandedPromptDialogCloseCommand(\"CodeAlta.Thread.ExpandPrompt.CloseWithCtrlEnter\", new KeyGesture(TerminalKey.Enter, TerminalModifiers.Ctrl), CommandPresentation.None))", StringComparison.Ordinal));
        Assert.IsFalse(workspaceSource.Contains("dialog.KeyDown(", StringComparison.Ordinal));
        Assert.IsFalse(promptComposerSource.Contains("dialog.KeyDown(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ThreadWorkspaceView_ExpandedPromptDialog_TransfersFocusOnOpenAndClose()
    {
        var promptComposerSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "PromptComposerView.cs"));

        Assert.IsTrue(promptComposerSource.Contains("dialog.App?.Focus(editor);", StringComparison.Ordinal));
        Assert.IsTrue(promptComposerSource.Contains("app?.Focus(Editor);", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ThreadWorkspaceView_BottomBar_RightAlignsPromptActions_AndSendBecomesAbort()
    {
        var workspaceSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "ThreadWorkspaceView.cs"));
        var promptComposerSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "PromptComposerView.cs"));
        var normalizedSource = workspaceSource.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.IsTrue(workspaceSource.Contains("promptComposerView.SendButton", StringComparison.Ordinal));
        Assert.IsTrue(normalizedSource.Contains("usageIndicator,\n            threadInfoButton,\n            promptComposerView.ExpandButton,\n            promptComposerView.SendButton,", StringComparison.Ordinal));
        Assert.IsTrue(promptComposerSource.Contains("var icon = isAbort ? $\"{NerdFont.MdSquare}\" : $\"{NerdFont.MdSend}\";", StringComparison.Ordinal));
        Assert.IsTrue(promptComposerSource.Contains("var tone = isAbort ? ControlTone.Error : ControlTone.Success;", StringComparison.Ordinal));
        Assert.IsTrue(promptComposerSource.Contains("var tooltipText = isAbort ? \"Abort the selected thread run.\" : \"Send the current prompt.\";", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ThreadDraftPersistence_UsesMachineSavedPromptsAndDeleteHooks()
    {
        var compositionSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "CodeAltaFrontendComposition.cs"));
        var catalogOptionsSource = File.ReadAllText(Path.GetFullPath(Path.Combine(GetCodeAltaSourceRoot(), "..", "CodeAlta.Catalog", "CatalogOptions.cs")));
        var promptDraftSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "PromptDraftUiCoordinator.cs"));
        var persistenceSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "ThreadPromptDraftPersistenceCoordinator.cs"));
        var threadStateSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "ShellThreadStateCoordinator.cs"));

        Assert.IsTrue(compositionSource.Contains("new ThreadPromptDraftService(frontend.LoadPromptDraft, frontend.DeletePromptDraft)", StringComparison.Ordinal));
        Assert.IsFalse(File.Exists(Path.Combine(GetCodeAltaSourceRoot(), "App", "IThreadStateFrontendPort.cs")));
        Assert.IsTrue(promptDraftSource.Contains("_promptDraftPersistence.ObservePromptDraft", StringComparison.Ordinal));
        Assert.IsTrue(catalogOptionsSource.Contains("saved_prompts", StringComparison.Ordinal));
        Assert.IsTrue(persistenceSource.Contains("PromptDraftsRoot", StringComparison.Ordinal));
        Assert.IsTrue(persistenceSource.Contains("saved_prompt_", StringComparison.Ordinal));
        Assert.IsTrue(threadStateSource.Contains("_promptDrafts.DeletePromptDraft(threadId);", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ThreadActivityIndicators_UseEditedDraftStateAndDotsSpinners()
    {
        var workspaceSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "ThreadStatusLineView.cs"));
        var sidebarHeaderSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "SidebarNodeHeaderView.cs"));
        var tabSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Presentation", "Tabs", "ThreadTabVisualFactory.cs"));
        var statusSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Presentation", "Shell", "StatusVisualFormatter.cs"));

        Assert.IsTrue(workspaceSource.Contains("new Spinner().Style(SpinnerStyles.Dots)", StringComparison.Ordinal));
        Assert.IsTrue(sidebarHeaderSource.Contains("new Spinner().Style(SpinnerStyles.Dots)", StringComparison.Ordinal));
        Assert.IsTrue(tabSource.Contains("OpenTabIndicatorKind.Edited", StringComparison.Ordinal));
        Assert.IsTrue(tabSource.Contains("new Spinner().Style(SpinnerStyles.Dots)", StringComparison.Ordinal));
        Assert.IsTrue(statusSource.Contains("BuildPromptEditedStatusText", StringComparison.Ordinal));
        Assert.IsTrue(statusSource.Contains("BuildPromptEditedIconMarkup", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ProjectThreadsDialog_ActionColumn_UsesDirectActivateButtonEditor()
    {
        var dialogSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "ProjectThreadsDialog.cs"));

        Assert.IsTrue(dialogSource.Contains("Header = new TextBlock(\"🧵 Thread\")", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("Header = new TextBlock(\"🤖 Provider\")", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("Header = new TextBlock(\"🕒 Updated\")", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("Header = new TextBlock(\"💬 Messages\")", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("Header = new TextBlock(\"🚀 Open\")", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("var row = (ProjectThreadsDialogRowViewModel)value.GetBinding().Owner;", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("return new TextBlock(() => row.LastUpdatedRelative)", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains(".Tooltip(new TextBlock(() => row.LastUpdatedExact));", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("CellTemplate = new DataTemplate<string>(BuildBackendCell, null)", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("SidebarThreadPresentation.BuildProviderMarkup(row.BackendId, row.BackendDisplayName, row.ThreadKind)", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains(".FilterRowVisible(_viewModel.Bind.FilterRowVisible)", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("new CheckBox(\"Filter row\").IsChecked(_viewModel.Bind.FilterRowVisible)", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("CellActivationMode = DataGridCellActivationMode.DirectActivate", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("CellTemplate = new DataTemplate<string>(BuildOpenButtonDisplay, null)", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("CellEditorTemplate = new DataTemplate<string>(null, BuildOpenButtonEditor)", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("IsHitTestVisible = false", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("BindingAccessor<ProjectThreadsDialogRowViewModel>", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("TypedValueAccessor = rowAccessor", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("ReadOnly = true", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("Show Filter", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("Hide Filter", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SidebarGlobalScope_CanOpenGlobalThreadsDialog()
    {
        var sidebarSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "SidebarView.cs"));
        var navigatorSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "NavigatorActionCoordinator.cs"));

        Assert.IsTrue(sidebarSource.Contains("SidebarRowActionKind.OpenProjectThreads when target?.Kind == SidebarSelectionKind.GlobalScope", StringComparison.Ordinal));
        Assert.IsTrue(navigatorSource.Contains("if (string.IsNullOrWhiteSpace(projectId))", StringComparison.Ordinal));
        Assert.IsTrue(navigatorSource.Contains("thread.Kind == WorkThreadKind.GlobalThread", StringComparison.Ordinal));
        Assert.IsTrue(navigatorSource.Contains("CreateGlobalDialogProject()", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SidebarCoordinator_PreservesMultipleExpandedProjectsAcrossRefresh()
    {
        var sidebarSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "SidebarCoordinator.cs"));
        var sidebarViewSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "SidebarView.cs"));

        Assert.IsTrue(sidebarSource.Contains("var expandedProjectIds = new HashSet<string>(_view.GetExpandedProjectIds(), StringComparer.OrdinalIgnoreCase);", StringComparison.Ordinal));
        Assert.IsTrue(sidebarSource.Contains("expandedProjectIds.Add(preferredExpandedProjectId);", StringComparison.Ordinal));
        Assert.IsTrue(sidebarViewSource.Contains("public IReadOnlyList<string> GetExpandedProjectIds()", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ModelProvidersDialog_UsesListBoxAndDedicatedViewModel()
    {
        var dialogSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "ModelProvidersDialog.cs"));
        var viewModelSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "ViewModels", "ModelProviderEditorItemViewModel.cs"));

        Assert.IsTrue(dialogSource.Contains("new ListBox<ModelProviderEditorItemViewModel>()", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("_providerList.SelectedIndex(_selectedProviderIndex.Bind.Value);", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("BuildProviderListItem(value)", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("BuildProviderListItemMarkup(value.GetValue())", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("BuildProviderListItem(value.GetValue())", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("new Select<ModelProviderEditorItemViewModel>()", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("ModelProviderEditorItemViewModel.IBindings", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("CreateBinding(", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("CreateTrackedCheckBox(", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("LoadDefinitionsIntoDialog(", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("private sealed class ModelProviderEditorItem", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("ScheduleProviderSelectionSync", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("_detailVersion", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("_diagnosticsVersion", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("item.Changed +=", StringComparison.Ordinal));
        Assert.IsTrue(viewModelSource.Contains("internal sealed partial class ModelProviderEditorItemViewModel", StringComparison.Ordinal));
        Assert.IsTrue(viewModelSource.Contains("[Bindable]", StringComparison.Ordinal));
        Assert.IsTrue(viewModelSource.Contains("public partial ModelProviderLastTestState LastTestState { get; private set; }", StringComparison.Ordinal));
        Assert.IsFalse(viewModelSource.Contains("public event Action<ModelProviderEditorItemViewModel, bool>? Changed;", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ModelProvidersDialog_GuardsAgainstClosingDuringActiveProviderOperations()
    {
        var dialogSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "ModelProvidersDialog.cs"));

        Assert.IsTrue(dialogSource.Contains("private readonly State<int> _activeOperationCount = new(0);", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("Please wait for the current provider operation to complete before closing this dialog.", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("TryBeginDialogOperation(", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("EndDialogOperation();", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ProviderFrontendCoordinator_TestProvider_DisposesBackendWithoutDoubleStop()
    {
        var coordinatorSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "ProviderFrontendCoordinator.cs"));

        Assert.IsTrue(coordinatorSource.Contains("await using var _ = backend;", StringComparison.Ordinal));
        Assert.IsTrue(coordinatorSource.Contains("await backend.StartAsync(cancellationToken);", StringComparison.Ordinal));
        Assert.IsTrue(coordinatorSource.Contains("var models = await backend.ListModelsAsync(cancellationToken);", StringComparison.Ordinal));
        Assert.IsFalse(coordinatorSource.Contains("await backend.StopAsync(cancellationToken);", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ModelProvidersDialog_CachesValidationMessagesToAvoidComputedLoops()
    {
        var dialogSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "ModelProvidersDialog.cs"));

        Assert.IsTrue(dialogSource.Contains("ValidationMessage? cachedMessage = null;", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("cachedText = text;", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("return cachedMessage;", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ModelProvidersDialog_QueuesProviderOperationsOutsideInputTracking()
    {
        var dialogSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "ModelProvidersDialog.cs"));

        Assert.IsTrue(dialogSource.Contains("_dialog.Dispatcher.InvokeAsync(", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("_ = Task.Run(", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains(".Click(() => StartReload(confirmWhenDirty: true));", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains(".Click(StartSave);", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains(".Click(() => StartTest(item));", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("PostToUi(", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("app.Post(action);", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("RunAfterTracking", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains(".Click(() => _ = TestSelectedAsync())", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("RestoreTerminalInputState", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("StartInput(new TerminalInputOptions", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("UseRawMode()", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("SetInputEcho(enabled: false)", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("EnableMouse(TerminalMouseMode.Drag)", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SidebarStateIndicators_KeepTreeIconsAndRefreshOnDraftOrRunChanges()
    {
        var compositionSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "CodeAltaFrontendComposition.cs"));
        var workspaceSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "WorkspaceProjectionController.cs"));
        var sidebarViewSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "SidebarView.cs"));

        Assert.IsTrue(compositionSource.Contains("new PromptDraftUiCoordinator(", StringComparison.Ordinal));
        Assert.IsTrue(workspaceSource.Contains("_workspaceContext.DispatchToUi(", StringComparison.Ordinal));
        Assert.IsTrue(workspaceSource.Contains("_workspaceContext.RefreshSidebarProjection();", StringComparison.Ordinal));
        Assert.IsFalse(sidebarViewSource.Contains("new Rune(' ')", StringComparison.Ordinal));
        Assert.IsTrue(sidebarViewSource.Contains("Icon = projection.Icon,", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PopupPresenters_RestorePromptFocusWhenClosed()
    {
        var controlsSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Presentation", "Controls", "AnchoredPopupView.cs"));
        var usageSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Presentation", "Usage", "SessionUsagePresenter.cs"));
        var threadInfoSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Presentation", "Threads", "ThreadInfoPresenter.cs"));

        Assert.IsTrue(controlsSource.Contains("_onClosed?.Invoke();", StringComparison.Ordinal));
        Assert.IsTrue(usageSource.Contains("new AnchoredPopupView(() => _createComputedVisual(BuildPopupContent), _focusPromptEditor)", StringComparison.Ordinal));
        Assert.IsTrue(threadInfoSource.Contains("new AnchoredPopupView(() => _createComputedVisual(BuildPopupContent), _focusPromptEditor)", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodeAltaSource_UsesBindingsInsteadOfRegisterDynamicUpdate()
    {
        var codeAltaRoot = GetCodeAltaSourceRoot();
        var sourceFiles = Directory.EnumerateFiles(codeAltaRoot, "*.cs", SearchOption.AllDirectories).ToArray();
        var workspaceSource = File.ReadAllText(Path.Combine(codeAltaRoot, "Views", "QueuedPromptStripView.cs"));

        AssertSourceDoesNotContain(sourceFiles, "RegisterDynamicUpdate(");
        AssertSourceDoesNotContain(sourceFiles, "BindingManager.");
        AssertSourceDoesNotContain(sourceFiles, "BindableObserver<");
        Assert.IsTrue(workspaceSource.Contains("QueuedPromptListView.Build(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodeAltaApp_SourceStaysWithinFacadeSizeBudget()
    {
        var appPath = Path.Combine(GetCodeAltaSourceRoot(), "App", "CodeAltaApp.cs");
        var appSize = new FileInfo(appPath).Length;

        Assert.IsTrue(appSize < 43000, $"CodeAltaApp.cs exceeded the temporary facade size budget: {appSize} bytes.");
    }

    [TestMethod]
    public void HelpAndWorkspaceCommands_ShareShellCommandCatalog()
    {
        var helpSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Frontend", "Help", "ShellHelpContentBuilder.cs"));
        var projectorSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Frontend", "Commands", "ShellCommandBindingProjector.cs"));

        Assert.IsTrue(helpSource.Contains("ShellCommandCatalog.Commands", StringComparison.Ordinal));
        Assert.IsTrue(projectorSource.Contains("ShellCommandCatalog.Get(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ShellSelectionState_UsesExplicitSelectionModel()
    {
        var selectionStateSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Models", "ShellSelectionState.cs"));

        Assert.IsTrue(selectionStateSource.Contains("ShellSelection Selection", StringComparison.Ordinal));
        Assert.IsFalse(selectionStateSource.Contains("bool DraftTabOpen", StringComparison.Ordinal));
        Assert.IsFalse(selectionStateSource.Contains("bool GlobalScopeSelected", StringComparison.Ordinal));
        Assert.IsFalse(selectionStateSource.Contains("string? SelectedThreadId", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ThreadSelectionContext_ExposesSelectionModelInsteadOfLegacySelectionBooleans()
    {
        var contextSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "Context", "ThreadSelectionContext.cs"));

        Assert.IsTrue(contextSource.Contains("public ShellSelection Selection", StringComparison.Ordinal));
        Assert.IsTrue(contextSource.Contains("public WorkspaceTarget Target", StringComparison.Ordinal));
        Assert.IsFalse(contextSource.Contains("public bool DraftTabOpen", StringComparison.Ordinal));
        Assert.IsFalse(contextSource.Contains("public bool GlobalScopeSelected", StringComparison.Ordinal));
        Assert.IsFalse(contextSource.Contains("public string? SelectedProjectId", StringComparison.Ordinal));
        Assert.IsFalse(contextSource.Contains("public string? SelectedThreadId", StringComparison.Ordinal));
    }

    [TestMethod]
    public void OpenThreadState_SplitsSessionWorkspaceAndTimelineLayers()
    {
        var openThreadStateSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "State", "OpenThreadState.cs"));

        Assert.IsTrue(openThreadStateSource.Contains("ThreadWorkspaceState Workspace", StringComparison.Ordinal));
        Assert.IsTrue(openThreadStateSource.Contains("ThreadTimelineState TimelineState", StringComparison.Ordinal));
        Assert.IsTrue(openThreadStateSource.Contains("ThreadSessionState Session", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AppContexts_DoNotExposeConcreteSelectorEditorOrLayoutControls()
    {
        var codeAltaRoot = GetCodeAltaSourceRoot();
        var modelProviderSelectorStateStoreSource = File.ReadAllText(Path.Combine(codeAltaRoot, "App", "State", "ModelProviderSelectorStateStore.cs"));
        var workspaceContextSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "Context", "ShellWorkspaceContext.cs"));

        Assert.IsFalse(File.Exists(Path.Combine(codeAltaRoot, "App", "Context", "ChatSelectorUiContext.cs")));
        Assert.IsFalse(modelProviderSelectorStateStoreSource.Contains("public Select<", StringComparison.Ordinal));
        Assert.IsFalse(modelProviderSelectorStateStoreSource.Contains("GetThreadInput()", StringComparison.Ordinal));
        Assert.IsFalse(workspaceContextSource.Contains("GetThreadPaneLayout()", StringComparison.Ordinal));
        Assert.IsFalse(workspaceContextSource.Contains("GetThreadBodySplitter()", StringComparison.Ordinal));
        Assert.IsFalse(workspaceContextSource.Contains("GetThreadInput()", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodeAltaApp_IsNoLongerPartial()
    {
        var codeAltaRoot = GetCodeAltaSourceRoot();
        var partialFiles = Directory
            .EnumerateFiles(codeAltaRoot, "CodeAltaApp*.cs", SearchOption.AllDirectories)
            .Where(static file => File.ReadAllText(file).Contains("partial class CodeAltaApp", StringComparison.Ordinal))
            .Select(static file => Path.GetRelativePath(GetCodeAltaSourceRoot(), file).Replace('\\', '/'))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.AreEqual(
            string.Empty,
            string.Join("|", partialFiles));
    }

    [TestMethod]
    public void ShellSurfaceConstruction_UsesSingleOptionsBagAndHostOwnsLifecycle()
    {
        var codeAltaRoot = GetCodeAltaSourceRoot();
        var sourceFiles = Directory.EnumerateFiles(codeAltaRoot, "*.cs", SearchOption.AllDirectories).ToArray();
        var allSource = string.Join(Environment.NewLine, sourceFiles.Select(File.ReadAllText));
        var appSource = File.ReadAllText(Path.Combine(codeAltaRoot, "App", "CodeAltaApp.cs"));
        var hostSource = File.ReadAllText(Path.Combine(codeAltaRoot, "App", "ShellFrontendHost.cs"));

        Assert.IsFalse(File.Exists(Path.Combine(codeAltaRoot, "App", "CodeAltaApp.Surface.cs")));
        Assert.IsFalse(allSource.Contains("CodeAltaAppSurfaceRequest", StringComparison.Ordinal));
        Assert.IsFalse(allSource.Contains("CodeAltaAppSurfaceFactory", StringComparison.Ordinal));
        Assert.IsTrue(File.Exists(Path.Combine(codeAltaRoot, "Views", "CodeAltaShellSurfaceOptions.cs")));
        Assert.IsTrue(appSource.Contains("CodeAltaShellViewFactory.CreateSurface(new CodeAltaShellSurfaceOptions", StringComparison.Ordinal));
        Assert.IsTrue(hostSource.Contains("Terminal.RunAsync(", StringComparison.Ordinal));
        Assert.IsTrue(hostSource.Contains("DisposeFrontendAsync", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("=> await _frontendHost.RunAsync(cancellationToken);", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("=> await _frontendHost.DisposeAsync();", StringComparison.Ordinal));
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

    private static IReadOnlyList<string> FindConstructorsWithTooManyDelegates(
        string codeAltaRoot,
        string file,
        int maxDelegateParameters)
    {
        var relativePath = Path.GetRelativePath(codeAltaRoot, file).Replace('\\', '/');
        var source = File.ReadAllText(file);
        return Regex.Matches(
                source,
                @"(?:public|internal|private)\s+(?<name>[A-Z][A-Za-z0-9_]*)\s*\((?<params>.*?)\)\s*(?:[:{])",
                RegexOptions.Singleline)
            .Cast<Match>()
            .Where(match => Regex.Matches(match.Groups["params"].Value, @"\b(?:Action|Func)\s*(?:<|\s+[A-Za-z_])").Count > maxDelegateParameters)
            .Select(match => $"{relativePath}:{match.Groups["name"].Value}")
            .ToArray();
    }

    private static IReadOnlyList<string> FindLargeDelegatingPorts(
        string codeAltaRoot,
        string file,
        int maxDelegateFields)
    {
        var relativePath = Path.GetRelativePath(codeAltaRoot, file).Replace('\\', '/');
        var source = File.ReadAllText(file);
        return Regex.Matches(
                source,
                @"internal\s+sealed\s+class\s+(?<name>Delegating\w+Port)\b(?<body>.*?)(?=\ninternal\s+(?:sealed\s+)?class\s+|\ninternal\s+interface\s+|\z)",
                RegexOptions.Singleline)
            .Cast<Match>()
            .Select(match => new
            {
                Name = match.Groups["name"].Value,
                Count = Regex.Matches(match.Groups["body"].Value, @"private\s+readonly\s+(?:Action|Func)\b").Count,
            })
            .Where(port => port.Count > maxDelegateFields)
            .Select(port => $"{relativePath}:{port.Name}:{port.Count}")
            .ToArray();
    }

    private static string GetCodeAltaSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (TryGetCodeAltaSourceRoot(directory.FullName, out var candidate) ||
                TryGetCodeAltaSourceRoot(Path.Combine(directory.FullName, "CodeAlta"), out candidate) ||
                TryGetCodeAltaSourceRoot(Path.Combine(directory.FullName, "src", "CodeAlta"), out candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        Assert.Fail("Could not locate the CodeAlta source directory from the test output path.");
        return null!;
    }

    private static bool TryGetCodeAltaSourceRoot(string candidate, [NotNullWhen(true)] out string? sourceRoot)
    {
        if (Directory.Exists(Path.Combine(candidate, "App")) &&
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
