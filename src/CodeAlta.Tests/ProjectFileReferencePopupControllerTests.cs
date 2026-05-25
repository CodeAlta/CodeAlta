using System.Diagnostics;
using System.Reflection;
using CodeAlta.Presentation.Prompting;
using CodeAlta.Catalog;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Views;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Hosting;
using XenoAtom.Terminal.UI.Input;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ProjectFileReferencePopupControllerTests
{
    [TestMethod]
    public void PromptEditorPluginHostExposesPromptReferenceProjectRoot()
    {
        using var tempDirectory = TempDirectory.Create();
        var session = new FakeProjectFileSearchSession(CreateState(tempDirectory.Path, [], isRefreshing: false, candidateCount: 0));
        var editor = new ChatPromptEditor(_ => { })
            .EnableProjectFileReferences(new FakeProjectFileSearchService(session), ProjectFileAppearanceRegistry.Default, () => tempDirectory.Path);

        var host = (IPluginPromptEditorHost)editor;

        Assert.AreEqual(tempDirectory.Path, host.ProjectPath);
    }

    [TestMethod]
    public void DialogOpensAcceptsSelectionAndRecordsUsage()
    {
        using var tempDirectory = TempDirectory.Create();
        var projectRoot = tempDirectory.Path;
        Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
        File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), string.Empty);
        Directory.CreateDirectory(Path.Combine(projectRoot, "docs"));

        var session = new FakeProjectFileSearchSession(
            CreateState(
                projectRoot,
                [
                    CreateResult(projectRoot, "src/app.cs"),
                    CreateResult(projectRoot, "docs", ProjectFileSearchItemKind.Directory),
                ],
                isRefreshing: false,
                candidateCount: 2));
        var service = new FakeProjectFileSearchService(session);
        var editor = new ChatPromptEditor(_ => { })
            .EnableProjectFileReferences(service, ProjectFileAppearanceRegistry.Default, () => projectRoot);

        using var terminalSession = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(120, 40)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var app = new TerminalApp(
            CreateHostRoot(editor),
            terminalSession.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
                EnableMouse = true,
                MouseMode = TerminalMouseMode.Move,
            });

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            app.Focus(editor);
            InvokeTerminalApp(app, "DispatchTextInput", "@");

            Assert.IsTrue(WaitForCondition(app, () => editor.HasProjectFileReferencePopup, TimeSpan.FromSeconds(1)));
            Assert.IsTrue(WaitForCondition(app, () => editor.ProjectFileReferenceItems.Count == 2, TimeSpan.FromSeconds(1)));
            Assert.AreEqual(1, session.RefreshCount);
            Assert.AreEqual("@", editor.Text);
            Assert.AreEqual(string.Empty, editor.ProjectFileReferenceQueryText);
            Assert.AreNotSame(editor, app.FocusedElement);

            InvokeKeyEvent(app, TerminalKey.Down);
            Assert.AreEqual("docs", editor.ProjectFileReferenceItems[editor.ProjectFileReferenceSelectedIndex].Result.Item.RelativePath);

            InvokeKeyEvent(app, TerminalKey.Enter);

            Assert.AreEqual("[docs](docs)", editor.Text);
            Assert.IsFalse(editor.HasProjectFileReferencePopup);
            Assert.AreSame(editor, app.FocusedElement);
            Assert.AreEqual(1, service.UsageEvents.Count);
            Assert.AreEqual(ProjectFileUsageAccessKind.PopupAccepted, service.UsageEvents[0].AccessKind);
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    [TestMethod]
    public void DialogRendersModalSearchSurfaceWhenOpened()
    {
        using var tempDirectory = TempDirectory.Create();
        var projectRoot = tempDirectory.Path;
        Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
        File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), string.Empty);

        var session = new FakeProjectFileSearchSession(
            CreateState(
                projectRoot,
                [CreateResult(projectRoot, "src/app.cs")],
                isRefreshing: false,
                candidateCount: 1));
        var service = new FakeProjectFileSearchService(session);
        var editor = new ChatPromptEditor(_ => { })
            .EnableProjectFileReferences(service, ProjectFileAppearanceRegistry.Default, () => projectRoot);

        using var terminalSession = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(120, 40)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var app = new TerminalApp(
            CreateHostRoot(editor),
            terminalSession.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
            });

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            app.Focus(editor);
            InvokeTerminalApp(app, "DispatchTextInput", "@");

            Assert.IsTrue(WaitForCondition(app, () => editor.ProjectFileReferenceItems.Count == 1, TimeSpan.FromSeconds(1)));
            TickTerminalApp(app);

            var backend = (InMemoryTerminalBackend)terminalSession.Instance.Backend;
            StringAssert.Contains(backend.GetOutText(), "Project files");
            StringAssert.Contains(backend.GetOutText(), "app.cs");
            StringAssert.Contains(backend.GetOutText(), "Arrows move");
            StringAssert.Contains(backend.GetOutText(), "1 indexed");
            StringAssert.Contains(backend.GetOutText(), "Enter inserts the selected link");
            Assert.AreNotSame(editor, app.FocusedElement);
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    [TestMethod]
    public void DialogSeedsSearchQueryFromExistingReference()
    {
        using var tempDirectory = TempDirectory.Create();
        var projectRoot = tempDirectory.Path;
        Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
        File.WriteAllText(Path.Combine(projectRoot, "src", "Program.cs"), string.Empty);

        var session = new FakeProjectFileSearchSession(
            CreateState(
                projectRoot,
                [CreateResult(projectRoot, "src/Program.cs")],
                isRefreshing: false,
                candidateCount: 1,
                query: "Program"));
        var service = new FakeProjectFileSearchService(session);
        var editor = new ChatPromptEditor(_ => { })
            .EnableProjectFileReferences(service, ProjectFileAppearanceRegistry.Default, () => projectRoot);

        using var terminalSession = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(120, 40)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var app = new TerminalApp(
            CreateHostRoot(editor),
            terminalSession.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
            });

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            editor.Text = "Open @Program";
            editor.CaretIndex = editor.Text.Length;
            editor.RefreshProjectFileReferencePopup();

            Assert.IsTrue(WaitForCondition(app, () => editor.HasProjectFileReferencePopup, TimeSpan.FromSeconds(1)));
            Assert.AreEqual("Program", editor.ProjectFileReferenceQueryText);
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    [TestMethod]
    public void DialogKeepsTypingResponsiveWhenSessionFloodsBackgroundUpdates()
    {
        using var tempDirectory = TempDirectory.Create();
        var projectRoot = tempDirectory.Path;
        Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
        File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), string.Empty);

        var session = new FloodingProjectFileSearchSession(
            CreateState(
                projectRoot,
                [CreateResult(projectRoot, "src/app.cs")],
                isRefreshing: false,
                candidateCount: 1),
            updateCountPerQuery: 1000);
        var service = new FakeProjectFileSearchService(session);
        var editor = new ChatPromptEditor(_ => { })
            .EnableProjectFileReferences(service, ProjectFileAppearanceRegistry.Default, () => projectRoot);

        using var terminalSession = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(120, 40)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var app = new TerminalApp(
            CreateHostRoot(editor),
            terminalSession.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
            });

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            app.Focus(editor);
            InvokeTerminalApp(app, "DispatchTextInput", "@");
            Assert.IsTrue(WaitForCondition(app, () => editor.HasProjectFileReferencePopup, TimeSpan.FromSeconds(1)));

            var elapsed = Stopwatch.StartNew();
            InvokeTerminalApp(app, "DispatchTextInput", "a");
            InvokeTerminalApp(app, "DispatchTextInput", "b");
            InvokeTerminalApp(app, "DispatchTextInput", "c");
            elapsed.Stop();

            Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromMilliseconds(200), $"Typing while dialog updates were flooding took {elapsed.Elapsed}.");
            Assert.IsTrue(WaitForCondition(app, () => editor.ProjectFileReferenceQueryText == "abc", TimeSpan.FromSeconds(1)));
            Assert.AreEqual("@", editor.Text);
            Assert.AreEqual(1, editor.CaretIndex);
            Assert.IsTrue(editor.HasProjectFileReferencePopup);
            Assert.AreNotSame(editor, app.FocusedElement);
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    [TestMethod]
    public void EscapeClosesDialogWithoutChangingPromptText()
    {
        using var tempDirectory = TempDirectory.Create();
        var projectRoot = tempDirectory.Path;
        Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
        File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), string.Empty);

        var session = new FakeProjectFileSearchSession(
            CreateState(
                projectRoot,
                [CreateResult(projectRoot, "src/app.cs")],
                isRefreshing: false,
                candidateCount: 1));
        var service = new FakeProjectFileSearchService(session);
        var editor = new ChatPromptEditor(_ => { })
            .EnableProjectFileReferences(service, ProjectFileAppearanceRegistry.Default, () => projectRoot);

        using var terminalSession = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(120, 40)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var app = new TerminalApp(
            CreateHostRoot(editor),
            terminalSession.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
            });

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            app.Focus(editor);
            InvokeTerminalApp(app, "DispatchTextInput", "@");
            Assert.IsTrue(WaitForCondition(app, () => editor.HasProjectFileReferencePopup, TimeSpan.FromSeconds(1)));

            InvokeKeyEvent(app, TerminalKey.Escape);

            Assert.IsFalse(editor.HasProjectFileReferencePopup);
            Assert.AreEqual("@", editor.Text);
            Assert.AreSame(editor, app.FocusedElement);
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    [TestMethod]
    public void DialogShowsImmediatelyWhenSessionCreationIsSlowAsync()
    {
        using var tempDirectory = TempDirectory.Create();
        var projectRoot = tempDirectory.Path;
        Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
        File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), string.Empty);

        var session = new FakeProjectFileSearchSession(
            CreateState(
                projectRoot,
                [CreateResult(projectRoot, "src/app.cs")],
                isRefreshing: false,
                candidateCount: 1));
        var service = new BlockingProjectFileSearchService(session, TimeSpan.FromMilliseconds(400));
        var editor = new ChatPromptEditor(_ => { })
            .EnableProjectFileReferences(service, ProjectFileAppearanceRegistry.Default, () => projectRoot);

        using var terminalSession = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(120, 40)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var app = new TerminalApp(
            CreateHostRoot(editor),
            terminalSession.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
            });

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            app.Focus(editor);
            InvokeTerminalApp(app, "DispatchTextInput", "@");

            Assert.IsTrue(WaitForCondition(app, () => editor.HasProjectFileReferencePopup, TimeSpan.FromMilliseconds(200)));

            var elapsed = Stopwatch.StartNew();
            InvokeTerminalApp(app, "DispatchTextInput", "P");
            elapsed.Stop();

            Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromMilliseconds(200), $"Typing while the session was still opening took {elapsed.Elapsed}.");
            Assert.AreEqual("P", editor.ProjectFileReferenceQueryText);
            Assert.IsTrue(WaitForCondition(app, () => editor.ProjectFileReferenceItems.Count == 1, TimeSpan.FromSeconds(2)));
            Assert.AreEqual(1, service.CreateSessionCallCount);
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    [TestMethod]
    public void AppearanceRegistry_UserOverridesBeatPluginAndBuiltIns()
    {
        var config = new ProjectFileAppearanceConfig
        {
            Directory = new ProjectFileAppearanceDescriptor("dir-user", Colors.MediumPurple, "user"),
        };
        config.Extensions[".cs"] = new ProjectFileAppearanceDescriptor("user-cs", Colors.Red, "user");
        var registry = new ProjectFileAppearanceRegistry(
            [new FakeAppearanceContribution()],
            config);

        var fileAppearance = registry.GetAppearance(CreateItem("c:\\repo", "Program.cs"));
        var directoryAppearance = registry.GetAppearance(CreateItem("c:\\repo", "src", ProjectFileSearchItemKind.Directory));

        Assert.AreEqual("user-cs", fileAppearance.Icon);
        Assert.AreEqual(Colors.Red, fileAppearance.IconForeground);
        Assert.AreEqual("dir-user", directoryAppearance.Icon);
    }

    private static ProjectFileSearchState CreateState(
        string projectRoot,
        IReadOnlyList<ProjectFileSearchResult> results,
        bool isRefreshing,
        int candidateCount,
        string query = "")
    {
        return new ProjectFileSearchState
        {
            Query = query,
            Results = results,
            IsRefreshing = isRefreshing,
            HasSnapshot = true,
            RefreshGeneration = 1,
            SnapshotGeneration = 1,
            CandidateCount = candidateCount,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static ProjectFileSearchResult CreateResult(
        string projectRoot,
        string relativePath,
        ProjectFileSearchItemKind kind = ProjectFileSearchItemKind.File,
        double score = 100)
        => new(CreateItem(projectRoot, relativePath, kind), score, false);

    private static ProjectFileSearchItem CreateItem(
        string projectRoot,
        string relativePath,
        ProjectFileSearchItemKind kind = ProjectFileSearchItemKind.File)
    {
        var basename = Path.GetFileName(relativePath);
        var extension = kind == ProjectFileSearchItemKind.Directory ? string.Empty : Path.GetExtension(basename);
        return new ProjectFileSearchItem
        {
            Kind = kind,
            ProjectRoot = projectRoot,
            RelativePath = relativePath.Replace('\\', '/'),
            FullPath = Path.Combine(projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)),
            Basename = basename,
            ParentPath = Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? string.Empty,
            Extension = extension,
            LastWriteTimeUtc = DateTimeOffset.UtcNow,
            SearchFields = new ProjectFileSearchFields(
                basename.ToLowerInvariant(),
                relativePath.ToLowerInvariant(),
                relativePath.Split('/').Select(static segment => segment.ToLowerInvariant()).ToArray(),
                extension.ToLowerInvariant()),
            Usage = null,
        };
    }

    private static void InvokeTerminalApp(TerminalApp app, string methodName)
    {
        var method = typeof(TerminalApp).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        method.Invoke(app, null);
    }

    private static void InvokeTerminalApp(TerminalApp app, string methodName, params object[] arguments)
    {
        var method = typeof(TerminalApp).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        method.Invoke(app, arguments);
    }

    private static void InvokeKeyEvent(TerminalApp app, TerminalKey key)
    {
        var method = typeof(TerminalApp).GetMethod("DispatchKeyEvent", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        method.Invoke(app, [new TerminalKeyEvent { Key = key }, true]);
    }

    private static bool WaitForCondition(TerminalApp app, Func<bool> condition, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(condition);

        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            TickTerminalApp(app);
            if (condition())
            {
                return true;
            }

            Thread.Sleep(10);
        }

        TickTerminalApp(app);
        return condition();
    }

    private static void TickTerminalApp(TerminalApp app)
    {
        var method = typeof(TerminalApp).GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        method.Invoke(app, [null]);
    }

    private static Visual CreateHostRoot(ChatPromptEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        return editor;
    }

    private sealed class FakeProjectFileSearchService(IProjectFileSearchSession session) : IProjectFileSearchService
    {
        public List<ProjectFileUsageEvent> UsageEvents { get; } = [];

        public ValueTask<IProjectFileSearchSession> CreateSessionAsync(
            ProjectFileSearchSessionOptions options,
            CancellationToken cancellationToken = default)
        {
            _ = session.RefreshAsync(cancellationToken);
            return ValueTask.FromResult(session);
        }

        public ValueTask<ProjectFileResolution> ResolveAsync(
            ProjectFileResolveQuery query,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask RecordUsageAsync(
            ProjectFileUsageEvent usageEvent,
            CancellationToken cancellationToken = default)
        {
            UsageEvents.Add(usageEvent);
            return ValueTask.CompletedTask;
        }

        public ValueTask InvalidateAsync(
            string projectRoot,
            ProjectFileInvalidationReason reason,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    private sealed class BlockingProjectFileSearchService(FakeProjectFileSearchSession session, TimeSpan createDelay) : IProjectFileSearchService
    {
        public int CreateSessionCallCount => _createSessionCallCount;

        private int _createSessionCallCount;

        public ValueTask<IProjectFileSearchSession> CreateSessionAsync(
            ProjectFileSearchSessionOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _createSessionCallCount);
            Thread.Sleep(createDelay);
            return ValueTask.FromResult<IProjectFileSearchSession>(session);
        }

        public ValueTask<ProjectFileResolution> ResolveAsync(
            ProjectFileResolveQuery query,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask RecordUsageAsync(
            ProjectFileUsageEvent usageEvent,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask InvalidateAsync(
            string projectRoot,
            ProjectFileInvalidationReason reason,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    private sealed class FakeProjectFileSearchSession(ProjectFileSearchState initialState) : IProjectFileSearchSession
    {
        private ProjectFileSearchState _current = initialState;

        public ProjectFileSearchState Current => _current;

        public int RefreshCount { get; private set; }

        public event EventHandler<ProjectFileSearchStateChangedEventArgs>? Updated;

        public ValueTask SetQueryAsync(string query, CancellationToken cancellationToken = default)
        {
            _current = _current with { Query = query };
            Updated?.Invoke(this, new ProjectFileSearchStateChangedEventArgs(_current));
            return ValueTask.CompletedTask;
        }

        public ValueTask RefreshAsync(CancellationToken cancellationToken = default)
        {
            RefreshCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
            => ValueTask.CompletedTask;
    }

    private sealed class FloodingProjectFileSearchSession(ProjectFileSearchState initialState, int updateCountPerQuery) : IProjectFileSearchSession
    {
        private ProjectFileSearchState _current = initialState;
        private int _queryGeneration;

        public ProjectFileSearchState Current => _current;

        public event EventHandler<ProjectFileSearchStateChangedEventArgs>? Updated;

        public ValueTask SetQueryAsync(string query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _current = _current with { Query = query };
            var generation = Interlocked.Increment(ref _queryGeneration);
            _ = Task.Run(() =>
            {
                for (var i = 0; i < updateCountPerQuery; i++)
                {
                    if (generation != Volatile.Read(ref _queryGeneration))
                    {
                        return;
                    }

                    var state = _current with
                    {
                        Query = query,
                        CandidateCount = i + 1,
                        IsRefreshing = i < updateCountPerQuery - 1,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };
                    _current = state;
                    Updated?.Invoke(this, new ProjectFileSearchStateChangedEventArgs(state));
                }
            }, cancellationToken);
            return ValueTask.CompletedTask;
        }

        public ValueTask RefreshAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
            => ValueTask.CompletedTask;
    }

    private sealed class FakeAppearanceContribution : IProjectFileAppearanceContribution
    {
        public IReadOnlyDictionary<string, ProjectFileAppearanceDescriptor> Extensions { get; } =
            new Dictionary<string, ProjectFileAppearanceDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                [".cs"] = new("plugin-cs", Colors.DodgerBlue, "plugin"),
            };

        public IReadOnlyDictionary<string, ProjectFileAppearanceDescriptor> Files { get; } =
            new Dictionary<string, ProjectFileAppearanceDescriptor>(StringComparer.OrdinalIgnoreCase);

        public ProjectFileAppearanceDescriptor? Directory { get; } =
            new("dir-plugin", Colors.Goldenrod, "plugin");
    }

    private sealed class TempDirectory(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"CodeAlta.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
