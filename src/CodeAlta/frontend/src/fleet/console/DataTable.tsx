import { Fragment, useMemo, type KeyboardEvent, type ReactNode } from "react";
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
  /** Its width, as CSS; the rest share what is left. */
  width?: string;
  align?: "start" | "end";
  /** A column a narrow window leaves out: what it says is in the details of the row. */
  secondary?: boolean;
  /** A column of actions, whose header is read by a screen reader only. */
  hiddenLabel?: boolean;
}>;

/**
 * A table of the Fleet console: a header that sorts by a column (again: the other way), a row per item that says what
 * matters of it, and the details of the selected row opened right under it. The arrow keys move the selection, Enter and
 * Space open or close the details of a row, Escape closes them.
 */
export function DataTable<T>({ label, rows, columns, rowKey, sort, onSort, selected, onSelect, details, empty, rowState, rowProps }: {
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
}) {
  const { t, locale } = useShellLanguage();
  const sorted = useMemo(() => {
    const column = sort ? columns.find(value => value.id === sort.column) : undefined;
    return sortRows(rows, column?.sort ?? null, sort?.direction ?? "asc", locale);
  }, [rows, columns, sort, locale]);
  const keys = sorted.map(rowKey);
  function move(event: KeyboardEvent<HTMLTableRowElement>, key: string) {
    const index = keys.indexOf(key);
    const next = event.key === "ArrowDown" ? keys[index + 1] : event.key === "ArrowUp" ? keys[index - 1] : undefined;
    if (next !== undefined) {
      event.preventDefault();
      if (selected !== null) onSelect(next);
      event.currentTarget.closest("tbody")?.querySelector<HTMLElement>(`tr[data-row-key="${CSS.escape(next)}"]`)?.focus();
    } else if (event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      onSelect(selected === key ? null : key);
    } else if (event.key === "Escape" && selected !== null) {
      event.preventDefault();
      event.stopPropagation();
      onSelect(null);
    }
  }

  return <div className="fleet-table-frame">
    <table className="fleet-table" aria-label={label}>
      <colgroup>{columns.map(column => <col key={column.id} data-secondary={column.secondary || undefined} style={column.width ? { width: column.width } : undefined} />)}</colgroup>
      <thead>
        <tr>{columns.map(column => {
          const sorting = sort?.column === column.id ? sort.direction : null;
          return <th key={column.id} scope="col" data-align={column.align} data-secondary={column.secondary || undefined}
            aria-sort={sorting === "asc" ? "ascending" : sorting === "desc" ? "descending" : undefined}>
            {column.sort ? <button type="button" className="fleet-table-sort" onClick={() => onSort(toggledSort(sort, column.id))}>
              <span>{t(column.label)}</span>
              <AppIcon name={sorting === "desc" ? "arrowDown" : sorting === "asc" ? "arrowUp" : "sort"} size={12} className={sorting ? undefined : "fleet-table-sort-idle"} />
            </button> : <span className={column.hiddenLabel ? "visually-hidden" : undefined}>{t(column.label)}</span>}
          </th>;
        })}</tr>
      </thead>
      <tbody>
        {sorted.length === 0 ? <tr className="fleet-table-empty"><td colSpan={columns.length}>{empty}</td></tr>
          : sorted.map(row => {
            const key = rowKey(row);
            const open = selected === key && !!details;
            const extra = rowProps?.(row);
            const data = Object.fromEntries(Object.entries(extra?.data ?? {}).map(([name, value]) => [`data-${name}`, value]));
            return <Fragment key={key}>
              <tr {...data} className={extra?.className} data-row-key={key} tabIndex={0} aria-selected={selected === key} aria-expanded={details ? open : undefined} data-state={rowState?.(row)}
                onClick={event => { if (!(event.target as HTMLElement).closest("button, a, input, label, select, [role=switch]")) onSelect(open ? null : key); }}
                onKeyDown={event => { if (event.target === event.currentTarget) move(event, key); }}>
                {columns.map(column => <td key={column.id} data-align={column.align} data-secondary={column.secondary || undefined}>{column.cell(row)}</td>)}
              </tr>
              {open && <tr className="fleet-table-details"><td colSpan={columns.length}><div className="fleet-table-details-body">{details!(row)}</div></td></tr>}
            </Fragment>;
          })}
      </tbody>
    </table>
  </div>;
}
