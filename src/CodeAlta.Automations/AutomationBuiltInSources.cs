using CodeAlta.Catalog;

namespace CodeAlta.Automations;

/// <summary>Adds the trigger sources of CodeAlta to the automations of a host.</summary>
internal static class AutomationBuiltInSources
{
    /// <summary>Adds the Project trigger, which reads a catalog and follows the changes this process makes to it.</summary>
    /// <param name="service">The automations of the host.</param>
    /// <param name="catalog">The catalog of the projects.</param>
    /// <returns>The source.</returns>
    internal static ProjectTriggerSource AddProjectTrigger(this AutomationService service, ProjectCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(catalog);
        var source = new ProjectTriggerSource(token => catalog.LoadAsync(token));
        catalog.Changed += source.OnCatalogChanged;
        service.Sources.AddBuiltIn(source.Contribution);
        return source;
    }
}
