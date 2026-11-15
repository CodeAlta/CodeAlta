// Disposable page: the disk usage over a host whose answers the test plays.
import { StrictMode, useState } from "react";
import { createRoot } from "react-dom/client";
import type { DiskUsageCleanEvent, DiskUsageCleanRequest, DiskUsageCleanResponse, DiskUsageLastResponse, DiskUsageMeasurement, DiskUsageScanEvent } from "#neoastra";
import type { InventoryRow } from "../worktrees/worktreeInventory";
import { DiskUsagePanel, type DiskUsageWorktreesApi } from "./DiskUsagePanel";
import type { DiskUsageApi } from "./diskUsage";
import { createDiskUsageHub } from "./diskUsageHub";
import { checkout, measurement, size, spaces } from "./diskUsageFixtures";
import "../charts/charts.css";

/**
 * Alpha with one more worktree that can go and be cleaned, one that has no build output and holds another worktree in
 * its folder, and one the application runs from.
 */
function played(): DiskUsageMeasurement {
  const value = measurement();
  const [alpha, ...rest] = value.projects;
  const added = 3 * size.gib + 10 * size.mib + 900 * size.mib;
  return { ...value, totalBytes: String(Number(value.totalBytes) + added), projects: [{ ...alpha, checkouts: [...alpha.checkouts,
    checkout("/trees/alpha/amber-denali", 3 * size.gib, { branch: "alta/amber-denali" }, 2 * size.gib),
    checkout("/trees/alpha/pale-wren", 10 * size.mib, { branch: "alta/pale-wren", protection: "holds_worktree", verdict: "has_local_work",
      heldCheckouts: ["/trees/alpha/pale-wren/inner"], worseHeldCheckouts: ["/trees/alpha/pale-wren/inner"] }),
    checkout("/trees/alpha/self-host", 900 * size.mib, { branch: "alta/self-host", runningApplication: true }, 800 * size.mib)] }, ...rest] };
}

type Stream<T> = { push: (event: T) => void; end: () => void; aborted: boolean };
function stream<T>(signal?: AbortSignal): { channel: AsyncIterable<T>; control: Stream<T> } {
  const queue: T[] = [];
  let wake: (() => void) | null = null, ended = false;
  const control: Stream<T> = { push: event => { queue.push(event); wake?.(); }, end: () => { ended = true; wake?.(); }, aborted: false };
  signal?.addEventListener("abort", () => { control.aborted = true; ended = true; wake?.(); });
  const channel = (async function* () {
    for (;;) {
      while (queue.length) yield queue.shift()!;
      if (ended) return;
      await new Promise<void>(resolve => { wake = resolve; });
      wake = null;
    }
  })();
  return { channel, control };
}

const state = {
  last: { status: "ok", measurement: played(), running: false, current: false } as DiskUsageLastResponse,
  lastAsked: 0,
  scans: [] as Stream<DiskUsageScanEvent>[],
  cleans: [] as DiskUsageCleanRequest[],
  /** What the host answers to a clean, by the name of the checkout; a checkout not named loses the folders asked. */
  cleanStatus: {} as Record<string, string>,
  /** While true a clean is answered only when the test releases it, the first asked first. */
  holdCleans: false,
  heldCleans: [] as (() => void)[],
  removals: [] as { paths: string[]; discard: string[] }[],
  inventoryAsked: [] as string[],
  sessions: [] as string[],
};

