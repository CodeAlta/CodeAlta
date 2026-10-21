using CodeAlta.Catalog.Issues;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.Issues;

/// <summary>The filters of a tracker with the value each one has, as <see cref="IssueFilterChoice.ChooseAsync"/> gives them.</summary>
/// <param name="Filters">The filters the tracker offers, its own and those of the fields asked for.</param>
/// <param name="Selected">The value of each filter, by id: a value id, several joined with <see cref="TrackedFilters.Separator"/>, or empty for every item.</param>
public sealed record IssueFilterSelection(IReadOnlyList<TrackedFilterDescriptor> Filters, IReadOnlyDictionary<string, string> Selected)
{
    /// <summary>Gets the values to list with: those that are not empty.</summary>
    public IReadOnlyDictionary<string, string> Applied
        => Selected.Where(static pair => pair.Value.Length > 0).ToDictionary(StringComparer.Ordinal);
}

/// <summary>
/// Gives the filters of a tracker their values, as the Issues tab and <c>alta issue</c> both do: an active filter of the
/// view has the value of the view, or the value chosen when the view lets the tab change it (<see cref="IssueFilterSetting.Pick"/>)
/// and the tracker offers it; any other filter has the value chosen when the tracker offers it, else none; and a filter
/// that depends on another is asked again once that one is known.
/// </summary>
public static class IssueFilterChoice
{
    /// <summary>The most filters one tracker describes.</summary>
    public const int MaximumFilters = IssueViewStore.MaximumFilters;

    /// <summary>The longest value of one filter, in UTF-16 units.</summary>
    public const int MaximumValue = 128;

    /// <summary>Asks the tracker for its filters and gives each one its value.</summary>
    /// <param name="tracker">The tracker.</param>
    /// <param name="chosen">The values chosen, by filter id: value ids, several joined with <see cref="TrackedFilters.Separator"/>; empty for every item.</param>
    /// <param name="settings">The filters of the view; <see cref="IssueFilterSettings.Empty"/> for none.</param>
    /// <param name="include">The filters made of fields to describe besides those the view names.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The filters and their values.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static async Task<IssueFilterSelection> ChooseAsync(IIssueTracker tracker, IReadOnlyDictionary<string, string> chosen, IssueFilterSettings settings,
        IReadOnlyList<string> include, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(chosen);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(include);
        // The fields the view names, those asked for, and those a value was chosen for.
        var fields = settings.Filters.Select(static setting => setting.Id).Concat(include).Concat(chosen.Keys)
            .Where(static id => id.Contains(':', StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).Take(MaximumFilters).ToArray();
        var asked = new Dictionary<string, string>(chosen.Where(static pair => pair.Value.Length > 0), StringComparer.Ordinal);
        IReadOnlyList<TrackedFilterDescriptor> descriptors = [];
        var selected = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var pass = 0; pass < 3; pass++)
        {
            descriptors = [.. (await tracker.GetFiltersAsync(new(asked, fields), cancellationToken).ConfigureAwait(false))
                .Where(static filter => IssueViewStore.IsFilterId(filter.Id)).Take(MaximumFilters)];
            selected.Clear();
            foreach (var descriptor in descriptors)
            {
                var setting = settings.Find(descriptor.Id);
                var picked = chosen.TryGetValue(descriptor.Id, out var value) && Offered(descriptor, value);
                selected[descriptor.Id] = setting is { Active: true, Pick: false } ? Match(descriptor, setting.Value ?? string.Empty)
                    : picked ? value!
                    : setting is { Active: true } ? Match(descriptor, setting.Value ?? string.Empty)
                    : string.Empty;
            }

            // The values of a filter depend on others: when one of those has a value that was not asked with, ask again.
            var stale = descriptors.Any(descriptor => descriptor.DependsOn.Any(other => (selected.GetValueOrDefault(other) ?? string.Empty) != (asked.GetValueOrDefault(other) ?? string.Empty)));
            if (!stale) break;
            asked = new(selected.Where(static pair => pair.Value.Length > 0), StringComparer.Ordinal);
        }

        return new(descriptors, selected);
    }

    /// <summary>
    /// Gets whether a value is one a filter takes: empty, any plain text for a text filter, a value the filter offers, or
    /// several of them for a filter that takes several.
    /// </summary>
    /// <param name="descriptor">The filter.</param>
    /// <param name="value">The value: value ids, several joined with <see cref="TrackedFilters.Separator"/>.</param>
    /// <returns><see langword="true"/> when the filter takes it.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static bool Offered(TrackedFilterDescriptor descriptor, string value)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0) return true;
        if (descriptor.Kind == TrackedFilterKind.Text) return IsPlainValue(value);
        var values = TrackedFilters.Split(value);
        return values.Count > 0 && (descriptor.Multiple || values.Count == 1) && values.All(one => descriptor.Values.Any(offered => offered.Id == one));
    }

    /// <summary>
    /// Gets the value ids a text names, as a person writes it in the configuration or on a command line: each value by
    /// its id or its label, without case; one the filter does not offer is left out. A text filter takes the text.
    /// </summary>
    /// <param name="descriptor">The filter.</param>
    /// <param name="named">The text: one value, or several joined with <see cref="TrackedFilters.Separator"/>.</param>
    /// <returns>The value ids, several joined with <see cref="TrackedFilters.Separator"/> for a filter that takes several; empty when none matches.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static string Match(TrackedFilterDescriptor descriptor, string named)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(named);
        if (named.Length == 0) return string.Empty;
        if (descriptor.Kind == TrackedFilterKind.Text) return IsPlainValue(named) ? named : string.Empty;
        var matched = TrackedFilters.Split(named).Select(one => (descriptor.Values.FirstOrDefault(value => string.Equals(value.Id, one, StringComparison.OrdinalIgnoreCase))
            ?? descriptor.Values.FirstOrDefault(value => string.Equals(value.Label, one, StringComparison.OrdinalIgnoreCase)))?.Id).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        return descriptor.Multiple ? TrackedFilters.Join(matched) : matched.FirstOrDefault() ?? string.Empty;
    }

    /// <summary>Gets whether a text can be one value of a filter: a single line of 128 units at most, without spaces around it.</summary>
    /// <param name="value">The text.</param>
    /// <returns><see langword="true"/> when it can.</returns>
    public static bool IsPlainValue(string? value)
        => value is { Length: > 0 and <= MaximumValue } && value == value.Trim() && !value.Any(char.IsControl);
}
