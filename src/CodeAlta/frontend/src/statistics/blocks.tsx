import { Button, Classes, NonIdealState, SegmentedControl } from "@blueprintjs/core";
import { useId, useMemo, useState, type ReactNode } from "react";
import { AppIcon } from "../AppIcon";
import { Sparkline } from "../charts";
import { useText } from "./text";
import type { QueryState } from "./useQuery";

// The pieces every page is made of: a block with a title that shows a skeleton, an error or a note while it has nothing to
// draw, a table that sorts, a ranked list, and a segmented control. They hold no question: a page gives its block the state
// of the question it asked (`useStatisticsQuery`).

/** The properties of `Block`. */
export type BlockProps = Readonly<{
  title: string;
  /** A second line under the title: what the block counts. */
  caption?: string;
  /** Controls at the right of the title. */
  actions?: ReactNode;
  /** How many of the 12 columns of the page the block takes at full width. */
  span?: 3 | 4 | 6 | 8 | 12;
  /** The height the body keeps while it loads, so the page does not move when the result comes. */
  minHeight?: number;
  /** The state of the question, when the block has one. */
  query?: Pick<QueryState<unknown>, "loading" | "error" | "retry" | "refreshing" | "placeholder">;
  /** Whether the result has nothing to show: the block then writes `emptyText`. */
  empty?: boolean;
  emptyText?: string;
  className?: string;
  children?: ReactNode;
}>;

/** A skeleton of a given height. */
export function Skeleton({ height = 120 }: Readonly<{ height?: number }>) {
  return <div className={`stats-skeleton ${Classes.SKELETON}`} style={{ height }} aria-hidden="true" />;
}

/** A titled block of a page; shows a skeleton, the error with its retry, or a quiet note when it has nothing to draw. */
export function Block({ title, caption, actions, span = 6, minHeight = 200, query, empty, emptyText, className, children }: BlockProps) {
  const { t } = useText();
  const id = useId();
  const state = query?.error ? "error" : query?.loading ? "loading" : empty ? "empty" : "ready";
  return <section className={`stats-block${className ? ` ${className}` : ""}`} style={{ "--span": span } as React.CSSProperties} data-span={span} aria-labelledby={id} data-state={state}
    data-refreshing={query?.refreshing || query?.placeholder || undefined} aria-busy={query?.loading || query?.refreshing || undefined}>
    <header className="stats-block-header">
      <div><h3 id={id}>{title}</h3>{caption && <p>{caption}</p>}</div>
      {actions && <div className="stats-block-actions">{actions}</div>}
    </header>
    <div className="stats-block-body" style={{ minHeight }}>
      {state === "loading" && <Skeleton height={Math.max(40, minHeight - 8)} />}
      {state === "error" && <NonIdealState className="stats-error" icon={<AppIcon name="warning" size={22} />} title={t("This could not be read")}
        description={query?.error?.message} action={<Button size="small" onClick={query?.retry}>{t("Try again")}</Button>} />}
      {state === "empty" && <p className="stats-empty">{emptyText ?? t("Nothing in this period.")}</p>}
      {state === "ready" && children}
    </div>
  </section>;
}

/** A choice between a few labelled values, small. */
export function Choice<T extends string>({ value, onChange, options, label }: Readonly<{ value: T; onChange: (value: T) => void; options: readonly Readonly<{ value: T; label: string }>[]; label: string }>) {
  return <SegmentedControl size="small" className="stats-choice" aria-label={label} value={value} onValueChange={next => onChange(next as T)} options={options.map(option => ({ value: option.value, label: option.label }))} />;
}

/** One column of a `DataTable`. */
export type Column<T> = Readonly<{
  id: string;
  header: string;
  /** The value rows are sorted on; a column without it does not sort. */
  sort?: (row: T) => number | string;
  render: (row: T) => ReactNode;
  /** Numbers sit at the right. */
  align?: "end";
  /** A column that may be the narrowest. */
  wide?: boolean;
}>;

/** The properties of `DataTable`. */
export type DataTableProps<T> = Readonly<{
  label: string;
  columns: readonly Column<T>[];
  rows: readonly T[];
  rowKey: (row: T) => string;
  /** The column sorted at first, and its direction. */
  initialSort?: Readonly<{ column: string; descending: boolean }>;
  /** How many rows show before "Show all". */
  limit?: number;
  /** A row that is deleted, dimmed. */
  dimmed?: (row: T) => boolean;
}>;

