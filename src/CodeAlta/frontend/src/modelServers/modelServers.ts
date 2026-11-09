import type { DownloadInfo, GpuInfo, HardwareInventory, HostTask, InstalledBuild, ModelHostStatus, ModelServersMachine, ServerDefinition, ServerModel, ServerState, ServerView,
  StoredModel } from "./types";

/** A machine and what its model host says, or why it could not be read. */
export type MachineStatus = Readonly<{ machine: ModelServersMachine; status: ModelHostStatus | null; problem: string | null; loading: boolean }>;

/** A size in bytes as people read it, in decimal units: `18.5 GB`. */
export function formatBytes(bytes: number | undefined | null, locale = "en"): string {
  if (bytes === undefined || bytes === null || !Number.isFinite(bytes) || bytes < 0) return "—";
  const units = ["B", "KB", "MB", "GB", "TB"];
  let value = bytes;
  let unit = 0;
  while (value >= 1000 && unit < units.length - 1) {
    value /= 1000;
    unit++;
  }
  const digits = unit === 0 ? 0 : value >= 100 ? 0 : 1;
  return `${new Intl.NumberFormat(locale, { maximumFractionDigits: digits, minimumFractionDigits: digits }).format(value)} ${units[unit]}`;
}

/** A speed: `92 MB/s`. */
export function formatSpeed(bytesPerSecond: number, locale = "en"): string {
  return bytesPerSecond > 0 ? `${formatBytes(bytesPerSecond, locale)}/s` : "";
}

/** The time a download has left, in whole minutes or seconds: `2 min`, `40 s`; empty when it cannot be told. */
export function timeLeft(download: Pick<DownloadInfo, "done" | "total" | "bytesPerSecond">): string {
  if (download.bytesPerSecond <= 0 || download.total <= 0 || download.done >= download.total) return "";
  const seconds = (download.total - download.done) / download.bytesPerSecond;
  if (seconds < 60) return `${Math.max(1, Math.round(seconds))} s`;
  if (seconds < 3600) return `${Math.round(seconds / 60)} min`;
  return `${Math.floor(seconds / 3600)} h ${Math.round((seconds % 3600) / 60)} min`;
}

/** The part of a download done, from 0 to 1. */
export function progressOf(download: Pick<DownloadInfo, "done" | "total">): number {
  return download.total > 0 ? Math.min(1, Math.max(0, download.done / download.total)) : 0;
}

/** Whether a download is still to end. */
export function isActive(download: Pick<DownloadInfo, "state">): boolean {
  return download.state === "queued" || download.state === "downloading" || download.state === "checking" || download.state === "paused";
}

/** The name of a model as people read it: `Qwen3.8 27B · UD-Q4_K_XL` for `unsloth/Qwen3.8-27B-GGUF:UD-Q4_K_XL`. */
export function modelName(id: string): string {
  const colon = id.lastIndexOf(":");
  const repo = colon > 0 ? id.slice(0, colon) : id;
  const tag = colon > 0 ? id.slice(colon + 1) : "";
  const name = (repo.split("/").pop() ?? repo).replace(/[-_.]?GGUF$/iu, "").replace(/-/gu, " ").trim();
  return tag ? `${name} · ${tag}` : name;
}

/** The publisher of a model: `unsloth`. */
export function modelPublisher(id: string): string {
  return id.split("/")[0] ?? "";
}

/** The GPUs of a machine in a few words: `2× Radeon AI PRO R9700 · Radeon Graphics`. */
export function gpuSummary(hardware: HardwareInventory | null | undefined, withIntegrated = false): string {
  if (!hardware) return "";
  const gpus = hardware.gpus.filter(gpu => withIntegrated || !gpu.integrated || hardware.gpus.every(other => other.integrated));
  const groups = new Map<string, number>();
  for (const gpu of gpus) groups.set(shortGpuName(gpu), (groups.get(shortGpuName(gpu)) ?? 0) + 1);
  return [...groups].map(([name, count]) => count > 1 ? `${count}× ${name}` : name).join(" · ");
}

/** The name of a GPU without its vendor prefix and trademarks: `Radeon AI PRO R9700`, `GeForce RTX 4070`. */
export function shortGpuName(gpu: Pick<GpuInfo, "name">): string {
  return gpu.name.replace(/\((TM|R)\)/giu, "").replace(/^(AMD|NVIDIA|Intel)\s+/iu, "").replace(/\s+/gu, " ").trim();
}

