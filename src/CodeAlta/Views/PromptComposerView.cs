using CodeAlta.Frontend.Commands;
using CodeAlta.Models;
using CodeAlta.Presentation.Chat;
using CodeAlta.Presentation.Prompting;
using CodeAlta.Presentation.Shell;
using CodeAlta.Presentation.Styling;
using CodeAlta.Catalog;
using PluginPromptEditorContribution = CodeAlta.Plugins.Abstractions.PluginPromptEditorContribution;
using CodeAlta.ViewModels;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Graphics;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Extensions.Markdown;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Text;

namespace CodeAlta.Views;

internal sealed class PromptComposerView
{
    private readonly PromptComposerViewModel _viewModel;
    private readonly Binding<string?> _promptText;
    private readonly Action _openHelp;
    private readonly Action _openCommandPalette;
    private readonly IProjectFileSearchService _projectFileSearchService;
    private readonly Func<string?> _getPromptReferenceProjectRoot;
    private readonly IReadOnlyList<PluginPromptEditorContribution> _promptEditorContributions;
    private readonly Func<Rectangle?> _getDialogBounds;
    private readonly PromptImageAttachmentStripView _promptImageAttachmentStripView;
    private Dialog? _expandedPromptDialog;

    public PromptComposerView(
        PromptComposerViewModel viewModel,
        ShellCommandSurfaceCoordinator shellCommandSurfaceCoordinator,
        IProjectFileSearchService projectFileSearchService,
        Func<string?> getPromptReferenceProjectRoot,
        IReadOnlyList<PluginPromptEditorContribution> promptEditorContributions,
        Binding<string?> promptText,
        PromptImageAttachmentStripView promptImageAttachmentStripView,
        Func<Rectangle?> getDialogBounds,
        PromptComposerViewController controller)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(shellCommandSurfaceCoordinator);
        ArgumentNullException.ThrowIfNull(projectFileSearchService);
        ArgumentNullException.ThrowIfNull(getPromptReferenceProjectRoot);
        ArgumentNullException.ThrowIfNull(promptEditorContributions);
        ArgumentNullException.ThrowIfNull(promptImageAttachmentStripView);
        ArgumentNullException.ThrowIfNull(getDialogBounds);
        ArgumentNullException.ThrowIfNull(controller);

        _viewModel = viewModel;
        _promptText = promptText;
        _openHelp = controller.OpenHelp;
        _openCommandPalette = controller.OpenCommandPalette;
        _projectFileSearchService = projectFileSearchService;
        _getPromptReferenceProjectRoot = getPromptReferenceProjectRoot;
        _promptEditorContributions = promptEditorContributions;
        _getDialogBounds = getDialogBounds;
        _promptImageAttachmentStripView = promptImageAttachmentStripView;

