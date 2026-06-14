using System.Reflection;
using CodeAlta.Catalog;
using CodeAlta.Views;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using CodeAlta.Presentation.Styling;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Hosting;

namespace CodeAlta.Tests;

[TestClass]
public sealed class DirectoryPathCompletionProviderTests
{
    [TestMethod]
    public void GetSuggestions_ListsMatchingDirectoriesWithinParent()
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

            var result = provider.GetSuggestions(input);

            CollectionAssert.AreEqual(
                new[]
                {
                    new OpenProjectSuggestion(OpenProjectSuggestionKind.Directory, Path.Combine(parent, "CodeAlta") + Path.DirectorySeparatorChar, Path.Combine(parent, "CodeAlta") + Path.DirectorySeparatorChar),
                    new OpenProjectSuggestion(OpenProjectSuggestionKind.Directory, Path.Combine(parent, "Codex") + Path.DirectorySeparatorChar, Path.Combine(parent, "Codex") + Path.DirectorySeparatorChar),
                },
                result.ToArray());
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
    public void GetSuggestions_ExistingDirectoryWithTrailingSeparator_IncludesDirectoryBeforeChildren()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-open-folder-tests", Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(root, "CodeAlta");
        Directory.CreateDirectory(Path.Combine(projectRoot, ".github"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "src"));