const api: DiskUsageApi = {
  last: async () => { state.lastAsked++; return state.last; },
  scan: async (_request, options) => {
    const { channel, control } = stream<DiskUsageScanEvent>(options?.signal);
    state.scans.push(control);
    return channel;
  },
  clean: async request => {
    state.cleans.push(request);
    const name = request.checkout!.split("/").pop()!;
    const { channel, control } = stream<DiskUsageCleanEvent>();
    const answer = () => {
      const status = state.cleanStatus[name] ?? "ok";
      const last = state.last.measurement!;
      const shown = last.projects.flatMap(project => project.checkouts).find(value => value.path === request.checkout)!;
      // Exactly the folders asked go; the others stay, with their size.
      const asked = new Set(request.paths ?? []);
      const gone = status === "ok" ? shown.planned.filter(entry => asked.has(entry.path)) : [];
      const entries = gone.map(entry => ({ path: entry.path, outcome: "removed", bytesBefore: entry.bytes, bytesFreed: entry.bytes }));
      const freed = entries.reduce((total, entry) => total + Number(entry.bytesFreed), 0);
      const result: DiskUsageCleanResponse = { status, message: null, entries, bytesFreed: String(freed), moreRemain: false,
        checkout: status === "ok" ? { ...shown, planned: shown.planned.filter(entry => !asked.has(entry.path)), measureId: `${shown.measureId}-again`,
          measure: { ...shown.measure!, totalBytes: String(Number(shown.measure!.totalBytes) - freed), buildOutputBytes: String(Number(shown.measure!.buildOutputBytes) - freed) } } : null };
      // The host keeps the checkout measured again in its last measurement, and its total with it.
      if (result.checkout) {
        const fresh = result.checkout;
        state.last = { ...state.last, measurement: { ...last, totalBytes: String(Number(last.totalBytes) - freed), projects: last.projects.map(project => ({ ...project,
          checkouts: project.checkouts.map(value => value.path === fresh.path ? fresh : value) })) } };
      }
      control.push({ kind: "running", progress: { done: 0, total: request.paths!.length }, result: null });
      control.push({ kind: "done", progress: null, result }); control.end();
    };
    if (state.holdCleans) state.heldCleans.push(answer); else queueMicrotask(answer);
    return channel;
  },
};

const row = (path: string, change: Partial<InventoryRow> = {}): InventoryRow => ({ path, name: path.split("/").pop()!, branch: `alta/${path.split("/").pop()}`, head: "1234567",
  main: false, project: false, locked: false, missing: false, folder: path, busy: false, protection: null, sessions: [], sessionCount: 0, lastUsedAt: null, ...change });
const worktreesApi = {
  inventory: async (request: { projectId: string }) => {
    state.inventoryAsked.push(request.projectId);
    return { status: "ok", projectId: request.projectId, sessionsKnown: true, worktrees: [
      row("/code/alpha", { main: true, project: true, protection: "main" }), row("/trees/alpha/quiet-heron"), row("/trees/alpha/amber-denali"),
      row("/trees/alpha/busy-finch", { busy: true, protection: "in_use" })] };
  },
  removeMany: async (request: { paths: string[]; discard: string[] }) => {
    state.removals.push({ paths: [...request.paths], discard: [...request.discard] });
    return { status: "ok", results: request.paths.map(path => ({ path, status: "ok", message: null, branchKept: null, branchDeleted: null, bytes: String(3 * size.gib) })) };
  },
} as unknown as DiskUsageWorktreesApi;

// One hub for the window, retained while Settings is closed.
const hub = createDiskUsageHub(api);
function Fixture() {
  const [shown, setShown] = useState(true);
  const [embedded, setEmbedded] = useState(false);
  const [width, setWidth] = useState(1100);
  const [height, setHeight] = useState(760);
  Object.assign(window, { diskUsageFixture: { state, played, setShown, setEmbedded, setWidth, setHeight, size } });
  const handlers = { onOpenSession: (id: string) => { state.sessions.push(id); } };
  return <div>
    <div className="ide-shell disk-usage-main-fixture" style={{ width, height, display: "flex", flexDirection: "column", overflow: "auto" }}>
      {shown && <DiskUsagePanel epoch="epoch" spaces={spaces} hub={hub} worktreesApi={worktreesApi} {...handlers} />}
    </div>
    {/* The content of the Settings window at 900 x 650: about 430 pixels, the card's padding taken. */}
    {embedded && <div className="ide-shell disk-usage-settings-fixture" style={{ width: 398, overflow: "auto" }}>
      <DiskUsagePanel epoch="epoch" spaces={spaces} hub={hub} worktreesApi={worktreesApi} {...handlers} />
    </div>}
  </div>;
}
createRoot(document.getElementById("root")!).render(<StrictMode><Fixture /></StrictMode>);
