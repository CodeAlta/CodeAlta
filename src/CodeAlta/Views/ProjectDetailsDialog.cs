using CodeAlta.Catalog;
using CodeAlta.ViewModels;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Views;

internal sealed class ProjectDetailsDialog
{
    private readonly ProjectDescriptor _project;
    private readonly ProjectDetailsDialogViewModel _viewModel;
    private readonly IProjectDetailsDialogService _dialogService;
    private readonly Dialog _dialog;

    public ProjectDetailsDialog(
        ProjectDescriptor project,
        IProjectDetailsDialogService dialogService)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(dialogService);

        _project = CloneProject(project);
        _dialogService = dialogService;
        _viewModel = new ProjectDetailsDialogViewModel
        {
            Id = project.Id,
            Slug = project.Slug,
            Name = project.Name,
            DisplayName = project.DisplayName,
            ProjectPath = project.ProjectPath,
            DefaultBranch = project.DefaultBranch,
            Description = project.Description ?? string.Empty,
            TagsText = string.Join(", ", project.Tags),
            CheckoutPathTemplate = project.Checkout.PathTemplate,
            SourcePath = project.SourcePath ?? string.Empty,
            Archived = project.Archived,
        };

        var closeButton = new Button(new TextBlock($"{NerdFont.MdClose} Close"))
        {
            HorizontalAlignment = Align.End,
            VerticalAlignment = Align.Start,
            Tone = ControlTone.Default,
        };
        closeButton.Click(Close);

