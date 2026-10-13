import { deviceMissing, resolveMicrophone, type InputDevice, type MicrophoneChoice } from "./microphones";
import { createChunkEncoder, type AudioChunk } from "./pcm";
import { workletFile, workletProcessor } from "./workletFile";

// The microphones of the page. A dictation is given them through `Microphones`, so a test gives it devices
// and chunks of its own: a headless browser has no microphone.

/** Why a capture ended by itself: the microphone could not be had, or the audio of the page failed. */
export type CaptureFailure = "microphone" | "failed";

/** What a capture opened. */
export type CaptureReady = Readonly<{
  /** The name of the device that is heard, as the browser tells it; null when it does not. */
  label: string | null;
  /** The device that was asked for is not there: the microphone of the system is heard instead. */
  fallback: boolean;
  /** The device that was asked for, found again under another id by its label: the choice to keep from now on. */
  moved: InputDevice | null;
}>;

export type CaptureHandlers = Readonly<{
  /** The microphone is open and its audio flows. */
  onReady: (opened: CaptureReady) => void;
  onChunk: (chunk: AudioChunk) => void;
  /** The capture is over and has released what it held. */
  onError: (failure: CaptureFailure) => void;
}>;

/** A capture that runs: stopping it closes the microphone, and can be asked more than once. */
export type Capture = Readonly<{ stop: () => void }>;

/**
 * Opens a microphone: the device of a choice, or the one of the system for none. It answers at once: opening
 * goes on behind (the system may ask the user first), and a capture stopped meanwhile releases the microphone
 * as soon as it gets it.
 */
export type StartCapture = (handlers: CaptureHandlers, device?: MicrophoneChoice) => Capture;

/** The microphones of a page: opening one, listing them, and being told when one comes or goes. */
export type Microphones = Readonly<{
  start: StartCapture;
  /**
   * The audio inputs the browser lists, or null when it lists none with a name. A browser names them only
   * to a page that was given a microphone: with `probe`, one is opened for a moment to be told.
   */
  list: (probe: boolean) => Promise<readonly InputDevice[] | null>;
  /** Tells when a device is plugged in or taken away, until what it returns is called. */
  watch: (changed: () => void) => () => void;
}>;

/** Whether the page can ask for a microphone at all. */
export const microphoneAvailable = () => typeof navigator !== "undefined" && typeof navigator.mediaDevices?.getUserMedia === "function"
  && typeof AudioWorkletNode === "function";

// The file of the worklet, which the build emits beside the entry document of the application: a script of
// the origin, as the content security policy of the page requires (no blob, no inline text).
const workletAddress = () => new URL(workletFile, document.baseURI).href;

// The noise suppression and the gain of the system help a model; echo cancellation is for calls and dulls the voice.
const sound = { channelCount: 1, noiseSuppression: true, autoGainControl: true, echoCancellation: false } as const;
const errorName = (error: unknown) => error instanceof Error ? error.name : "";
const close = (stream: MediaStream | null) => { for (const track of stream?.getTracks() ?? []) track.stop(); };

async function inputDevices(): Promise<InputDevice[]> {
  return (await navigator.mediaDevices.enumerateDevices()).filter(device => device.kind === "audioinput").map(device => ({ id: device.deviceId, label: device.label }));
}
const named = (devices: readonly InputDevice[]) => devices.some(device => device.id !== "" && device.label !== "");

