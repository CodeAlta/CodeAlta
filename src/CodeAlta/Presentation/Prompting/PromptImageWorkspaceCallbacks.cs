using CodeAlta.Catalog;
using CodeAlta.Models;

namespace CodeAlta.Presentation.Prompting;

internal sealed class PromptImageWorkspaceCallbacks
{
    public static PromptImageWorkspaceCallbacks Empty { get; } = new(
        static () => [],
        static () => SR.T("Image-1"),
        static _ => { },
        static (_, _) => { },
        static _ => { },
        static () => false,
        static () => SR.T("The selected model does not support image input."),
        static (_, _) => { });

    public PromptImageWorkspaceCallbacks(
        Func<IReadOnlyList<PromptImageAttachment>> getPromptImages,
        Func<string> getNextPromptImageTitle,
        Action<PromptImageAttachment> addPromptImage,
        Action<string, string> renamePromptImage,
        Action<string> deletePromptImage,
        Func<bool> canPastePromptImages,
        Func<string> getPromptImageUnsupportedMessage,
        Action<string, StatusTone> setPromptImageStatus)
    {
        ArgumentNullException.ThrowIfNull(getPromptImages);
        ArgumentNullException.ThrowIfNull(getNextPromptImageTitle);
        ArgumentNullException.ThrowIfNull(addPromptImage);
        ArgumentNullException.ThrowIfNull(renamePromptImage);
        ArgumentNullException.ThrowIfNull(deletePromptImage);
        ArgumentNullException.ThrowIfNull(canPastePromptImages);
        ArgumentNullException.ThrowIfNull(getPromptImageUnsupportedMessage);
        ArgumentNullException.ThrowIfNull(setPromptImageStatus);

        GetPromptImages = getPromptImages;
        GetNextPromptImageTitle = getNextPromptImageTitle;
        AddPromptImage = addPromptImage;
        RenamePromptImage = renamePromptImage;
        DeletePromptImage = deletePromptImage;
        CanPastePromptImages = canPastePromptImages;
        GetPromptImageUnsupportedMessage = getPromptImageUnsupportedMessage;
        SetPromptImageStatus = setPromptImageStatus;
    }

    public Func<IReadOnlyList<PromptImageAttachment>> GetPromptImages { get; }

    public Func<string> GetNextPromptImageTitle { get; }

    public Action<PromptImageAttachment> AddPromptImage { get; }

    public Action<string, string> RenamePromptImage { get; }

    public Action<string> DeletePromptImage { get; }

    public Func<bool> CanPastePromptImages { get; }

    public Func<string> GetPromptImageUnsupportedMessage { get; }

    public Action<string, StatusTone> SetPromptImageStatus { get; }
}