        try
        {
            var provider = new DirectoryPathCompletionProvider(root);
            var input = projectRoot + Path.DirectorySeparatorChar;

            var result = provider.GetSuggestions(input);

            Assert.AreEqual(3, result.Count);
            Assert.AreEqual(new OpenProjectSuggestion(OpenProjectSuggestionKind.Directory, input, input), result[0]);
            CollectionAssert.AreEqual(
                new[]
                {
                    new OpenProjectSuggestion(OpenProjectSuggestionKind.Directory, Path.Combine(projectRoot, ".github") + Path.DirectorySeparatorChar, Path.Combine(projectRoot, ".github") + Path.DirectorySeparatorChar),
                    new OpenProjectSuggestion(OpenProjectSuggestionKind.Directory, Path.Combine(projectRoot, "src") + Path.DirectorySeparatorChar, Path.Combine(projectRoot, "src") + Path.DirectorySeparatorChar),
                },
                result.Skip(1).ToArray());
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
    public void GetSuggestions_BlankInput_IncludesProjectsBeforeRoots()
    {
        var provider = new DirectoryPathCompletionProvider(
            Environment.CurrentDirectory,
            projects: () => [CreateProject("codealta", "CodeAlta")]);

        var result = provider.GetSuggestions(string.Empty);

        Assert.IsTrue(result.Count > 0);
        Assert.AreEqual(OpenProjectSuggestionKind.Project, result[0].Kind);
        Assert.AreEqual("CodeAlta", result[0].PrimaryText);
        Assert.AreEqual(Path.Combine(Path.GetTempPath(), "codealta"), result[0].SecondaryText);
        Assert.IsTrue(result.Any(static candidate => candidate.Kind == OpenProjectSuggestionKind.Directory));
    }

    [TestMethod]
    public void GetSuggestions_WhitespaceInput_ReturnsNoSuggestions()
    {
        var provider = new DirectoryPathCompletionProvider(
            Environment.CurrentDirectory,
            projects: () => [CreateProject("codealta", "CodeAlta")]);

        var result = provider.GetSuggestions(" ");

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void GetSuggestions_ProjectReference_UsesDisplayNameOnly()
    {
        var projects = new[]
        {
            CreateProject("codealta", "CodeAlta"),
            CreateProject("codex-cli", "Workbench"),
            CreateProject("other", "Other"),
        };
        var provider = new DirectoryPathCompletionProvider(
            Environment.CurrentDirectory,
            projects: () => projects);

        var result = provider.GetSuggestions("Cod");

        CollectionAssert.AreEqual(
            new[]
            {
                new OpenProjectSuggestion(OpenProjectSuggestionKind.Project, "CodeAlta", "CodeAlta", Path.Combine(Path.GetTempPath(), "codealta")),
            },
            result.ToArray());
    }

    [TestMethod]
    public void GetSuggestions_HiddenProjects_AreExcludedUntilIncludeHiddenIsEnabled()
    {
        var includeHidden = false;
        var hiddenProject = CreateProject("hidden-project", "Hidden Project");
        hiddenProject.Archived = true;
        var provider = new DirectoryPathCompletionProvider(
            Environment.CurrentDirectory,
            includeHidden: () => includeHidden,
            projects: () => [CreateProject("codealta", "CodeAlta"), hiddenProject]);

        var hiddenResult = provider.GetSuggestions("Hid");

        Assert.AreEqual(0, hiddenResult.Count);

        includeHidden = true;
        hiddenResult = provider.GetSuggestions("Hid");

        CollectionAssert.AreEqual(
            new[]
            {
                new OpenProjectSuggestion(OpenProjectSuggestionKind.Project, "Hidden Project", "Hidden Project", Path.Combine(Path.GetTempPath(), "hidden-project")),
            },
            hiddenResult.ToArray());
    }

    [TestMethod]
    public void Dialog_RendersProjectDisplayNameAndFolderPath()
    {
        const string projectPath = @"C:\repo\CodeAlta";
        var dialog = new DirectoryPathDialog(
            "Open Project",
            "Type a project name from the sidebar or a rooted folder path.",
            "Open",
            CreateDialogService(() => [CreateProject("codealta", "CodeAlta", projectPath)]));

        using var terminalSession = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(120, 40)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var app = new TerminalApp(
            new TextBlock("host"),
            terminalSession.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
            });

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            dialog.Show();
            TickTerminalApp(app);

            var backend = (InMemoryTerminalBackend)terminalSession.Instance.Backend;
            var output = backend.GetOutText();
            StringAssert.Contains(output, "CodeAlta");
            StringAssert.Contains(output, projectPath);
            StringAssert.Contains(output, TerminalIcons.MdFolderOutline.ToString());
            StringAssert.Contains(output, "╭");
            StringAssert.Contains(output, "Ctrl+I hidden");
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    [TestMethod]
    public void Dialog_WhitespaceInput_ShowsValidationMessage()
    {
        var dialog = new DirectoryPathDialog(
            "Open Project",
            "Type a project name from the sidebar or a rooted folder path.",
            "Open",
            CreateDialogService());

        using var terminalSession = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(120, 40)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var app = new TerminalApp(
            new TextBlock("host"),
            terminalSession.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
            });

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            dialog.Show();
            TickTerminalApp(app);

            var backend = (InMemoryTerminalBackend)terminalSession.Instance.Backend;
            backend.PushEvent(new TerminalTextEvent { Text = " " });
            TickTerminalApp(app);

            StringAssert.Contains(backend.GetOutText(), "A project name or rooted path is required.");
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    [TestMethod]
    public void Dialog_SubmitExistingDirectoryWithTrailingSeparator_OpensTypedDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-open-folder-tests", Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(root, "CodeAlta");
        Directory.CreateDirectory(Path.Combine(projectRoot, ".github"));

        try
        {
            var openedPath = string.Empty;
            var typedPath = projectRoot + Path.DirectorySeparatorChar;
            var dialog = new DirectoryPathDialog(
                "Open Project",
                "Type a project name from the sidebar or a rooted folder path.",
                "Open",
                CreateDialogService(openFolderAsync: (path, _) =>
                {
                    openedPath = path;
                    return Task.CompletedTask;
                }));

            using var terminalSession = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(120, 40)), new TerminalOptions { ImplicitStartInput = true }, force: true);
            var app = new TerminalApp(
                new TextBlock("host"),
                terminalSession.Instance,
                new TerminalAppOptions
                {
                    HostKind = TerminalHostKind.Fullscreen,
                });

            InvokeTerminalApp(app, "BeginRun");
            try
            {
                dialog.Show();
                TickTerminalApp(app);

                var backend = (InMemoryTerminalBackend)terminalSession.Instance.Backend;
                backend.PushEvent(new TerminalTextEvent { Text = typedPath });
                TickTerminalApp(app);

                backend.PushEvent(new TerminalKeyEvent { Key = TerminalKey.Enter });
                TickTerminalApp(app);

                Assert.AreEqual(typedPath, openedPath);
            }
            finally
            {
                InvokeTerminalApp(app, "EndRun");
            }
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
    public void Dialog_ToggleIncludeHiddenCommand_RefreshesSuggestions()
    {
        var visibleProject = CreateProject("codealta", "CodeAlta", @"C:\repo\CodeAlta");
        var hiddenProject = CreateProject("hidden", "Hidden Project", @"C:\repo\Hidden");
        hiddenProject.Archived = true;

        var dialog = new DirectoryPathDialog(
            "Open Project",
            "Type a project name from the sidebar or a rooted folder path.",
            "Open",
            CreateDialogService(() => [visibleProject, hiddenProject]));

        using var terminalSession = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(120, 40)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var app = new TerminalApp(
            new TextBlock("host"),
            terminalSession.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
            });

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            dialog.Show();
            TickTerminalApp(app);

            var backend = (InMemoryTerminalBackend)terminalSession.Instance.Backend;
            Assert.IsFalse(backend.GetOutText().Contains("Hidden Project", StringComparison.Ordinal));

            var dialogField = typeof(DirectoryPathDialog).GetField("_dialog", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(dialogField);
            var hostDialog = (Dialog?)dialogField.GetValue(dialog);
            Assert.IsNotNull(hostDialog);
            var toggleCommand = hostDialog.Commands.FirstOrDefault(static command => string.Equals(command.Id, "CodeAlta.DirectoryPathDialog.ToggleIncludeHidden", StringComparison.Ordinal));
            Assert.IsNotNull(toggleCommand);

            toggleCommand.Execute(hostDialog);
            TickTerminalApp(app);

            StringAssert.Contains(backend.GetOutText(), "Hidden Project");
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    private static void InvokeTerminalApp(TerminalApp app, string methodName)
    {
        var method = typeof(TerminalApp).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        method.Invoke(app, null);
    }

    private static void TickTerminalApp(TerminalApp app)
    {
        var method = typeof(TerminalApp).GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        method.Invoke(app, [null]);
    }

    private static DirectoryPathDialogService CreateDialogService(
        Func<IEnumerable<ProjectDescriptor>>? getProjects = null,
        Func<string, bool, Task>? openFolderAsync = null)
        => new(
            () => new XenoAtom.Terminal.UI.Geometry.Rectangle(0, 0, 120, 40),
            static () => null,
            openFolderAsync ?? (static (_, _) => Task.CompletedTask),
            getProjects: getProjects);

    private static ProjectDescriptor CreateProject(string slug, string displayName, string? projectPath = null)
    {
        return new ProjectDescriptor
        {
            Id = $"project-{slug}",
            Slug = slug,
            Name = displayName,
            DisplayName = displayName,
            ProjectPath = projectPath ?? Path.Combine(Path.GetTempPath(), slug),
            DefaultBranch = "main",
        };
    }
}