/** The microphones of the system, through `getUserMedia` and an audio worklet. */
export const startMicrophone: StartCapture = (handlers, device = null) => {
  let stopped = false;
  let stream: MediaStream | null = null, context: AudioContext | null = null, node: AudioWorkletNode | null = null, source: MediaStreamAudioSourceNode | null = null;
  // Every stream that was opened on the way, so none stays open whatever interrupts the opening.
  const opened = new Set<MediaStream>();
  const open = async (id: string | null) => {
    const next = await navigator.mediaDevices.getUserMedia({ audio: id === null ? sound : { ...sound, deviceId: { exact: id } } });
    if (stopped) close(next); else opened.add(next);
    return next;
  };
  const release = () => {
    if (node) { node.port.onmessage = null; node.disconnect(); node = null; }
    source?.disconnect(); source = null;
    for (const one of opened) close(one);
    opened.clear();
    stream = null;
    if (context && context.state !== "closed") void context.close().catch(() => { /* A context that is already closing. */ });
    context = null;
  };
  const stop = () => {
    if (stopped) return;
    stopped = true;
    window.removeEventListener("pagehide", stop);
    release();
  };
  const fail = (failure: CaptureFailure) => {
    if (stopped) return;
    stop();
    handlers.onError(failure);
  };
  // A page that goes away never leaves the microphone open.
  window.addEventListener("pagehide", stop);
  void (async () => {
    let fallback = false, moved: InputDevice | null = null;
    try {
      try { stream = await open(device?.id ?? null); }
      catch (error) {
        if (stopped || !device || !deviceMissing(errorName(error), true)) throw error;
        // The chosen device is not there. The microphone of the system is opened in its place; the browser then
        // names the devices, and the chosen one may be there under another id (it changes between runs).
        stream = await open(null);
        const found = stopped ? null : resolveMicrophone(await inputDevices().catch(() => []), { id: "", label: device.label });
        const again = found ? await open(found.id).catch(() => null) : null;
        if (again && found && !stopped) { opened.delete(stream); close(stream); stream = again; moved = found; }
        else fallback = true;
      }
    } catch {
      // Refused, no microphone, one that another application holds, or a page that may not ask: one thing to the user.
      fail("microphone"); return;
    }
    if (stopped) { release(); return; }
    try {
      context = new AudioContext();
      await context.audioWorklet.addModule(workletAddress());
      if (stopped) { release(); return; }
      // A context made outside a gesture of the user can start suspended. Asked to run and not waited for: where
      // the system holds it back, the capture can still be stopped, and gives its audio once it runs.
      if (context.state === "suspended") void context.resume().catch(() => { /* It stays suspended: no audio comes. */ });
      const encoder = createChunkEncoder(context.sampleRate);
      source = context.createMediaStreamSource(stream);
      node = new AudioWorkletNode(context, workletProcessor, { numberOfInputs: 1, numberOfOutputs: 1, outputChannelCount: [1] });
      node.port.onmessage = (event: MessageEvent<Float32Array>) => {
        if (stopped) return;
        for (const chunk of encoder.push(event.data)) handlers.onChunk(chunk);
      };
      // A microphone that is taken away (unplugged, permission withdrawn) ends its track.
      for (const track of stream.getAudioTracks()) track.addEventListener("ended", () => fail("microphone"), { once: true });
      source.connect(node);
      // The audio thread only runs what leads to the output: the worklet writes nothing there, so nothing is heard.
      node.connect(context.destination);
      handlers.onReady({ label: stream.getAudioTracks()[0]?.label || null, fallback, moved });
    } catch {
      fail("failed");
    }
  })();
  return { stop };
};

/** The microphones of the system. */
export const systemMicrophones: Microphones = Object.freeze({
  start: startMicrophone,
  async list(probe: boolean) {
    if (!microphoneAvailable() || typeof navigator.mediaDevices.enumerateDevices !== "function") return null;
    try {
      const devices = await inputDevices();
      if (named(devices) || !probe) return named(devices) ? devices : null;
      // Not named yet: a microphone is opened for the time of the question, and closed at once.
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      try { const told = await inputDevices(); return named(told) ? told : null; }
      finally { close(stream); }
    } catch { return null; }
  },
  watch(changed: () => void) {
    if (typeof navigator === "undefined" || !navigator.mediaDevices?.addEventListener) return () => { };
    navigator.mediaDevices.addEventListener("devicechange", changed);
    return () => navigator.mediaDevices.removeEventListener("devicechange", changed);
  },
});
