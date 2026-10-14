import { useCallback, useId, useLayoutEffect, useRef, useState } from "react";
import { Button } from "@blueprintjs/core";
import { Rnd } from "react-rnd";
import { NotesPanel } from "./NotesPanel";
import { AppIcon } from "./AppIcon";
import { useElementSize } from "./AppWindow";
import type { createNotesReader } from "./sessionNotes";
import type { createMutationCapability } from "./sessionOperations";
import { useShellLanguage } from "./shellLanguage";
import { clampWindowGeometry, loadWindowGeometry, saveWindowGeometry, type WindowGeometry, type WindowSize } from "./windowGeometry";
import { modalDialogOpen } from "./modalDialogs";

// One placement for every session pane, stored relative to the pane's top-right corner so it
// survives pane resizes: `x` is the distance from the right edge, `y` from the top.
const storageKey = "codealta.desktop.notes-window.v1";
const minimum = { width: 240, height: 140 };

// The border box of an element, which the drag bounds of a window must match.
function useOuterSize(element: HTMLElement | null): WindowSize {
  const [size, setSize] = useState<WindowSize>({ width: 0, height: 0 });
  useLayoutEffect(() => {
    if (!element) return;
    const measure = () => setSize(previous => previous.width === element.offsetWidth && previous.height === element.offsetHeight
      ? previous : { width: element.offsetWidth, height: element.offsetHeight });
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    measure();
    return () => observer.disconnect();
  }, [element]);
  return size;
}

export function SessionNotesOverlay({ sessionId, epoch, capability, fallbackMarkdown, hidden = false, reader, observing = true }: {
  reader: ReturnType<typeof createNotesReader>;
  sessionId: string; epoch?: string; capability?: ReturnType<typeof createMutationCapability>;
  fallbackMarkdown: string;
  /** Hides the whole overlay, its tab included; the notes stay loaded for when it is shown again. */
  hidden?: boolean;
  observing?: boolean;
}) {
  const { t } = useShellLanguage();
  const panelId = useId();
  const [collapsed, setCollapsed] = useState(true);
  const [bounds, setBounds] = useState<HTMLElement | null>(null);
  const area = useElementSize(bounds);
  const [stored, setStored] = useState<WindowGeometry | null>(() => loadWindowGeometry(storageKey));
  const hadContent = useRef(false);
  const trigger = useRef<HTMLButtonElement>(null);
  const [triggerElement, setTriggerElement] = useState<HTMLButtonElement | null>(null);
  const tab = useOuterSize(collapsed ? triggerElement : null);
  const dragged = useRef(false);
  const showContent = useCallback((markdown: string) => {
    const nonempty = !!markdown.trim();
    if (nonempty && !hadContent.current) setCollapsed(false);
    if (!nonempty && hadContent.current) setCollapsed(true);
    hadContent.current = nonempty;
  }, []);
  const setTrigger = useCallback((element: HTMLButtonElement | null) => { trigger.current = element; setTriggerElement(element); }, []);
  function collapse() {
    const disclosure = trigger.current, overlay = bounds, document = disclosure?.ownerDocument;
    const restore = document && overlay?.contains(document.activeElement);
    setCollapsed(true);
    // The hidden panel stays mounted, retaining its session-owned read/clear
    // evidence. Return keyboard focus to its disclosure, not the editor.
    if (restore) requestAnimationFrame(() => {
      if (trigger.current === disclosure && disclosure?.isConnected && document
        && !modalDialogOpen()
        && (document.activeElement === document.body || overlay?.contains(document.activeElement)))
        disclosure.focus({ preventScroll: true });
    });
  }
  const update = (value: WindowGeometry | null) => { setStored(value); saveWindowGeometry(storageKey, value); };
  const ready = area.width > 0 && area.height > 0;
  // Leave the first message's heading visible. Only the default moves; a saved drag still uses
  // the original top-right coordinate system (including when proposals reserve space below).
  const preferred = stored ?? { x: 0, y: 36, width: 360, height: Math.min(360, Math.round(area.height * 0.6)) };
  // Convert the right-anchored placement to left/top coordinates inside the pane.
  const geometry = ready ? clampWindowGeometry({ ...preferred, x: area.width - preferred.x - preferred.width }, area, minimum) : null;
  // Collapsed, the window is the size of its tab, so that the tab can reach every edge of the pane.
  // The tab keeps the right edge of the expanded window, which then opens inside the pane again.
  const shown = geometry && collapsed && tab.width > 0 && tab.height > 0
    ? { width: tab.width, height: tab.height,
      x: Math.round(Math.min(Math.max(area.width - preferred.x - tab.width, 0), Math.max(area.width - tab.width, 0))),
      y: Math.round(Math.min(Math.max(preferred.y, 0), Math.max(area.height - tab.height, 0))) }
    : geometry;
  const place = (x: number, y: number, width: number, height: number, right = x + width) => update({ x: Math.max(0, Math.round(area.width - right)), y: Math.round(y), width, height });
  return <aside ref={setBounds} className="session-notes-overlay" data-collapsed={collapsed} data-hidden={hidden} inert={hidden} aria-label={t("Notes")}>
    {geometry && shown && <Rnd className="session-notes-window" bounds="parent" enableResizing={!collapsed}
      dragHandleClassName={collapsed ? "session-notes-toggle" : "window-drag-handle"} cancel=".window-drag-handle button"
      size={{ width: shown.width, height: shown.height }} position={{ x: shown.x, y: shown.y }}
      minWidth={Math.min(collapsed ? shown.width : minimum.width, area.width)} minHeight={Math.min(collapsed ? shown.height : minimum.height, area.height)}
      onDragStart={() => { dragged.current = false; }} onDrag={(_event, data) => { if (Math.abs(data.deltaX) + Math.abs(data.deltaY) > 0) dragged.current = true; }}
      onDragStop={(_event, data) => {
        if (!dragged.current) return;
        place(data.x, data.y, geometry.width, geometry.height, data.x + shown.width);
        // The click of this release, if the pointer is still on the tab, comes before this timer.
        setTimeout(() => { dragged.current = false; });
      }}
      onResizeStop={(_event, _direction, element, _delta, position) => place(position.x, position.y, element.offsetWidth, element.offsetHeight)}>
      <button ref={setTrigger} type="button" className="session-notes-toggle" hidden={!collapsed}
        title={t("Expand notes")} aria-label={t("Expand notes")} aria-expanded={!collapsed} aria-controls={panelId}
        // A drag that moved the chip must not also expand the panel.
        onClick={() => { if (!dragged.current) setCollapsed(false); }}>
        <AppIcon name="notes" size={14} />{t("Notes")}<AppIcon name="chevronDown" size={14} /></button>
      <div className="session-notes-content" hidden={collapsed}>
        <NotesPanel observing={observing} epoch={epoch} sessionId={sessionId} reader={reader} capability={capability}
          fallbackMarkdown={fallbackMarkdown} embedded panelId={panelId} onContent={showContent} preferredHeight={320}
          onResize={() => {}} onReset={() => {}} onCleared={() => { showContent(""); collapse(); }} onClose={collapse}
          headerActions={<Button variant="minimal" size="small" icon={<AppIcon name="reset" size={14} />} disabled={!stored}
            aria-label={t("Restore default size and position")} title={t("Restore default size and position")} onClick={() => update(null)} />} />
      </div>
    </Rnd>}
  </aside>;
}
