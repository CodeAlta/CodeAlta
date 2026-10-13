import type { SpeechAudioRequest, SpeechInstallState, SpeechListingResponse, SpeechModelInfo, SpeechStopRequest, SpeechTranscript } from "#neoastra";
import type { AudioChunk } from "./pcm";
import type { CaptureFailure, CaptureHandlers, CaptureReady, Microphones, StartCapture } from "./capture";
import type { InputDevice, MicrophoneChoice } from "./microphones";

// A host and a microphone that a test plays: the unit tests of the dictation and the fixture of the browser
// test are given the same ones. Nothing here touches a real microphone, a model or the network.

type CallOptions = { signal?: AbortSignal; timeoutMilliseconds?: number };

/** A stream of the host the test feeds: it ends when the test ends it, or when its reader goes away. */
export function fixtureStream<T>(signal?: AbortSignal) {
  const waiting: T[] = [];
  let closed = signal?.aborted ?? false, wake: (() => void) | undefined;
  const close = () => { closed = true; wake?.(); };
  signal?.addEventListener("abort", close, { once: true });
  return {
    push(value: T) { if (!closed) { waiting.push(value); wake?.(); } },
    close,
    get closed() { return closed; },
    async *[Symbol.asyncIterator](): AsyncGenerator<T> {
      for (;;) {
        while (waiting.length) yield waiting.shift()!;
        if (closed) return;
        await new Promise<void>(resolve => { wake = resolve; });
        wake = undefined;
      }
    },
  };
}

export const fixtureModels: readonly SpeechModelInfo[] = Object.freeze([
  { id: "parakeet-tdt-0.6b-v3", value: "parakeet-tdt-0.6b-v3", name: "Parakeet TDT 0.6b v3", author: "NVIDIA", languages: "european", license: "CC BY 4.0", bytes: "702545920", installed: false, preset: true },
  { id: "parakeet-tdt-0.6b-v2", value: "parakeet-tdt-0.6b-v2", name: "Parakeet TDT 0.6b v2", author: "NVIDIA", languages: "english", license: "CC BY 4.0", bytes: "693108736", installed: false, preset: true },
  { id: "whisper-small", value: "whisper-small", name: "Whisper small", author: "OpenAI", languages: "multilingual", license: "MIT", bytes: "394264576", installed: false, preset: true },
  { id: "moonshine-base-en", value: "moonshine-base-en", name: "Moonshine base", author: "Useful Sensors", languages: "english", license: "MIT", bytes: "147849216", installed: false, preset: true },
  { id: "moonshine-tiny-en", value: "moonshine-tiny-en", name: "Moonshine tiny", author: "Useful Sensors", languages: "english", license: "MIT", bytes: "46137344", installed: false, preset: true },
]);

const customAddress = /^https:\/\/huggingface\.co\/([\w.-]+)\/([\w.-]+)(\/tree\/[\w.-]+(\/.*)?)?$/;

