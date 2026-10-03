import { useCallback, useEffect, useId, useRef, useState } from "react";
import { NotesPanel } from "./NotesPanel";
import { AppIcon } from "./AppIcon";
import type { createNotesReader } from "./sessionNotes";
import type { createMutationCapability } from "./sessionOperations";
import { useShellLanguage } from "./shellLanguage";

export function SessionNotesOverlay({ sessionId, epoch, capability, fallbackMarkdown, toggle, reader, observing = true }: {
  reader: ReturnType<typeof createNotesReader>;
  sessionId: string; epoch?: string; capability?: ReturnType<typeof createMutationCapability>;
  fallbackMarkdown: string; toggle?: boolean;
  observing?: boolean;
}) {
  const { t } = useShellLanguage();
  const panelId = useId();
  const [collapsed, setCollapsed] = useState(true);
  const hadContent = useRef(false);
  const observedToggle = useRef(toggle);
  const trigger = useRef<HTMLButtonElement>(null);
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
    const disclosure = trigger.current, overlay = disclosure?.parentElement, document = disclosure?.ownerDocument;
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
  return <aside className="session-notes-overlay" data-collapsed={collapsed} aria-label={t("Alta notes")}>
    <button ref={trigger} type="button" className="session-notes-toggle" hidden={!collapsed}
      title={t("Expand Alta notes")} aria-label={t("Expand Alta notes")} aria-expanded={!collapsed} aria-controls={panelId}
      onClick={() => setCollapsed(false)}><AppIcon name="notes" size={14} />{t("Alta notes")}<AppIcon name="chevronDown" size={14} /></button>
    <div className="session-notes-content" hidden={collapsed}>
      <NotesPanel observing={observing} epoch={epoch} sessionId={sessionId} reader={reader} capability={capability}
        fallbackMarkdown={fallbackMarkdown} embedded panelId={panelId} onContent={showContent} preferredHeight={320}
        onResize={() => {}} onReset={() => {}} onCleared={() => { showContent(""); collapse(); }} onClose={collapse} />
    </div>
  </aside>;
}
