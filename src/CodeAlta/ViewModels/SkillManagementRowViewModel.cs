using CodeAlta.App;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;
using XenoAtom.Terminal.UI;

namespace CodeAlta.ViewModels;

internal sealed partial class SkillManagementRowViewModel
{
    private readonly Func<SkillManagementRowViewModel, SkillEnablementScope, bool, Task> _setEnabledAsync;
    private bool _suppressEnablementUpdates = true;

    public SkillManagementRowViewModel(
        SkillDescriptor descriptor,
        Func<SkillManagementRowViewModel, SkillEnablementScope, bool, Task> setEnabledAsync)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(setEnabledAsync);

        _setEnabledAsync = setEnabledAsync;
        Descriptor = descriptor;
        Name = string.Empty;
        Status = string.Empty;
        BuiltIn = string.Empty;
        UpdateFromDescriptor(descriptor);
        _suppressEnablementUpdates = false;
    }

    public SkillDescriptor Descriptor { get; private set; }

    public string SkillKey => Descriptor.SkillFilePath;

    [Bindable]
    public partial bool GlobalEnabled { get; set; }

    [Bindable]
    public partial bool ProjectEnabled { get; set; }

    [Bindable]
    public partial string BuiltIn { get; set; }

    [Bindable]
    public partial string Name { get; set; }

    [Bindable]
    public partial string Status { get; set; }

    public void UpdateFromDescriptor(SkillDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var previousSuppress = _suppressEnablementUpdates;
        _suppressEnablementUpdates = true;
        try
        {
            Descriptor = descriptor;
            GlobalEnabled = !descriptor.IsDisabledGlobally;
            ProjectEnabled = !descriptor.IsDisabledForProject;
            BuiltIn = descriptor.SourceKind == SkillSourceKind.Builtin ? "✓" : string.Empty;
            Name = descriptor.Name;
            Status = FormatStatus(descriptor);
        }
        finally
        {
            _suppressEnablementUpdates = previousSuppress;
        }
    }

    public bool Matches(string filterText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filterText);

        return Descriptor.Name.Contains(filterText, StringComparison.OrdinalIgnoreCase) ||
               Descriptor.Description.Contains(filterText, StringComparison.OrdinalIgnoreCase) ||
               Descriptor.SkillFilePath.Contains(filterText, StringComparison.OrdinalIgnoreCase) ||
               Descriptor.SkillRootPath.Contains(filterText, StringComparison.OrdinalIgnoreCase) ||
               Descriptor.SourceKind.ToString().Contains(filterText, StringComparison.OrdinalIgnoreCase) ||
               Descriptor.Scope.ToString().Contains(filterText, StringComparison.OrdinalIgnoreCase);
    }

    partial void OnGlobalEnabledChanged(bool value) => NotifyEnablementChanged(SkillEnablementScope.Global, value);

    partial void OnProjectEnabledChanged(bool value) => NotifyEnablementChanged(SkillEnablementScope.Project, value);

    private void NotifyEnablementChanged(SkillEnablementScope scope, bool enabled)
    {
        if (_suppressEnablementUpdates)
        {
            return;
        }

        _ = _setEnabledAsync(this, scope, enabled);
    }

    private static string FormatStatus(SkillDescriptor descriptor)
    {
        if (!descriptor.IsEnabled)
        {
            return SR.T("disabled");
        }

        if (descriptor.IsShadowed)
        {
            return SR.T("shadowed");
        }

        return descriptor.IsValid ? SR.T("valid") : SR.T("invalid");
    }
}
