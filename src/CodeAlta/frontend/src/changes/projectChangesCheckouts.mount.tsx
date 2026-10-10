// Disposable page; the Changes tab of a project over a host the test plays: which checkout it asks for, and which row it shows as chosen.
import { StrictMode, createElement } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import type { FileTab } from "../fileTabs";
import type { Worktree } from "../worktrees/worktrees";
import { ProjectChangesPanel } from "./ProjectChangesPanel";

const root = createRoot(document.getElementById("root")!);
const row = (path: string, change: Partial<Worktree> = {}): Worktree => ({ path, name: path.split("\\").pop()!, branch: `alta/${path.split("\\").pop()}`, head: "1234567",
  main: false, project: false, locked: false, missing: false, folder: path, busy: false, ...change });
// A project that was registered in a linked worktree: the main checkout of its repository is another folder, and so is a second worktree.
const repository = row("C:\\code\\repository", { branch: "main", main: true });
const home = row("C:\\trees\\app\\home", { main: true, project: true });
const other = row("C:\\trees\\app\\other");
// A project in the main checkout of its repository, as most are.
const plain = row("C:\\code\\plain", { branch: "main", main: true, project: true });

const state = {
  rows: [repository, home, other] as Worktree[],
  /** The folder of the project: what the host answers for when no checkout is named. */
  projectFolder: home.folder,
  /** The checkout each list of changes was asked for; null is the folder of the project. */
  asked: [] as (string | null)[],
  lists: 0,
};
type ChangesRequest = { worktree?: string | null };
const api = {
  changes: async (request: ChangesRequest) => {
    const folder = request.worktree ?? null;
    state.asked.push(folder);
    const shown = folder ?? state.projectFolder;
    // The host answers for a folder of the repository, whichever checkout it is.
    if (!state.rows.some(value => value.folder === shown)) return { status: "worktree_missing" };
    return { status: "ok", projectId: "p", revision: `r:${shown}`, root: shown, prefix: "", branch: state.rows.find(value => value.folder === shown)!.branch, detached: false,
      comparison: "head", baseReference: null, baseAhead: null, files: [], insertions: 0, deletions: 0, truncated: false };
  },
  commits: async () => ({ status: "ok", projectId: "p", revision: "c", commits: [], more: false }),
  file: async () => ({ status: "not_changed" }),
};
const trees = {
  list: async () => { state.lists++; return { status: "ok", projectId: "p", worktrees: state.rows, newFolder: null }; },
  remove: async () => ({ status: "failed" }), branches: async () => ({ status: "failed", branches: [], busy: false }), switch: async () => ({ status: "failed" }),
};

const fixture = {
  state, repository, home, other, plain,
  /** The tab, under StrictMode as in the application; `request` is what `alta diff show` or the window of the worktrees asks to show. */
  show(request: Readonly<{ path: string | null; worktree?: string | null }> | null = null) {
    const tab: FileTab = { projectId: "p", projectPath: state.projectFolder, view: "changes" };
    flushSync(() => root.render(createElement(StrictMode, null, createElement("div", { style: { height: 600, display: "flex" } }, createElement(ProjectChangesPanel, {
      tab, projectName: "App", epoch: "epoch", visible: true, active: true, onActivate: () => { }, onOpenFile: () => { }, request, sessions: [],
      api: api as never, trees: trees as never,
    })))));
  },
  clear() { flushSync(() => root.render(null)); },
};
Object.assign(window, { checkoutsFixture: fixture });
