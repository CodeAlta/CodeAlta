using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Prompting;
using CodeAlta.ViewModels;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Graphics;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;
using ImageControl = XenoAtom.Terminal.UI.Graphics.Image;

namespace CodeAlta.Views;

internal sealed class PromptImageAttachmentStripView
{
    private readonly PromptComposerViewModel _promptComposerViewModel;
    private readonly Func<IReadOnlyList<PromptImageAttachment>> _getPromptImages;
    private readonly Func<string> _getNextPromptImageTitle;
    private readonly Action<PromptImageAttachment> _addPromptImage;
    private readonly Action<string, string> _renamePromptImage;
    private readonly Action<string> _deletePromptImage;
    private readonly Func<bool> _canPastePromptImages;
    private readonly Func<string> _getPromptImageUnsupportedMessage;
    private readonly Action<string, StatusTone> _setPromptImageStatus;
    private readonly Func<Rectangle?> _getDialogBounds;
    private readonly Func<ChatPromptEditor?> _getPromptEditor;

    public PromptImageAttachmentStripView(
        PromptComposerViewModel promptComposerViewModel,
        PromptImageWorkspaceCallbacks? promptImageCallbacks,
        Func<Rectangle?> getDialogBounds,
        Func<ChatPromptEditor?> getPromptEditor)
    {
        ArgumentNullException.ThrowIfNull(promptComposerViewModel);
        ArgumentNullException.ThrowIfNull(getDialogBounds);
        ArgumentNullException.ThrowIfNull(getPromptEditor);

        _promptComposerViewModel = promptComposerViewModel;
        var promptImages = promptImageCallbacks ?? PromptImageWorkspaceCallbacks.Empty;
        _getPromptImages = promptImages.GetPromptImages;
        _getNextPromptImageTitle = promptImages.GetNextPromptImageTitle;
        _addPromptImage = promptImages.AddPromptImage;
        _renamePromptImage = promptImages.RenamePromptImage;
        _deletePromptImage = promptImages.DeletePromptImage;
        _canPastePromptImages = promptImages.CanPastePromptImages;
        _getPromptImageUnsupportedMessage = promptImages.GetPromptImageUnsupportedMessage;
        _setPromptImageStatus = promptImages.SetPromptImageStatus;
        _getDialogBounds = getDialogBounds;
        _getPromptEditor = getPromptEditor;
        Root = new ComputedVisual(BuildPromptImageAttachmentStrip);
    }

    public Visual Root { get; }

    public void ConfigurePromptImagePasteHandler(ChatPromptEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        editor.ClipboardPasteHandler = new TextEditorClipboardPasteHandler(HandlePromptClipboardPaste);
    }

    private Visual BuildPromptImageAttachmentStrip()
    {
        _ = _promptComposerViewModel.PromptImageAttachmentVersion;
        var images = _getPromptImages();
        if (images.Count == 0)
        {
            return new Placeholder { IsVisible = false };
        }

        var children = new List<Visual>(images.Count + 1)
        {
            new Markup($"[dim]{SR.T("Images ({0})", images.Count)}[/]") { Wrap = false },
        };
        foreach (var image in images)
        {
            var imageId = image.Id;
            children.Add(new Button(new TextBlock($"▧ {image.Title}") { Wrap = false })
                .Click(() => OpenPromptImageDetailsDialog(imageId))
                .Tooltip(new TextBlock(SR.T("Open pasted image {0}", image.Title))));
        }

        return new Border(new HStack([.. children]) { Spacing = 1, HorizontalAlignment = Align.Stretch })
        {
            HorizontalAlignment = Align.Stretch,
        }
        .Padding(new Thickness(1, 0, 1, 0));
    }

    private string? HandlePromptClipboardPaste(TextEditorClipboardPasteContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.HasImage)
        {
            return null;
        }

        var defaultTitle = _getNextPromptImageTitle();
        if (!_canPastePromptImages())
        {
            _setPromptImageStatus(_getPromptImageUnsupportedMessage(), StatusTone.Warning);
            return string.Empty;
        }

