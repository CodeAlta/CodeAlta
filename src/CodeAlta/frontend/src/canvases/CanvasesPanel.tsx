import { useEffect, useMemo, useState, useSyncExternalStore } from "react";
import { Button, Card, HTMLSelect, NonIdealState, Tag } from "@blueprintjs/core";
import type { CanvasItem } from "#neoastra";
import { AppIcon } from "../AppIcon";
import type { FileTab } from "../fileTabs";
import { useShellLanguage } from "../shellLanguage";
import { CanvasIcon } from "./CanvasIcon";
import type { CanvasHub } from "./canvasHub";
import { canvasRef, canvasScope, defaultCanvasTarget, openCanvases, type CanvasProject, type CanvasScope, type CanvasSelection, type CanvasTarget } from "./canvasPages";

/** A session the page can open a canvas for. */
export type CanvasSessionChoice = Readonly<{ id: string; title: string; project: CanvasProject | null; projectName: string | null }>;

const mostSessions = 40;
const scopeLabels = { Application: "Application", Project: "Project", Session: "Session" } as const satisfies Record<CanvasScope, "Application" | "Project" | "Session">;

/**
 * The Canvases tab: what the plugins declare, one card for each, with what is open in this space and a way to open it. A canvas about a
 * project or a session is opened for the one the window has selected, or for the one chosen on its card. "New canvas" asks an agent for one.
 * The list is read again each time the tab is shown, and not while it is hidden.
 */
export function CanvasesPanel({ hub, tabs, projects, sessions, selection, visible, onActivate, onOpen, onNew }: {
  hub: Pick<CanvasHub, "getCatalog" | "subscribeCatalog" | "refresh">;
  /** The tabs of the space that is shown: the ones that show a canvas say which are open. */
  tabs: readonly FileTab[];
  /** The projects of the space that is shown, which can be worked in. */
  projects: readonly (CanvasProject & Readonly<{ name: string }>)[];
  /** The sessions of the space that is shown, the ones used last first. */
  sessions: readonly CanvasSessionChoice[];
  selection: CanvasSelection;
  visible: boolean;
  onActivate: () => void;
  onOpen: (item: CanvasItem, target: CanvasTarget) => void;
  /** Asks an agent for a canvas in the project in front; null where no project is. */
  onNew: (() => void) | null;
}) {
  const { t } = useShellLanguage();
  const canvases = useSyncExternalStore(hub.subscribeCatalog, hub.getCatalog);
  // The declarations are read again when the tab comes to the screen; a hidden tab reads nothing.
  useEffect(() => { if (visible) void hub.refresh(); }, [hub, visible]);
  return <div className="work-page canvases-page" onPointerDown={onActivate} data-visible={visible || undefined}>
    <header className="work-header">
      <span className="work-emblem canvases-emblem"><AppIcon name="canvases" size={22} /></span>
      <div><h2>{t("Canvases")}</h2><p>{t("The tabs that plugins provide: boards, checklists, dashboards.")}</p></div>
      <Button icon={<AppIcon name="plus" size={15} />} disabled={!onNew} onClick={() => onNew?.()}
        title={onNew ? undefined : t("Select a project first.")}>{t("New canvas")}</Button>
    </header>
    {canvases.length === 0
      ? <NonIdealState className="canvases-empty" icon={<AppIcon name="canvases" size={36} />} title={t("No plugin provides a canvas yet.")}
          description={t("Ask an agent for one: it writes a plugin that shows a tab.")}
          action={onNew ? <Button icon={<AppIcon name="plus" size={15} />} onClick={() => onNew()}>{t("New canvas")}</Button> : undefined} />
      : <ul className="canvases-list" aria-label={t("Canvases")}>
          {canvases.map(item => <li key={canvasRef(item)}>
            <CanvasCard item={item} tabs={tabs} projects={projects} sessions={sessions} selection={selection} onOpen={onOpen} />
          </li>)}
        </ul>}
  </div>;
}

