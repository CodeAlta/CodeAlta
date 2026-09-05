using CodeAlta.Tui.Models;
using CodeAlta.Tui.Presentation.Prompting;
using CodeAlta.Tui.Threading;

namespace CodeAlta.Tui.App;

internal sealed class LegacyPromptSessionPort : IPromptSessionPort
{
    private readonly IUiDispatcher _uiDispatcher;
    private readonly Func<bool> _isPromptEmpty;
    private readonly Func<string?> _getPromptText;
    private readonly Action _clearPrompt;
    private readonly Action<string> _restorePromptText;
    private readonly Func<IReadOnlyList<PromptImageAttachment>> _snapshotPromptImages;
    private readonly Action<IReadOnlyList<PromptImageAttachment>> _restorePromptImages;
    private readonly Action _updatePromptAvailability;
    private readonly Action _updatePromptAttachments;
    private readonly Dictionary<PromptSessionId, PromptSessionBinding> _bindings = new();

    public LegacyPromptSessionPort(
        IUiDispatcher uiDispatcher,
        Func<bool> isPromptEmpty,
        Action clearPrompt,
        Action<string> restorePromptText,
        Func<IReadOnlyList<PromptImageAttachment>> snapshotPromptImages,
        Action<IReadOnlyList<PromptImageAttachment>> restorePromptImages,
        Func<string?>? getPromptText = null,
        Action? updatePromptAvailability = null,
        Action? updatePromptAttachments = null)
    {
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        ArgumentNullException.ThrowIfNull(isPromptEmpty);
        ArgumentNullException.ThrowIfNull(clearPrompt);
        ArgumentNullException.ThrowIfNull(restorePromptText);
        ArgumentNullException.ThrowIfNull(snapshotPromptImages);
        ArgumentNullException.ThrowIfNull(restorePromptImages);

        _uiDispatcher = uiDispatcher;
        _isPromptEmpty = isPromptEmpty;
        _getPromptText = getPromptText ?? (static () => null);
        _clearPrompt = clearPrompt;
        _restorePromptText = restorePromptText;
        _snapshotPromptImages = snapshotPromptImages;
        _restorePromptImages = restorePromptImages;
        _updatePromptAvailability = updatePromptAvailability ?? (() => { });
        _updatePromptAttachments = updatePromptAttachments ?? (() => { });
    }

    public PromptSessionSnapshot GetPromptSession(PromptSessionId promptSessionId)
    {
        ValidatePromptSessionId(promptSessionId);
        if (!_bindings.TryGetValue(promptSessionId, out var binding))
        {
            throw new KeyNotFoundException($"Prompt session '{promptSessionId.Value}' is not bound.");
        }

        return new PromptSessionSnapshot(binding, IsPromptEmpty(promptSessionId));
    }

    public PromptSubmission CapturePrompt(PromptSessionId promptSessionId, string? submittedText)
    {
        ValidatePromptSessionId(promptSessionId);
        return _uiDispatcher.Invoke(() => PromptSubmission.Create(submittedText ?? _getPromptText(), _snapshotPromptImages()));
    }

    public bool IsPromptEmpty(PromptSessionId promptSessionId)
    {
        ValidatePromptSessionId(promptSessionId);
        return _uiDispatcher.Invoke(_isPromptEmpty);
    }

    public void BindPromptSession(PromptSessionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        _bindings[binding.PromptSessionId] = binding;
    }

    public void ClearPrompt(PromptSessionId promptSessionId)
    {
        ValidatePromptSessionId(promptSessionId);
        _uiDispatcher.Invoke(_clearPrompt);
    }

    public void RestorePrompt(PromptSessionId promptSessionId, PromptSubmission prompt)
    {
        ValidatePromptSessionId(promptSessionId);
        ArgumentNullException.ThrowIfNull(prompt);
        _uiDispatcher.Invoke(() =>
        {
            _restorePromptText(prompt.Text);
            _restorePromptImages(prompt.Images);
        });
    }

    public void UpdatePromptAvailability(PromptSessionId promptSessionId)
    {
        ValidatePromptSessionId(promptSessionId);
        _uiDispatcher.Invoke(_updatePromptAvailability);
    }

    public void UpdatePromptAttachments(PromptSessionId promptSessionId)
    {
        ValidatePromptSessionId(promptSessionId);
        _uiDispatcher.Invoke(_updatePromptAttachments);
    }

    private static void ValidatePromptSessionId(PromptSessionId promptSessionId)
    {
        if (promptSessionId.IsEmpty)
        {
            throw new ArgumentException("Prompt session id cannot be empty.", nameof(promptSessionId));
        }
    }
}