        var form = new Grid
            {
                HorizontalAlignment = Align.Stretch,
                RowGap = 1,
            }
            .Rows(
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto })
            .Columns(
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star(1) });

        var displayNameBox = new TextBox().Text(_viewModel.Bind.DisplayName);
        var nameBox = new TextBox().Text(_viewModel.Bind.Name);
        var pathBox = new TextBox().Text(_viewModel.Bind.ProjectPath);
        var branchBox = new TextBox().Text(_viewModel.Bind.DefaultBranch);
        var descriptionBox = new TextBox().Text(_viewModel.Bind.Description);
        var tagsBox = new TextBox().Text(_viewModel.Bind.TagsText);

        form.Cell(new TextBlock("Id"), 0, 0);
        form.Cell(new TextBlock(() => _viewModel.Id), 0, 1);
        form.Cell(new TextBlock("Slug"), 1, 0);
        form.Cell(new TextBlock(() => _viewModel.Slug), 1, 1);
        form.Cell(new TextBlock("Display Name"), 2, 0);
        form.Cell(displayNameBox.Validate(
            _viewModel.Bind.DisplayName,
            static value => string.IsNullOrWhiteSpace(value)
                ? new ValidationMessage(ValidationSeverity.Error, "Display name is required.")
                : null), 2, 1);
        form.Cell(new TextBlock("Name"), 3, 0);
        form.Cell(nameBox.Validate(
            _viewModel.Bind.Name,
            static value => ValidateProjectName(value)), 3, 1);
        form.Cell(new TextBlock("Project Path"), 4, 0);
        form.Cell(pathBox.Validate(
            _viewModel.Bind.ProjectPath,
            static value => string.IsNullOrWhiteSpace(value)
                ? new ValidationMessage(ValidationSeverity.Error, "Project path is required.")
                : null), 4, 1);
        form.Cell(new TextBlock("Default Branch"), 5, 0);
        form.Cell(branchBox.Validate(
            _viewModel.Bind.DefaultBranch,
            static value => string.IsNullOrWhiteSpace(value)
                ? new ValidationMessage(ValidationSeverity.Error, "Default branch is required.")
                : null), 5, 1);
        form.Cell(new TextBlock("Description"), 6, 0);
        form.Cell(descriptionBox, 6, 1);
        form.Cell(new TextBlock("Tags"), 7, 0);
        form.Cell(tagsBox, 7, 1);
        form.Cell(new TextBlock("Checkout Template"), 8, 0);
        form.Cell(new TextBlock(() => _viewModel.CheckoutPathTemplate), 8, 1);
        form.Cell(new TextBlock("Metadata File"), 9, 0);
        form.Cell(new TextBlock(() => string.IsNullOrWhiteSpace(_viewModel.SourcePath) ? "Unavailable" : _viewModel.SourcePath).Wrap(true), 9, 1);
        form.Cell(new TextBlock("Archived"), 10, 0);
        form.Cell(new TextBlock(() => _viewModel.Archived ? "Yes" : "No"), 10, 1);

        var cancelButton = new Button("Cancel")
        {
            Tone = ControlTone.Default,
        };
        cancelButton.Click(Close);

        var saveButton = new Button("Save")
        {
            Tone = ControlTone.Primary,
        };
        saveButton.Click(() => _ = SaveAsync());

        var buttonRow = new HStack(cancelButton, saveButton)
        {
            HorizontalAlignment = Align.End,
            Spacing = 2,
        };

        var content = new DockLayout()
            .Content(new ScrollViewer(form, focusable: false).Stretch())
            .Bottom(buttonRow)
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch);

        _dialog = new Dialog()
            .Title($"Project Details · {project.DisplayName}")
            .TopRightText(closeButton)
            .BottomRightText(new Markup("[dim]Esc Close[/]"))
            .IsModal(true)
            .Padding(1)
            .Content(content);
        ResponsiveDialogSize.Apply(_dialog, _dialogService.GetDialogBounds(), minWidth: 70, minHeight: 16, widthFactor: 0.55, heightFactor: 0.55);
        _dialog.AddCommand(new Command
        {
            Id = "CodeAlta.ProjectDetails.Close",
            LabelMarkup = "Close",
            DescriptionMarkup = "Close the project details dialog.",
            Gesture = new KeyGesture(TerminalKey.Escape),
            Importance = CommandImportance.Primary,
            Execute = _ => Close(),
        });
    }

    public void Show()
        => _dialog.Show();

    private async Task SaveAsync()
    {
        var updatedProject = CloneProject(_project);
        updatedProject.DisplayName = (_viewModel.DisplayName ?? string.Empty).Trim();
        updatedProject.Name = (_viewModel.Name ?? string.Empty).Trim();
        updatedProject.ProjectPath = (_viewModel.ProjectPath ?? string.Empty).Trim();
        updatedProject.DefaultBranch = (_viewModel.DefaultBranch ?? string.Empty).Trim();
        updatedProject.Description = string.IsNullOrWhiteSpace(_viewModel.Description) ? null : _viewModel.Description.Trim();
        updatedProject.Tags = (_viewModel.TagsText ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        try
        {
            updatedProject.Validate();
        }
        catch (ArgumentException)
        {
            return;
        }

        await _dialogService.SaveProjectAsync(updatedProject);
        Close();
    }

    private void Close()
    {
        var app = _dialog.App;
        _dialog.Close();
        if (_dialogService.GetDialogFocusTarget() is { } focusTarget)
        {
            app?.Focus(focusTarget);
        }
    }

    private static ValidationMessage? ValidateProjectName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new ValidationMessage(ValidationSeverity.Error, "Project name is required.");
        }

        if (value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.Contains(Path.DirectorySeparatorChar) ||
            value.Contains(Path.AltDirectorySeparatorChar))
        {
            return new ValidationMessage(ValidationSeverity.Error, "Use a single valid directory name.");
        }

        return null;
    }

    private static ProjectDescriptor CloneProject(ProjectDescriptor project)
    {
        return new ProjectDescriptor
        {
            Id = project.Id,
            Slug = project.Slug,
            Name = project.Name,
            DisplayName = project.DisplayName,
            ProjectPath = project.ProjectPath,
            DefaultBranch = project.DefaultBranch,
            Description = project.Description,
            Tags = [.. project.Tags],
            Archived = project.Archived,
            Checkout = new CheckoutRule
            {
                PathTemplate = project.Checkout.PathTemplate,
                Depth = project.Checkout.Depth,
                Submodules = project.Checkout.Submodules,
            },
            SourcePath = project.SourcePath,
            MarkdownBody = project.MarkdownBody,
        };
    }
}
