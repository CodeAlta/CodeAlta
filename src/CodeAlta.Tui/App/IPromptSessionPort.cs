using CodeAlta.Tui.Models;
using CodeAlta.Tui.Presentation.Prompting;
using CodeAlta.Tui.Threading;

namespace CodeAlta.Tui.App;

internal sealed record PromptSessionSnapshot(PromptSessionBinding Binding, bool IsPromptEmpty);

internal interface IPromptSessionPort
{
    PromptSessionSnapshot GetPromptSession(PromptSessionId promptSessionId);

    PromptSubmission CapturePrompt(PromptSessionId promptSessionId, string? submittedText);

    bool IsPromptEmpty(PromptSessionId promptSessionId);

    void BindPromptSession(PromptSessionBinding binding);

    void ClearPrompt(PromptSessionId promptSessionId);

    void RestorePrompt(PromptSessionId promptSessionId, PromptSubmission prompt);

    void UpdatePromptAvailability(PromptSessionId promptSessionId);

    void UpdatePromptAttachments(PromptSessionId promptSessionId);
}

internal sealed class PromptSessionPort : IPromptSessionPort
{
    private readonly IUiDispatcher _uiDispatcher;
    private readonly Func<bool> _isPromptEmpty;
    private readonly Action _clearPrompt;
    private readonly Action<string> _restorePromptText;
    private readonly Func<IReadOnlyList<PromptImageAttachment>> _snapshotPromptImages;
    private readonly Action<IReadOnlyList<PromptImageAttachment>> _restorePromptImages;
    private readonly Action _updatePromptAvailability;
    private readonly Action _updatePromptAttachments;
    private readonly Dictionary<PromptSessionId, PromptSessionBinding> _bindings = new();

    public PromptSessionPort(
        IUiDispatcher uiDispatcher,
        Func<bool> isPromptEmpty,
        Action clearPrompt,
        Action<string> restorePromptText,
        Func<IReadOnlyList<PromptImageAttachment>> snapshotPromptImages,
        Action<IReadOnlyList<PromptImageAttachment>> restorePromptImages,
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
        _clearPrompt = clearPrompt;
        _restorePromptText = restorePromptText;
        _snapshotPromptImages = snapshotPromptImages;
        _restorePromptImages = restorePromptImages;
        _updatePromptAvailability = updatePromptAvailability ?? (() => { });
        _updatePromptAttachments = updatePromptAttachments ?? (() => { });
    }

    public PromptSessionSnapshot GetPromptSession(PromptSessionId promptSessionId)
    {
        var binding = GetBinding(promptSessionId);
        return new PromptSessionSnapshot(binding, IsPromptEmpty(promptSessionId));
    }

    public PromptSubmission CapturePrompt(PromptSessionId promptSessionId, string? submittedText)
    {
        _ = GetBinding(promptSessionId);
        return _uiDispatcher.Invoke(() => PromptSubmission.Create(submittedText, _snapshotPromptImages()));
    }

    public bool IsPromptEmpty(PromptSessionId promptSessionId)
    {
        _ = GetBinding(promptSessionId);
        return _uiDispatcher.Invoke(_isPromptEmpty);
    }

    public void BindPromptSession(PromptSessionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        _bindings[binding.PromptSessionId] = binding;
    }

    public void ClearPrompt(PromptSessionId promptSessionId)
    {
        _ = GetBinding(promptSessionId);
        _uiDispatcher.Invoke(_clearPrompt);
    }

    public void RestorePrompt(PromptSessionId promptSessionId, PromptSubmission prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        _ = GetBinding(promptSessionId);
        _uiDispatcher.Invoke(() =>
        {
            _restorePromptText(prompt.Text);
            _restorePromptImages(prompt.Images);
        });
    }

    public void UpdatePromptAvailability(PromptSessionId promptSessionId)
    {
        _ = GetBinding(promptSessionId);
        _uiDispatcher.Invoke(_updatePromptAvailability);
    }

    public void UpdatePromptAttachments(PromptSessionId promptSessionId)
    {
        _ = GetBinding(promptSessionId);
        _uiDispatcher.Invoke(_updatePromptAttachments);
    }

    private PromptSessionBinding GetBinding(PromptSessionId promptSessionId)
    {
        if (promptSessionId.IsEmpty)
        {
            throw new ArgumentException("Prompt session id cannot be empty.", nameof(promptSessionId));
        }

        if (_bindings.TryGetValue(promptSessionId, out var binding))
        {
            return binding;
        }

        throw new KeyNotFoundException($"Prompt session '{promptSessionId.Value}' is not bound.");
    }
}
