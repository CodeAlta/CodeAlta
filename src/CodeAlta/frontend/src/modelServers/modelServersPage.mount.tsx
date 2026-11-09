// Disposable page: Settings > Fleet > Model servers over two machines, this computer (CUDA) and pc-ai (ROCm), driven by
// modelServersPage.browser.test.ts through `modelServersFixture`: what the page asks is recorded, and the machines answer as
// a model host does.
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { ShellLanguageContext } from "../shellLanguage";
import type { ModelServersApi } from "./api";
import { ModelServersSettings } from "./ModelServersSettings";
import type { HostTask, InstalledBuild, ModelHostAction, ModelHostStatus, ServerView, StoredModel } from "./types";

document.documentElement.classList.add("bp6-dark");

const calls: string[] = [];
const build = (backend: string, tag: string, number: number): InstalledBuild => ({ tag, build: number, backend, folder: "", sizeBytes: 400e6, installedAt: "2026-11-01T00:00:00Z",
  devices: [{ name: backend === "cuda" ? "CUDA0" : "ROCm0", description: backend === "cuda" ? "NVIDIA GeForce RTX 4070" : "AMD Radeon AI PRO R9700", totalMiB: 12000, freeMiB: 11000 }],
  current: true, previous: false, bad: false, source: "ggml-org" });
const model = (id: string, size: number, measured = false): StoredModel => ({ id, repo: id.split(":")[0], tag: id.split(":")[1], revision: "c0ffee0123456789", files: [], sizeBytes: size,
  freedBytes: size, vision: false, state: "ready", modelPath: `/models/${id}.gguf`,
  measured: measured ? { backend: "cuda", build: "b12110", context: 40960, gpuLayers: -1, arguments: "-c 40960 -ngl -1", at: "2026-11-09T10:00:00Z",
    devices: [{ device: "CUDA0", modelBytes: 1050 * 1048576, contextBytes: 4480 * 1048576, computeBytes: 352 * 1048576 }] } : undefined });
const server = (id: string, name: string, backend: string, models: string[], state: ServerView["status"]["state"] = "ready"): ServerView => ({
  definition: { id, name, kind: "several", purpose: "chat", port: 9931, backend, devices: backend === "rocm" ? ["ROCm0"] : [], loadedAtOnce: 1, memoryMarginMiB: 1024,
    access: "my-computers", enabled: true, keepRunning: false, chatPage: false, extraArguments: [],
    models: models.map(item => ({ model: item, devices: [], sampling: {}, loadOnStart: false, vision: true, options: {} })) },
  status: { state, restarts: 0, address: state === "ready" ? "http://127.0.0.1:9931" : undefined, buildTag: "b12110",
    models: models.map(item => ({ model: item, state: "loaded", busySlots: 0, totalSlots: 4, vision: false })) },
});
const base = (part: Partial<ModelHostStatus>): ModelHostStatus => ({
  hardware: { os: "windows", architecture: "x64", gpus: [], libraries: { hipBlas: false, vulkan: true }, notes: [] }, builds: [], candidates: [],
  settings: { downloadsAtOnce: 2, updates: { channel: "stable", schedule: "manual", time: "04:00", day: "sunday", updateModels: false, allowLemonadeRocm: false } },
  history: [], modelsFolder: "/models", modelsBytes: 0, freeBytes: 900e9, models: [], downloads: [], servers: [], unmanaged: [], warnings: [], tasks: [], revision: 1,
  latest: { tag: "b12193", build: 12193, publishedAt: "2026-11-08T00:00:00Z", assets: [], source: "ggml-org" }, ...part,
});
const statuses: Record<string, ModelHostStatus> = {
  local: base({ builds: [build("cuda", "b12110", 12110)], models: [model("unsloth/Qwen3-1.7B-GGUF:Q4_K_M", 1.1e9, true)], modelsBytes: 1.1e9,
    servers: [server("qwen3", "Qwen3 1.7B", "cuda", ["unsloth/Qwen3-1.7B-GGUF:Q4_K_M"])] }),
  pcai: base({ hardware: { os: "linux", architecture: "x64", gpus: [], libraries: { hipBlas: true, vulkan: true, rocmVersion: "7.2.1" }, notes: [] },
    builds: [build("rocm", "b12110", 12110)], models: [model("unsloth/gemma-4-E2B-it-GGUF:UD-Q4_K_XL", 3.2e9), model("unsloth/Qwen3.6-27B-GGUF:UD-Q4_K_XL", 18.5e9)],
    modelsBytes: 21.7e9, servers: [server("gemma", "gemma 4 E2B it", "rocm", ["unsloth/gemma-4-E2B-it-GGUF:UD-Q4_K_XL"])] }),
};
// An update ends at the first read of its machine, as a quick update of a machine with idle servers would.
const tasks = new Map<string, HostTask>();
const api: ModelServersApi = {
  machines: async () => [
    { id: "local", name: "this-pc", local: true, state: "online", manages: true, uses: true },
    { id: "pcai", name: "pc-ai", local: false, state: "online", manages: true, uses: true },
  ],
  status: async machine => {
    const task = tasks.get(machine);
    return { ...statuses[machine], tasks: task ? [{ ...task, state: "done", endedAt: "2026-11-09T10:01:00Z", message: "rocm: b12110 → b12110." }] : [] };
  },
  act: async (machine, action: ModelHostAction) => {
    calls.push(JSON.stringify({ machine, ...action }));
    if (action.action === "updateNow") {
      const task: HostTask = { id: `task-${machine}`, kind: "update", title: "Update llama.cpp", state: "running", done: 0, total: 1, startedAt: "2026-11-09T10:00:00Z" };
      tasks.set(machine, task);
      return { task };
    }
    if (action.action === "setServer" && action.server) return { server: { definition: { ...action.server, id: "copied" }, status: { state: "waiting", restarts: 0, models: [] } } };
    if (action.action === "serverLog") return { log: [] };
    return {};
  },
  useInSessions: async () => [],
  hubSearch: async () => [],
  hubModel: async () => { throw new Error("not in this fixture"); },
  watch: async signal => { await new Promise(resolve => signal.addEventListener("abort", resolve)); },
};

Object.assign(window, { modelServersFixture: { calls } });
createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => {} }}>
    <div className="ide-shell settings-dialog-content" style={{ padding: 12, height: "100%", boxSizing: "border-box", overflow: "auto" }}>
      <ModelServersSettings epoch="12345678-1234-1234-1234-123456789abc" api={api} />
    </div>
  </ShellLanguageContext.Provider>
</StrictMode>);
