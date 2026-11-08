import { Fragment, useEffect, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from "react";
import { Button } from "@blueprintjs/core";
import { AppIcon } from "../../AppIcon";
import { useShellLanguage } from "../../shellLanguage";
import type { MessageKey } from "../../localization";
import { sortRows, toggledSort, type SortValue, type TableSort } from "./tableRows";

/** A column of a table of the Fleet console: its header, what a cell shows, and what it sorts by (none: it does not sort). */
export type DataColumn<T> = Readonly<{
  id: string;
  label: MessageKey;
  cell: (row: T) => ReactNode;
  sort?: (row: T) => SortValue;
  /** The width it asks, as CSS; the others share what is left, and the table scrolls sideways before a column has no room. */
  width?: string;
  align?: "start" | "end";
  /** A column a narrow window leaves out: what it says is in the details of the row. */
  secondary?: boolean;
  /** A column of actions, whose header is read by a screen reader only. */
  hiddenLabel?: boolean;
}>;

/** How many rows a table shows at first, and how many more each "Show more" adds: an organization's fleet stays light. */
export const tablePageSize = 100;

/**
 * A table of the Fleet console: a header that sorts by a column (again: the other way), a row per item that says what
 * matters of it, and the details of the selected row opened right under it. The arrow keys move the selection, Enter and
 * Space open or close the details of a row, Escape closes them.
 */
export function DataTable<T>({ label, rows, columns, rowKey, sort, onSort, selected, onSelect, details, empty, rowState, rowProps, group, pageSize = tablePageSize, focus = null }: {
  label: string;
  rows: readonly T[];
  columns: readonly DataColumn<T>[];
  rowKey: (row: T) => string;
  sort: TableSort | null;
  onSort: (sort: TableSort) => void;
  /** The key of the row whose details are open; null for none. */
  selected: string | null;
  onSelect: (key: string | null) => void;
  /** The details of a row, under it once selected; null for a table without details. */
  details?: ((row: T) => ReactNode) | null;
  /** What a table without rows shows. */
  empty: ReactNode;
  /** A state the row shows by its color at its start (`online`, `offline`, `warning`...); none for no mark. */
  rowState?: (row: T) => string | undefined;
  /** A class and data attributes of the row of an item (`data-person`...), for what finds it. */
  rowProps?: (row: T) => Readonly<{ className?: string; data?: Readonly<Record<string, string>> }>;
  /**
   * The group of a row in the order shown (its day...): a line names the group above the first row of each; none for no
   * groups. A table gives it for the sort that keeps a group together.
   */
  group?: ((row: T) => Readonly<{ key: string; label: ReactNode }>) | null;
  /** How many rows it shows at first, and adds at each "Show more". */
  pageSize?: number;
  /** The row that takes the focus once the table shows, scrolled into view: the one whose page the person comes back from. */
  focus?: string | null;
}) {
  const { t, locale } = useShellLanguage();
  const sorted = useMemo(() => {
    const column = sort ? columns.find(value => value.id === sort.column) : undefined;
    return sortRows(rows, column?.sort ?? null, sort?.direction ?? "asc", locale);
  }, [rows, columns, sort, locale]);
  const keys = sorted.map(rowKey);
  // The rows past the pages shown are not drawn; the selected row always is, with what comes before it.
  const [pages, setPages] = useState(1);
  const chosen = selected === null ? -1 : keys.indexOf(selected);
  const limit = Math.max(pages * pageSize, chosen + 1);
  const shown = sorted.length > limit ? sorted.slice(0, limit) : sorted;
  const more = sorted.length - shown.length;
  // One row is in the order of Tab, the selected one or the first: the arrows move between the others, as in a list.
  const entry = chosen >= 0 ? selected : keys[0];
  const focusRow = (body: Element | null, key: string) => body?.querySelector<HTMLElement>(`tr[data-row-key="${CSS.escape(key)}"]`)?.focus();
  const frame = useRef<HTMLDivElement>(null);
  // Back from the page of a row: that row has the focus, in view in the table and in the page, as it was left.
  useEffect(() => {
    if (focus === null) return;
    const row = frame.current?.querySelector<HTMLElement>(`tr[data-row-key="${CSS.escape(focus)}"]`);
    row?.focus({ preventScroll: true });
    row?.scrollIntoView({ block: "nearest" });
  }, [focus]);
  function move(event: KeyboardEvent<HTMLTableRowElement>, key: string) {
    const index = keys.indexOf(key);
    const next = event.key === "ArrowDown" ? keys[index + 1] : event.key === "ArrowUp" ? keys[index - 1] : undefined;
    if (next !== undefined) {
      event.preventDefault();
      if (selected !== null) onSelect(next);
      const body = event.currentTarget.closest("tbody");
      // Past the last row shown, the next page comes, and the row below it takes the focus once it is drawn.
      if (keys.indexOf(next) >= shown.length) { setPages(value => value + 1); requestAnimationFrame(() => focusRow(body, next)); }
      else focusRow(body, next);
    } else if (event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      onSelect(selected === key ? null : key);
    } else if (event.key === "Escape" && selected !== null) {
      event.preventDefault();
      event.stopPropagation();
      onSelect(null);
    }
  }

  return <div className="fleet-table-frame" ref={frame}>
    <table className="fleet-table" aria-label={label}>
      <thead>
        <tr>{columns.map(column => {
          const sorting = sort?.column === column.id ? sort.direction : null;
          return <th key={column.id} scope="col" data-column={column.id} data-align={column.align} data-secondary={column.secondary || undefined} style={column.width ? { width: column.width } : undefined}
            aria-sort={sorting === "asc" ? "ascending" : sorting === "desc" ? "descending" : undefined}>
            {column.sort ? <button type="button" className="fleet-table-sort settings-own-button" onClick={() => onSort(toggledSort(sort, column.id))}>
              <span>{t(column.label)}</span>
              <AppIcon name={sorting === "desc" ? "arrowDown" : sorting === "asc" ? "arrowUp" : "sort"} size={12} className={sorting ? undefined : "fleet-table-sort-idle"} />
            </button> : <span className={column.hiddenLabel ? "visually-hidden" : undefined}>{t(column.label)}</span>}
          </th>;
        })}</tr>
      </thead>
      <tbody>
        {sorted.length === 0 ? <tr className="fleet-table-empty"><td colSpan={columns.length}>{empty}</td></tr>
          : shown.map((row, index) => {
            const key = rowKey(row);
            const open = selected === key && !!details;
            const extra = rowProps?.(row);
            const data = Object.fromEntries(Object.entries(extra?.data ?? {}).map(([name, value]) => [`data-${name}`, value]));
            const heading = group?.(row);
            const starts = !!heading && (index === 0 || group!(shown[index - 1]).key !== heading.key);
            return <Fragment key={key}>
              {starts && <tr className="fleet-table-group" data-group={heading.key}><th scope="rowgroup" colSpan={columns.length}>{heading.label}</th></tr>}
              <tr {...data} className={extra?.className} data-row-key={key} tabIndex={key === entry ? 0 : -1} aria-selected={selected === key} aria-expanded={details ? open : undefined} data-state={rowState?.(row)}
                onClick={event => { if (!(event.target as HTMLElement).closest("button, a, input, label, select, [role=switch]")) onSelect(open ? null : key); }}
                onKeyDown={event => { if (event.target === event.currentTarget) move(event, key); }}>
                {columns.map(column => <td key={column.id} data-column={column.id} data-align={column.align} data-secondary={column.secondary || undefined}>{column.cell(row)}</td>)}
              </tr>
              {open && <tr className="fleet-table-details"><td colSpan={columns.length}><div className="fleet-table-details-body">{details!(row)}</div></td></tr>}
            </Fragment>;
          })}
      </tbody>
    </table>
    {more > 0 && <div className="fleet-table-more" role="status">
      <span className="muted-text">{t("{shown} of {total}", { shown: shown.length, total: sorted.length })}</span>
      <Button size="small" variant="outlined" onClick={() => setPages(Math.ceil(shown.length / pageSize) + 1)}>{t("Show {count} more", { count: Math.min(pageSize, more) })}</Button>
      <Button size="small" variant="minimal" onClick={() => setPages(Math.ceil(sorted.length / pageSize))}>{t("Show all")}</Button>
    </div>}
  </div>;
}