        Editor = CreatePromptEditor(viewModel, controller.OpenHelp, controller.OpenCommandPalette, projectFileSearchService, getPromptReferenceProjectRoot, promptEditorContributions, controller.AcceptPrompt, shellCommandSurfaceCoordinator, promptText)
            .IsEnabled(viewModel.Bind.IsEnabled);
        _promptImageAttachmentStripView.ConfigurePromptImagePasteHandler(Editor);
        EditorView = Editor.Scrollable().IsTabStop(false);
        SendButton = CreatePromptActionButton(viewModel, controller.SendPrompt, controller.AbortSession);
        ExpandButton = CreateIconButton(
            $"{TerminalIcons.MdSquareEditOutline}",
            SR.T("Open the current prompt in a large editor window (F6)."),
            OpenExpandedPromptDialog,
            button => button.IsEnabled(viewModel.Bind.IsEnabled));
    }

    public ChatPromptEditor Editor { get; }

    public Visual EditorView { get; }

    public Visual SendButton { get; }

    public Visual ExpandButton { get; }

    public void OpenExpandedPromptDialog()
    {
        if (_expandedPromptDialog is { App: not null })
        {
            return;
        }

        var editor = CreateStyledPromptEditor(_ => CloseExpandedPromptDialog(), _openHelp, _openCommandPalette, _projectFileSearchService, _getPromptReferenceProjectRoot, _promptEditorContributions, placeholder: null)
            .Placeholder(_viewModel.Bind.Placeholder)
            .Text(_promptText)
            .MinHeight(12)
            .IsEnabled(_viewModel.Bind.IsEnabled);
        _promptImageAttachmentStripView.ConfigurePromptImagePasteHandler(editor);
        editor.AddCommand(CreateExpandedPromptDialogCloseCommand("CodeAlta.Session.ExpandPrompt.Close", new KeyGesture(TerminalKey.Escape)));
        editor.AddCommand(CreateExpandedPromptDialogCloseCommand("CodeAlta.Session.ExpandPrompt.CloseWithCtrlEnter", new KeyGesture(TerminalKey.Enter, TerminalModifiers.Ctrl), CommandPresentation.None));

        var closeButton = new Button(new TextBlock($"{TerminalIcons.MdClose} {SR.T("Close")}"))
        {
            HorizontalAlignment = Align.End,
            VerticalAlignment = Align.Start,
            Tone = ControlTone.Error,
        };
        closeButton.Click(CloseExpandedPromptDialog);

        var dialog = new Dialog()
            .Title(SR.T("Edit Prompt"))
            .TopRightText(closeButton)
            .BottomRightText(new Markup($"[dim]{SR.T("Enter/Esc/Ctrl+Enter Close · Shift+Enter New line · draft preserved")}[/]"))
            .IsModal(true)
            .Padding(1)
            .Content(editor.Scrollable().IsTabStop(false));
        ResponsiveDialogSize.Apply(dialog, _getDialogBounds(), minWidth: 60, minHeight: 18);
        dialog.AddCommand(CreateExpandedPromptDialogCloseCommand("CodeAlta.Session.ExpandPrompt.Close", new KeyGesture(TerminalKey.Escape)));
        dialog.AddCommand(CreateExpandedPromptDialogCloseCommand("CodeAlta.Session.ExpandPrompt.CloseWithCtrlEnter", new KeyGesture(TerminalKey.Enter, TerminalModifiers.Ctrl), CommandPresentation.None));

        _expandedPromptDialog = dialog;
        dialog.Show();
        dialog.App?.Focus(editor);

        Command CreateExpandedPromptDialogCloseCommand(string id, KeyGesture gesture, CommandPresentation presentation = CommandPresentation.CommandBar)
            => new()
            {
                Id = id,
                LabelMarkup = SR.T("Close"),
                DescriptionMarkup = SR.T("Close the large prompt editor and keep the current draft."),
                Gesture = gesture,
                Importance = CommandImportance.Primary,
                Presentation = presentation,
                Execute = _ => CloseExpandedPromptDialog(),
            };
    }

    private void CloseExpandedPromptDialog()
    {
        var dialog = _expandedPromptDialog;
        _expandedPromptDialog = null;
        var app = dialog?.App ?? Editor.App;
        dialog?.Close();
        app?.Focus(Editor);
    }

    private static ChatPromptEditor CreatePromptEditor(
        PromptComposerViewModel promptComposerViewModel,
        Action openHelp,
        Action openCommandPalette,
        IProjectFileSearchService projectFileSearchService,
        Func<string?> getPromptReferenceProjectRoot,
        IReadOnlyList<PluginPromptEditorContribution> promptEditorContributions,
        Action<string> acceptPrompt,
        ShellCommandSurfaceCoordinator shellCommandSurfaceCoordinator,
        Binding<string?> promptText)
    {
        var editor = CreateStyledPromptEditor(acceptPrompt, openHelp, openCommandPalette, projectFileSearchService, getPromptReferenceProjectRoot, promptEditorContributions, placeholder: null)
            .Placeholder(promptComposerViewModel.Bind.Placeholder)
            .Text(promptText);

        foreach (var command in shellCommandSurfaceCoordinator.CommandsFor(ShellCommandPlacement.PromptEditor))
        {
            editor.AddCommand(shellCommandSurfaceCoordinator.CreateViewCommand(command));
        }

        return editor;
    }

    internal static ChatPromptEditor CreateStyledPromptEditor(
        Action<string> onAccepted,
        Action? onOpenHelp,
        Action? onOpenCommandPalette,
        string? placeholder)
        => CreateStyledPromptEditor(onAccepted, onOpenHelp, onOpenCommandPalette, projectFileSearchService: null, getPromptReferenceProjectRoot: null, promptEditorContributions: [], placeholder);

    internal static ChatPromptEditor CreateStyledPromptEditor(
        Action<string> onAccepted,
        Action? onOpenHelp,
        Action? onOpenCommandPalette,
        IProjectFileSearchService? projectFileSearchService,
        Func<string?>? getPromptReferenceProjectRoot,
        IReadOnlyList<PluginPromptEditorContribution> promptEditorContributions,
        string? placeholder)
    {
        ArgumentNullException.ThrowIfNull(onAccepted);

        var converter = new MarkdownMarkupConverter();
        ITextSnapshot? cachedSnapshot = null;
        Theme? cachedTheme = null;
        string? cachedText = null;
        string? cachedProjectRoot = null;
        List<StyledRun>? cachedRuns = null;
        ChatPromptEditor? editor = null;
        editor = new ChatPromptEditor(onAccepted, onOpenHelp, onOpenCommandPalette)
            .PromptMarkup("[primary]>[/] ")
            .ContinuationPromptMarkup("[muted]·[/] ")
            .Placeholder(placeholder)
            .EscapeBehavior(PromptEditorEscapeBehavior.CancelCompletionOnly)
            .EnableWordHints(true)
            .Highlighter(HighlightMarkdown)
            .MinHeight(3)
            .Style(PromptEditorStyle.Default with
            {
                Padding = new Thickness(0, 0, 1, 0),
            });
        if (projectFileSearchService is not null && getPromptReferenceProjectRoot is not null)
        {
            editor.EnableProjectFileReferences(
                projectFileSearchService,
                ProjectFileAppearanceRegistry.Default,
                getPromptReferenceProjectRoot);
        }

        if (promptEditorContributions.Count > 0)
        {
            editor.EnablePromptEditorContributions(promptEditorContributions);
        }

        return editor;

        void HighlightMarkdown(in PromptEditorHighlightRequest request, List<StyledRun> runs)
        {
            if (cachedRuns is not null &&
                ReferenceEquals(cachedSnapshot, request.Snapshot) &&
                Equals(cachedTheme, request.Theme) &&
                string.Equals(cachedProjectRoot, getPromptReferenceProjectRoot?.Invoke(), StringComparison.Ordinal))
            {
                runs.AddRange(cachedRuns);
                return;
            }

            var text = SnapshotToString(request.Snapshot);
            var projectRoot = getPromptReferenceProjectRoot?.Invoke();
            if (cachedRuns is not null &&
                string.Equals(cachedText, text, StringComparison.Ordinal) &&
                Equals(cachedTheme, request.Theme) &&
                string.Equals(cachedProjectRoot, projectRoot, StringComparison.Ordinal))
            {
                cachedSnapshot = request.Snapshot;
                runs.AddRange(cachedRuns);
                return;
            }

            converter.Theme = request.Theme;
            converter.Highlight(text, runs);
            ProjectFilePromptHighlighter.AddRuns(text, projectRoot, request.Theme, runs);
            cachedSnapshot = request.Snapshot;
            cachedTheme = request.Theme;
            cachedText = text;
            cachedProjectRoot = projectRoot;
            cachedRuns = [.. runs];
        }

        static string SnapshotToString(ITextSnapshot snapshot)
        {
            if (snapshot.Length == 0)
            {
                return string.Empty;
            }

            return string.Create(snapshot.Length, snapshot, static (span, s) => s.CopyTo(0, span));
        }
    }

    private static Visual CreatePromptActionButton(
        PromptComposerViewModel promptComposerViewModel,
        Action sendPrompt,
        Action abortSession)
    {
        return new ComputedVisual(() =>
        {
            var isAbort = promptComposerViewModel.CanAbort;
            var icon = isAbort ? $"{TerminalIcons.MdSquare}" : $"{TerminalIcons.MdSend}";
            var tooltipText = isAbort ? SR.T("Abort the selected session run.") : SR.T("Send the current prompt.");
            var action = isAbort ? abortSession : sendPrompt;
            var tone = isAbort ? ControlTone.Error : ControlTone.Success;
            var isEnabled = isAbort ? promptComposerViewModel.CanAbort : promptComposerViewModel.CanSend;

            return CreateIconButton(
                icon,
                tooltipText,
                action,
                button =>
                {
                    button.Tone = tone;
                    button.IsEnabled = isEnabled;
                });
        });
    }

    private static Visual CreateIconButton(string icon, string tooltipText, Action onClick, Action<Button>? configureButton = null)
    {
        var button = new Button(new TextBlock(icon) { Wrap = false, IsSelectable = false })
            .Click(onClick);
        configureButton?.Invoke(button);
        return button.Tooltip(new TextBlock(tooltipText));
    }
}
