import { createContext } from "react";
import type { speech, SpeechListingResponse, SpeechTranscript } from "#neoastra";
import type { Capture, CaptureFailure, CaptureReady, Microphones } from "./capture";
import { dictationSilenceDelay, microphoneStorageKey, noSound, readMicrophone, resolveMicrophone, saveMicrophone, shortMicrophoneName, silenceDelay, silencePeak,
  type MicrophoneChoice } from "./microphones";
import { createSpeechStore, dictationFailure, idleDictation, installView, silenceNotice, withDictation, withInstall, withMonitor, withoutHost,
  type SpeechNotice, type SpeechState } from "./speechStore";
import { unwritten } from "./spokenText";
import { sendsOnStop, type StopReason } from "./stopping";

/** The part of the host a window dictates with. */
export type SpeechApi = Pick<typeof speech, "read" | "install" | "cancelInstall" | "watch" | "start" | "append" | "listen" | "stop">;

/** Where a dictation writes: the prompt of the composer that started it. */
export type DictationSink = Readonly<{
  /** Writes a finished piece at the caret. */
  write: (text: string) => void;
  /** Shows what is being spoken, greyed and not part of the prompt; an empty text removes it. */
  interim: (text: string) => void;
  /** Gives the keyboard to the prompt. */
  focus: () => void;
  /** Sends the prompt as the Send button of the composer would; absent where the composer sends nothing by itself. */
  send?: () => void;
}>;

export type SpeechDependencies = Readonly<{
  api: SpeechApi;
  microphones: Microphones;
  /** Tells the user what went wrong with a dictation, or with its microphone. */
  notify: (notice: SpeechNotice) => void;
  /** Where the choice of a microphone is kept; the storage of the page by default. */
  preferences?: Readonly<{ read: () => string | null; write: (value: string) => void }>;
  /** How long a microphone whose level is shown may give nothing before a line says so; three seconds by default. */
  silenceAfter?: number;
  /** How long a dictation may hear nothing at all before a notice says so; eight seconds by default. */
  warnAfter?: number;
  /** How long a send waits for the last words of a dictation; ten seconds by default. */
  sendWait?: number;
}>;

// One dictation, from the click that starts it until the host has given its last word. It outlives its place
// in the state when another composer starts one: it still writes what it heard into its own prompt.
type Running = {
  readonly owner: string; readonly listening: AbortController;
  sink: DictationSink | null; id: string | null; capture: Capture | null;
  /** How much of the final text of the host is in the prompt. */
  written: number;
  sequence: number;
  /** The stop was asked of the host, or is about to be: no audio goes after it. */
  stopping: boolean;
  /** Its composer can no longer take text. */
  abandoned: boolean;
  ended: boolean;
  told: boolean;
  /** The loudest sample heard, and the wait after which a microphone that gave nothing is said. */
  loudest: number; silence: ReturnType<typeof setTimeout> | undefined;
  /** The whole text it wrote: what decides whether there is enough to send. */
  text: string;
  /** A send of its composer waits for its last words: told when it has ended. */
  settled?: { readonly ended: Promise<void>; readonly resolve: () => void };
};

// A microphone that is open for its level only.
type Monitoring = { readonly key: string; capture: Capture | null; ended: boolean; loudest: number; silence: ReturnType<typeof setTimeout> | undefined };

const pageStorage = { read: () => localStorage.getItem(microphoneStorageKey), write: (value: string) => localStorage.setItem(microphoneStorageKey, value) };

/**
 * Speech to prompt of one window: what the host tells of the models and of a download, the microphone the
 * user chose, and the one dictation. It is made once by the window and given to the composers through `SpeechContext`.
 */
