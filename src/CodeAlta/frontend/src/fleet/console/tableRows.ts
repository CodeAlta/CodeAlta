// The rows of a table of the Fleet console (machines, people, devices): sorted by a column, and filtered by facets (a value
// of several in one facet, every facet at once) and by a text typed in its search.

/** The direction of a sort. */
export type SortDirection = "asc" | "desc";

/** The column a table is sorted by, and how. */
export type TableSort = Readonly<{ column: string; direction: SortDirection }>;

/** The value a column sorts by: a text, a number, or nothing, which comes last whatever the direction. */
export type SortValue = string | number | null | undefined;

/** The values chosen in each facet, by its id: a row matches when it has one of the values of every facet that has some. */
export type FacetValues = Readonly<Record<string, readonly string[]>>;

/** A facet of a table: the values a row has in it (a row may have several, such as its labels). */
export type Facet<T> = Readonly<{ id: string; values: (row: T) => readonly string[] }>;

/** The next sort when the header of a column is clicked: ascending first, then descending, then ascending again. */
export function toggledSort(current: TableSort | null, column: string): TableSort {
  return current?.column === column && current.direction === "asc" ? { column, direction: "desc" } : { column, direction: "asc" };
}

/**
 * The rows sorted by a column, stable for rows that sort the same: texts compared as people read them (numbers inside
 * a text in their order, case aside), nothing last.
 */
export function sortRows<T>(rows: readonly T[], sortValue: ((row: T) => SortValue) | null, direction: SortDirection, locale?: string): T[] {
  if (!sortValue) return [...rows];
  const collator = new Intl.Collator(locale, { numeric: true, sensitivity: "base" });
  const sign = direction === "asc" ? 1 : -1;
  return rows.map((row, index) => ({ row, index, value: sortValue(row) })).sort((left, right) => {
    const a = left.value, b = right.value;
    const aNone = a === null || a === undefined || a === "", bNone = b === null || b === undefined || b === "";
    if (aNone || bNone) return aNone === bNone ? left.index - right.index : aNone ? 1 : -1;
    const order = typeof a === "number" && typeof b === "number" ? a - b : collator.compare(String(a), String(b));
    return order !== 0 ? order * sign : left.index - right.index;
  }).map(entry => entry.row);
}

/** The rows that match the facets and the text: the text matches when every word of it is in what the row says of itself. */
export function filterRows<T>(rows: readonly T[], facets: readonly Facet<T>[], values: FacetValues, query: string, text: (row: T) => string): T[] {
  const words = query.trim().toLocaleLowerCase().split(/\s+/u).filter(Boolean);
  const active = facets.filter(facet => (values[facet.id]?.length ?? 0) > 0);
  return rows.filter(row => {
    if (active.some(facet => !facet.values(row).some(value => values[facet.id]!.includes(value)))) return false;
    if (words.length === 0) return true;
    const said = text(row).toLocaleLowerCase();
    return words.every(word => said.includes(word));
  });
}

/** How many rows have each value of a facet, in the order the values first come. */
export function facetCounts<T>(rows: readonly T[], facet: Facet<T>): ReadonlyMap<string, number> {
  const counts = new Map<string, number>();
  for (const row of rows) for (const value of new Set(facet.values(row))) counts.set(value, (counts.get(value) ?? 0) + 1);
  return counts;
}

/** The values with one value of a facet added or taken away. */
export function toggledFacet(values: FacetValues, facet: string, value: string): FacetValues {
  const chosen = values[facet] ?? [];
  const next = chosen.includes(value) ? chosen.filter(item => item !== value) : [...chosen, value];
  return { ...values, [facet]: next };
}

/** How many values are chosen across the facets. */
export function chosenCount(values: FacetValues): number {
  return Object.values(values).reduce((sum, chosen) => sum + chosen.length, 0);
}
