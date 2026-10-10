import { useCallback, useEffect, useMemo, useRef } from "react";
import type { ChartDatum, ChartPeriod } from "../charts";
import { useText } from "./text";
import { StatChart } from "./StatChart";
import { usePageColors } from "./colors";
import type { FilterKey, PageId } from "./frame";
import { comparisonPhrase, perFrequency, type Translate } from "./labels";
import { distributionOption, periodOfBrush, timeSeriesOption, unreadBuckets } from "./options";
import { useStatistics } from "./runtime";
import type { DistributionResult, SeriesLine, SeriesResult } from "./types";

// What several pages share: the click that adds a filter, a time chart and a histogram built from a result.

/** The actions a click on a chart or a row can lead to. */
export type Drill = Readonly<{
  filterBy: (key: FilterKey, value: string, label?: string) => void;
  /** Sets the period to one day. */
  pickDay: (day: string) => void;
  /** Sets the period to two days, or to nothing special when null. */
  pickRange: (range: string | null) => void;
  goto: (page: PageId) => void;
  openSession: (sessionId: string) => void;
}>;

/** The drill-down actions of the canvas around the caller. */
export function useDrill(): Drill {
  const { dispatch, context } = useStatistics();
  const openSession = context.openSession;
  return useMemo<Drill>(() => ({
    filterBy: (key, value, label) => dispatch({ type: "filter", key, entry: { value, label } }),
    pickDay: day => dispatch({ type: "period", period: { kind: "custom", from: day, to: day } }),
    pickRange: range => {
      const match = range ? /^(\d{4}-\d{2}-\d{2})\.\.(\d{4}-\d{2}-\d{2})$/.exec(range) : null;
      if (match) dispatch({ type: "period", period: { kind: "custom", from: match[1], to: match[2] } });
    },
    goto: page => dispatch({ type: "page", page }),
    openSession: id => openSession?.(id),
  }), [dispatch, openSession]);
}

/** The name of a line of a group: a kind of tool or a sender gets its words; the others are their own name. */
export type LineNamer = (line: SeriesLine) => string;

/** The properties of `SeriesChart`. */
export type SeriesChartProps = Readonly<{
  result: SeriesResult;
  kind?: "bar" | "area" | "line";
  stacked?: boolean;
  /** A slider under the chart that narrows the period. */
  brush?: boolean;
  height?: number;
  ariaLabel: string;
  /** The unit to write the values in, when it is not the result's. */
  unit?: string;
  /** Writes the name of a line. */
  name?: LineNamer;
  /** The color of a line by its key; the page palette by name when absent. */
  colorOf?: (line: SeriesLine, index: number) => string | undefined;
  /** Writes the values without their sign: lines removed are drawn below the axis. */
  absolute?: boolean;
  /** A click on a bar or a point: the line it belongs to. */
  onLine?: (line: SeriesLine) => void;
  /** The group of the chart (charts of a group share a cursor). */
  group?: string;
}>;

/** A time chart of a series: stacked bars, areas or lines, the compared period, the hatch of what is not read yet, a brush. */
export function SeriesChart({ result, kind = "bar", stacked, brush, height = 240, ariaLabel, unit, absolute, name, colorOf, onLine, group }: SeriesChartProps) {
  const { t } = useText();
  const { fmt, frame, dispatch } = useStatistics();
  const colors = usePageColors();
  const named = useMemo<SeriesResult>(() => name ? { ...result, series: result.series.map(line => ({ ...line, label: name(line) })) } : result, [result, name]);
  const built = useMemo(() => timeSeriesOption({ result: named, kind, stacked, brush, unit, absolute, fmt, previousName: comparisonPhraseTitle(t, frame.comparison), otherName: t("Other"), muted: colors.muted,
    color: (key, index) => colorOf?.(result.series[index], index) ?? colors.color(key) }), [named, result, kind, stacked, brush, unit, absolute, fmt, t, frame.comparison, colors, colorOf]);
  const hatch = useMemo(() => ({ unread: unreadBuckets(result.buckets, result.query.coverage), buckets: result.buckets.length, boundaryGap: built.boundaryGap, plot: built.plot }), [result, built]);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(() => () => { if (timer.current) clearTimeout(timer.current); }, []);
  const onPeriod = useCallback((period: ChartPeriod) => {
    if (timer.current) clearTimeout(timer.current);
    timer.current = setTimeout(() => {
      const range = periodOfBrush(result, period.start, period.end);
      const match = range ? /^(\d{4}-\d{2}-\d{2})\.\.(\d{4}-\d{2}-\d{2})$/.exec(range) : null;
      if (match) dispatch({ type: "period", period: { kind: "custom", from: match[1], to: match[2] } });
    }, 450);
  }, [result, dispatch]);
  const onSelect = useCallback((datum: ChartDatum) => {
    if (!onLine) return;
    const index = built.option.series && Array.isArray(built.option.series) ? datum.seriesIndex : -1;
    const line = index >= 0 && index < result.series.length ? result.series[index] : undefined;
    if (line) onLine(line);
  }, [onLine, built, result]);
  return <StatChart key={`${result.query.from}|${result.query.to}|${result.query.frequency}|${result.metric}|${result.group ?? ""}`} option={built.option} table={built.table} ariaLabel={`${ariaLabel} ${perFrequency(t, result.query.frequency)}`}
    height={height} hatch={hatch} renderer={result.buckets.length > 1500 ? "canvas" : "svg"} onSelect={onLine ? onSelect : undefined} onPeriod={brush ? onPeriod : undefined} group={group} />;
}

/** "Previous period" as the name of the dashed line. */
function comparisonPhraseTitle(t: Translate, comparison: "none" | "previousPeriod" | "samePeriodLastYear"): string {
  return comparison === "samePeriodLastYear" ? t("Same period last year") : t("Previous period");
}

/** The properties of `DistributionChart`. */
export type DistributionChartProps = Readonly<{
  result: DistributionResult;
  name: string;
  ariaLabel: string;
  height?: number;
  /** Writes a value; the unit of the result by default. */
  value?: (value: number) => string;
  group?: string;
}>;

/** A histogram of a distribution on a logarithmic scale, with the median and the 90th percentile marked. */
export function DistributionChart({ result, name, ariaLabel, height = 220, value, group }: DistributionChartProps) {
  const { t } = useText();
  const { fmt } = useStatistics();
  const built = useMemo(() => distributionOption({ result, fmt, name, medianName: t("Median"), p90Name: t("90th percentile"), value }), [result, fmt, name, t, value]);
  return <StatChart key={`${result.query.from}|${result.query.to}|${result.measure}|${result.subject ?? ""}`} option={built.option} table={built.table} ariaLabel={ariaLabel} height={height} group={group ?? `distribution-${result.measure}`} />;
}

/** Which of the tiles a unit of the cost goes with, for a sentence. */
export const compareWith = (t: Translate, comparison: "none" | "previousPeriod" | "samePeriodLastYear"): string => comparisonPhrase(t, comparison);
