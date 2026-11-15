import type { CSSProperties } from "react";
import { Chart, type ChartDatum } from "../charts/Chart";
import { seriesVariable } from "../charts/palette";
import "./storage.css";

/** A segment of a share donut, in the order of the palette: the first takes its first color. */
export type ShareSegment = Readonly<{ name: string; value: number; id?: string | null }>;

/** The color of a row of a table that is the legend of a donut: the color of its segment. */
export function ShareSwatch({ index, className }: { index: number; className?: string }) {
  return <span className={`storage-swatch${className ? ` ${className}` : ""}`} aria-hidden="true" style={{ "--storage-swatch": `var(${seriesVariable(index)})` } as CSSProperties} />;
}

/**
 * A donut of shares beside the table that is its legend: no legend and no table toggle of its own. Segments take the
 * colors of the palette in their order; a click on one gives its `id`. Its size is in the CSS (`storage-donut`).
 */
export function ShareDonut({ ariaLabel, segments, visible, onSelect }: {
  ariaLabel: string; segments: readonly ShareSegment[]; visible?: boolean; onSelect?: (id: string) => void;
}) {
  const select = onSelect && ((datum: ChartDatum) => {
    const id = (datum.data as { id?: unknown } | null)?.id;
    if (typeof id === "string") onSelect(id);
  });
  return <Chart className="storage-donut" ariaLabel={ariaLabel} height={168} hideTableToggle visible={visible} onSelect={select}
    option={{ tooltip: { trigger: "item", formatter: "{b}: {d}%" }, series: [{ type: "pie", radius: ["56%", "94%"], padAngle: 1, itemStyle: { borderRadius: 2 },
      label: { show: false }, emphasis: { scale: false }, data: segments.map(segment => ({ name: segment.name, value: segment.value, ...segment.id ? { id: segment.id } : {} })) }] }} />;
}
