namespace CodeAlta.Persistence;

/// <summary>
/// Represents a cursor-paged set of task records.
/// </summary>
/// <param name="Tasks">Task records for the current page.</param>
/// <param name="NextCursor">Cursor to fetch the next page, or <see langword="null"/> when there are no more items.</param>
public sealed record TaskListPage(IReadOnlyList<TaskRecord> Tasks, string? NextCursor);

