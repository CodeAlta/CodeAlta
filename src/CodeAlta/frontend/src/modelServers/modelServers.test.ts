import { test } from "node:test";
import assert from "node:assert/strict";
import { updateInTurn, activityOf, copiedServer, defaultBackend, formatBytes, gpuSummary, knownBuilds, modelName, modelRows, modelServersSummary, parseModelReference, serverRows,
  serverStateOf, timeLeft, updateAvailable, type MachineStatus } from "./modelServers";
import { buildChoiceProblem } from "./ModelServerDialogs";
import type { HostTask, InstalledBuild, ModelHostStatus, ServerView } from "./types";

const build = (backend: string, number: number, current = true): InstalledBuild => ({ tag: `b${number}`, build: number, backend, folder: "", sizeBytes: 1, installedAt: "",
  devices: [], current, previous: false, bad: false, source: "ggml-org" });
const server = (id: string, model: string, state: ServerView["status"]["state"] = "ready", modelState: "loaded" | "loading" | "unloaded" = "loaded"): ServerView => ({
  definition: { id, name: id, kind: "several", purpose: "chat", port: 9931, backend: "rocm", devices: [], loadedAtOnce: 1, memoryMarginMiB: 1024, access: "my-computers",
    enabled: true, keepRunning: false, chatPage: false, extraArguments: [], models: [{ model, devices: [], sampling: {}, loadOnStart: false, vision: true, options: {} }] },
  status: { state, restarts: 0, models: [{ model, state: modelState, busySlots: 1, totalSlots: 4, tokensPerSecond: 42, vision: false }] },
});
const status = (part: Partial<ModelHostStatus>): ModelHostStatus => ({
  hardware: { os: "windows", architecture: "x64", gpus: [], libraries: { hipBlas: false, vulkan: true }, notes: [] }, builds: [], candidates: [], settings: { downloadsAtOnce: 2,
    updates: { channel: "stable", schedule: "manual", time: "04:00", day: "sunday", updateModels: false, allowLemonadeRocm: false } }, history: [], modelsFolder: "", modelsBytes: 0,
  freeBytes: 0, models: [], downloads: [], servers: [], unmanaged: [], warnings: [], tasks: [], revision: 1, ...part,
});
const machine = (id: string, value: ModelHostStatus): MachineStatus => ({ machine: { id, name: id, local: id === "local", state: "online", manages: true, uses: true }, status: value,
  problem: null, loading: false });

test("A model is named as people read it, and a reference typed or pasted is read as Hugging Face names it", () => {
  assert.equal(modelName("unsloth/Qwen3.8-27B-GGUF:UD-Q4_K_XL"), "Qwen3.8 27B · UD-Q4_K_XL");
  assert.deepEqual(parseModelReference("https://huggingface.co/unsloth/Qwen3-8B-GGUF"), { repo: "unsloth/Qwen3-8B-GGUF", tag: null });
  assert.deepEqual(parseModelReference("hf.co/unsloth/Qwen3-8B-GGUF:q4_k_m"), { repo: "unsloth/Qwen3-8B-GGUF", tag: "Q4_K_M" });
  assert.equal(parseModelReference("not a model"), null);
  assert.equal(parseModelReference("a/b:bad tag"), null);
});

test("Sizes, speeds and the time left read in decimal units", () => {
  assert.equal(formatBytes(18_500_000_000), "18.5 GB");
  assert.equal(formatBytes(512), "512 B");
  assert.equal(formatBytes(null), "—");
  assert.equal(timeLeft({ done: 0, total: 120_000_000, bytesPerSecond: 1_000_000 }), "2 min");
  assert.equal(timeLeft({ done: 10, total: 10, bytesPerSecond: 1 }), "", "a download that ended has no time left");
});

