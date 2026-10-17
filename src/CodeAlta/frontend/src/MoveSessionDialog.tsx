import { useState, type KeyboardEvent as ReactKeyboardEvent } from "react";
import type { WorkspaceProject } from "#neoastra";
import { ActivitySpinner } from "./ActivitySpinner";
import { AppIcon } from "./AppIcon";
import { AppWindow } from "./AppWindow";
import { InputGroup } from "@blueprintjs/core";
import { useShellLanguage } from "./shellLanguage";

/** The projects a session can be moved to: those of the list that are not archived, other than its own (none: every one). */
export function moveTargets(projects: readonly WorkspaceProject[], currentProjectId: string | null, query: string): readonly WorkspaceProject[] {
  const text = query.trim().toLowerCase();
  return projects.filter(project => !project.archived && project.id !== currentProjectId
    && (!text || project.name.toLowerCase().includes(text) || project.path.toLowerCase().includes(text)));
}

/**
 * Asks which project a session goes to. The session moves with the sessions it started in its project; choosing a
 * project starts the move at once. With `continuing`, it asks the project a Claude Code conversation started elsewhere
 * is continued in, among every project (`projectId` null): choosing one imports it there.
 */
export function MoveSessionDialog({ title, projectId, projects, busy, onMove, onClose, continuing = false }: {
  title: string; projectId: string | null; projects: readonly WorkspaceProject[]; busy: boolean;
  onMove: (project: WorkspaceProject) => void; onClose: () => void; continuing?: boolean;
}) {
  const { t } = useShellLanguage();
  const [query, setQuery] = useState("");
  const [active, setActive] = useState(0);
  const matches = moveTargets(projects, projectId, query);
  const index = matches.length === 0 ? -1 : Math.min(active, matches.length - 1);
  function choose(project: WorkspaceProject | undefined) {
    if (project && !busy) onMove(project);
  }
  function keys(event: ReactKeyboardEvent<HTMLElement>) {
    if (event.defaultPrevented || event.nativeEvent.isComposing || event.ctrlKey || event.altKey || event.metaKey) return;
    if ((event.key === "ArrowDown" || event.key === "ArrowUp") && matches.length > 0) {
      event.preventDefault();
      setActive((index + (event.key === "ArrowDown" ? 1 : matches.length - 1)) % matches.length);
    } else if (event.key === "Enter" && !event.repeat) {
      event.preventDefault();
      choose(matches[index]);
    }
  }
  return <AppWindow storageKey="codealta.desktop.window.move-session.v1" className="open-project-dialog move-session-dialog" titleId="move-session-title"
    title={<><AppIcon name="folderInput" size={14} /> {continuing ? t("Continue {title} in a project", { title }) : t("Move {title} to another project", { title })}</>}
    preferredSize={viewport => ({ width: Math.min(640, viewport.width - 40), height: Math.min(520, viewport.height - 40) })} minimumSize={{ width: 400, height: 300 }}
    onClose={onClose} closeLabel={t("Close")} onCancel={event => { event.preventDefault(); onClose(); }}
    onKeyDown={event => {
      event.stopPropagation();
      if (event.key === "Escape" && !event.nativeEvent.isComposing) { event.preventDefault(); onClose(); }
    }}
    onOpened={() => document.getElementById("move-session-filter")?.focus()}
    headerActions={<span className="reference-status" role="status">{busy && <ActivitySpinner size={12} />}
      {matches.length === 1 ? t("1 project") : t("{count} projects", { count: matches.length })}</span>}>
    <p className="move-session-hint bp6-text-muted">{continuing
      ? t("The conversation is imported into the project you choose and continues there. Its session works in the worktree it ended in when that worktree belongs to the project.")
      : t("The sessions it started in this project move with it. Each keeps working in its git worktree when the other project is a checkout of the same repository.")}</p>
    <InputGroup id="move-session-filter" className="reference-search" type="search" role="combobox" aria-autocomplete="list" spellCheck={false}
      leftIcon={<AppIcon name="search" size={15} className="bp6-icon" />} aria-label={t("Project name or folder path")}
      aria-expanded={matches.length > 0} aria-controls="move-session-results" disabled={busy}
      aria-activedescendant={index >= 0 ? `move-session-${index}` : undefined} value={query}
      onChange={event => { setQuery(event.target.value); setActive(0); }} onKeyDown={keys} placeholder={t("Project name or folder path")} />
    <div id="move-session-results" className="reference-list" role="listbox" aria-label={continuing ? t("Projects to continue the conversation in") : t("Projects to move the session to")}>
      {matches.map((project, i) => <div role="option" id={`move-session-${i}`} key={project.id} className="reference-row"
        aria-selected={i === index} aria-disabled={busy} title={project.path}
        onMouseMove={() => { if (i !== index) setActive(i); }} onClick={() => choose(project)}>
        <span className="reference-icon" data-file-tone="gold"><AppIcon name="folder" size={16} /></span>
        <span className="reference-name">{project.name}</span>
        <span />
        <span className="reference-parent">{project.path}</span>
      </div>)}
      {matches.length === 0 && <p className="reference-empty" role="status">{t(query.trim()
        ? "No other project matches this name or path." : "No other project in this space.")}</p>}
    </div>
    <footer className="reference-hint"><span><kbd>↑</kbd><kbd>↓</kbd> {t("move")}</span><span><kbd>Enter</kbd> {t("Move here")}</span><span><kbd>Esc</kbd> {t("close")}</span></footer>
  </AppWindow>;
}
