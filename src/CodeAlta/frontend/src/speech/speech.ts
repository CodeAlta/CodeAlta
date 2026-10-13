import { createContext } from "react";
import type { speech, SpeechListingResponse, SpeechTranscript } from "#neoastra";
import type { MessageKey } from "../localization";
import type { Capture, CaptureFailure, StartCapture } from "./capture";
import { createSpeechStore, dictationFailure, idleDictation, initialSpeechState, installView, withDictation, withInstall, type SpeechState } from "./speechStore";
import { unwritten } from "./spokenText";

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
}>;

export type SpeechDependencies = Readonly<{
  api: SpeechApi;
  capture: StartCapture;
  /** Tells the user what went wrong with a dictation. */
  notify: (message: MessageKey) => void;
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
};

/**
 * Speech to prompt of one window: what the host tells of the models and of a download, and the one dictation.
 * It is made once by the window and given to the composers through `SpeechContext`.
 */
export function createSpeech({ api, capture, notify }: SpeechDependencies) {
  const store = createSpeechStore();
  const levels = new Set<(level: number, owner: string | null) => void>();
  const live = new Set<Running>();
  let epoch: string | null = null;
  let current: Running | null = null;
  let connection = 0;

  const level = (value: number, owner: string | null) => { for (const listener of [...levels]) listener(value, owner); };
  const shown = (running: Running) => current === running;

  function tell(running: Running, error: string | null) {
    if (running.told || running.abandoned) return;
    running.told = true;
    notify(dictationFailure(error));
  }

  function write(running: Running, finalText: string) {
    const piece = unwritten(finalText, running.written);
    if (!piece || !running.sink || running.abandoned) return;
    // Counted before it is written: what the write causes (a change of the prompt, a send) never writes it again.
    running.written = finalText.length;
    running.sink.write(piece);
  }

  function end(running: Running) {
    if (running.ended) return;
    running.ended = true;
    live.delete(running);
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

  /** Ends a dictation with the host: its last words are written, unless `cancel` drops them. */
  async function finish(running: Running, cancel: boolean) {
    if (cancel && !running.abandoned) { running.abandoned = true; running.sink?.interim(""); }
    if (running.ended || running.stopping) return;
    running.stopping = true;
    running.capture?.stop(); running.capture = null;
    if (shown(running)) { level(0, running.owner); store.update(state => withDictation(state, { status: "finishing" })); }
    const id = running.id, at = epoch;
    // Still starting: the answer of the host ends it.
    if (!id) return;
    try {
      const reply = await api.stop({ expectedEpoch: at, id, cancel }, { timeoutMilliseconds: 30_000 });
      if (reply.status !== "unknown") write(running, reply.text);
      if (reply.status === "failed") tell(running, reply.error);
    } catch { /* The host is gone: what was heard is what was written. */ }
    end(running);
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
    if (!running.ended && !running.stopping) void finish(running, false);
  }

  async function begin(owner: string, sink: DictationSink) {
    const at = epoch;
    if (!at) return;
    const running: Running = { owner, sink, listening: new AbortController(), id: null, capture: null, written: 0, sequence: 0,
      stopping: false, abandoned: false, ended: false, told: false };
    current = running; live.add(running);
    store.update(state => ({ ...state, asking: null, dictation: { status: "starting", owner, partial: "" } }));
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
      if (reply.status === "model_missing") store.update(state => ({ ...state, asking: owner }));
      else if (reply.status === "installing") store.update(state => ({ ...state, reveals: state.reveals + 1 }));
      // Turned off, or not of this window: the buttons follow what the host says now.
      else if (["disabled", "unsupported", "unavailable", "stale_epoch"].includes(reply.status)) void refresh();
      else notify(dictationFailure("start"));
      return;
    }
    running.id = id;
    void follow(running, id, at);
    // The caret shows where the words will go.
    running.sink?.focus();
    running.capture = capture({
      onReady() { if (!running.stopping && !running.ended && shown(running)) store.update(state => withDictation(state, { status: "recording" })); },
      onChunk(chunk) {
        if (running.stopping || running.ended) return;
        if (shown(running)) level(chunk.level, running.owner);
        // Not awaited: the host puts the chunks back in order. One it refuses is the last one sent.
        const refused = () => { if (!running.stopping && !running.ended) void finish(running, false); };
        api.append({ expectedEpoch: at, id, sequence: running.sequence++, base64: chunk.base64 }, { timeoutMilliseconds: 15_000 })
          .then(result => { if (result.status !== "ok") refused(); }, refused);
      },
      onError(failure: CaptureFailure) {
        if (running.ended) return;
        tell(running, failure === "microphone" ? "microphone" : "start");
        void finish(running, true);
      },
    });
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
        if (!first && before && before.state !== install.state && ["done", "failed", "canceled"].includes(install.state)) void refresh();
        first = false;
      }
    } catch { /* The stream closed with the host: the download, if any, goes on there. */ }
  }

  return {
    getState: store.getState as () => SpeechState,
    subscribe: store.subscribe,
    /** Follows the level of the voice, about ten times a second while a dictation records: not a state of React. */
    subscribeLevel(listener: (level: number, owner: string | null) => void) { levels.add(listener); return () => { levels.delete(listener); }; },

    /**
     * Follows the host of an epoch: reads the settings and the models, and follows the download. Returns what
     * ends it; connecting again (another epoch, the second run of an effect) starts from nothing.
     */
    connect(next: string | null): () => void {
      const turn = ++connection;
      for (const running of [...live]) void finish(running, true);
      epoch = next;
      store.update(() => initialSpeechState);
      if (!next) return () => { };
      const abort = new AbortController();
      void refresh();
      void watch(next, abort.signal);
      return () => {
        abort.abort();
        if (connection !== turn) return;
        for (const running of [...live]) void finish(running, true);
        epoch = null;
        store.update(() => initialSpeechState);
      };
    },
    refresh,
    /** Takes what a settings page was told by the host, so the buttons follow it at once. */
    setListing(listing: SpeechListingResponse) { if (epoch) store.update(state => ({ ...state, listing })); },

    /** Starts a dictation into the prompt of a composer, or stops the one that composer has. */
    toggle(owner: string, sink: DictationSink) {
      const running = current;
      if (running && !running.ended) {
        if (running.owner === owner) { void finish(running, false); return; }
        // One dictation for the window: the other composer keeps what it heard.
        void finish(running, false);
      }
      void begin(owner, sink);
    },
    /** Stops the dictation of a composer and writes its last words. */
    stop(owner: string) { if (current?.owner === owner) void finish(current, false); },
    /** A composer can no longer take text (sent, hidden, read-only, gone): its dictation ends and writes nothing more. */
    abandon(owner: string) {
      for (const running of [...live]) if (running.owner === owner) void finish(running, true);
      store.update(state => state.asking === owner ? { ...state, asking: null } : state);
    },
    /** Closes the question of the first use. */
    dismissAsk(owner: string) { store.update(state => state.asking === owner ? { ...state, asking: null } : state); },

    /**
     * Starts the download of a model, the chosen one when none is named, and gives the status of the host, or
     * `failed`. The host owns the download and tells how it goes. A refusal is told here: another download that
     * runs is shown again, anything else is said.
     */
    async install(model: string | null): Promise<string> {
      if (!epoch) return "unavailable";
      let status = "failed";
      try { status = (await api.install({ expectedEpoch: epoch, model }, { timeoutMilliseconds: 30_000 })).status; } catch { /* Told below. */ }
      if (status === "busy") store.update(state => ({ ...state, reveals: state.reveals + 1 }));
      else if (status !== "ok") notify(status === "unsupported" ? "Speech to prompt is not available on this system." : "The speech model could not be installed.");
      return status;
    },
    cancelInstall() {
      if (epoch) void api.cancelInstall({ expectedEpoch: epoch }, { timeoutMilliseconds: 30_000 }).catch(() => { /* The download ends with the host. */ });
    },
  };
}

export type Speech = ReturnType<typeof createSpeech>;

/** Speech to prompt of the window, for its composers; null where there is none (a test of a composer alone). */
export const SpeechContext = createContext<Speech | null>(null);
