import { useCallback, useEffect, useLayoutEffect, useRef, useState, type DialogHTMLAttributes, type ReactNode, type Ref } from "react";
import { Button, PortalProvider } from "@blueprintjs/core";
import { Rnd } from "react-rnd";
import { AppIcon } from "./AppIcon";
import { keptOnOutsidePress } from "./modalDialogs";
import { useShellLanguage } from "./shellLanguage";
import { canStepWindow, centeredWindowGeometry, clampWindowGeometry, loadWindowGeometry, saveWindowGeometry, steppedWindowGeometry, type WindowGeometry, type WindowSize } from "./windowGeometry";

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

/**
 * The height of the native title bar when its controls (the caption buttons of Windows, the traffic lights of macOS)
 * are drawn over the page, as NeoAstra gives it on the document element; 0 without them (fullscreen, a browser).
 * A window kept out of it never has its own buttons under those of the main window.
 */
export function nativeTitleBarHeight(): number {
  if (typeof document === "undefined") return 0;
  const style = getComputedStyle(document.documentElement);
  const pixels = (name: string) => Number.parseFloat(style.getPropertyValue(name)) || 0;
  return pixels("--neoastra-titlebar-inset-left") > 0 || pixels("--neoastra-titlebar-inset-right") > 0 ? pixels("--neoastra-titlebar-height") : 0;
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
 * remembered per `storageKey`; the title bar's Smaller and Larger change its size a step at a time, around its center,
 * and its restore button (or a double-click on it) resets it.
 */
export function AppWindowSurface({ storageKey, title, titleId, preferredSize, minimumSize = { width: 360, height: 220 },
  onClose, closeLabel, headerActions, children, closeRef }: SurfaceProps) {
  const { t } = useShellLanguage();
  const [viewport, setViewport] = useState(viewportSize);
  const [titleBar, setTitleBar] = useState(nativeTitleBarHeight);
  const [stored, setStored] = useState<WindowGeometry | null>(() => loadWindowGeometry(storageKey));
  const preferred = useRef(preferredSize); preferred.current = preferredSize;
  useEffect(() => {
    const resized = () => { setViewport(viewportSize()); setTitleBar(nativeTitleBarHeight()); };
    window.addEventListener("resize", resized);
    // The title bar changes without a resize too: its controls go and come back with fullscreen, its style can change.
    const titleBarChanged = new MutationObserver(() => setTitleBar(nativeTitleBarHeight()));
    titleBarChanged.observe(document.documentElement, { attributes: true, attributeFilter: ["style", "data-neoastra-titlebar", "data-neoastra-window-state"] });
    return () => { window.removeEventListener("resize", resized); titleBarChanged.disconnect(); };
  }, []);
  const update = useCallback((value: WindowGeometry | null) => { setStored(value); saveWindowGeometry(storageKey, value); }, [storageKey]);
  // The window keeps out of the native title bar: its size is chosen for the room below it.
  const geometry = stored ? clampWindowGeometry(stored, viewport, minimumSize, titleBar)
    : centeredWindowGeometry(viewport, preferred.current({ width: viewport.width, height: Math.max(viewport.height - titleBar, 0) }), minimumSize, titleBar);
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
        <span className="app-window-size" role="group" aria-label={t("Window size")}>
          <Button variant="minimal" size="small" icon={<AppIcon name="windowShrink" size={15} />} disabled={!canStepWindow(geometry, viewport, minimumSize, titleBar, -1)}
            aria-label={t("Smaller")} title={t("Make the window smaller")} onClick={() => update(steppedWindowGeometry(geometry, viewport, minimumSize, titleBar, -1))} />
          <Button variant="minimal" size="small" icon={<AppIcon name="windowGrow" size={15} />} disabled={!canStepWindow(geometry, viewport, minimumSize, titleBar, 1)}
            aria-label={t("Larger")} title={t("Make the window larger")} onClick={() => update(steppedWindowGeometry(geometry, viewport, minimumSize, titleBar, 1))} />
        </span>
        <Button variant="minimal" size="small" icon={<AppIcon name="reset" size={15} />} disabled={!stored}
          aria-label={t("Restore default size and position")} title={t("Restore default size and position")} onClick={() => update(null)} />
        <Button ref={closeRef} className="app-window-close" variant="minimal" size="small" icon={<AppIcon name="close" size={16} />} aria-label={closeLabel} title={closeLabel} onClick={onClose} />
      </div>
    </header>
    <div className="app-window-body">{children}</div>
  </Rnd>;
}

/**
 * A modal desktop-style window: a native modal dialog covers the viewport and hosts one movable,
 * resizable surface. Blueprint overlays opened inside it are portaled into the dialog so they stay
 * in the top layer. A press beside the window dismisses it as Escape does (`onCancel`).
 */
export function AppWindow({ storageKey, title, titleId, className, preferredSize, minimumSize = { width: 480, height: 320 },
  onClose, closeLabel, headerActions, onOpened, keepOnOutsidePress = false, children, ...dialog }: Omit<SurfaceProps, "closeRef" | "titleId"> & {
  titleId: string; className?: string;
  /** Called once, after the content is mounted inside the open dialog (for initial focus). */
  onOpened?: () => void;
  /** The window asks something, or holds what is being typed: a press beside it leaves it open. */
  keepOnOutsidePress?: boolean;
} & Omit<DialogHTMLAttributes<HTMLDialogElement>, "title" | "className" | "children">) {
  const [layer, setLayer] = useState<HTMLDialogElement | null>(null);
  useLayoutEffect(() => {
    if (!layer) return;
    layer.showModal();
    // The dialog focuses its first control: not a size of the window, which Enter would change, but its close button.
    if (document.activeElement instanceof HTMLElement && document.activeElement.closest(".app-window-size"))
      layer.querySelector<HTMLElement>(".app-window-close")?.focus();
    return () => { if (layer.open) layer.close(); };
  }, [layer]);
  const opened = useRef(false);
  const mounted = !!layer;
  useLayoutEffect(() => { if (mounted && !opened.current) { opened.current = true; onOpened?.(); } }, [mounted]);
  return <dialog ref={setLayer} className={`app-window-layer${className ? ` ${className}` : ""}`} aria-modal="true" aria-labelledby={titleId}
    {...(keepOnOutsidePress ? keptOnOutsidePress : null)} {...dialog}>
    {layer && <PortalProvider portalContainer={layer}>
      <AppWindowSurface storageKey={storageKey} title={title} titleId={titleId} preferredSize={preferredSize} minimumSize={minimumSize}
        onClose={onClose} closeLabel={closeLabel} headerActions={headerActions}>{children}</AppWindowSurface>
    </PortalProvider>}
  </dialog>;
}
