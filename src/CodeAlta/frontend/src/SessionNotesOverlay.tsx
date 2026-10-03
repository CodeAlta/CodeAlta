import { useCallback, useEffect, useId, useRef, useState } from "react";
import { Button } from "@blueprintjs/core";
import { Rnd } from "react-rnd";
import { NotesPanel } from "./NotesPanel";
import { AppIcon } from "./AppIcon";
import { useElementSize } from "./AppWindow";
import type { createNotesReader } from "./sessionNotes";
import type { createMutationCapability } from "./sessionOperations";
import { useShellLanguage } from "./shellLanguage";
import { clampWindowGeometry, loadWindowGeometry, saveWindowGeometry, type WindowGeometry } from "./windowGeometry";

// One placement for every session pane, stored relative to the pane's top-right corner so it
// survives pane resizes: `x` is the distance from the right edge, `y` from the top.
const storageKey = "codealta.desktop.notes-window.v1";
const minimum = { width: 240, height: 140 };

export function SessionNotesOverlay({ sessionId, epoch, capability, fallbackMarkdown, toggle, reader, observing = true }: {
  reader: ReturnType<typeof createNotesReader>;
  sessionId: string; epoch?: string; capability?: ReturnType<typeof createMutationCapability>;
  fallbackMarkdown: string; toggle?: boolean;
  observing?: boolean;
}) {
  const { t } = useShellLanguage();
  const panelId = useId();
  const [collapsed, setCollapsed] = useState(true);
  const [bounds, setBounds] = useState<HTMLElement | null>(null);
  const area = useElementSize(bounds);
  const [stored, setStored] = useState<WindowGeometry | null>(() => loadWindowGeometry(storageKey));
  const hadContent = useRef(false);
  const observedToggle = useRef(toggle);
  const trigger = useRef<HTMLButtonElement>(null);
  const dragged = useRef(false);
  const showContent = useCallback((markdown: string) => {
    const nonempty = !!markdown.trim();
    if (nonempty && !hadContent.current) setCollapsed(false);
    if (!nonempty && hadContent.current) setCollapsed(true);
    hadContent.current = nonempty;
  }, []);
  useEffect(() => {
    if (toggle !== undefined && observedToggle.current !== undefined && toggle !== observedToggle.current)
      setCollapsed(value => !value);
    observedToggle.current = toggle;
  }, [toggle]);
  function collapse() {
    const disclosure = trigger.current, overlay = bounds, document = disclosure?.ownerDocument;
    const restore = document && overlay?.contains(document.activeElement);
    setCollapsed(true);
    // The hidden panel stays mounted, retaining its session-owned read/clear
    // evidence. Return keyboard focus to its disclosure, not the editor.
    if (restore) requestAnimationFrame(() => {
      if (trigger.current === disclosure && disclosure?.isConnected && document
        && !document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')
        && (document.activeElement === document.body || overlay?.contains(document.activeElement)))
        disclosure.focus({ preventScroll: true });
    });
  }
  const update = (value: WindowGeometry | null) => { setStored(value); saveWindowGeometry(storageKey, value); };
  const ready = area.width > 0 && area.height > 0;
  const preferred = stored ?? { x: 0, y: 0, width: 360, height: Math.min(360, Math.round(area.height * 0.6)) };
  // Convert the right-anchored placement to left/top coordinates inside the pane.
  const geometry = ready ? clampWindowGeometry({ ...preferred, x: area.width - preferred.x - preferred.width }, area, minimum) : null;
  const place = (x: number, y: number, width: number, height: number) => update({ x: Math.max(0, Math.round(area.width - x - width)), y: Math.round(y), width, height });
  return <aside ref={setBounds} className="session-notes-overlay" data-collapsed={collapsed} aria-label={t("Alta notes")}>
    {geometry && <Rnd className="session-notes-window" bounds="parent" enableResizing={!collapsed}
      dragHandleClassName={collapsed ? "session-notes-toggle" : "window-drag-handle"} cancel=".window-drag-handle button"
      size={{ width: geometry.width, height: geometry.height }} position={{ x: geometry.x, y: geometry.y }}
      minWidth={Math.min(minimum.width, area.width)} minHeight={Math.min(minimum.height, area.height)}
      onDragStart={() => { dragged.current = false; }} onDrag={(_event, data) => { if (Math.abs(data.deltaX) + Math.abs(data.deltaY) > 0) dragged.current = true; }}
      onDragStop={(_event, data) => { if (dragged.current) place(data.x, data.y, geometry.width, geometry.height); }}
      onResizeStop={(_event, _direction, element, _delta, position) => place(position.x, position.y, element.offsetWidth, element.offsetHeight)}>
      <button ref={trigger} type="button" className="session-notes-toggle" hidden={!collapsed}
        title={t("Expand Alta notes")} aria-label={t("Expand Alta notes")} aria-expanded={!collapsed} aria-controls={panelId}
        // A drag that moved the chip must not also expand the panel.
        onClick={() => { if (dragged.current) dragged.current = false; else setCollapsed(false); }}>
        <AppIcon name="notes" size={14} />{t("Alta notes")}<AppIcon name="chevronDown" size={14} /></button>
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
