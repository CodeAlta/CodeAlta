import { createContext, useContext, useEffect, useId, useRef, useState, type ComponentType } from "react";
import { Button } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import { AppWindow } from "./AppWindow";
import { diagramPng } from "./diagrams";
import { ImageView } from "./editor/ImageView";
import { useShellLanguage } from "./shellLanguage";
import { copyTimelineImage } from "./timelineImages";

/**
 * The read-only editor that shows the source of a diagram in its window: the code editor of the window, which
 * provides it. Null where the window has none (a page of tests): the source is shown as text.
 */
export const DiagramSourceEditorContext = createContext<ComponentType<{ text: string; label: string }> | null>(null);

/**
 * A drawn diagram: its picture in the colors of the page (`diagramPicture`), its text (Mermaid, or the Vega-Lite spec of a
 * chart), and the color behind it.
 */
export type ExpandedDiagram = Readonly<{ picture: string; source: string; background: string; chart?: boolean }>;

/** The SVG of a picture made by `diagramPicture`, as text; null for any other address. */
export function pictureSvg(picture: string): string | null {
  const prefix = "data:image/svg+xml;charset=utf-8,";
  if (!picture.startsWith(prefix)) return null;
  try { return decodeURIComponent(picture.slice(prefix.length)); } catch { return null; }
}

type CopyState = "idle" | "copied" | "failed";

/** A copy that says for a moment whether it was made. */
function useCopyState(): [CopyState, (copy: () => Promise<boolean>) => void] {
  const [state, setState] = useState<CopyState>("idle");
  const reset = useRef<number | undefined>(undefined);
  const sequence = useRef(0);
  useEffect(() => () => { sequence.current++; window.clearTimeout(reset.current); }, []);
  return [state, copy => {
    const current = ++sequence.current;
    window.clearTimeout(reset.current);
    setState("idle");
    void copy().catch(() => false).then(copied => {
      if (current !== sequence.current) return;
      setState(copied ? "copied" : "failed");
      reset.current = window.setTimeout(() => { if (current === sequence.current) setState("idle"); }, 1600);
    });
  }];
}

/**
 * The window of a drawn diagram, opened by the Expand button of its block: nearly as large as the window of the
 * application, the diagram fitted to it until it is zoomed (the tools, Ctrl and the wheel, + and -), then scrolled
 * or dragged. Its title bar copies the diagram as a picture (PNG, twice its size, on the color behind it, so that
 * its text reads wherever it is pasted), as SVG, and its text, and shows that text beside it in a read-only editor.
 */
export function DiagramViewer({ diagram, onClose }: { diagram: ExpandedDiagram; onClose: () => void }) {
  const { t } = useShellLanguage();
  const titleId = useId();
  const body = useRef<HTMLDivElement>(null);
  const SourceEditor = useContext(DiagramSourceEditorContext);
  const [showSource, setShowSource] = useState(false);
  const [image, copyImage] = useCopyState();
  const [text, copyText] = useCopyState();
  const [vector, copyVector] = useCopyState();
  const title = diagram.chart ? t("Chart") : t("Diagram");
  const imageLabel = image === "copied" ? t("Image copied") : image === "failed" ? t("Copy failed") : t("Copy image");
  const vectorLabel = vector === "copied" ? t("SVG copied") : vector === "failed" ? t("Copy failed") : t("Copy SVG");
  const textLabel = text === "copied" ? t("Source copied") : text === "failed" ? t("Copy failed") : t("Copy the source");
  const sourceLabel = diagram.chart ? t("Source of the chart") : t("Source of the diagram");
  const icon = (state: CopyState, name: "fileImage" | "fileCode" | "copy") => <AppIcon name={state === "copied" ? "checked" : state === "failed" ? "error" : name} size={15} />;
  return <AppWindow storageKey="codealta.desktop.window.diagram.v1" className="diagram-viewer-dialog" titleId={titleId} title={title}
    preferredSize={viewport => ({ width: Math.max(viewport.width - 48, 0), height: Math.max(viewport.height - 48, 0) })}
    minimumSize={{ width: 420, height: 300 }} onClose={onClose} closeLabel={t("Close")}
    headerActions={<>
      <Button variant="minimal" size="small" className={`diagram-viewer-copy-image copy-${image}`} icon={icon(image, "fileImage")} aria-label={imageLabel} title={imageLabel}
        // The clipboard is opened while the page still has the click; the picture is made meanwhile.
        onClick={() => copyImage(() => copyTimelineImage(diagram.picture, undefined, url => diagramPng(url, diagram.background)))} />
      <Button variant="minimal" size="small" className={`diagram-viewer-copy-svg copy-${vector}`} icon={icon(vector, "fileCode")} aria-label={vectorLabel} title={vectorLabel}
        onClick={() => copyVector(async () => { const svg = pictureSvg(diagram.picture); if (!svg || !navigator.clipboard?.writeText) return false; await navigator.clipboard.writeText(svg); return true; })} />
      <Button variant="minimal" size="small" className={`diagram-viewer-copy-source copy-${text}`} icon={icon(text, "copy")} aria-label={textLabel} title={textLabel}
        onClick={() => copyText(async () => { if (!navigator.clipboard?.writeText) return false; await navigator.clipboard.writeText(diagram.source); return true; })} />
      <span className="sr-only" aria-live="polite">{image !== "idle" ? imageLabel : vector !== "idle" ? vectorLabel : text !== "idle" ? textLabel : ""}</span>
      <Button variant="minimal" size="small" className="diagram-viewer-source-toggle" icon={<AppIcon name="code" size={15} />} active={showSource} aria-pressed={showSource}
        aria-label={t("Show the source")} title={t("Show the source")} onClick={() => setShowSource(value => !value)} />
    </>}
    // The keys of the diagram (+, -, 0, the arrows) reach it at once.
    onOpened={() => body.current?.querySelector<HTMLElement>(".editor-image-view")?.focus()}
    // The window is a child of the text in React: what happens in it is not a click or a key of the row around the text.
    onCancel={event => { event.preventDefault(); onClose(); }} onKeyDown={event => event.stopPropagation()} onClick={event => event.stopPropagation()}>
    <div ref={body} className="diagram-viewer" data-source={showSource}>
      <ImageView vector url={diagram.picture} label={title} />
      {showSource && <div className="diagram-viewer-source" role="region" aria-label={sourceLabel}>
        {SourceEditor ? <SourceEditor text={diagram.source} label={sourceLabel} /> : <pre tabIndex={0}>{diagram.source}</pre>}
      </div>}
    </div>
  </AppWindow>;
}
