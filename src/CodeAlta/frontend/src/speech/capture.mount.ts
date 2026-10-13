// Test-owned mount of the production capture, for a browser started with a microphone of its own making
// (`--use-fake-device-for-media-stream`): the real `getUserMedia`, audio context and worklet file, no real sound.
import { microphoneAvailable, startMicrophone, type Capture } from "./capture";

const streams: MediaStream[] = [], contexts: AudioContext[] = [];
// What the capture opens is kept, to see that it closes it.
const open = navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);
navigator.mediaDevices.getUserMedia = async constraints => { fixture.constraints.push(constraints); const stream = await open(constraints); streams.push(stream); return stream; };
const Context = window.AudioContext;
window.AudioContext = class extends Context { constructor(options?: AudioContextOptions) { super(options); contexts.push(this); } };

let capture: Capture | null = null;
const fixture = {
  available: microphoneAvailable(), ready: 0, errors: [] as string[], constraints: [] as (MediaStreamConstraints | undefined)[],
  chunks: [] as { bytes: number; level: number }[],
  start() {
    capture = startMicrophone({ onReady: () => { fixture.ready++; }, onError: failure => { fixture.errors.push(failure); },
      onChunk: chunk => { fixture.chunks.push({ bytes: atob(chunk.base64).length, level: chunk.level }); } });
  },
  stop() { capture?.stop(); },
  /** What is still open: tracks that are live, audio contexts that are not closed. */
  held: () => ({ tracks: streams.flatMap(stream => stream.getTracks()).filter(track => track.readyState !== "ended").length,
    contexts: contexts.filter(context => context.state !== "closed").length, rates: contexts.map(context => context.sampleRate) }),
};
Object.assign(window, { captureFixture: fixture });
