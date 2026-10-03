import { useCallback, useEffect, useLayoutEffect, useRef, useState, type DialogHTMLAttributes, type ReactNode, type Ref } from "react";
import { Button, PortalProvider } from "@blueprintjs/core";
import { Rnd } from "react-rnd";
import { AppIcon } from "./AppIcon";
import { useShellLanguage } from "./shellLanguage";
import { centeredWindowGeometry, clampWindowGeometry, loadWindowGeometry, saveWindowGeometry, type WindowGeometry, type WindowSize } from "./windowGeometry";

/** Tracks an element's client size; zero until it is mounted and measured. */
export function useElementSize(element: HTMLElement | null): WindowSize {
  const [size, setSize] = useState<WindowSize>({ width: 0, height: 0 });
  useLayoutEffect(() => {
    if (!element) return;
    const measure = () => setSize(previous => previous.width === element.clientWidth && previous.height === element.clientHeight
      ? previous : { width: element.clientWidth, height: element.clientHeight });
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    measure();
    return () => observer.disconnect();
  }, [element]);
  return size;
}

// Static rendering (tests) has no window; any plausible size produces valid markup.
function viewportSize(): WindowSize {
  return typeof window === "undefined" ? { width: 1280, height: 800 } : { width: window.innerWidth, height: window.innerHeight };
}

type SurfaceProps = {
  storageKey: string; title: ReactNode; titleId?: string;
  preferredSize: (viewport: WindowSize) => WindowSize; minimumSize?: WindowSize;
  onClose: () => void; closeLabel: string; headerActions?: ReactNode; children: ReactNode;
  /** Receives the title-bar close button, for hosts that manage initial focus themselves. */
  closeRef?: Ref<HTMLButtonElement>;
};

/**
 * The movable, resizable surface of a desktop-style window (react-rnd). It must be the only child of a
 * full-viewport layer, normally a modal `dialog`: `AppWindow` supplies one, and an existing
 * `dialog.app-dialog` becomes such a layer when this surface is its direct child. The geometry is
 * remembered per `storageKey`; the title bar's restore button (or a double-click on it) resets it.
 */
export function AppWindowSurface({ storageKey, title, titleId, preferredSize, minimumSize = { width: 360, height: 220 },
  onClose, closeLabel, headerActions, children, closeRef }: SurfaceProps) {
  const { t } = useShellLanguage();
  const [viewport, setViewport] = useState(viewportSize);
  const [stored, setStored] = useState<WindowGeometry | null>(() => loadWindowGeometry(storageKey));
  const preferred = useRef(preferredSize); preferred.current = preferredSize;
  useEffect(() => {
    const resized = () => setViewport(viewportSize());
    window.addEventListener("resize", resized);
    return () => window.removeEventListener("resize", resized);
  }, []);
  const update = useCallback((value: WindowGeometry | null) => { setStored(value); saveWindowGeometry(storageKey, value); }, [storageKey]);
  const geometry = stored ? clampWindowGeometry(stored, viewport, minimumSize) : centeredWindowGeometry(viewport, preferred.current(viewport), minimumSize);
  // react-rnd measures its offset from the parent when it mounts. The hosting dialog is usually not open yet
  // (its showModal runs in a parent layout effect), so every rectangle is empty and a real position would be
  // recorded as an offset. Mount at the origin, then place the window before the first paint.
  const [placed, setPlaced] = useState(false);
  useLayoutEffect(() => setPlaced(true), []);
  return <Rnd className="app-window" bounds="parent" dragHandleClassName="app-window-titlebar" cancel=".app-window-titlebar button, .app-window-titlebar input"
    size={{ width: geometry.width, height: geometry.height }} position={placed ? { x: geometry.x, y: geometry.y } : { x: 0, y: 0 }}
    minWidth={Math.min(minimumSize.width, viewport.width)} minHeight={Math.min(minimumSize.height, viewport.height)}
    onDragStop={(_event, data) => update({ ...geometry, x: data.x, y: data.y })}
    onResizeStop={(_event, _direction, element, _delta, position) => update({ x: position.x, y: position.y, width: element.offsetWidth, height: element.offsetHeight })}>
    <header className="app-window-titlebar" onDoubleClick={event => { if (!(event.target as HTMLElement).closest("button")) update(null); }}>
      <h2 id={titleId}>{title}</h2>
      <div className="app-window-actions">{headerActions}
        <Button variant="minimal" size="small" icon={<AppIcon name="reset" size={15} />} disabled={!stored}
          aria-label={t("Restore default size and position")} title={t("Restore default size and position")} onClick={() => update(null)} />
        <Button ref={closeRef} variant="minimal" size="small" icon={<AppIcon name="close" size={16} />} aria-label={closeLabel} title={closeLabel} onClick={onClose} />
      </div>
    </header>
    <div className="app-window-body">{children}</div>
  </Rnd>;
}

/**
 * A modal desktop-style window: a native modal dialog covers the viewport and hosts one movable,
 * resizable surface. Blueprint overlays opened inside it are portaled into the dialog so they stay
 * in the top layer.
 */
export function AppWindow({ storageKey, title, titleId, className, preferredSize, minimumSize = { width: 480, height: 320 },
  onClose, closeLabel, headerActions, onOpened, children, ...dialog }: Omit<SurfaceProps, "closeRef" | "titleId"> & {
  titleId: string; className?: string;
  /** Called once, after the content is mounted inside the open dialog (for initial focus). */
  onOpened?: () => void;
} & Omit<DialogHTMLAttributes<HTMLDialogElement>, "title" | "className" | "children">) {
  const [layer, setLayer] = useState<HTMLDialogElement | null>(null);
  useLayoutEffect(() => {
    if (!layer) return;
    layer.showModal();
    return () => { if (layer.open) layer.close(); };
  }, [layer]);
  const opened = useRef(false);
  const mounted = !!layer;
  useLayoutEffect(() => { if (mounted && !opened.current) { opened.current = true; onOpened?.(); } }, [mounted]);
  return <dialog ref={setLayer} className={`app-window-layer${className ? ` ${className}` : ""}`} aria-modal="true" aria-labelledby={titleId} {...dialog}>
    {layer && <PortalProvider portalContainer={layer}>
      <AppWindowSurface storageKey={storageKey} title={title} titleId={titleId} preferredSize={preferredSize} minimumSize={minimumSize}
        onClose={onClose} closeLabel={closeLabel} headerActions={headerActions}>{children}</AppWindowSurface>
    </PortalProvider>}
  </dialog>;
}
