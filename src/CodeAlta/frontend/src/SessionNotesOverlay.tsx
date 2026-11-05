import { useCallback, useEffect, useId, useLayoutEffect, useRef, useState } from "react";
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
import type { NotesOverlayMemory } from "./sessionOverlayMemory";
import { keepOutProperty } from "./workItems/useWorkCardsPlacement";

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

// The bands along the edges of the pane that Notes keep clear of: the proposals docked on that side are there.
type KeepOut = Readonly<{ top: number; right: number; bottom: number; left: number }>;
const noKeepOut: KeepOut = Object.freeze({ top: 0, right: 0, bottom: 0, left: 0 });

function useKeepOut(pane: HTMLElement | null | undefined): KeepOut {
  const [value, setValue] = useState<KeepOut>(noKeepOut);
  useLayoutEffect(() => {
    if (!pane) return;
    const read = () => {
      const band = (side: "above" | "right" | "below" | "left") => parseFloat(pane.style.getPropertyValue(keepOutProperty(side))) || 0;
      const next = { top: band("above"), right: band("right"), bottom: band("below"), left: band("left") };
      setValue(previous => previous.top === next.top && previous.right === next.right && previous.bottom === next.bottom && previous.left === next.left
        ? previous : next);
    };
    const observer = new MutationObserver(read);
    observer.observe(pane, { attributes: true, attributeFilter: ["style"] });
    read();
    return () => observer.disconnect();
  }, [pane]);
  return value;
}

// A window moved out of the bands, and made smaller only when the rest of the pane cannot hold it.
function keepClear(value: WindowGeometry, area: WindowSize, keep: KeepOut): WindowGeometry {
  if (keep === noKeepOut) return value;
  const width = Math.min(value.width, Math.max(area.width - keep.left - keep.right, 1));
  const height = Math.min(value.height, Math.max(area.height - keep.top - keep.bottom, 1));
  return { width, height,
    x: Math.round(Math.max(keep.left, Math.min(value.x, area.width - keep.right - width))),
    y: Math.round(Math.max(keep.top, Math.min(value.y, area.height - keep.bottom - height))) };
}

export function SessionNotesOverlay({ sessionId, epoch, capability, fallbackMarkdown, hidden = false, reader, observing = true, memory }: {
  reader: ReturnType<typeof createNotesReader>;
  sessionId: string; epoch?: string; capability?: ReturnType<typeof createMutationCapability>;
  fallbackMarkdown: string;
  /** Hides the whole overlay, its tab included; the notes stay loaded for when it is shown again. */
  hidden?: boolean;
  observing?: boolean;
  /** What the overlay of this session remembers when the pane is mounted again (a switch of space). */
  memory?: NotesOverlayMemory;
}) {
  const { t } = useShellLanguage();
  const panelId = useId();
  const [remembered] = useState<NotesOverlayMemory>(() => memory ?? { collapsed: true, hadContent: false });
  const [collapsed, setCollapsed] = useState(remembered.collapsed);
  useEffect(() => { remembered.collapsed = collapsed; }, [remembered, collapsed]);
  const [bounds, setBounds] = useState<HTMLElement | null>(null);
  const area = useElementSize(bounds);
  const keep = useKeepOut(bounds?.parentElement);
  const [stored, setStored] = useState<WindowGeometry | null>(() => loadWindowGeometry(storageKey));
  const trigger = useRef<HTMLButtonElement>(null);
  const [triggerElement, setTriggerElement] = useState<HTMLButtonElement | null>(null);
  const tab = useOuterSize(collapsed ? triggerElement : null);
  const dragged = useRef(false);
  const showContent = useCallback((markdown: string) => {
    const nonempty = !!markdown.trim();
    // Only a change between none and some notes opens or closes them: the same notes read again after
    // the pane was mounted again leave them as the user had them.
    if (nonempty && !remembered.hadContent) setCollapsed(false);
    if (!nonempty && remembered.hadContent) setCollapsed(true);
    remembered.hadContent = nonempty;
  }, [remembered]);
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
  const free = ready ? clampWindowGeometry({ ...preferred, x: area.width - preferred.x - preferred.width }, area, minimum) : null;
  // Docked proposals take a band of the pane: the window shown keeps clear of it, the saved one does not change.
  const geometry = free && keepClear(free, area, keep);
  // Collapsed, the window is the size of its tab, so that the tab can reach every edge of the pane.
  // The tab keeps the right edge of the expanded window, which then opens inside the pane again.
  const shown = geometry && collapsed && tab.width > 0 && tab.height > 0
    ? { width: tab.width, height: tab.height,
      x: Math.round(Math.min(Math.max(area.width - preferred.x - tab.width, keep.left), Math.max(area.width - keep.right - tab.width, keep.left))),
      y: Math.round(Math.min(Math.max(preferred.y, keep.top), Math.max(area.height - keep.bottom - tab.height, keep.top))) }
    : geometry;
  const place = (x: number, y: number, width: number, height: number, right = x + width) => update({ x: Math.max(0, Math.round(area.width - right)), y: Math.round(y), width, height });
  return <aside ref={setBounds} className="session-notes-overlay" data-collapsed={collapsed} data-hidden={hidden} inert={hidden} aria-label={t("Notes")}>
    {free && geometry && shown && <Rnd className="session-notes-window" bounds="parent" enableResizing={!collapsed}
      dragHandleClassName={collapsed ? "session-notes-toggle" : "window-drag-handle"} cancel=".window-drag-handle button"
      size={{ width: shown.width, height: shown.height }} position={{ x: shown.x, y: shown.y }}
      minWidth={Math.min(collapsed ? shown.width : minimum.width, area.width)} minHeight={Math.min(collapsed ? shown.height : minimum.height, area.height)}
      onDragStart={() => { dragged.current = false; }} onDrag={(_event, data) => { if (Math.abs(data.deltaX) + Math.abs(data.deltaY) > 0) dragged.current = true; }}
      onDragStop={(_event, data) => {
        if (!dragged.current) return;
        place(data.x, data.y, free.width, free.height, data.x + shown.width);
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
