import { test } from "node:test";
import assert from "node:assert/strict";
import { activityOf, defaultBackend, formatBytes, gpuSummary, modelName, modelRows, parseModelReference, serverRows, serverStateOf, timeLeft, updateAvailable,
  type MachineStatus } from "./modelServers";
import type { InstalledBuild, ModelHostStatus, ServerView } from "./types";

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

test("A ready server that loads a model says it loads, and its activity adds its models up", () => {
  assert.equal(serverStateOf(server("a", "m:Q", "ready", "loading")), "loading");
  assert.equal(serverStateOf(server("a", "m:Q", "failed")), "failed");
  assert.deepEqual(activityOf(server("a", "m:Q")), { busy: 1, total: 4, tokensPerSecond: 42 });
});