test("The GPUs of a machine say their count and leave the integrated GPU out beside a discrete one", () => {
  const hardware = status({ hardware: { os: "windows", architecture: "x64", libraries: { hipBlas: true, vulkan: true }, notes: [], gpus: [
    { id: "0", vendor: "amd", name: "AMD Radeon AI PRO R9700", integrated: false }, { id: "1", vendor: "amd", name: "AMD Radeon AI PRO R9700", integrated: false },
    { id: "2", vendor: "amd", name: "AMD Radeon(TM) Graphics", integrated: true },
  ] } }).hardware;
  assert.equal(gpuSummary(hardware), "2× Radeon AI PRO R9700");
  assert.equal(gpuSummary(hardware, true), "2× Radeon AI PRO R9700 · Radeon Graphics");
});

test("An update shows when the channel offers a newer build than a backend runs, not for a build of the person's own", () => {
  const latest = { tag: "b12110", build: 12110, publishedAt: "", assets: [], source: "ggml-org" };
  assert.equal(updateAvailable({ builds: [build("rocm", 12000)], latest }), true);
  assert.equal(updateAvailable({ builds: [build("rocm", 12110)], latest }), false);
  assert.equal(updateAvailable({ builds: [{ ...build("rocm", 1), source: "own" }], latest }), false);
  assert.equal(updateAvailable({ builds: [build("rocm", 12000)] }), false, "nothing is said before the channel is read");
});

test("A new server takes the best installed build, else the best one that fits", () => {
  assert.equal(defaultBackend({ builds: [build("vulkan", 1), build("cuda", 1)], candidates: [] }), "cuda");
  assert.equal(defaultBackend({ builds: [], candidates: [{ backend: "rocm", verdict: "fits", message: "", score: 1, gpuIds: [] }] }), "rocm");
  assert.equal(defaultBackend({ builds: [], candidates: [] }), "cpu");
});

test("The rows list each server and each model across machines, with the downloads of models not there yet", () => {
  const local = status({ servers: [server("coder", "u/Qwen-GGUF:Q4_K_M")], models: [{ id: "u/Qwen-GGUF:Q4_K_M", repo: "u/Qwen-GGUF", tag: "Q4_K_M", revision: "a", files: [],
    sizeBytes: 5, freedBytes: 5, vision: false, state: "ready" }], downloads: [{ id: "d", model: "u/Gemma-GGUF:Q8_0", state: "downloading", done: 1, total: 2, bytesPerSecond: 1, files: 1,
    createdAt: "" }, { id: "old", model: "u/Old-GGUF:Q2_K", state: "done", done: 1, total: 1, bytesPerSecond: 0, files: 1, createdAt: "" }] });
  const remote = status({ servers: [server("embed", "u/Embed-GGUF:F16", "starting", "unloaded")] });
  const machines = [machine("local", local), machine("pc-ai", remote), { ...machine("mac", remote), status: null }];
  assert.deepEqual(serverRows(machines).map(row => row.key), ["local/coder", "pc-ai/embed"], "a machine not read has no rows");
  const models = modelRows(machines);
  assert.deepEqual(models.map(row => row.id), ["u/Qwen-GGUF:Q4_K_M", "u/Gemma-GGUF:Q8_0"], "a download that ended is no row");
  assert.deepEqual(models[0].usedBy, ["coder"]);
  assert.equal(models[1].model, null);
});

test("A failed download stays a row until a later download of its model ends", () => {
  const download = (id: string, model: string, state: "failed" | "done", createdAt: string) => ({ id, model, state, done: 1, total: 2, bytesPerSecond: 0, files: 1, createdAt,
    error: state === "failed" ? "disk full" : undefined });
  const local = status({ downloads: [download("f", "u/Gemma-GGUF:Q8_0", "failed", "2026-11-09T10:00:00Z"), download("f2", "u/Phi-GGUF:Q4_0", "failed", "2026-11-09T10:00:00Z"),
    download("d2", "u/Phi-GGUF:Q4_0", "done", "2026-11-09T11:00:00Z")] });
  const models = modelRows([machine("local", local)]);
  assert.deepEqual(models.map(row => [row.id, row.download?.error]), [["u/Gemma-GGUF:Q8_0", "disk full"]]);
});