export function createSpeech({ api, microphones, notify, preferences = pageStorage, silenceAfter = silenceDelay, warnAfter = dictationSilenceDelay, sendWait = 10_000 }: SpeechDependencies) {
  const store = createSpeechStore();
  store.update(state => ({ ...state, microphone: readMicrophone(preferences.read) }));
  const levels = new Set<(level: number, owner: string | null) => void>();
  const live = new Set<Running>();
  // The composers whose microphone button is there now: a setup can be shown beside them.
  const present = new Set<string>();
  let epoch: string | null = null;
  let current: Running | null = null;
  let monitoring: Monitoring | null = null;
  let connection = 0;
  // The chosen device that was said not to be connected: said once for it.
  let toldMissing: string | null = null;
  let watchers = 0, unwatch: (() => void) | null = null;

  const level = (value: number, owner: string | null) => { for (const listener of [...levels]) listener(value, owner); };
  const shown = (running: Running) => current === running;

  function tell(running: Running, error: string | null) {
    if (running.told || running.abandoned) return;
    running.told = true;
    notify({ message: dictationFailure(error), intent: "danger" });
  }

  function keepMicrophone(device: MicrophoneChoice, confirmed: boolean) {
    const before = store.getState().microphone;
    if (before.confirmed === confirmed && before.device?.id === device?.id && before.device?.label === device?.label) return;
    const microphone = { device, confirmed };
    if (before.device?.id !== device?.id) toldMissing = null;
    store.update(state => ({ ...state, microphone }));
    saveMicrophone(preferences.write, microphone);
  }

  // The reading of the devices that is under way: asked twice at once (an effect that runs twice), it is one reading.
  let reading: { probe: boolean; done: Promise<void> } | null = null;
  /** Reads the audio inputs the browser lists. A device whose id changed since it was chosen is found by its label, and kept under its new id. */
  function listDevices(probe: boolean): Promise<void> {
    if (reading && (reading.probe || !probe)) return reading.done;
    const mine = { probe, done: (async () => {
      const devices = await microphones.list(probe).catch(() => null);
      if (!devices) return;
      store.update(state => ({ ...state, devices }));
      const { device, confirmed } = store.getState().microphone;
      const found = resolveMicrophone(devices, device);
      if (device && found && found.id !== device.id) keepMicrophone({ id: found.id, label: device.label }, confirmed);
    })() };
    reading = mine;
    void mine.done.finally(() => { if (reading === mine) reading = null; });
    return mine.done;
  }

  // What a capture opened, for a dictation as for a level: the browser now names the devices.
  function opened(ready: CaptureReady) {
    const { device, confirmed } = store.getState().microphone;
    if (ready.moved && device) keepMicrophone({ id: ready.moved.id, label: device.label }, confirmed);
    if (ready.fallback && device && toldMissing !== device.id) {
      toldMissing = device.id;
      notify({ message: "The chosen microphone is not connected. The microphone of the system is used.", intent: "warning" });
    }
    void listDevices(false);
  }

  // The device to open for the choice of the user: under the id the browser lists it with now, when it does.
  const chosenDevice = (choice: MicrophoneChoice) => choice ? resolveMicrophone(store.getState().devices ?? [], choice) ?? choice : null;

  function stopMonitor(key?: string) {
    const monitor = monitoring;
    if (!monitor || key !== undefined && monitor.key !== key) return;
    monitoring = null;
    monitor.ended = true;
    clearTimeout(monitor.silence);
    monitor.capture?.stop(); monitor.capture = null;
    level(0, monitor.key);
    store.update(state => state.monitor ? { ...state, monitor: null } : state);
  }

  function write(running: Running, finalText: string) {
    const piece = unwritten(finalText, running.written);
    if (!piece || !running.sink || running.abandoned) return;
    // Counted before it is written: what the write causes (a change of the prompt, a send) never writes it again.
    running.written = finalText.length;
    running.text = finalText;
    running.sink.write(piece);
  }

  function end(running: Running) {
    if (running.ended) return;
    running.ended = true;
    live.delete(running);
    running.settled?.resolve();
    clearTimeout(running.silence);
    running.capture?.stop(); running.capture = null;
    running.listening.abort();
    const sink = running.sink;
    running.sink = null;
    sink?.interim("");
    if (!shown(running)) return;
    current = null;
    level(0, running.owner);
    store.update(state => ({ ...state, dictation: idleDictation }));
    // The keyboard goes back to the prompt after a dictation that recorded, not after a click that only asked something.
    if (running.id && !running.abandoned) sink?.focus();
  }

  /**
   * Ends a dictation with the host: its last words are written, unless `cancel` drops them. `reason` says what
   * ended it: the prompt is then sent only when the user stopped it themselves, in the mode that sends.
   */
  async function finish(running: Running, cancel: boolean, reason: StopReason) {
    if (cancel && !running.abandoned) { running.abandoned = true; running.sink?.interim(""); }
    if (running.ended || running.stopping) return;
    running.stopping = true;
    clearTimeout(running.silence);
    running.capture?.stop(); running.capture = null;
    if (shown(running)) { level(0, running.owner); store.update(state => withDictation(state, { status: "finishing" })); }
    const id = running.id, at = epoch;
    // Still starting: the answer of the host ends it.
    if (!id) return;
    const sink = running.sink;
    let whole = false;
    try {
      const reply = await api.stop({ expectedEpoch: at, id, cancel }, { timeoutMilliseconds: 30_000 });
      if (reply.status !== "unknown") write(running, reply.text);
      if (reply.status === "failed") tell(running, reply.error);
      whole = reply.status === "ok";
    } catch { /* The host is gone: what was heard is what was written. */ }
    // A send that waits for these words is the composer's own: it is not sent a second time here.
    const send = whole && !running.abandoned && !running.settled && sendsOnStop(store.getState().listing?.send, reason, running.text);
    end(running);
    // After the last words are in the prompt and the button is back: the composer sends as its Send button would.
    if (send) sink?.send?.();
  }

  function receive(running: Running, item: SpeechTranscript) {
    if (running.ended) return;
    write(running, item.finalText);
    const over = item.state === "done" || item.state === "failed";
    const partial = over ? "" : item.partialText;
    if (!running.abandoned) running.sink?.interim(partial);
    if (shown(running)) store.update(state => withDictation(state, { partial }));
    if (item.state === "failed") tell(running, item.error);
  }

  async function follow(running: Running, id: string, at: string) {
    try {
      for await (const item of await api.listen({ expectedEpoch: at, id }, { signal: running.listening.signal })) {
        if (running.listening.signal.aborted) return;
        receive(running, item);
        if (item.state === "done" || item.state === "failed") break;
      }
    } catch { /* The stream closed. */ }
    // The host ended the dictation by itself, or the stream was lost: nothing more comes.
    if (!running.ended && !running.stopping) void finish(running, false, "ended");
  }

  async function begin(owner: string, sink: DictationSink) {
    const at = epoch;
    if (!at) return;
    // One use of the microphone at a time: a level that is shown gives it up.
    stopMonitor();
    const running: Running = { owner, sink, listening: new AbortController(), id: null, capture: null, written: 0, sequence: 0,
      stopping: false, abandoned: false, ended: false, told: false, loudest: 0, silence: undefined, text: "" };
    current = running; live.add(running);
    store.update(state => ({ ...state, asking: null, dictation: { status: "starting", owner, partial: "", microphone: null } }));
    let reply: { status: string; id: string | null };
    try { reply = await api.start({ expectedEpoch: at }, { timeoutMilliseconds: 20_000 }); }
    catch { reply = { status: "failed", id: null }; }
    const id = reply.status === "ok" ? reply.id : null;
    // Stopped while the host answered, or the host changed: the dictation it started is given back.
    if (running.stopping || running.ended || epoch !== at) {
      if (id) void api.stop({ expectedEpoch: at, id, cancel: true }, { timeoutMilliseconds: 30_000 }).catch(() => { /* The host is gone. */ });
      end(running);
      return;
    }
    if (!id) {
      end(running);
      // The model, or its engine, is not there: the setup of the composer offers to download it.
      if (reply.status === "model_missing") store.update(state => ({ ...state, asking: owner, missing: true }));
      else if (reply.status === "installing") store.update(state => ({ ...state, reveals: state.reveals + 1 }));
      // Turned off, or not of this window: the buttons follow what the host says now.
      else if (["disabled", "unsupported", "unavailable", "stale_epoch"].includes(reply.status)) void refresh();
      else notify({ message: dictationFailure("start"), intent: "danger" });
      return;
    }
    running.id = id;
    void follow(running, id, at);
    // The caret shows where the words will go.
    running.sink?.focus();
    running.capture = microphones.start({
      onReady(ready) {
        if (running.stopping || running.ended) return;
        if (shown(running)) store.update(state => withDictation(state, { status: "recording", microphone: ready.label }));
        opened(ready);
        // A microphone that gives nothing at all (a virtual device with no source, one that is muted) is said once, and
        // the dictation goes on. The wait is long: one with noise removal is silent too until somebody speaks.
        running.silence = setTimeout(() => {
          // The wait is over: the clock of the page need not say so again.
          if (running.stopping || running.ended || !noSound([running.loudest], warnAfter, warnAfter)) return;
          notify(silenceNotice(ready.label ? shortMicrophoneName(ready.label) : null, running.owner));
        }, warnAfter);
      },
      onChunk(chunk) {
        if (running.stopping || running.ended) return;
        if (chunk.peak > running.loudest) running.loudest = chunk.peak;
        if (shown(running)) level(chunk.level, running.owner);
        // Not awaited: the host puts the chunks back in order. One it refuses is the last one sent.
        const refused = () => { if (!running.stopping && !running.ended) void finish(running, false, "ended"); };
        api.append({ expectedEpoch: at, id, sequence: running.sequence++, base64: chunk.base64 }, { timeoutMilliseconds: 15_000 })
          .then(result => { if (result.status !== "ok") refused(); }, refused);
      },
      onError(failure: CaptureFailure) {
        if (running.ended) return;
        tell(running, failure === "microphone" ? "microphone" : "start");
        void finish(running, true, "ended");
      },
    }, chosenDevice(store.getState().microphone.device));
    // A capture that failed at once has already ended the dictation.
    if (running.stopping || running.ended) { running.capture.stop(); running.capture = null; }
  }

  async function refresh() {
    const at = epoch, turn = connection;
    if (!at) return;
    let listing: SpeechListingResponse | null = null;
    try { listing = await api.read({ expectedEpoch: at }, { timeoutMilliseconds: 20_000 }); } catch { /* Not read: nothing to dictate with. */ }
    if (epoch === at && connection === turn) store.update(state => ({ ...state, listing }));
  }

  async function watch(at: string, signal: AbortSignal) {
    let first = true;
    try {
      for await (const item of await api.watch({ expectedEpoch: at }, { signal })) {
        if (signal.aborted) return;
        const install = installView(item), before = store.getState().install;
        store.update(state => withInstall(state, install));
        // A model that has just been installed, or removed by a download that failed: the list is read again.
        if (!first && before && before.state !== install.state && ["done", "failed", "canceled"].includes(install.state)) {
          if (install.state === "done") store.update(state => state.missing ? { ...state, missing: false } : state);
          void refresh();
        }
        first = false;
      }
    } catch { /* The stream closed with the host: the download, if any, goes on there. */ }
  }

  // Ends whatever dictation there is, keeping what it heard, before the microphone is given to something else.
  const endDictations = () => { for (const running of [...live]) void finish(running, false, "taken"); };

  async function install(model: string | null): Promise<string> {
    if (!epoch) return "unavailable";
    let status = "failed";
    try { status = (await api.install({ expectedEpoch: epoch, model }, { timeoutMilliseconds: 30_000 })).status; } catch { /* Told below. */ }
    if (status === "busy") store.update(state => ({ ...state, reveals: state.reveals + 1 }));
    else if (status !== "ok") notify({ message: status === "unsupported" ? "Speech to prompt is not available on this system." : "The speech model could not be installed.", intent: "danger" });
    return status;
  }

  return {
    getState: store.getState as () => SpeechState,
    subscribe: store.subscribe,
    /** Follows the level of a microphone, about ten times a second while it is open: not a state of React. `owner` is the composer that dictates, or who asked for the level. */
    subscribeLevel(listener: (level: number, owner: string | null) => void) { levels.add(listener); return () => { levels.delete(listener); }; },

    /**
     * Follows the host of an epoch: reads the settings and the models, and follows the download. Returns what
     * ends it; connecting again (another epoch, the second run of an effect) starts from nothing.
     */
    connect(next: string | null): () => void {
      const turn = ++connection;
      for (const running of [...live]) void finish(running, true, "abandoned");
      epoch = next;
      store.update(withoutHost);
      if (!next) return () => { };
      const abort = new AbortController();
      void refresh();
      void watch(next, abort.signal);
      return () => {
        abort.abort();
        if (connection !== turn) return;
        for (const running of [...live]) void finish(running, true, "abandoned");
        stopMonitor();
        epoch = null;
        store.update(withoutHost);
      };
    },
    refresh,
    /** Takes what a settings page was told by the host, so the buttons follow it at once. */
    setListing(listing: SpeechListingResponse) { if (epoch) store.update(state => ({ ...state, listing })); },

    /**
     * The press of the microphone of a composer: it stops the dictation that composer has, starts one, or, until
     * the user has said which microphone to use, shows the setup beside the button.
     */
    toggle(owner: string, sink: DictationSink) {
      const running = current;
      if (running && !running.ended) {
        if (running.owner === owner) { void finish(running, false, "button"); return; }
        // One dictation for the window: the other composer keeps what it heard.
        void finish(running, false, "taken");
      }
      if (!store.getState().microphone.confirmed) { store.update(state => ({ ...state, asking: owner })); return; }
      void begin(owner, sink);
    },
    /**
     * A send was asked of a composer. While it dictates, the dictation is stopped first and what this returns
     * settles once its last words are in the prompt: the composer then sends what the prompt holds. It settles
     * as well when the stop fails, and after ten seconds, from when nothing more is written. Null when the
     * composer does not dictate: there is nothing to wait for.
     */
    finishForSend(owner: string): Promise<void> | null {
      const running = [...live].find(candidate => candidate.owner === owner && !candidate.ended && !candidate.abandoned);
      if (!running) return null;
      if (!running.settled) {
        let resolve = () => { };
        running.settled = { ended: new Promise<void>(done => { resolve = done; }), resolve };
      }
      void finish(running, false, "send");
      const settled = running.settled.ended;
      return new Promise<void>(done => {
        const late = setTimeout(() => {
          // The host does not answer: the prompt goes as it is, and what comes after is not written into the next one.
          if (!running.ended) { running.abandoned = true; running.sink?.interim(""); }
          done();
        }, sendWait);
        void settled.then(() => { clearTimeout(late); done(); });
      });
    },
    /** Stops the dictation of a composer and writes its last words, without sending anything: what Escape does. */
    stop(owner: string) { if (current?.owner === owner) void finish(current, false, "escape"); },
    /** A composer can no longer take text (sent, hidden, read-only, gone): its dictation ends and writes nothing more. */
    abandon(owner: string) {
      for (const running of [...live]) if (running.owner === owner) void finish(running, true, "abandoned");
      stopMonitor(owner);
      store.update(state => state.asking === owner ? { ...state, asking: null } : state);
    },

    /** The microphone button of a composer is there, until what this returns is called: its setup can be shown. */
    attach(owner: string): () => void { present.add(owner); return () => { present.delete(owner); }; },
    /** Shows the setup of a composer, when its button is there; false when there is none to show it beside. */
    proposeSetup(owner: string | undefined): boolean {
      if (!owner || !present.has(owner)) return false;
      endDictations();
      store.update(state => ({ ...state, asking: owner }));
      return true;
    },
    /** Closes the setup of a composer and keeps nothing of it; the microphone it showed the level of is released at once. */
    dismissAsk(owner: string) {
      stopMonitor(owner);
      store.update(state => state.asking === owner ? { ...state, asking: null } : state);
    },
    /** The answer of the setup: the microphone is kept, and the dictation starts with the same press. */
    confirmAndStart(owner: string, device: MicrophoneChoice, sink: DictationSink) {
      keepMicrophone(device, true);
      store.update(state => state.asking === owner ? { ...state, asking: null } : state);
      void begin(owner, sink);
    },
    /** The answer of the setup when the model is missing: the microphone is kept, and the download starts. */
    confirmAndDownload(owner: string, device: MicrophoneChoice) {
      keepMicrophone(device, true);
      stopMonitor(owner);
      store.update(state => state.asking === owner ? { ...state, asking: null } : state);
      void install(null);
    },

    /** Keeps the microphone chosen in the settings: a choice made there needs no other question. */
    setMicrophone(device: MicrophoneChoice) { keepMicrophone(device, true); },
    /** Reads the audio inputs; with `probe`, a microphone is opened for a moment when the browser does not name them yet. */
    listDevices,
    /** Reads the audio inputs again when one comes or goes, until what this returns is called. */
    watchDevices(): () => void {
      if (watchers++ === 0) unwatch = microphones.watch(() => { void listDevices(true); });
      let done = false;
      return () => { if (done) return; done = true; if (--watchers === 0) { unwatch?.(); unwatch = null; } };
    },
    /**
     * Opens a microphone only to show its level, for who asks (`key`: the composer of a setup, or the settings).
     * A dictation and another level give the microphone up first: one use of it at a time.
     */
    startMonitor(key: string, device: MicrophoneChoice) {
      stopMonitor();
      endDictations();
      const monitor: Monitoring = { key, capture: null, ended: false, loudest: 0, silence: undefined };
      monitoring = monitor;
      store.update(state => ({ ...state, monitor: { key, open: false, silent: false, label: null } }));
      // Asked when the wait is over, and again at the first sound after it.
      const judge = () => { if (!monitor.ended) store.update(state => withMonitor(state, { silent: noSound([monitor.loudest], silenceAfter, silenceAfter) })); };
      monitor.capture = microphones.start({
        onReady(ready) {
          if (monitor.ended) return;
          store.update(state => withMonitor(state, { open: true, label: ready.label }));
          monitor.silence = setTimeout(judge, silenceAfter);
          opened(ready);
        },
        onChunk(chunk) {
          if (monitor.ended) return;
          level(chunk.level, key);
          if (chunk.peak > monitor.loudest) monitor.loudest = chunk.peak;
          // The first sound takes back what was said of a microphone that gave none.
          if (chunk.peak > silencePeak && store.getState().monitor?.silent) judge();
        },
        onError(failure: CaptureFailure) {
          if (monitor.ended) return;
          stopMonitor(key);
          notify({ message: dictationFailure(failure === "microphone" ? "microphone" : "start"), intent: "danger" });
        },
      }, chosenDevice(device));
      if (monitor.ended) { monitor.capture.stop(); monitor.capture = null; }
    },
    stopMonitor,

    /**
     * Starts the download of a model, the chosen one when none is named, and gives the status of the host, or
     * `failed`. The host owns the download and tells how it goes. A refusal is told here: another download that
     * runs is shown again, anything else is said.
     */
    install,
    cancelInstall() {
      if (epoch) void api.cancelInstall({ expectedEpoch: epoch }, { timeoutMilliseconds: 30_000 }).catch(() => { /* The download ends with the host. */ });
    },
  };
}

export type Speech = ReturnType<typeof createSpeech>;

/** Speech to prompt of the window, for its composers; null where there is none (a test of a composer alone). */
export const SpeechContext = createContext<Speech | null>(null);
