// Disposable page; the worktree button of the header of the files of a project, over a host the test plays: when it asks
// for the worktrees, what the list shows, and what is asked to be opened. Driven by editorWorktrees.browser.test.ts through
// `window.worktreesFixture`.
import { StrictMode, createElement } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import { ShellLanguageContext } from "../shellLanguage";
import type { Worktree } from "../worktrees/worktrees";
import { EditorWorktrees } from "./EditorWorktrees";

const root = createRoot(document.getElementById("root")!);
const row = (name: string, change: Partial<Worktree> = {}): Worktree => ({ path: `/trees/app/${name}`, name, branch: `alta/${name}`, head: "1234567", main: false, project: false,
  locked: false, missing: false, folder: `/trees/app/${name}`, busy: false, ...change });
const project = row("app", { path: "/code/app", folder: "/code/app", branch: "main", main: true, project: true });
const heron = row("quiet-heron");
const gone = row("old-fox", { missing: true, branch: null });

const state = {
  worktrees: [project, heron, gone] as Worktree[],
  /** How many times the host was asked for the list. */
  lists: 0,
  /** Makes the next reads fail. */
  failing: false,
  /** What the host answers instead of the list, when it is not `ok`. */
  status: "ok",
  /** The folders the user asked to open, in order. */
  opened: [] as string[],
};
const api = {
  list: async () => {
    state.lists++;
    if (state.failing) throw new Error("The host is gone.");
    return { status: state.status, projectId: "p", worktrees: state.worktrees, newFolder: null };
  },
};
const fixture = {
  state, project, heron, gone,
  /** The button, under StrictMode as in the application; `visible` says whether the tab is the one shown. */
  show(visible = true) {
    flushSync(() => root.render(createElement(StrictMode, null, createElement(ShellLanguageContext.Provider, { value: { locale: "en", choice: "en", setLanguage: () => { } } },
      createElement("div", { style: { padding: 24, display: "flex", justifyContent: "flex-end", width: 400 } },
        createElement(EditorWorktrees, { epoch: "epoch", projectId: "p", projectName: "App", visible, onOpen: (worktree: Worktree) => { state.opened.push(worktree.path); }, api: api as never }))))));
  },
  clear() { flushSync(() => root.render(null)); },
};
Object.assign(window, { worktreesFixture: fixture });