/** The current build of each backend of a machine: `b12110 · ROCm`. */
export function currentBuilds(builds: readonly InstalledBuild[]): string {
  return builds.filter(build => build.current).map(build => `${build.tag} · ${backendName(build.backend)}`).join(", ");
}

/** The name of a backend as people read it. */
export function backendName(backend: string): string {
  switch (backend) {
    case "cuda": return "CUDA";
    case "rocm": return "ROCm";
    case "vulkan": return "Vulkan";
    case "metal": return "Metal";
    case "cpu": return "CPU";
    case "sycl": return "SYCL";
    default: return backend;
  }
}

/** Whether a newer build than the one a backend runs is offered by the channel. */
export function updateAvailable(status: Pick<ModelHostStatus, "builds" | "latest">): boolean {
  const latest = status.latest;
  if (!latest) return false;
  const current = status.builds.filter(build => build.current && build.source !== "own");
  return current.length > 0 && current.some(build => build.build < latest.build);
}

/** A server of a machine, as a row of the Servers tab. */
export type ServerRow = Readonly<{ key: string; machine: ModelServersMachine; status: ModelHostStatus; server: ServerView }>;

/** A model of a machine, or a download of one, as a row of the Models tab. */
export type ModelRow = Readonly<{ key: string; machine: ModelServersMachine; status: ModelHostStatus; id: string; model: StoredModel | null; download: DownloadInfo | null;
  usedBy: readonly string[] }>;

/** The rows of the Servers tab, across machines. */
export function serverRows(machines: readonly MachineStatus[]): ServerRow[] {
  return machines.flatMap(entry => entry.status ? entry.status.servers.map(server => ({ key: `${entry.machine.id}/${server.definition.id}`, machine: entry.machine,
    status: entry.status!, server })) : []);
}

/** The last download of each model, kept while it runs or when it failed: a failed download stays in sight until it is resumed or another ends. */
function shownDownloads(downloads: readonly DownloadInfo[]): Map<string, DownloadInfo> {
  const last = new Map<string, DownloadInfo>();
  for (const download of downloads) {
    const key = download.model.toLowerCase();
    const seen = last.get(key);
    if (!seen || download.createdAt >= seen.createdAt) last.set(key, download);
  }
  for (const [key, download] of last) if (!isActive(download) && download.state !== "failed") last.delete(key);
  return last;
}

/** The rows of the Models tab, across machines: each model on disk, and each download of a model not there yet, running or failed. */
export function modelRows(machines: readonly MachineStatus[]): ModelRow[] {
  const rows: ModelRow[] = [];
  for (const entry of machines) {
    const status = entry.status;
    if (!status) continue;
    const downloads = shownDownloads(status.downloads);
    const users = (id: string) => status.servers.filter(server => server.definition.models.some(model => model.model.toLowerCase() === id.toLowerCase())).map(server => server.definition.name);
    for (const model of status.models) {
      rows.push({ key: `${entry.machine.id}/${model.id}`, machine: entry.machine, status, id: model.id, model, download: downloads.get(model.id.toLowerCase()) ?? null, usedBy: users(model.id) });
      downloads.delete(model.id.toLowerCase());
    }
    for (const download of downloads.values()) {
      rows.push({ key: `${entry.machine.id}/${download.model}`, machine: entry.machine, status, id: download.model, model: null, download, usedBy: users(download.model) });
    }
  }
  return rows;
}

/** The state a server shows, with the model loading when one is. */
export function serverStateOf(server: ServerView): ServerState | "loading" {
  if (server.status.state === "ready" && server.status.models.some(model => model.state === "loading")) return "loading";
  return server.status.state;
}

/** The intent of a state: green when it works, orange while it waits or loads, red when it failed. */
export function stateIntent(state: string): "success" | "warning" | "danger" | "none" {
  switch (state) {
    case "ready": case "loaded": case "done": case "online": return "success";
    case "starting": case "loading": case "waiting": case "updating": case "downloading": case "checking": case "queued": case "sleeping": return "warning";
    case "failed": case "missing": return "danger";
    default: return "none";
  }
}

/** The loading progress of a server's model loading now, from 0 to 1, or null. */
export function loadingProgress(server: ServerView): number | null {
  const loading = server.status.models.find(model => model.state === "loading");
  return loading?.progress ?? null;
}

