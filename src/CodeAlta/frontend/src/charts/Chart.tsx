import { Button, HTMLTable } from "@blueprintjs/core";
import { useCallback, useEffect, useId, useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type RefObject } from "react";
import { useShellLanguage } from "../shellLanguage";
import type { ChartInstance, ChartRenderer } from "./chartEngine";
import type { ChartOption } from "./sanitize";
import { tableFromOption, tableToText, type ChartTable } from "./table";
import { buildChartTheme } from "./theme";
import { readChartTokens, type ChartTokens } from "./tokens";
import { carryViewState, emptyViewState, legendEntries, periodOfOption, prepareOption, zoomOfOption, type ChartPeriod, type ChartViewState } from "./viewState";

/** What a click on a chart is about: the item the user clicked. */
export type ChartDatum = Readonly<{ name: string; seriesName: string; seriesIndex: number; dataIndex: number; value: unknown; data: unknown }>;

/** The properties of `Chart`. */
export type ChartProps = Readonly<{
  /**
   * The ECharts option: the series, the axes, the data. Plain data in the common cases (no function), so it can
   * come from JSON; the theme, the legend, the accessible description and the motion are the component's.
   * A new object with the same content replaces nothing: the chart redraws when the content changes.
   */
  option: ChartOption;
  /** What the chart is, for a screen reader and the "Show as table" button. */
  ariaLabel: string;
  /** The data of the table when it cannot be read from the option. */
  table?: ChartTable;
  /** A click on an item (a point, a bar, a slice): the drill-down. */
  onSelect?: (datum: ChartDatum) => void;
  /** The user zoomed or panned a data zoom: the period it now shows. */
  onPeriod?: (period: ChartPeriod) => void;
  /** Charts with the same group show the cursor of one on the others. */
  group?: string;
  /** The height of the chart in pixels (default 240); its width is its container's. */
  height?: number;
  /** `svg` (default) stays sharp at any page zoom; `canvas` for a series too long for SVG. */
  renderer?: ChartRenderer;
  /** False while the tab or the panel that holds the chart is hidden: the chart then draws nothing and waits. */
  visible?: boolean;
  /** `replace` (default) draws the new option from scratch, keeping what the user zoomed and hid; `merge` updates the chart in place. */
  update?: "replace" | "merge";
  /** Hides the "Show as table" button. */
  hideTableToggle?: boolean;
  className?: string;
}>;

type Engine = typeof import("./chartEngine");

const reducedMotionQuery = "(prefers-reduced-motion: reduce)";

/** True while the user asks the system for less motion. */
function useReducedMotion(): boolean {
  const [reduced, setReduced] = useState(() => typeof matchMedia === "function" && matchMedia(reducedMotionQuery).matches);
  useEffect(() => {
    const query = matchMedia(reducedMotionQuery);
    const change = () => setReduced(query.matches);
    change();
    query.addEventListener("change", change);
    return () => query.removeEventListener("change", change);
  }, []);
  return reduced;
}

/** A number that changes when the theme or the color scheme of the page does: the classes and variables on the root. */
function useThemeVersion(): number {
  const [version, setVersion] = useState(0);
  useEffect(() => {
    let frame = 0;
    const observer = new MutationObserver(() => {
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(() => setVersion(value => value + 1));
    });
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ["class", "data-theme", "data-palette", "style"] });
    return () => { observer.disconnect(); cancelAnimationFrame(frame); };
  }, []);
  return version;
}

/** The pixel ratio of the page, which changes with the page zoom and with the display the window is on. */
function useDevicePixelRatio(): number {
  const [ratio, setRatio] = useState(() => window.devicePixelRatio);
  useEffect(() => {
    let query: MediaQueryList | undefined, current = window.devicePixelRatio;
    const watch = () => {
      query?.removeEventListener("change", change);
      current = window.devicePixelRatio;
      query = matchMedia(`(resolution: ${current}dppx)`);
      query.addEventListener("change", change);
    };
    function change() { setRatio(window.devicePixelRatio); watch(); }
    // A change of page zoom also resizes the window, which is the signal some hosts give alone.
    const resized = () => { if (window.devicePixelRatio !== current) change(); };
    window.addEventListener("resize", resized);
    change();
    return () => { query?.removeEventListener("change", change); window.removeEventListener("resize", resized); };
  }, []);
  return ratio;
}

