// Disposable page; the production lists of sessions of the Explorer for two open scopes that are not the selected one (the chats
// and another project), beside a title bar whose buttons are asked about the selected project and session. Driven by
// explorerSessionMenu.browser.test.ts through `window.explorerFixture`: what the host is asked and what a line of a menu does is recorded.
import { StrictMode, createElement } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import type { CanvasItem, WorkspaceSession } from "#neoastra";
import { sessionCanvasMenuEntries } from "../canvases/canvasPages";
import { PluginButtons } from "../pluginButtons/PluginButtons";
import { PluginButtonsContext, createHiddenButtons, type PluginButtonsHost } from "../pluginButtons/pluginButtonModel";
import { sessionHierarchy } from "../sessionHierarchy";
import { ExplorerSessions } from "./ExplorerSessions";
import { sessionList } from "./sessionTree";

document.documentElement.classList.add("bp6-dark");
const root = createRoot(document.getElementById("root")!);
type Wire = Record<string, unknown>;
const state = {
  reads: [] as { place: string | null; spaceId: string | null; projectId: string | null; sessionId: string | null }[],
  activated: [] as { button: string; commandId: string | null; projectId: string | null; sessionId: string | null }[],
  /** What the rows themselves were asked to do: opening a session is what selects its scope. */
  actions: [] as { session: string; action: string }[],
  buttons: [] as Wire[],
  /** The canvases the plugins declare, and what the lines of a menu opened: the canvas, and what it is about. */
  catalog: [] as CanvasItem[],
  canvases: [] as { canvas: string; project: { id: string; path: string } | null; sessionId: string | null }[],
  pages: 0,
};

/** A canvas as the host declares it. */
const canvas = (id: string, scope: string): CanvasItem => ({ pluginKey: "global:tools", plugin: "Tools", package: "plugin:global:tools", id, title: id[0].toUpperCase() + id.slice(1),
  description: null, icon: null, iconData: null, scope, input: false, actions: 0, describes: false });
// The projects of the workspace: project-a is the selected one.
const projects = [{ id: "project-a", path: "/code/a", archived: false }, { id: "project-b", path: "/code/b", archived: false }];

/** A button as the host lists it; the test overrides what it wants. */
function wire(over: Wire): Wire {
  const id = String(over.buttonId ?? "button");
  return { id: `builtin:fixture/${id}`, pluginKey: "builtin:fixture", pluginId: "fixture", plugin: "Fixture", buttonId: id, place: "SessionMenu", icon: "chart-column", iconData: null, label: id,
    commandId: "command-1", canvas: null, canvasScope: null, badge: "none", count: 0, tone: "Info", hidden: false, disabled: false, tooltip: null, ...over };
}

function host(): PluginButtonsHost {
  return {
    epoch: "epoch-1", spaceId: "work", hidden: createHiddenButtons(null),
    api: {
      buttons: async request => {
        state.reads.push({ place: request.place, spaceId: request.spaceId, projectId: request.projectId, sessionId: request.sessionId ?? null });
        return { status: "ok", place: request.place, buttons: state.buttons.filter(button => request.place === null || String(button.place).toLowerCase() === String(request.place).toLowerCase()) } as never;
      },
    },
    activate: (button, context) => { state.activated.push({ button: button.buttonId, commandId: button.commandId, projectId: context.projectId, sessionId: context.sessionId }); },
    isActive: () => false,
  };
}

const session = (id: string, projectId: string | null, parentSessionId: string | null = null): WorkspaceSession => ({
  messageCount: null, automationId: null, worktreePath: null, worktreeRoot: null, worktreeName: null, worktreeMissing: false, createdAt: null, id, title: `title of ${id}`, fullTitle: `title of ${id}`, fullTitleTruncated: false, parentSessionId,
  scopeKind: projectId === null ? "global" : "project", projectId, lineageIssue: null, workspacePath: projectId === null ? "/home" : `/${projectId}`,
  providerKey: "codex", updatedAt: "2026-01-01T00:00:00Z",
});
// The selected scope is project-a with its session a-1; the two lists below are of scopes that are open and not selected.
const sessions = [session("a-1", "project-a"), session("chat-1", null), session("chat-2", null), session("b-1", "project-b"), session("b-2", "project-b", "b-1")];
const tree = { toggle: () => { }, more: () => { }, fewer: () => { } };

function Scope({ scope }: { scope: string | null }) {
  const rows = sessionHierarchy(sessions.filter(value => value.projectId === scope), sessions, scope);
  const entries = sessionList(rows, { count: 50, subCount: 4, active: null, extra: () => 0, collapsed: () => false }).entries;
  return <section data-scope={scope ?? "chats"}><h2>{scope ?? "Chats"}</h2>
    <ExplorerSessions entries={entries} global={scope === null} projectId={scope} more={0} extended={false} tree={tree}
      menuEntries={row => sessionCanvasMenuEntries(state.catalog, row, projects.find(value => value.id === scope), {
        label: title => `Open ${title}`, more: "More…", openPage: () => { state.pages++; },
        open: (item, target) => { state.canvases.push({ canvas: item.id, project: target.project, sessionId: target.sessionId }); } })}
      access={() => ({ rename: true, delete: true })} marks={() => null} onAction={(value, action) => { state.actions.push({ session: value.id, action }); }}
      onMore={() => { }} onFewer={() => { }} /></section>;
}

const fixture = {
  state,
  wire,
  canvas,
  /** What the host answers from now on. */
  setButtons(buttons: Wire[]) { state.buttons = buttons; },
  /** The canvases the plugins declare from now on. */
  setCanvases(items: CanvasItem[]) { state.catalog = items; },
  /** Under StrictMode, as in the application: React then runs each effect of a new component twice. */
  render() {
    flushSync(() => root.render(createElement(StrictMode, null, createElement(PluginButtonsContext.Provider, { value: host() },
      createElement("div", { className: "ide-shell", style: { padding: 12, width: 320 } },
        createElement("div", { className: "window-actions" }, createElement(PluginButtons, { place: "TitleBar", context: { projectId: "project-a", sessionId: "a-1" } })),
        createElement(Scope, { scope: null }), createElement(Scope, { scope: "project-b" }))))));
  },
  clear() { flushSync(() => root.render(null)); },
};
Object.assign(window, { explorerFixture: fixture });