/** How many requests a server works on now, and how many it serves at once. */
export function activityOf(server: ServerView): Readonly<{ busy: number; total: number; tokensPerSecond: number | null }> {
  const busy = server.status.models.reduce((sum, model) => sum + model.busySlots, 0);
  const total = server.status.models.reduce((sum, model) => sum + model.totalSlots, 0);
  const speeds = server.status.models.map(model => model.tokensPerSecond).filter((speed): speed is number => typeof speed === "number" && speed > 0);
  return { busy, total, tokensPerSecond: speeds.length > 0 ? Math.max(...speeds) : null };
}

/** The settings a new model of a server starts with. */
export function newServerModel(model: string, devices: readonly string[] = []): ServerModel {
  return { model, devices, sampling: {}, loadOnStart: false, vision: true, options: {} };
}

/** A new server: several models, the best build of the machine, its first free port chosen by the machine. */
export function newServer(name: string, backend: string, models: readonly string[]): ServerDefinition {
  return {
    id: "", name, kind: "several", purpose: "chat", port: 0, backend, devices: [], loadedAtOnce: 1, memoryMarginMiB: 1024, access: "my-computers", enabled: true,
    keepRunning: false, chatPage: false, extraArguments: [], models: models.map(model => newServerModel(model)),
  };
}

/** The backends a machine has a current build of, the best first. */
export function installedBackends(status: Pick<ModelHostStatus, "builds" | "candidates">): string[] {
  const order = ["cuda", "rocm", "metal", "vulkan", "sycl", "cpu"];
  return [...new Set(status.builds.filter(build => build.current).map(build => build.backend))].sort((a, b) => order.indexOf(a) - order.indexOf(b));
}

/** The backend a new server of a machine takes: its best installed one, else the best one that fits. */
export function defaultBackend(status: Pick<ModelHostStatus, "builds" | "candidates">): string {
  return installedBackends(status)[0] ?? status.candidates.find(candidate => candidate.verdict === "fits")?.backend ?? "cpu";
}

/** The devices a build of a backend sees on a machine, its discrete GPUs first. */
export function devicesOf(status: Pick<ModelHostStatus, "builds">, backend: string): InstalledBuild["devices"] {
  return status.builds.find(build => build.current && build.backend === backend)?.devices ?? [];
}

/**
 * Updates the machines one after the other: each update is waited for (its servers idle, the switch, the test), and a
 * machine whose update failed or went back to its previous build stops the others. Says which machine stopped them, or
 * null when all updated.
 */
export async function updateInTurn(machines: readonly string[], start: (machine: string) => Promise<HostTask | null>,
  readTask: (machine: string, task: string) => Promise<HostTask | null>, pause = (ms: number) => new Promise(resolve => setTimeout(resolve, ms))): Promise<string | null> {
  for (const machine of machines) {
    const started = await start(machine);
    if (!started) return machine;
    let task: HostTask = started;
    // An update waits for the servers to be idle, an hour at most, then switches and tests them.
    for (let waited = 0; !task.endedAt && waited < 75 * 60_000; waited += 3000) {
      await pause(3000);
      task = await readTask(machine, started.id) ?? task;
    }

    if (!task.endedAt || task.state !== "done" || /rolled back|went back|failed/iu.test(task.message ?? "")) return machine;
  }

  return null;
}

/** The tasks of a machine that run now. */
export function runningTasks(status: Pick<ModelHostStatus, "tasks">): HostTask[] {
  return status.tasks.filter(task => task.state === "running" || task.state === "waiting");
}

/** The parts of a model repository id typed by a person: `org/repo` or `org/repo:TAG`, also a Hugging Face address. */
export function parseModelReference(text: string): Readonly<{ repo: string; tag: string | null }> | null {
  let value = text.trim();
  const url = /^https?:\/\/(?:www\.)?huggingface\.co\/([^/\s]+\/[^/\s?#]+)/iu.exec(value);
  if (url) value = url[1];
  value = value.replace(/^hf\.co\//iu, "");
  const colon = value.lastIndexOf(":");
  const repo = colon > 0 ? value.slice(0, colon) : value;
  const tag = colon > 0 ? value.slice(colon + 1) : null;
  if (!/^[A-Za-z0-9][\w.-]*\/[A-Za-z0-9][\w.-]*$/u.test(repo)) return null;
  if (tag !== null && !/^[\w.-]+$/u.test(tag)) return null;
  return { repo, tag: tag ? tag.toUpperCase() : null };
}

/** Whether a machine runs a model server that names a model. */
export function serversUsing(status: Pick<ModelHostStatus, "servers">, model: string): string[] {
  return status.servers.filter(server => server.definition.models.some(item => item.model.toLowerCase() === model.toLowerCase())).map(server => server.definition.name);
}
