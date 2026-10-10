import { useLayoutEffect, useRef, useState, type CSSProperties } from "react";
import { Chart, type ChartProps } from "../charts";
import { useText } from "./text";
import { hatchFraction, type PlotBox } from "./options";
import { useStatistics } from "./runtime";

/** The part of a chart that is not read yet: how many buckets, out of how many, and where the plot sits in the chart. */
export type Hatch = Readonly<{ unread: number; buckets: number; boundaryGap: boolean; plot: PlotBox }>;

/** The properties of `StatChart`. */
export type StatChartProps = ChartProps & Readonly<{ hatch?: Hatch | null }>;

/** The top and height of an element inside its figure, kept up to date while the figure changes size (a legend that wraps). */
function useSurface(frame: React.RefObject<HTMLDivElement | null>): Readonly<{ top: number; height: number }> {
  const [box, setBox] = useState({ top: 0, height: 0 });
  useLayoutEffect(() => {
    const element = frame.current;
    if (!element) return;
    const measure = () => {
      const surface = element.querySelector<HTMLElement>(".chart-surface");
      if (!surface) return;
      const next = { top: surface.offsetTop, height: surface.offsetHeight };
      setBox(known => known.top === next.top && known.height === next.height ? known : next);
    };
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    return () => observer.disconnect();
  }, [frame]);
  return box;
}

/**
 * A chart of the canvas: the shared `Chart`, shown only while the canvas is, in the cursor group of the page, and with the
 * part of the period that is not read yet laid over its plot as a hatch. The hatch is drawn with CSS over the plot box the
 * option fixed (no chart library writes it), so it carries no legend entry, no tooltip row and no row of the table.
 */
export function StatChart({ hatch, visible = true, group = "statistics", height = 240, ...chart }: StatChartProps) {
  const { visible: canvasVisible } = useStatistics();
  const { t } = useText();
  const frame = useRef<HTMLDivElement>(null);
  const surface = useSurface(frame);
  const fraction = hatch ? hatchFraction(hatch.unread, hatch.buckets, hatch.boundaryGap) : 0;
  const style = hatch && fraction > 0 ? {
    top: surface.top + hatch.plot.top, height: Math.max(0, surface.height - hatch.plot.top - hatch.plot.bottom), left: hatch.plot.left,
    width: `calc((100% - ${hatch.plot.left + hatch.plot.right}px) * ${fraction})`,
  } as CSSProperties : null;
  return <div className="stat-chart" ref={frame}>
    <Chart {...chart} height={height} group={group} visible={visible && canvasVisible} />
    {style && <div className="stat-hatch" style={style} data-testid="hatch"><span>{t("Not read yet")}</span></div>}
  </div>;
}
