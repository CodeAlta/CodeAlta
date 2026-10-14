import { useState, type KeyboardEvent as ReactKeyboardEvent } from "react";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { AppWindow } from "../AppWindow";
import { useShellLanguage } from "../shellLanguage";
import { SpaceIcon } from "./SpaceViews";
import type { Space } from "./spaces";

/** The spaces a session can be given: every space but the one it is in. */
export function spaceMoveTargets(spaces: readonly Space[], currentId: string): readonly Space[] {
  return spaces.filter(space => space.id !== currentId);
}

/**
 * Asks which space a session goes to. The session moves with the sessions it started, and its next turn uses what
 * the new space sets up. Choosing a space starts the move at once.
 */
export function MoveToSpaceDialog({ title, spaces, currentId, busy, onMove, onClose }: {
  title: string; spaces: readonly Space[];
  /** The space the session is in now. */
  currentId: string;
  busy: boolean;
  onMove: (space: Space) => void; onClose: () => void;
}) {
  const { t } = useShellLanguage();
  const targets = spaceMoveTargets(spaces, currentId);
  const [active, setActive] = useState(0);
  const index = targets.length === 0 ? -1 : Math.min(active, targets.length - 1);
  function choose(space: Space | undefined) {
    if (space && !busy) onMove(space);
  }
  function keys(event: ReactKeyboardEvent<HTMLElement>) {
    if (event.defaultPrevented || event.nativeEvent.isComposing || event.ctrlKey || event.altKey || event.metaKey) return;
    if ((event.key === "ArrowDown" || event.key === "ArrowUp") && targets.length > 0) {
      event.preventDefault();
      setActive((index + (event.key === "ArrowDown" ? 1 : targets.length - 1)) % targets.length);
    } else if (event.key === "Enter" && !event.repeat) {
      event.preventDefault();
      choose(targets[index]);
    }
  }
  return <AppWindow storageKey="codealta.desktop.window.move-to-space.v1" className="open-project-dialog move-session-dialog move-to-space-dialog" titleId="move-to-space-title"
    title={<><AppIcon name="space" size={14} /> {t("Move {title} to another space", { title })}</>}
    preferredSize={viewport => ({ width: Math.min(460, viewport.width - 40), height: Math.min(380, viewport.height - 40) })} minimumSize={{ width: 340, height: 240 }}
    onClose={onClose} closeLabel={t("Close")} onCancel={event => { event.preventDefault(); onClose(); }}
    onKeyDown={event => {
      event.stopPropagation();
      if (event.key === "Escape" && !event.nativeEvent.isComposing) { event.preventDefault(); onClose(); return; }
      keys(event);
    }}
    onOpened={() => document.getElementById("move-to-space-results")?.focus()}
    headerActions={<span className="reference-status" role="status">{busy && <ActivitySpinner size={12} />}</span>}>
    <p className="move-session-hint bp6-text-muted">{t("The sessions it started move with it. Its next turn uses the providers, tools and identity of the new space.")}</p>
    <div id="move-to-space-results" className="reference-list" role="listbox" tabIndex={0} aria-label={t("Spaces to move the session to")}
      aria-activedescendant={index >= 0 ? `move-to-space-${index}` : undefined}>
      {targets.map((space, i) => <div role="option" id={`move-to-space-${i}`} key={space.id} className="reference-row"
        aria-selected={i === index} aria-disabled={busy} title={space.description ?? space.name}
        onMouseMove={() => { if (i !== index) setActive(i); }} onClick={() => choose(space)}>
        <span className="reference-icon"><SpaceIcon space={space} size={16} /></span>
        <span className="reference-name">{space.name}</span>
        <span />
        <span className="reference-parent">{space.description ?? ""}</span>
      </div>)}
      {targets.length === 0 && <p className="reference-empty" role="status">{t("There is no other space.")}</p>}
    </div>
    <footer className="reference-hint"><span><kbd>↑</kbd><kbd>↓</kbd> {t("move")}</span><span><kbd>Enter</kbd> {t("Move here")}</span><span><kbd>Esc</kbd> {t("close")}</span></footer>
  </AppWindow>;
}
