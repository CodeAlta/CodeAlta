// Test-owned mount of the production capture, for a browser started with microphones of its own making
// (`--use-fake-device-for-media-stream`): the real `getUserMedia`, device list, audio context and worklet file, no real sound.
import { microphoneAvailable, systemMicrophones, type Capture, type CaptureReady } from "./capture";
import type { InputDevice, MicrophoneChoice } from "./microphones";

const streams: MediaStream[] = [], contexts: AudioContext[] = [];
// What the capture opens is kept, to see that it closes it.
const open = navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);
navigator.mediaDevices.getUserMedia = async constraints => { fixture.constraints.push(constraints); const stream = await open(constraints); streams.push(stream); return stream; };
const Context = window.AudioContext;
window.AudioContext = class extends Context { constructor(options?: AudioContextOptions) { super(options); contexts.push(this); } };

let capture: Capture | null = null;
const fixture = {
  available: microphoneAvailable(), ready: 0, errors: [] as string[], constraints: [] as (MediaStreamConstraints | undefined)[],
  chunks: [] as { bytes: number; level: number; peak: number }[],
  /** What each capture said it opened. */
  opened: [] as CaptureReady[],
  devices: undefined as readonly InputDevice[] | null | undefined, changes: 0,
  start(device: MicrophoneChoice = null) {
    capture = systemMicrophones.start({ onReady: ready => { fixture.ready++; fixture.opened.push(ready); }, onError: failure => { fixture.errors.push(failure); },
      onChunk: chunk => { fixture.chunks.push({ bytes: atob(chunk.base64).length, level: chunk.level, peak: chunk.peak }); } }, device);
  },
  stop() { capture?.stop(); },
  async list(probe: boolean) { fixture.devices = await systemMicrophones.list(probe); return fixture.devices; },
  watch() { return systemMicrophones.watch(() => { fixture.changes++; }); },
  /** What is still open: tracks that are live, audio contexts that are not closed. */
  held: () => ({ tracks: streams.flatMap(stream => stream.getTracks()).filter(track => track.readyState !== "ended").length,
    contexts: contexts.filter(context => context.state !== "closed").length, rates: contexts.map(context => context.sampleRate) }),
};
Object.assign(window, { captureFixture: fixture });