/** A host of speech to prompt in memory: settings, models, one download and dictations, each step by the test. */
export function createFixtureHost(options: { epoch?: string; installed?: readonly string[]; enabled?: boolean; supported?: boolean; send?: string } = {}) {
  const epoch = options.epoch ?? "fixture";
  const state = {
    status: "ok", supported: options.supported ?? true, enabled: options.enabled ?? true, model: "parakeet-tdt-0.6b-v3",
    /** What happens to the prompt when a dictation stops: `manual` or `on_stop`. */
    send: options.send ?? "manual",
    /** The send modes that were saved; a save that keeps the mode (null) is not one of them. */
    sends: [] as string[],
    models: fixtureModels.map(model => ({ ...model, installed: options.installed?.includes(model.id) ?? false })) as SpeechModelInfo[],
    /** What `start` answers when it is not `ok`; null starts a dictation when the chosen model is installed. */
    startStatus: null as string | null,
    /** What `append` answers. */
    appendStatus: "ok",
    /** What `stop` answers beside the text; null takes the final text of the dictation. */
    stopReply: null as { status: string; text: string; error: string | null } | null,
    /** Holds the answers of `start` and `stop` until the test lets them go. */
    hold: false,
    install: { state: "idle", modelId: null, modelName: null, receivedBytes: "0", totalBytes: "0", error: null } as SpeechInstallState,
    reads: 0, starts: 0, saves: [] as { enabled: boolean; model: string | null }[], installs: [] as (string | null)[], cancels: 0, deletes: [] as (string | null)[],
    appends: [] as SpeechAudioRequest[], stops: [] as SpeechStopRequest[],
    watches: [] as ReturnType<typeof fixtureStream<SpeechInstallState>>[],
    dictations: [] as { id: string; stream: ReturnType<typeof fixtureStream<SpeechTranscript>>; finalText: string }[],
    held: [] as (() => void)[],
  };
  const modelId = (value: string) => { const match = customAddress.exec(value); return match ? `${match[1]}--${match[2]}`.toLowerCase() : value; };
  const listing = (status = state.status, error: string | null = null): SpeechListingResponse => {
    const id = modelId(state.model);
    const models = state.models.some(model => model.id === id) ? state.models
      : [...state.models, { id, value: state.model, name: state.model.replace("https://huggingface.co/", ""), author: state.model.split("/")[3] ?? "", languages: "", license: "", bytes: "0", installed: false, preset: false }];
    return { status, supported: state.supported, enabled: state.enabled, model: state.model, modelId: id, models, rootPath: "C:\\Users\\alexandre.mutel.contoso\\.alta\\cache\\speech", send: state.send, error };
  };
  const refuse = (expected: string | null) => expected === epoch ? null : "stale_epoch";
  const held = <T>(value: T) => state.hold ? new Promise<T>(resolve => state.held.push(() => resolve(value))) : Promise.resolve(value);
  const tell = (install: Partial<SpeechInstallState>) => {
    state.install = { ...state.install, ...install };
    for (const watch of state.watches) watch.push(state.install);
  };
  const api = {
    read: async (request: { expectedEpoch: string | null }) => { state.reads++; return refuse(request.expectedEpoch) ? { ...listing(), status: "stale_epoch" } : listing(); },
    saveSettings: async (request: { expectedEpoch: string | null; enabled: boolean; model: string | null; send: string | null }) => {
      state.saves.push({ enabled: request.enabled, model: request.model });
      if (request.send !== null && !["manual", "on_stop"].includes(request.send)) return listing("invalid_request", "The send mode is not known.");
      const model = request.model?.trim() ?? "";
      if (!state.models.some(value => value.preset && value.id === model) && !customAddress.test(model))
        return listing("invalid_request", "The model is not a preset or a Hugging Face repository address.");
      state.enabled = request.enabled; state.model = model;
      // Null keeps the mode that is saved.
      if (request.send !== null) { state.send = request.send; state.sends.push(request.send); }
      return listing();
    },
    install: async (request: { expectedEpoch: string | null; model: string | null }) => {
      state.installs.push(request.model);
      if (state.install.state === "downloading" || state.install.state === "installing") return { status: "busy" };
      const id = modelId(request.model ?? state.model), model = listing().models.find(value => value.id === id);
      tell({ state: "downloading", modelId: id, modelName: model?.name ?? id, receivedBytes: "0", totalBytes: "0", error: null });
      return { status: "ok" };
    },
    cancelInstall: async () => { state.cancels++; tell({ state: "canceled" }); return { status: "ok" }; },
    delete: async (request: { expectedEpoch: string | null; model: string | null }) => {
      state.deletes.push(request.model);
      state.models = state.models.filter(model => model.preset || model.id !== request.model).map(model => model.id === request.model ? { ...model, installed: false } : model);
      return listing();
    },
    watch: async (_request: { expectedEpoch: string | null }, call?: CallOptions) => {
      const stream = fixtureStream<SpeechInstallState>(call?.signal);
      state.watches.push(stream);
      // The first item replays where the download stands.
      stream.push(state.install);
      return stream;
    },
    start: async (request: { expectedEpoch: string | null }) => {
      state.starts++;
      if (refuse(request.expectedEpoch)) return held({ status: "stale_epoch", id: null });
      if (state.startStatus) return held({ status: state.startStatus, id: null });
      if (!listing().models.find(model => model.id === modelId(state.model))?.installed) return held({ status: "model_missing", id: null });
      const id = `dictation-${state.dictations.length + 1}`;
      state.dictations.push({ id, stream: fixtureStream<SpeechTranscript>(), finalText: "" });
      return held({ status: "ok", id });
    },
    append: async (request: SpeechAudioRequest) => { state.appends.push(request); return { status: state.appendStatus }; },
    listen: async (request: { expectedEpoch: string | null; id: string | null }, call?: CallOptions) => {
      const dictation = state.dictations.find(value => value.id === request.id);
      call?.signal?.addEventListener("abort", () => dictation?.stream.close(), { once: true });
      return dictation?.stream ?? fixtureStream<SpeechTranscript>();
    },
    stop: async (request: SpeechStopRequest) => {
      state.stops.push(request);
      const dictation = state.dictations.find(value => value.id === request.id);
      if (!dictation) return held({ status: "unknown", text: "", error: null });
      const reply = state.stopReply ?? { status: "ok", text: dictation.finalText, error: null };
      return held(reply).then(value => {
        // The stream tells the end as well, with the same text.
        dictation.stream.push({ state: value.status === "failed" ? "failed" : "done", finalText: value.text, partialText: "", error: value.error });
        dictation.stream.close();
        return value;
      });
    },
  };
  return {
    epoch, state, api, tell,
    /** The host heard something: the whole final text so far, and what is being spoken. */
    hear(finalText: string, partialText = "", snapshot: Partial<SpeechTranscript> = {}) {
      const dictation = state.dictations.at(-1)!;
      dictation.finalText = finalText;
      dictation.stream.push({ state: "listening", finalText, partialText, error: null, ...snapshot });
    },
    /** A download that ends: the model is in the cache. */
    installed(id: string) {
      state.models = listing().models.map(model => model.id === id ? { ...model, installed: true } : model);
      tell({ state: "done", modelId: id });
    },
    /** Lets the held answers go. */
    release() { for (const resolve of state.held.splice(0)) resolve(); },
  };
}
export type FixtureHost = ReturnType<typeof createFixtureHost>;

