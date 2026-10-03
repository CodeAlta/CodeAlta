import { useCallback, useLayoutEffect, useRef, useState, type DialogHTMLAttributes, type ReactNode } from "react";
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

/**
 * A modal desktop-style window: a native modal dialog covers the viewport and hosts one movable,
 * resizable surface (react-rnd). Its geometry is remembered per `storageKey` and can be restored.
 * Blueprint overlays opened inside it are portaled into the dialog so they stay in the top layer.
 */
export function AppWindow({ storageKey, title, titleId, className, preferredSize, minimumSize = { width: 480, height: 320 },
  onClose, closeLabel, headerActions, onOpened, children, ...dialog }: {
  storageKey: string; title: ReactNode; titleId: string; className?: string;
  preferredSize: (viewport: WindowSize) => WindowSize; minimumSize?: WindowSize;
  onClose: () => void; closeLabel: string; headerActions?: ReactNode; children: ReactNode;
  /** Called once, after the content is mounted inside the open dialog (for initial focus). */
  onOpened?: () => void;
} & Omit<DialogHTMLAttributes<HTMLDialogElement>, "title" | "className" | "children">) {
  const { t } = useShellLanguage();
  const [layer, setLayer] = useState<HTMLDialogElement | null>(null);
  const viewport = useElementSize(layer);
  const [stored, setStored] = useState<WindowGeometry | null>(() => loadWindowGeometry(storageKey));
  const preferred = useRef(preferredSize); preferred.current = preferredSize;
  useLayoutEffect(() => {
    if (!layer) return;
    layer.showModal();
    return () => { if (layer.open) layer.close(); };
  }, [layer]);
  const update = useCallback((value: WindowGeometry | null) => { setStored(value); saveWindowGeometry(storageKey, value); }, [storageKey]);
  const ready = viewport.width > 0 && viewport.height > 0;
  const geometry = !ready ? null : stored ? clampWindowGeometry(stored, viewport, minimumSize)
    : centeredWindowGeometry(viewport, preferred.current(viewport), minimumSize);
  const opened = useRef(false);
  const mounted = !!layer && !!geometry;
  useLayoutEffect(() => { if (mounted && !opened.current) { opened.current = true; onOpened?.(); } }, [mounted]);
  return <dialog ref={setLayer} className={`app-window-layer${className ? ` ${className}` : ""}`} aria-modal="true" aria-labelledby={titleId} {...dialog}>
    {layer && geometry && <PortalProvider portalContainer={layer}>
      <Rnd className="app-window" bounds="parent" dragHandleClassName="app-window-titlebar" cancel=".app-window-titlebar button, .app-window-titlebar input"
        size={{ width: geometry.width, height: geometry.height }} position={{ x: geometry.x, y: geometry.y }}
        minWidth={Math.min(minimumSize.width, viewport.width)} minHeight={Math.min(minimumSize.height, viewport.height)}
        onDragStop={(_event, data) => update({ ...geometry, x: data.x, y: data.y })}
        onResizeStop={(_event, _direction, element, _delta, position) => update({ x: position.x, y: position.y, width: element.offsetWidth, height: element.offsetHeight })}>
        <header className="app-window-titlebar" onDoubleClick={event => { if (!(event.target as HTMLElement).closest("button")) update(null); }}>
          <h2 id={titleId}>{title}</h2>
          <div className="app-window-actions">{headerActions}
            <Button variant="minimal" size="small" icon={<AppIcon name="reset" size={15} />} disabled={!stored}
              aria-label={t("Restore default size and position")} title={t("Restore default size and position")} onClick={() => update(null)} />
            <Button variant="minimal" size="small" icon={<AppIcon name="close" size={16} />} aria-label={closeLabel} title={closeLabel} onClick={onClose} />
          </div>
        </header>
        <div className="app-window-body">{children}</div>
      </Rnd>
    </PortalProvider>}
  </dialog>;
}
