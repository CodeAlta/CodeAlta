import { createChunkEncoder, type AudioChunk } from "./pcm";
import { workletFile, workletProcessor } from "./workletFile";

// The microphone of the page. A dictation is given this through `StartCapture`, so a test gives it chunks of
// its own: a headless browser has no microphone.

/** Why a capture ended by itself: the microphone could not be had, or the audio of the page failed. */
export type CaptureFailure = "microphone" | "failed";

export type CaptureHandlers = Readonly<{
  /** The microphone is open and its audio flows. */
  onReady: () => void;
  onChunk: (chunk: AudioChunk) => void;
  /** The capture is over and has released what it held. */
  onError: (failure: CaptureFailure) => void;
}>;

/** A capture that runs: stopping it closes the microphone, and can be asked more than once. */
export type Capture = Readonly<{ stop: () => void }>;

/**
 * Opens the microphone. It answers at once: opening goes on behind (the system may ask the user first), and
 * a capture stopped meanwhile releases the microphone as soon as it gets it.
 */
export type StartCapture = (handlers: CaptureHandlers) => Capture;

/** Whether the page can ask for a microphone at all. */
export const microphoneAvailable = () => typeof navigator !== "undefined" && typeof navigator.mediaDevices?.getUserMedia === "function"
  && typeof AudioWorkletNode === "function";

// The file of the worklet, which the build emits beside the entry document of the application: a script of
// the origin, as the content security policy of the page requires (no blob, no inline text).
const workletAddress = () => new URL(workletFile, document.baseURI).href;

/** The microphone of the system, through `getUserMedia` and an audio worklet. */
export const startMicrophone: StartCapture = handlers => {
  let stopped = false;
  let stream: MediaStream | null = null, context: AudioContext | null = null, node: AudioWorkletNode | null = null, source: MediaStreamAudioSourceNode | null = null;
  const release = () => {
    if (node) { node.port.onmessage = null; node.disconnect(); node = null; }
    source?.disconnect(); source = null;
    for (const track of stream?.getTracks() ?? []) track.stop();
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
    try {
      // The noise suppression and the gain of the system help a model; echo cancellation is for calls and dulls the voice.
      stream = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, noiseSuppression: true, autoGainControl: true, echoCancellation: false } });
    } catch {
      // Refused, no microphone, one that another application holds, or a page that may not ask: one thing to the user.
      fail("microphone"); return;
    }
    if (stopped) { release(); return; }
    try {
      context = new AudioContext();
      await context.audioWorklet.addModule(workletAddress());
      if (stopped) { release(); return; }
      // A context made outside a gesture of the user can start suspended.
      if (context.state === "suspended") await context.resume();
      if (stopped) { release(); return; }
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
      handlers.onReady();
    } catch {
      fail("failed");
    }
  })();
  return { stop };
};