/** True while the element is on screen and the page is not hidden. */
function useOnScreen(host: RefObject<HTMLElement | null>): boolean {
  const [intersecting, setIntersecting] = useState(true);
  const [pageVisible, setPageVisible] = useState(() => !document.hidden);
  useEffect(() => {
    const element = host.current;
    if (!element || typeof IntersectionObserver !== "function") return;
    const observer = new IntersectionObserver(entries => { const last = entries[entries.length - 1]; if (last) setIntersecting(last.isIntersecting); });
    observer.observe(element);
    return () => observer.disconnect();
  }, [host]);
  useEffect(() => {
    const change = () => setPageVisible(!document.hidden);
    document.addEventListener("visibilitychange", change);
    return () => document.removeEventListener("visibilitychange", change);
  }, []);
  return intersecting && pageVisible;
}

const sameContent = (left: unknown, right: unknown): boolean => {
  if (Object.is(left, right)) return true;
  if (typeof left !== "object" || typeof right !== "object" || left === null || right === null) return false;
  if (Array.isArray(left) !== Array.isArray(right)) return false;
  const leftKeys = Object.keys(left), rightKeys = Object.keys(right);
  if (leftKeys.length !== rightKeys.length) return false;
  return leftKeys.every(key => Object.hasOwn(right, key) && sameContent((left as Record<string, unknown>)[key], (right as Record<string, unknown>)[key]));
};

/**
 * A chart: one ECharts instance in a box, with the colors of the application, a description for screen readers,
 * a legend that the keyboard reaches, a table of the same numbers, and no work while it is hidden.
 *
 * ECharts is loaded when the first chart is shown (a separate chunk), so a page without a chart does not pay for it.
 * The component is safe under React StrictMode: the chart is created and disposed by one effect.
 */
