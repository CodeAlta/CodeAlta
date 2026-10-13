import type { SpeechInstallState, SpeechListingResponse, SpeechModelInfo } from "#neoastra";
import type { MessageKey } from "../localization";

// What the window knows of speech to prompt: the settings and the models, the download of a model, and the
// one dictation of the window. It changes often (a level, a percentage), so nothing of it is state of `App`:
// a component follows the one fact it shows.

export type DictationStatus = "idle" | "starting" | "recording" | "finishing";
export type InstallPhase = "idle" | "downloading" | "installing" | "done" | "failed" | "canceled";

/** The download of a model as the host tells it, with its sizes as numbers. */
export type InstallView = Readonly<{ state: InstallPhase; modelId: string | null; modelName: string | null;
  /** Bytes received and expected; the total is 0 until it is known. */
  received: number; total: number; error: string | null }>;

/** The one dictation of the window: where it stands, and the composer it writes into. */
export type DictationView = Readonly<{ status: DictationStatus; owner: string | null;
  /** What is being spoken: shown greyed at the caret, and not in the prompt yet. */
  partial: string }>;

export type SpeechState = Readonly<{
  /** What the host last told of the settings and the models; null until it is read, and when it could not be. */
  listing: SpeechListingResponse | null;
  /** Null until the host has told where the download stands. */
  install: InstallView | null;
  dictation: DictationView;
  /** The composer whose button asks whether to download the model. */
  asking: string | null;
  /** Counts the times the progress of the download was asked for again, after its notice was closed. */
  reveals: number;
}>;

export const idleDictation: DictationView = Object.freeze({ status: "idle", owner: null, partial: "" });
export const initialSpeechState: SpeechState = Object.freeze({ listing: null, install: null, dictation: idleDictation, asking: null, reveals: 0 });

const phases: readonly string[] = ["idle", "downloading", "installing", "done", "failed", "canceled"];
// An Int64 of the host is a string of digits.
const bytes = (value: string | null | undefined) => {
  if (!value || !/^\d+$/.test(value)) return 0;
  const number = Number(value);
  return Number.isFinite(number) ? number : 0;
};

/** What the host says of the download; a state the page does not know reads as no download. */
export function installView(wire: SpeechInstallState): InstallView {
  return { state: (phases.includes(wire.state) ? wire.state : "idle") as InstallPhase, modelId: wire.modelId, modelName: wire.modelName,
    received: bytes(wire.receivedBytes), total: bytes(wire.totalBytes), error: wire.error };
}

/** The size of a model as a number. */
export const modelBytes = (model: Pick<SpeechModelInfo, "bytes">) => bytes(model.bytes);

/** Whether a download is under way. */
export const installing = (install: InstallView | null) => install?.state === "downloading" || install?.state === "installing";

/** How far the download is, from 0 to 1, or null while its size is not known. */
export function installProgress(install: InstallView | null): number | null {
  if (!install || install.state !== "downloading" || install.total <= 0) return null;
  return Math.min(1, Math.max(0, install.received / install.total));
}

/** Whether dictation exists in this window and is turned on. */
export function speechEnabled(state: SpeechState): boolean {
  return state.listing?.status === "ok" && state.listing.supported && state.listing.enabled;
}

/** The model the settings choose, when the host lists it. */
export function chosenModel(listing: SpeechListingResponse | null): SpeechModelInfo | null {
  return listing?.models.find(model => model.id === listing.modelId) ?? null;
}

/** The languages a model hears, as the host classes them; null when it does not say. */
export function modelLanguages(model: Pick<SpeechModelInfo, "languages">): MessageKey | null {
  return model.languages === "english" ? "English" : model.languages === "european" ? "25 languages" : model.languages === "multilingual" ? "Multilingual" : null;
}

/** What the microphone button of a composer shows; `hidden` where there is nothing to dictate with. */
export type MicView = "hidden" | "idle" | "starting" | "recording" | "finishing" | "installing";