test("A ready server that loads a model says it loads, and its activity adds its models up", () => {
  assert.equal(serverStateOf(server("a", "m:Q", "ready", "loading")), "loading");
  assert.equal(serverStateOf(server("a", "m:Q", "failed")), "failed");
  assert.deepEqual(activityOf(server("a", "m:Q")), { busy: 1, total: 4, tokensPerSecond: 42 });
});
test("Updating machines in turn waits for each, and the first that fails stops the others", async () => {
  const task = (id: string, state: HostTask["state"], ended: boolean, message?: string): HostTask => ({ id, kind: "update", title: "Update llama.cpp", state, done: 0, total: 0,
    startedAt: "", endedAt: ended ? "x" : undefined, message });
  const started: string[] = [];
  const outcomes: Record<string, HostTask> = { a: task("ta", "done", true, "rocm: b1 → b2."), b: task("tb", "failed", true, "rocm: b2 rolled back to b1."), c: task("tc", "done", true) };
  const reads: Record<string, number> = {};
  const stoppedAt = await updateInTurn(["a", "b", "c"], async machine => { started.push(machine); return task(outcomes[machine].id, "running", false); },
    async machine => (reads[machine] = (reads[machine] ?? 0) + 1) < 2 ? null : outcomes[machine], async () => {});
  assert.equal(stoppedAt, "b");
  assert.deepEqual(started, ["a", "b"], "the machine after the one that failed is not updated");
  assert.equal(await updateInTurn(["a", "c"], async machine => task(outcomes[machine].id, "running", false), async machine => outcomes[machine], async () => {}), null);
  assert.equal(await updateInTurn(["a"], async () => null, async () => null, async () => {}), "a", "an update that did not start stops them");
});

test("A server copied to another machine keeps its models and settings, on that machine's build, GPUs and first free port", () => {
  const source = server("coder", "u/Qwen-GGUF:Q4_K_M").definition;
  const definition = { ...source, backend: "cuda", port: 9931, build: "b12110", devices: ["CUDA0"], models: source.models.map(model => ({ ...model, devices: ["CUDA0"], context: 32768 })) };
  const rocm = status({ builds: [build("rocm", 12110)], servers: [server("other", "u/Gemma-GGUF:Q8_0")] });
  const copy = copiedServer(definition, rocm);
  assert.deepEqual({ id: copy.id, port: copy.port, backend: copy.backend, build: copy.build, devices: copy.devices }, { id: "", port: 0, backend: "rocm", build: undefined, devices: [] });
  assert.deepEqual(copy.models.map(model => [model.model, model.devices, model.context]), [["u/Qwen-GGUF:Q4_K_M", [], 32768]]);
  assert.equal(copiedServer(definition, status({ builds: [build("cuda", 12110)] })).backend, "cuda", "the same backend where it runs");
  assert.equal(copiedServer(definition, status({ servers: [server("coder", "x/y:Q")] })).name, `${definition.name} (2)`, "a name the machine has is not taken twice");
});

test("One build for several machines is one of llama.cpp, or the newest of each channel", () => {
  const a = status({ builds: [build("cuda", 12110), { ...build("rocm", 1371), tag: "lemonade-b1371", source: "lemonade" }], latest: { tag: "b12193", build: 12193, publishedAt: "", assets: [], source: "ggml-org" } });
  const b = status({ builds: [build("rocm", 12000)] });
  assert.deepEqual(knownBuilds([a, b]), ["b12193", "b12110", "b12000"], "newest first, without Lemonade's own numbers");
  assert.equal(buildChoiceProblem(""), null);
  assert.equal(buildChoiceProblem("b12110"), null);
  assert.notEqual(buildChoiceProblem("12110"), null);
});

test("The Fleet tile counts the servers ready, the models and the work under way", () => {
  const running = { id: "t", kind: "update" as const, title: "Update", state: "running" as const, done: 0, total: 1, startedAt: "" };
  const summary = modelServersSummary([status({ servers: [server("a", "m:Q"), server("b", "m:Q", "failed")], tasks: [running] }), status({ servers: [server("c", "m:Q")] })]);
  assert.deepEqual(summary, { ready: 2, servers: 3, models: 0, machines: 2, working: 1 });
});
