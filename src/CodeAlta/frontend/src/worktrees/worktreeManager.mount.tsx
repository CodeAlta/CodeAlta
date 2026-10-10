// Disposable page; the window of the worktrees over a host whose answers the test plays.
import { StrictMode, createElement } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import type { WorkspaceProject, WorkspaceSnapshot } from "#neoastra";
import { ProjectRailRows } from "../explorer/ProjectRailRows";
import { WorktreeManager, type WorktreeManagerApi } from "./WorktreeManager";
import type { InventoryRow } from "./worktreeInventory";

const root = createRoot(document.getElementById("root")!);
const row = (name: string, change: Partial<InventoryRow> = {}): InventoryRow => ({ path: `C:\\trees\\alpha\\${name}`, name, branch: `alta/${name}`, head: "1234567",
  main: false, project: false, locked: false, missing: false, folder: `C:\\trees\\alpha\\${name}`, busy: false, protection: null, sessions: [], sessionCount: 0, lastUsedAt: null, ...change });
const name = (path: string) => path.split("\\").pop()!;

type Removal = { paths: string[]; discard: string[]; deleteMergedBranches: boolean };
const state = {
  /** What the host lists now. */
  rows: [] as InventoryRow[],
  /** While true an inventory is not answered: the test answers each question itself, in the order it chooses. */
  hold: false,
  questions: [] as ((rows: readonly InventoryRow[]) => void)[],
  asked: 0,
  removals: [] as Removal[],
  /** What the host says of a worktree by its name; a worktree that is not named goes. */
  outcomes: {} as Record<string, { status: string; message?: string; branchKept?: string; branchDeleted?: string }>,
  /** The host does not answer a removal. */
  lost: false,
  editors: [] as string[], editor: "ok",
  closed: 0, changed: 0, shown: [] as string[],
  /** The projects whose worktrees the menu of a row asked for. */
  managed: [] as string[],
};
const reply = (rows: readonly InventoryRow[]) => ({ status: "ok", projectId: "p", worktrees: rows, sessionsKnown: true });
const api = {
  inventory: () => {
    state.asked++;
    return state.hold ? new Promise(resolve => { state.questions.push(rows => resolve(reply(rows))); }) : Promise.resolve(reply(state.rows));
  },
  removeMany: async (request: Removal) => {
    state.removals.push({ paths: [...request.paths], discard: [...request.discard], deleteMergedBranches: request.deleteMergedBranches });
    if (state.lost) throw new Error("The host did not answer.");
    const results = request.paths.map(path => {
      const said = state.outcomes[name(path)] ?? { status: "ok" };
      // A worktree that holds changes goes once the request names it for that.
      const status = said.status === "dirty" && request.discard.includes(path) ? "ok" : said.status;
      return { path, status, message: said.message ?? null, branchKept: said.branchKept ?? null, branchDeleted: said.branchDeleted ?? null };
    });
    state.rows = state.rows.filter(value => !results.some(result => result.path === value.path && result.status === "ok"));
    return { status: "ok", results };
  },
  openEditor: async (request: { path: string }) => { state.editors.push(request.path); return { status: state.editor }; },
} as unknown as WorktreeManagerApi;

const fixture = {
  state, row,
  /** A repository with its folder, worktrees that can go, one in use, one locked and one whose folder is gone. */
  repository: (): InventoryRow[] => [
    row("alpha", { path: "C:\\code\\alpha", folder: "C:\\code\\alpha", branch: "main", main: true, project: true, protection: "main", sessionCount: 12, lastUsedAt: "2026-10-09T09:00:00+00:00",
      sessions: [{ id: "s0", title: "Review the release notes", updatedAt: "2026-10-09T09:00:00+00:00", running: false }] }),
    row("quiet-heron", { sessionCount: 3, lastUsedAt: "2026-10-03T08:00:00+00:00", sessions: [{ id: "s1", title: "Fix the parser", updatedAt: "2026-10-03T08:00:00+00:00", running: false }] }),
    row("amber-denali"),
    row("busy-finch", { busy: true, protection: "in_use", sessionCount: 1, lastUsedAt: "2026-10-10T06:00:00+00:00",
      sessions: [{ id: "s2", title: "Port the tests", updatedAt: "2026-10-10T06:00:00+00:00", running: true }] }),
    row("kept-ibis", { locked: true, protection: "locked" }),
    row("calm-egret"), row("pale-wren"),
    row("lost-otter", { missing: true, sessionCount: 1, lastUsedAt: "2026-09-12T08:00:00+00:00", sessions: [{ id: "s3", title: "Try the new layout", updatedAt: "2026-09-12T08:00:00+00:00", running: false }] }),
  ],
  /** The window, under StrictMode as in the application. */
  open() {
    flushSync(() => root.render(createElement(StrictMode, null, createElement(WorktreeManager, {
      epoch: "epoch", project: { id: "p", name: "Alpha", path: "C:\\code\\alpha" }, api,
      onClose: () => { state.closed++; }, onChanged: () => { state.changed++; }, onShowChanges: (value: InventoryRow) => { state.shown.push(value.folder); },
    }))));
  },
  /** The rows of two projects, whose menus offer the worktrees the way the Explorer does: not for an archived project. */
  projectMenu() {
    const projects: WorkspaceProject[] = [{ id: "p1", path: "/code/one", name: "One", archived: false }, { id: "p2", path: "/code/two", name: "Two", archived: true }];
    const snapshot: WorkspaceSnapshot = { configured: true, projects, sessions: [], projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false };
    const nothing = () => { };
    flushSync(() => root.render(createElement("div", { className: "ide-shell", style: { width: 272 } }, createElement("aside", { className: "project-rail" }, createElement(ProjectRailRows, {
      projects, selectedId: "p1", onSelect: nothing, canRename: true, renameBusy: false, onRename: nothing,
      actions: { current: () => ({ snapshot, projectId: "p1", sessionId: null, hostEpoch: "epoch", hostAvailable: true, refreshVersion: 1, refreshReady: true, active: true,
        generation: 1, modalGeneration: 1, canMutate: true, locked: false }), open: nothing, rename: nothing, archive: nothing,
        worktrees: (project: WorkspaceProject) => { state.managed.push(project.id); } },
    })))));
  },
  clear() { flushSync(() => root.render(null)); },
};
Object.assign(window, { worktreesFixture: fixture });