export function micView(state: SpeechState, owner: string, microphone: boolean): MicView {
  if (!microphone || !speechEnabled(state)) return "hidden";
  if (state.dictation.owner === owner && state.dictation.status !== "idle") return state.dictation.status;
  // The download of the chosen model: the next click would only be told to wait for it.
  return installing(state.install) && state.install!.modelId === state.listing!.modelId ? "installing" : "idle";
}

/** What the notice of the download shows after a change of its state; null leaves it as it is. */
export type InstallNotice =
  | Readonly<{ kind: "progress"; phase: "downloading" | "installing"; model: string; received: number; total: number }>
  | Readonly<{ kind: "ready" }>
  | Readonly<{ kind: "failed"; message: MessageKey }>
  | Readonly<{ kind: "dismiss" }>;

const failures: Readonly<Record<string, MessageKey>> = Object.freeze({
  network: "The speech model could not be downloaded: check the network connection.",
  not_found: "The speech model was not found at its address.",
  checksum: "The downloaded speech model is damaged: download it again.",
  unsupported_model: "This speech model is not supported.",
  busy_elsewhere: "Another CodeAlta is downloading this speech model.",
  disk: "The speech model could not be written to the disk.",
  unsupported_platform: "Speech to prompt is not available on this system.",
  load_failed: "The speech model could not be loaded.",
});

/** What to say of a download that failed. */
export const installFailure = (error: string | null): MessageKey => failures[error ?? ""] ?? "The speech model could not be installed.";

/**
 * The notice for the state the host tells, given the one before it. The first state is a replay of what the
 * host holds, possibly the end of a download of an hour ago: only a download that runs is shown then. An end
 * (ready, failed, canceled) is told once, when the page sees the state change.
 */
export function installNotice(previous: InstallView | null, current: InstallView): InstallNotice | null {
  if (current.state === "downloading" || current.state === "installing")
    return { kind: "progress", phase: current.state, model: current.modelName ?? current.modelId ?? "", received: current.received, total: current.total };
  if (!previous || previous.state === current.state) return null;
  if (current.state === "done") return { kind: "ready" };
  if (current.state === "failed") return { kind: "failed", message: installFailure(current.error) };
  // Canceled, or back to nothing: a notice of progress that is still shown goes away.
  return installing(previous) ? { kind: "dismiss" } : null;
}

/** What to say of a dictation that ended by itself. */
export function dictationFailure(error: string | null): MessageKey {
  return error === "microphone" ? "The microphone is not available. Check the privacy settings of the system."
    : error === "load_failed" ? "The speech model could not be loaded."
    : error === "start" ? "The dictation could not start."
    : "The dictation stopped: the speech engine failed.";
}

/** A store of the state: it is replaced, never changed in place, and tells its listeners when it is. */
export function createSpeechStore(initial: SpeechState = initialSpeechState) {
  let state = initial;
  const listeners = new Set<() => void>();
  return {
    getState: () => state,
    subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
    /** Replaces the state with what `change` gives; the same object changes nothing and tells nobody. */
    update(change: (state: SpeechState) => SpeechState) {
      const next = change(state);
      if (next === state) return;
      state = next;
      for (const listener of [...listeners]) listener();
    },
  };
}
export type SpeechStore = ReturnType<typeof createSpeechStore>;

/** The state with other facts of its dictation; the same state when they are the ones it has. */
export function withDictation(state: SpeechState, change: Partial<DictationView>): SpeechState {
  const dictation = { ...state.dictation, ...change };
  return dictation.status === state.dictation.status && dictation.owner === state.dictation.owner && dictation.partial === state.dictation.partial
    ? state : { ...state, dictation };
}

/** The state with what the host tells of the download; the same state when nothing of it changed. */
export function withInstall(state: SpeechState, install: InstallView): SpeechState {
  const before = state.install;
  return before && before.state === install.state && before.modelId === install.modelId && before.modelName === install.modelName
    && before.received === install.received && before.total === install.total && before.error === install.error ? state : { ...state, install };
}