function CanvasCard({ item, tabs, projects, sessions, selection, onOpen }: {
  item: CanvasItem; tabs: readonly FileTab[]; projects: readonly (CanvasProject & Readonly<{ name: string }>)[]; sessions: readonly CanvasSessionChoice[];
  selection: CanvasSelection; onOpen: (item: CanvasItem, target: CanvasTarget) => void;
}) {
  const { t } = useShellLanguage();
  const scope = canvasScope(item);
  // What the user chose on this card; until then the card follows what the window has selected.
  const [chosen, setChosen] = useState<string | null>(null);
  const followed = defaultCanvasTarget(item, selection);
  const projectId = scope === "Project" ? chosen ?? followed?.project?.id ?? null : null;
  const sessionId = scope === "Session" ? chosen ?? followed?.sessionId ?? null : null;
  const target: CanvasTarget | null = scope === "Application" ? { project: null, sessionId: null }
    : scope === "Project" ? (projects.find(project => project.id === projectId) ? { project: projects.find(project => project.id === projectId)!, sessionId: null } : null)
    : (sessions.find(session => session.id === sessionId) ? { project: sessions.find(session => session.id === sessionId)!.project, sessionId } : null);
  const open = useMemo(() => openCanvases(tabs, item), [tabs, item]);
  const named = open.map(tab => tab.sessionId ? sessions.find(session => session.id === tab.sessionId)?.title : tab.projectId ? projects.find(project => project.id === tab.projectId)?.name : undefined)
    .filter((name): name is string => !!name);
  const needs = scope === "Project" ? t("Select a project first.") : scope === "Session" ? t("Select a session first.") : "";
  const actions = item.actions;
  return <Card className="canvas-card" data-open={open.length > 0 || undefined}>
    <div className="canvas-card-head">
      <span className="canvas-card-icon"><CanvasIcon name={item.icon} pluginKey={item.pluginKey} size={18} /></span>
      <strong className="canvas-card-title">{item.title}</strong>
      <Tag minimal round>{t(scopeLabels[scope])}</Tag>
    </div>
    {item.description && <p className="canvas-card-description">{item.description}</p>}
    <p className="canvas-card-facts">
      <span>{item.plugin}</span>
      {actions > 0 && <span>{t(actions === 1 ? "{count} action" : "{count} actions", { count: actions })}</span>}
      {open.length > 0 && <Tag minimal round intent="success" title={named.join("\n") || undefined}>
        {named.length > 0 ? t("Open: {names}", { names: named.slice(0, 3).join(", ") + (named.length > 3 ? ` +${named.length - 3}` : "") }) : t("Open in this space")}</Tag>}
    </p>
    <div className="canvas-card-actions">
      {scope === "Project" && <HTMLSelect aria-label={t("Project of {title}", { title: item.title })} value={projectId ?? ""} disabled={projects.length === 0}
        onChange={event => setChosen(event.target.value || null)}>
        {!projectId && <option value="">{t("Project")}</option>}
        {projects.map(project => <option key={project.id} value={project.id}>{project.name}</option>)}
      </HTMLSelect>}
      {scope === "Session" && <HTMLSelect aria-label={t("Session of {title}", { title: item.title })} value={sessionId ?? ""} disabled={sessions.length === 0}
        onChange={event => setChosen(event.target.value || null)}>
        {!sessionId && <option value="">{t("Session")}</option>}
        {sessions.slice(0, mostSessions).map(session => <option key={session.id} value={session.id}>{session.projectName ? `${session.title} · ${session.projectName}` : session.title}</option>)}
        {sessionId && !sessions.slice(0, mostSessions).some(session => session.id === sessionId) && sessions.find(session => session.id === sessionId) && <option value={sessionId}>{sessions.find(session => session.id === sessionId)!.title}</option>}
      </HTMLSelect>}
      <Button intent="primary" icon={<AppIcon name="open" size={15} />} disabled={!target} title={target ? undefined : needs}
        aria-label={t("Open {title}", { title: item.title })} onClick={() => { if (target) onOpen(item, target); }}>{t("Open")}</Button>
    </div>
  </Card>;
}