export function Chart(props: ChartProps) {
  const { option, ariaLabel, table, onSelect, onPeriod, group, height = 240, renderer = "svg", visible = true, update = "replace", hideTableToggle, className } = props;
  const { t, locale } = useShellLanguage();
  const labelId = useId();
  const frame = useRef<HTMLElement>(null);
  const host = useRef<HTMLDivElement>(null);
  const chart = useRef<ChartInstance | null>(null);
  const [engine, setEngine] = useState<Engine | null>(null);
  const [failure, setFailure] = useState<string | null>(null);
  const [tokens, setTokens] = useState<ChartTokens | null>(null);
  const [created, setCreated] = useState(0);
  const [showTable, setShowTable] = useState(false);
  const [hidden, setHidden] = useState<ReadonlySet<string>>(() => new Set());
  const onScreen = useOnScreen(frame);
  const active = visible && onScreen;
  const themeVersion = useThemeVersion();
  const reducedMotion = useReducedMotion();
  const ratio = useDevicePixelRatio();

  // What the effects read without depending on it.
  const latest = useRef({ option, onSelect, onPeriod, group, update, reducedMotion });
  useLayoutEffect(() => { latest.current = { option, onSelect, onPeriod, group, update, reducedMotion }; });
  const state = useRef<ChartViewState>(emptyViewState);
  const applied = useRef<{ option: ChartOption; reduced: boolean } | null>(null);
  const activeRef = useRef(active);
  activeRef.current = active;

  // The theme is read, and the chart rebuilt, only while the chart is shown: a hidden chart keeps what it has until it is shown again.
  const [shownTheme, setShownTheme] = useState(themeVersion);
  const [shownRatio, setShownRatio] = useState(ratio);
  const [everActive, setEverActive] = useState(active);
  useEffect(() => {
    if (!active) return;
    setEverActive(true);
    setShownTheme(themeVersion);
    setShownRatio(ratio);
  }, [active, themeVersion, ratio]);

  useEffect(() => {
    let cancelled = false;
    import("./chartEngine").then(loaded => { if (!cancelled) setEngine(loaded); },
      error => { if (!cancelled) setFailure(error instanceof Error ? error.message : String(error)); });
    return () => { cancelled = true; };
  }, []);

  // The chart: created when the engine is there and the chart has been shown, disposed when anything it was built from changes.
  const canvasRatio = renderer === "canvas" ? shownRatio : 0;
  useEffect(() => {
    const element = host.current;
    if (!engine || !element || !everActive) return;
    const read = readChartTokens(element);
    const created = engine.createChart(element, buildChartTheme(read), renderer);
    chart.current = created;
    applied.current = null;
    if (latest.current.group) { created.group = latest.current.group; engine.connectGroup(latest.current.group); }
    created.on("click", (params: unknown) => {
      const p = params as { name?: string; seriesName?: string; seriesIndex?: number; dataIndex?: number; value?: unknown; data?: unknown };
      latest.current.onSelect?.({ name: p.name ?? "", seriesName: p.seriesName ?? "", seriesIndex: p.seriesIndex ?? 0, dataIndex: p.dataIndex ?? 0, value: p.value, data: p.data });
    });
    created.on("datazoom", () => {
      const current = created.getOption() as Record<string, unknown>;
      state.current = { hidden: state.current.hidden, zoom: zoomOfOption(current) };
      const period = periodOfOption(current);
      if (period) latest.current.onPeriod?.(period);
    });
    created.on("brushEnd", (params: unknown) => {
      const area = (params as { areas?: { coordRange?: unknown }[] }).areas?.[0]?.coordRange;
      if (Array.isArray(area) && area.length === 2 && typeof area[0] === "number" && typeof area[1] === "number") {
        latest.current.onPeriod?.({ start: 0, end: 100, from: area[0], to: area[1] });
      }
    });
    created.on("legendselectchanged", (params: unknown) => {
      const selected = (params as { selected?: Record<string, boolean> }).selected ?? {};
      const names = new Set(Object.keys(selected).filter(name => !selected[name]));
      state.current = { hidden: names, zoom: state.current.zoom };
      setHidden(names);
    });
    setTokens(read);
    setCreated(value => value + 1);
    return () => { created.dispose(); if (chart.current === created) chart.current = null; };
  }, [engine, everActive, renderer, shownTheme, canvasRatio]);

  // The option: drawn when the chart exists and is shown, and again when its content, not its identity, changes.
  useEffect(() => {
    const instance = chart.current;
    if (!instance || !active) return;
    const previous = applied.current;
    if (previous && previous.reduced === reducedMotion && sameContent(previous.option, option)) return;
    if (previous) state.current = carryViewState(previous.option, option, state.current);
    instance.setOption(prepareOption(option, { reducedMotion, state: state.current }), { notMerge: update === "replace" });
    applied.current = { option, reduced: reducedMotion };
    setHidden(state.current.hidden);
  }, [option, active, created, reducedMotion, update]);

  // The size: the chart follows its box, and is told again when it is shown.
  useEffect(() => {
    const element = host.current;
    if (!element) return;
    const fit = () => { if (activeRef.current && element.clientWidth > 0 && element.clientHeight > 0) chart.current?.resize(); };
    const observer = new ResizeObserver(fit);
    observer.observe(element);
    fit();
    return () => observer.disconnect();
  }, [created, active]);

  const shown = useMemo(() => showTable ? table ?? tableFromOption(option) : null, [showTable, table, option]);
  const entries = useMemo(() => option.legend === undefined ? [] : legendEntries(option, tokens?.series ?? []), [option, tokens]);
  const toggle = useCallback((name: string) => {
    const instance = chart.current;
    if (instance) instance.dispatchAction({ type: "legendToggleSelect", name });
    else setHidden(known => { const next = new Set(known); if (!next.delete(name)) next.add(name); return next; });
  }, []);
  const copy = () => { if (shown) void navigator.clipboard?.writeText(tableToText(shown)); };

  const style = { "--chart-height": `${height}px` } as CSSProperties;
  return <figure className={`chart${className ? ` ${className}` : ""}`} ref={frame} aria-labelledby={labelId} style={style} data-active={active || undefined} data-failed={failure ? "" : undefined}>
    <div className="chart-title-row">
      <span className="chart-label" id={labelId}>{ariaLabel}</span>
      {!hideTableToggle && <Button variant="minimal" size="small" className="chart-table-toggle" aria-pressed={showTable} onClick={() => setShowTable(value => !value)}>
        {showTable ? t("Show as chart") : t("Show as table")}</Button>}
    </div>
    {entries.length > 0 && !showTable && <ul className="chart-legend" aria-label={t("Series")}>{entries.map(entry =>
      <li key={entry.name}><button type="button" className="chart-legend-item" aria-pressed={!hidden.has(entry.name)} onClick={() => toggle(entry.name)}>
        <i style={{ background: entry.color }} aria-hidden="true" />{entry.name}</button></li>)}</ul>}
    <div className="chart-surface" role="img" aria-label={ariaLabel} hidden={showTable} ref={host} />
    {failure && <p className="chart-failure" role="alert">{failure}</p>}
    {shown && <div className="chart-table-scroll">
      <HTMLTable compact striped className="chart-table">
        <caption>{ariaLabel}</caption>
        <thead><tr>{shown.columns.map((column, index) => <th key={index} scope="col">{column}</th>)}</tr></thead>
        <tbody>{shown.rows.map((row, index) => <tr key={index}>{row.map((value, at) =>
          at === 0 ? <th key={at} scope="row">{value ?? ""}</th> : <td key={at}>{typeof value === "number" ? value.toLocaleString(locale) : value ?? ""}</td>)}</tr>)}</tbody>
      </HTMLTable>
      <Button variant="minimal" size="small" className="chart-table-copy" onClick={copy}>{t("Copy")}</Button>
    </div>}
  </figure>;
}