        if (PromptImageClipboardReader.TryReadImage(context, defaultTitle, out var image, out var failureReason) && image is not null)
        {
            OpenAddPromptImageDialog(image);
            return string.Empty;
        }

        _setPromptImageStatus(failureReason ?? SR.T("The clipboard does not contain a supported image payload."), StatusTone.Warning);
        return string.Empty;
    }

    private void OpenAddPromptImageDialog(PromptImageAttachment image)
    {
        ArgumentNullException.ThrowIfNull(image);

        Dialog? dialog = null;
        var titleState = new State<string?>(image.Title);
        var titleBox = new TextBox().Text(titleState).HorizontalAlignment(Align.Stretch);
        var size = ResponsiveDialogSize.Resolve(_getDialogBounds(), minWidth: 64, minHeight: 22, widthFactor: 0.65, heightFactor: 0.65);
        var preview = CreatePromptImagePreview(
            image,
            cellWidth: Math.Max(24, size.Width - 6),
            cellHeight: Math.Max(8, size.Height - 9));
        var addButton = new Button(new TextBlock(SR.T("Add To Prompt"))) { Tone = ControlTone.Primary };
        var cancelButton = new Button(new TextBlock(SR.T("Cancel")));

        void AddImage()
        {
            var title = PromptImageAttachment.NormalizeTitle(titleState.Value ?? string.Empty);
            _addPromptImage(image.WithTitle(title));
            dialog?.Close();
            FocusPromptEditor();
            _setPromptImageStatus(SR.T("Added image {0} to the prompt.", title), StatusTone.Ready);
        }

        void Cancel()
        {
            dialog?.Close();
            FocusPromptEditor();
        }

        addButton.Click(AddImage);
        cancelButton.Click(Cancel);
        var content = new DockLayout()
            .Top(new Markup($"[dim]{SR.T("Review the pasted image and choose a title before adding it to the prompt.")}[/]") { Wrap = true })
            .Content(new Border(preview).Padding(1))
            .Bottom(CreatePromptImageDialogBottom(titleBox, addButton, cancelButton))
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch);

        dialog = new Dialog()
            .Title(SR.T("Add Image To Prompt"))
            .BottomRightText(new Markup($"[dim]{SR.T("Ctrl+Enter Add · Esc Cancel")}[/]"))
            .IsModal(true)
            .Padding(1)
            .Content(content);
        dialog.Width(size.Width).Height(size.Height).MinWidth(64).MinHeight(22);
        dialog.AddCommand(new Command { Id = "CodeAlta.Prompt.ImageAdd.Accept", LabelMarkup = SR.T("Add To Prompt"), DescriptionMarkup = SR.T("Add the pasted image to the prompt."), Gesture = new KeyGesture(TerminalKey.Enter, TerminalModifiers.Ctrl), Importance = CommandImportance.Primary, Execute = _ => AddImage() });
        dialog.AddCommand(new Command { Id = "CodeAlta.Prompt.ImageAdd.Cancel", LabelMarkup = SR.T("Cancel"), DescriptionMarkup = SR.T("Cancel adding the pasted image."), Gesture = new KeyGesture(TerminalKey.Escape), Importance = CommandImportance.Primary, Execute = _ => Cancel() });
        dialog.Show();
        dialog.App?.Focus(titleBox);
    }

    private void OpenPromptImageDetailsDialog(string imageId)
    {
        var image = _getPromptImages().FirstOrDefault(image => string.Equals(image.Id, imageId, StringComparison.Ordinal));
        if (image is null)
        {
            return;
        }

        Dialog? dialog = null;
        var titleState = new State<string?>(image.Title);
        var size = ResponsiveDialogSize.Resolve(_getDialogBounds(), minWidth: 64, minHeight: 20, widthFactor: 0.8, heightFactor: 0.8);
        var titleBox = new TextBox().Text(titleState).HorizontalAlignment(Align.Stretch);
        var preview = CreatePromptImagePreview(
            image,
            cellWidth: Math.Max(24, size.Width - 8),
            cellHeight: Math.Max(8, size.Height - 10));
        var saveButton = new Button(new TextBlock(SR.T("Rename"))) { Tone = ControlTone.Primary };
        var deleteButton = new Button(new TextBlock(SR.T("Delete"))) { Tone = ControlTone.Error };
        var closeButton = new Button(new TextBlock(SR.T("Close")));

        void Close()
        {
            dialog?.Close();
            FocusPromptEditor();
        }

        void Rename()
        {
            var title = PromptImageAttachment.NormalizeTitle(titleState.Value ?? string.Empty);
            _renamePromptImage(imageId, title);
            dialog?.Title(title);
            _setPromptImageStatus(SR.T("Renamed image to {0}.", title), StatusTone.Ready);
        }

        void Delete()
        {
            _deletePromptImage(imageId);
            dialog?.Close();
            FocusPromptEditor();
            _setPromptImageStatus(SR.T("Deleted image {0} from the prompt.", image.Title), StatusTone.Ready);
        }

        saveButton.Click(Rename);
        deleteButton.Click(Delete);
        closeButton.Click(Close);
        var content = new DockLayout()
            .Top(new Markup($"[dim]{SR.T("Preview, rename, or remove this prompt image attachment.")}[/]") { Wrap = true })
            .Content(new Border(preview).Padding(1))
            .Bottom(CreatePromptImageDialogBottom(titleBox, saveButton, deleteButton, closeButton))
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch);

        dialog = new Dialog()
            .Title(image.Title)
            .BottomRightText(new Markup($"[dim]{SR.T("Esc Close")}[/]"))
            .IsModal(true)
            .Padding(1)
            .Content(content);
        dialog.Width(size.Width).Height(size.Height).MinWidth(64).MinHeight(20);
        dialog.AddCommand(new Command { Id = "CodeAlta.Prompt.ImageDetails.Close", LabelMarkup = SR.T("Close"), DescriptionMarkup = SR.T("Close image preview."), Gesture = new KeyGesture(TerminalKey.Escape), Importance = CommandImportance.Primary, Execute = _ => Close() });
        dialog.Show();
        dialog.App?.Focus(titleBox);
    }

    private void FocusPromptEditor()
    {
        var promptEditor = _getPromptEditor();
        promptEditor?.App?.Focus(promptEditor);
    }

    private static Visual CreatePromptImageDialogBottom(TextBox titleBox, params Button[] buttons)
    {
        ArgumentNullException.ThrowIfNull(titleBox);
        ArgumentNullException.ThrowIfNull(buttons);

        var titleEditor = new Grid()
            .Rows(new RowDefinition { Height = GridLength.Auto })
            .Columns(new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Star(1) })
            .Cell(new TextBlock(SR.T("Title")) { Wrap = false }, 0, 0)
            .Cell(titleBox, 0, 1);
        var buttonRow = new HStack(buttons)
        {
            Spacing = 2,
            HorizontalAlignment = Align.End,
        };

        return new Grid()
            .Rows(new RowDefinition { Height = GridLength.Auto })
            .Columns(new ColumnDefinition { Width = GridLength.Star(1) }, new ColumnDefinition { Width = GridLength.Auto })
            .Cell(titleEditor, 0, 0)
            .Cell(buttonRow, 0, 1);
    }

    private static Visual CreatePromptImagePreview(PromptImageAttachment image, int cellWidth, int cellHeight)
    {
        var fallback = new Border(new VStack(
            new TextBlock(image.Title) { Wrap = false },
            new TextBlock($"{image.MediaType} · {FormatByteSize(image.Bytes.Length)}") { Wrap = false }))
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };
        return new ImageControl(TerminalImageSource.FromEncodedBytes(image.Bytes, $"prompt-image:{image.Id}"))
        {
            CellWidth = cellWidth,
            CellHeight = cellHeight,
            ScaleMode = ImageScaleMode.Fit,
            PreserveAspectRatio = true,
            AccessibilityText = image.Title,
            FallbackContent = fallback,
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };
    }

    private static string FormatByteSize(int byteCount)
    {
        if (byteCount < 1024)
        {
            return $"{byteCount} B";
        }

        var kib = byteCount / 1024d;
        if (kib < 1024)
        {
            return $"{kib:0.#} KiB";
        }

        return $"{kib / 1024d:0.#} MiB";
    }
}
