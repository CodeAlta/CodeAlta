// Disposable page; the production buttons of plugins in a stand-in of the title bar and the rail, over a host the test plays.
import { StrictMode, createElement, useState } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import { Button } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { SessionTabMenu } from "../SessionTabMenu";
import { CanvasIcon } from "../canvases/CanvasIcon";
import { PluginButtons } from "./PluginButtons";
import { pluginIconFiles } from "./pluginIcons";
import { usePluginMenuEntries } from "./pluginMenu";
import { PluginButtonSwitches } from "./PluginButtonSwitches";
import { PluginButtonsContext, createHiddenButtons, pluginButtonsChangedEvent, type PluginButtonView, type PluginButtonsHost } from "./pluginButtonModel";

document.documentElement.classList.add("bp6-dark");
const root = createRoot(document.getElementById("root")!);
type Wire = Record<string, unknown>;
const state = {
  reads: [] as { place: string | null; spaceId: string | null; projectId: string | null; sessionId: string | null }[],
  activated: [] as { button: string; projectId: string | null; sessionId: string | null }[],
  active: new Set<string>(),
  // What the host answers: a list per place, as the plugin's state gave it.
  buttons: [] as Wire[],
  fail: false,
};

/** A button as the host lists it; the test overrides what it wants. */
function wire(over: Wire): Wire {
  const id = String(over.buttonId ?? "button");
  return { id: `builtin:fixture/${id}`, pluginKey: "builtin:fixture", pluginId: "fixture", plugin: "Fixture", buttonId: id, place: "TitleBar", icon: "chart-column", iconData: null, label: id,
    commandId: "command-1", canvas: null, canvasScope: null, badge: "none", count: 0, tone: "Info", hidden: false, disabled: false, tooltip: null, ...over };
}

const storage = new Map<string, string>();
const memory = { getItem: (key: string) => storage.get(key) ?? null, setItem: (key: string, value: string) => { storage.set(key, value); } };
let hidden = createHiddenButtons(memory);

function host(): PluginButtonsHost {
  return {
    epoch: "epoch-1", spaceId: "work", hidden,
    api: {
      buttons: async request => {
        state.reads.push({ place: request.place, spaceId: request.spaceId, projectId: request.projectId, sessionId: request.sessionId });
        if (state.fail) throw new Error("host gone");
        return { status: "ok", place: request.place, buttons: state.buttons.filter(button => request.place === null || String(button.place).toLowerCase() === String(request.place).toLowerCase()) } as never;
      },
    },
    activate: (button, context) => { state.activated.push({ button: button.buttonId, projectId: context.projectId, sessionId: context.sessionId }); },
    isActive: button => state.active.has(button.buttonId),
  };
}

/** The title bar and the rail as the shell lays them out, with the controls of the application that the buttons sit beside. */
function Shell({ context, menu }: { context: { projectId: string | null; sessionId: string | null }; menu: boolean }) {
  return <div className="ide-shell" style={{ ["--titlebar-height" as string]: "38px" }}>
    <div className="workspace-shell" style={{ position: "relative", height: 120 }}>
      <div className="window-brand"><nav className="activity-rail" aria-label="Workspace navigation">
        <Button variant="minimal" size="small" className="activity-issues" icon={<AppIcon name="issueOpen" size={16} />} aria-label="Issues" />
        <PluginButtons place="Rail" context={context} />
        <Button variant="minimal" size="small" className="activity-settings" icon={<AppIcon name="settings" size={16} />} aria-label="Settings" />
      </nav></div>
      <div className="window-actions">
        <PluginButtons place="TitleBar" context={context} />
        <Button variant="minimal" size="small" className="space-switch" aria-label="Space: Work" text="Work" />
        <Button variant="minimal" size="small" className="window-zoom" aria-label="Zoom" text="100%" />
        <Button variant="minimal" size="small" className="theme-switch" icon={<AppIcon name="settings" size={16} />} aria-label="Theme" />
      </div>
      {menu && <RowMenu />}
    </div>
  </div>;
}

/** A menu of a project row that plugins add lines to, read for that row. */
function RowMenu() {
  const [open, setOpen] = useState(true);
  const entries = usePluginMenuEntries("ProjectMenu", open ? { projectId: "project-row", sessionId: null } : null);
  const [anchor, setAnchor] = useState<HTMLElement | null>(null);
  return <>
    <button ref={setAnchor} id="row-trigger" type="button" style={{ position: "absolute", left: 20, top: 60 }}>…</button>
    {open && anchor && <SessionTabMenu anchor={anchor} container={document.body} title="Project actions" current={() => true} onClose={() => setOpen(false)}
      items={[{ key: "open", label: "Open", onSelect: () => { } }, ...entries]} />}
  </>;
}

const fixture = {
  state,
  wire,
  /** What the host answers from now on. */
  setButtons(buttons: Wire[]) { state.buttons = buttons; },
  /** A plugin said that its buttons changed. */
  invalidate() { window.dispatchEvent(new Event(pluginButtonsChangedEvent)); },
  resetHidden() { storage.clear(); hidden = createHiddenButtons(memory); },
  storage: () => storage.get("codealta.desktop.pluginButtons.hidden.v1") ?? null,
  /** Under StrictMode, as in the application: React then runs each effect of a new component twice. */
  render(options: { menu?: boolean; project?: string | null; session?: string | null } = {}) {
    const context = { projectId: options.project === undefined ? "project-1" : options.project, sessionId: options.session === undefined ? "session-1" : options.session };
    flushSync(() => root.render(createElement(StrictMode, null, createElement(PluginButtonsContext.Provider, { value: host() }, createElement(Shell, { context, menu: options.menu ?? false })))));
  },
  renderSwitches(buttons: Wire[]) {
    flushSync(() => root.render(createElement(StrictMode, null, createElement(PluginButtonsContext.Provider, { value: host() },
      createElement(PluginButtonSwitches, { buttons: buttons as unknown as PluginButtonView[] })))));
  },
  /** The icons of canvases and buttons: a file the host sent, one the window draws, one of the library, a brand, and names that are nothing. */
  renderIcons() {
    pluginIconFiles.register("p", "icons/a.svg", "data:image/svg+xml;base64," + btoa('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><path d="M2 2h20v20H2z"/></svg>'));
    const names = ["icons/a.svg", "star", "chart-column", "openai", "no-such-icon", "icons/missing.svg", "../..", "Not Valid"];
    flushSync(() => root.render(createElement(StrictMode, null, createElement("div", { id: "icons" }, names.map(name => createElement("span", { key: name, "data-icon": name, style: { display: "inline-block", margin: 4 } },
      createElement(CanvasIcon, { name, pluginKey: "p", size: 20 })))))));
  },
  clear() { flushSync(() => root.render(null)); },
};
Object.assign(window, { buttonsFixture: fixture });