/** The audio inputs of a Windows computer as Chromium lists them: the two it repeats under names of its own, then the real ones. */
export const fixtureDevices: readonly InputDevice[] = Object.freeze([
  { id: "default", label: "Default - Microphone (NVIDIA Broadcast)" },
  { id: "communications", label: "Communications - Microphone (Yeti Stereo Microphone) (b58e:9e84)" },
  { id: "id-broadcast", label: "Microphone (NVIDIA Broadcast)" },
  { id: "id-yeti", label: "Microphone (Yeti Stereo Microphone) (b58e:9e84)" },
]);

/** A place for the preference of the microphone that a test reads and fills: a user who already said which one to use, by default. */
export function createFixturePreferences(initial: string | null = JSON.stringify({ device: null, confirmed: true })) {
  const state = { value: initial, writes: 0 };
  return { state, read: () => state.value, write: (value: string) => { state.value = value; state.writes++; } };
}

/** The microphones of a page the test plays: it lists them, plugs them in and out, and speaks into the one that is open. */
export function createFixtureMicrophone() {
  const captures: { handlers: CaptureHandlers; device: MicrophoneChoice; stopped: boolean }[] = [];
  const watchers = new Set<() => void>();
  const state = {
    captures,
    /** What opening the microphone gives: it opens, it fails, or it waits for the test. */
    opening: "ready" as "ready" | "wait" | CaptureFailure,
    /** What the browser lists. */
    devices: [...fixtureDevices] as InputDevice[],
    /** Whether the browser names the devices without being asked for a microphone first. */
    named: true,
    lists: [] as boolean[],
  };
  // What a real capture tells of what it opened: the device asked for, the one of the system when it is not there.
  const ready = (device: MicrophoneChoice): CaptureReady => {
    const system = state.devices.find(candidate => candidate.id === "default")?.label ?? null;
    if (!device) return { label: system, fallback: false, moved: null };
    const exact = state.devices.find(candidate => candidate.id === device.id);
    if (exact) return { label: exact.label, fallback: false, moved: null };
    const again = state.devices.find(candidate => candidate.label === device.label && candidate.id !== "default" && candidate.id !== "communications");
    return again ? { label: again.label, fallback: false, moved: again } : { label: system, fallback: true, moved: null };
  };
  const start: StartCapture = (handlers, device = null) => {
    const capture = { handlers, device, stopped: false };
    captures.push(capture);
    if (state.opening === "ready") queueMicrotask(() => { if (!capture.stopped) handlers.onReady(ready(device)); });
    else if (state.opening !== "wait") { const failure = state.opening; queueMicrotask(() => { if (!capture.stopped) { capture.stopped = true; handlers.onError(failure); } }); }
    return { stop() { capture.stopped = true; } };
  };
  const microphones: Microphones = {
    start,
    list: async probe => { state.lists.push(probe); return state.named || probe || captures.some(capture => !capture.stopped) ? [...state.devices] : null; },
    watch(changed) { watchers.add(changed); return () => { watchers.delete(changed); }; },
  };
  return {
    state, start, microphones,
    /** Whether a microphone is open now. */
    open: () => captures.some(capture => !capture.stopped),
    /** The id of the device the open capture asked for; "" for the one of the system, null when none is open. */
    heard: () => { const capture = [...captures].reverse().find(candidate => !candidate.stopped); return capture ? capture.device?.id ?? "" : null; },
    /** A tenth of a second of sound, as the capture would give it. A stopped capture gives nothing. */
    speak(chunk: Partial<AudioChunk> = {}) {
      const capture = captures.at(-1);
      if (capture && !capture.stopped) capture.handlers.onChunk({ base64: "AAAA", level: .5, peak: .1, ...chunk });
    },
    /** A tenth of a second of a microphone that gives nothing. */
    silence() { this.speak({ level: 0, peak: 0 }); },
    ready() { const capture = captures.at(-1); if (capture && !capture.stopped) capture.handlers.onReady(ready(capture.device)); },
    fail(failure: CaptureFailure) { const capture = captures.at(-1); if (capture && !capture.stopped) { capture.stopped = true; capture.handlers.onError(failure); } },
    /** A device is plugged in or taken away: the list changes, and who watches is told. */
    plug(devices: readonly InputDevice[]) { state.devices = [...devices]; for (const watcher of [...watchers]) watcher(); },
    watching: () => watchers.size,
  };
}