/** A table whose headers sort, with the first rows and a button for the rest. */
export function DataTable<T>({ label, columns, rows, rowKey, initialSort, limit = 25, dimmed }: DataTableProps<T>) {
  const { t, locale } = useText();
  const [sort, setSort] = useState(initialSort ?? null);
  const [all, setAll] = useState(false);
  const sorted = useMemo(() => {
    const column = columns.find(item => item.id === sort?.column);
    if (!column?.sort) return rows;
    const pick = column.sort;
    const direction = sort?.descending ? -1 : 1;
    return [...rows].sort((a, b) => {
      const left = pick(a), right = pick(b);
      return direction * (typeof left === "number" && typeof right === "number" ? left - right : String(left).localeCompare(String(right), locale));
    });
  }, [rows, columns, sort, locale]);
  const shown = all ? sorted : sorted.slice(0, limit);
  return <div className="stats-table-wrap">
    <table className="stats-table" aria-label={label}>
      <thead><tr>{columns.map(column => {
        const active = sort?.column === column.id;
        return <th key={column.id} scope="col" data-align={column.align} data-wide={column.wide || undefined} aria-sort={active ? (sort!.descending ? "descending" : "ascending") : column.sort ? "none" : undefined}>
          {column.sort ? <button type="button" onClick={() => setSort(active ? { column: column.id, descending: !sort!.descending } : { column: column.id, descending: column.align === "end" })}>
            {column.header}<span aria-hidden="true">{active ? (sort!.descending ? "▾" : "▴") : ""}</span></button> : column.header}
        </th>;
      })}</tr></thead>
      <tbody>{shown.map(row => <tr key={rowKey(row)} data-dimmed={dimmed?.(row) || undefined}>{columns.map(column =>
        <td key={column.id} data-align={column.align} data-wide={column.wide || undefined}>{column.render(row)}</td>)}</tr>)}</tbody>
    </table>
    {sorted.length > limit && <Button variant="minimal" size="small" className="stats-table-more" onClick={() => setAll(value => !value)}>
      {all ? t("Show fewer") : t("Show all {count}", { count: sorted.length.toLocaleString(locale) })}</Button>}
  </div>;
}

/** One row of a ranked list. */
export type RankedItem = Readonly<{ key: string; label: string; detail?: string; value: number; text: string; share: number; spark?: readonly number[] }>;

/** The properties of `RankedBars`. */
export type RankedBarsProps = Readonly<{
  label: string;
  items: readonly RankedItem[];
  /** A row is a button that adds a filter or opens something; without it the rows are plain. */
  onSelect?: (item: RankedItem) => void;
  /** What the button of a row says to a screen reader. */
  selectLabel?: (item: RankedItem) => string;
  /** The color of the bars. */
  color?: string;
  /** Writes the value of a row after its bar; the formatted value by default. */
  showSpark?: boolean;
}>;

/** A ranked list: one line per item, a bar as long as its value over the largest, its number and, when given, a sparkline. */
export function RankedBars({ label, items, onSelect, selectLabel, color, showSpark }: RankedBarsProps) {
  const largest = Math.max(1e-9, ...items.map(item => item.value));
  return <ol className="stats-ranked" aria-label={label} style={color ? { "--ranked-color": color } as React.CSSProperties : undefined}>
    {items.map(item => {
      const body = <>
        {/* A long name is cut to the width of its column: its title has all of it. */}
        <span className="stats-ranked-name"><span title={item.label}>{item.label}</span>{item.detail && <small title={item.detail}>{item.detail}</small>}</span>
        <span className="stats-ranked-bar" aria-hidden="true"><i style={{ width: `${Math.max(1.5, (item.value / largest) * 100)}%` }} /></span>
        {showSpark && item.spark && item.spark.length > 1 && <Sparkline values={item.spark} width={56} height={18} className="stats-ranked-spark" />}
        <span className="stats-ranked-value">{item.text}</span>
      </>;
      return <li key={item.key}>{onSelect
        ? <button type="button" className="stats-ranked-row" onClick={() => onSelect(item)} aria-label={selectLabel ? selectLabel(item) : `${item.label}: ${item.text}`}>{body}</button>
        : <div className="stats-ranked-row">{body}</div>}</li>;
    })}
  </ol>;
}
