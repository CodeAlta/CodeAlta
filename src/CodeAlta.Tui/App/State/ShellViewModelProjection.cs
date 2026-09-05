using CodeAlta.Tui.Models;
using CodeAlta.Tui.Presentation.Shell;
using CodeAlta.Tui.ViewModels;

namespace CodeAlta.Tui.App.State;

internal static class ShellViewModelProjection
{
    public static void ApplyStatus(CodeAltaShellViewModel viewModel, ShellStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(snapshot);

        viewModel.StatusText = snapshot.Text;
        viewModel.StatusBusy = snapshot.Busy;
        viewModel.StatusTone = snapshot.Tone;
        viewModel.StatusIconMarkup = snapshot.IconMarkup ?? StatusVisualFormatter.BuildStatusIconMarkup(snapshot.Tone);
    }

    public static void ApplyProviderSessionLoadStatus(CodeAltaShellViewModel viewModel, string? message)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        viewModel.ProviderSessionLoadStatusText = message?.Trim() ?? string.Empty;
    }
}

internal sealed record ShellStatusSnapshot(
    string Text,
    bool Busy,
    StatusTone Tone,
    string? IconMarkup = null);
