import { useEffect, useMemo, useState } from "react";
import { readChartTokens, seriesColor, type ChartTokens } from "../charts";
import { useStatistics } from "./runtime";

// The colors the option builders take. ECharts cannot read a CSS variable, so the page reads its palette (the `--chart-*`
// variables) and the three state colors from the root, and again when the theme or the color scheme changes.

/** What the pages draw with. */
export type PageColors = Readonly<{
  tokens: ChartTokens;
  /** The color of a series by its name: the same name has the same color everywhere in this canvas. */
  color: (key: string) => string;
  /** The color of the series at a place of the palette. */
  at: (index: number) => string;
  muted: string;
  /** The states: a completed run, a failed one, an interrupted one. */
  good: string;
  bad: string;
  warn: string;
}>;

function useThemeVersion(): number {
  const [version, setVersion] = useState(0);
  useEffect(() => {
    let frame = 0;
    const observer = new MutationObserver(() => { cancelAnimationFrame(frame); frame = requestAnimationFrame(() => setVersion(value => value + 1)); });
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ["class", "data-theme", "data-palette", "style"] });
    return () => { observer.disconnect(); cancelAnimationFrame(frame); };
  }, []);
  return version;
}

/** The plain color a CSS variable stands for on the page, or `fallback` when it is not defined. */
export function readVariable(variable: string, fallback: string): string {
  const probe = document.createElement("span");
  probe.style.cssText = "position:absolute;visibility:hidden;pointer-events:none;width:0;height:0";
  document.body.appendChild(probe);
  try {
    if (getComputedStyle(document.documentElement).getPropertyValue(variable).trim() === "") return fallback;
    probe.style.color = `var(${variable})`;
    return getComputedStyle(probe).color || fallback;
  } finally { probe.remove(); }
}

/** The colors of the page, read again when the theme changes. */
export function usePageColors(): PageColors {
  const { colorIndex } = useStatistics();
  const version = useThemeVersion();
  const tokens = useMemo(() => readChartTokens(document.documentElement), [version]); // eslint-disable-line react-hooks/exhaustive-deps
  const states = useMemo(() => ({
    good: readVariable("--green", tokens.dark ? "#43bf4d" : "#29a634"), bad: readVariable("--red", tokens.dark ? "#ec7b6f" : "#d33d17"),
    warn: readVariable("--amber", readVariable("--orange", tokens.dark ? "#f29b57" : "#d9822b")),
  }), [tokens]);
  return useMemo<PageColors>(() => ({
    tokens, ...states, muted: tokens.muted,
    color: key => seriesColor(tokens.series, colorIndex(key)),
    at: index => seriesColor(tokens.series, index),
  }), [tokens, states, colorIndex]);
}
