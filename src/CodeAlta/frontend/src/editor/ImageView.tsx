import { useEffect, useRef, useState } from "react";
import { Button } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";

const steps = [0.1, 0.25, 0.5, 0.75, 1, 1.5, 2, 3, 4, 6, 8, 12, 16] as const;
/** The next zoom step above or below a zoom, within the steps the view offers. */
export function nextImageZoom(zoom: number, direction: 1 | -1): number {
  return direction > 0 ? steps.find(step => step > zoom + 0.001) ?? steps[steps.length - 1] : [...steps].reverse().find(step => step < zoom - 0.001) ?? steps[0];
}
/** The zoom at which a picture fits a view. Only a drawing is enlarged to fill it: pixels are not. */
export function fitImageZoom(width: number, height: number, viewWidth: number, viewHeight: number, enlarge = false): number {
  if (!(width > 0 && height > 0 && viewWidth > 0 && viewHeight > 0)) return 1;
  const fit = Math.min(viewWidth / width, viewHeight / height);
  return enlarge ? fit : Math.min(1, fit);
}

/**
 * A picture on a checkered background: fitted to the pane until it is zoomed, with its real size one click away.
 * Ctrl and the wheel zoom too. A drawing (SVG) fills the pane, whatever size it says it has.
 */
export function ImageView({ url, label, vector = false, onSize }: {
  url: string; label: string;
  /** The picture is a drawing: it is enlarged to the pane, and stays sharp at any zoom. */
  vector?: boolean;
  /** The size of the picture in pixels, once it is known; null when it could not be drawn. */
  onSize?: (size: Readonly<{ width: number; height: number }> | null) => void;
}) {
  const { t } = useShellLanguage();
  const view = useRef<HTMLDivElement>(null);
  const [size, setSize] = useState<{ width: number; height: number } | null>(null);
  const [zoom, setZoom] = useState<number | null>(null);
  const [failed, setFailed] = useState(false);
  const report = useRef(onSize); report.current = onSize;
  useEffect(() => { setSize(null); setFailed(false); }, [url]);
  const fitted = () => size && view.current ? fitImageZoom(size.width, size.height, view.current.clientWidth - 32, view.current.clientHeight - 32, vector) : 1;
  const step = (direction: 1 | -1) => setZoom(current => nextImageZoom(current ?? fitted(), direction));
  // Ctrl and the wheel zoom; without Ctrl the wheel scrolls a picture larger than the pane.
  useEffect(() => {
    const node = view.current;
    if (!node) return;
    const wheel = (event: WheelEvent) => {
      if (!event.ctrlKey || event.deltaY === 0) return;
      event.preventDefault();
      step(event.deltaY < 0 ? 1 : -1);
    };
    node.addEventListener("wheel", wheel, { passive: false });
    return () => node.removeEventListener("wheel", wheel);
  });
  const shownZoom = zoom ?? fitted();
  return <div className="editor-image" data-fit={zoom === null} data-vector={vector}>
    <div ref={view} className="editor-image-view" tabIndex={0} aria-label={label}>
      {failed ? <p className="editor-image-failed"><AppIcon name="imageOff" size={28} />{t("This picture cannot be drawn.")}</p>
        : <img src={url} alt={label} draggable={false} data-pixels={!vector && shownZoom >= 3}
          style={zoom === null || !size ? undefined : { width: Math.max(1, Math.round(size.width * zoom)), height: Math.max(1, Math.round(size.height * zoom)) }}
          onLoad={event => {
            const loaded = { width: event.currentTarget.naturalWidth, height: event.currentTarget.naturalHeight };
            setSize(loaded); report.current?.(loaded);
          }}
          onError={() => { setFailed(true); report.current?.(null); }} />}
    </div>
    {!failed && <div className="editor-image-tools" role="toolbar" aria-label={t("Zoom")}>
      <Button variant="minimal" size="small" icon={<AppIcon name="zoomOut" size={14} />} aria-label={t("Zoom out")} title={t("Zoom out")} disabled={!size || shownZoom <= steps[0]} onClick={() => step(-1)} />
      <Button variant="minimal" size="small" className="editor-image-zoom" title={t("Actual size")} disabled={!size} onClick={() => setZoom(1)}>{Math.round(shownZoom * 100)}%</Button>
      <Button variant="minimal" size="small" icon={<AppIcon name="zoomIn" size={14} />} aria-label={t("Zoom in")} title={t("Zoom in")} disabled={!size || shownZoom >= steps[steps.length - 1]} onClick={() => step(1)} />
      <Button variant="minimal" size="small" icon={<AppIcon name="fit" size={14} />} aria-label={t("Fit to the pane")} title={t("Fit to the pane")} active={zoom === null} onClick={() => setZoom(null)} />
    </div>}
  </div>;
}
