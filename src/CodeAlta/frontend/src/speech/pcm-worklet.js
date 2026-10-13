// The part of the capture that runs on the audio thread. It only gathers what the microphone gives, a tenth
// of a second at a time, and hands it to the page, which changes its rate and encodes it (pcm.ts).
// This is a file of the build of its own, which vite.config.ts copies beside the entry document as it is: the
// content security policy of the page allows no script from a blob or from text. It imports nothing.
class PcmCollector extends AudioWorkletProcessor {
  constructor() {
    super();
    // sampleRate is the rate of the audio context, a global of the worklet.
    this.size = Math.max(128, Math.round(sampleRate / 10));
    this.block = new Float32Array(this.size);
    this.filled = 0;
  }

  process(inputs) {
    // The first channel of the first input: the microphone is asked for one channel.
    const channel = inputs[0] && inputs[0][0];
    if (!channel) return true;
    let offset = 0;
    while (offset < channel.length) {
      const take = Math.min(this.size - this.filled, channel.length - offset);
      this.block.set(channel.subarray(offset, offset + take), this.filled);
      this.filled += take;
      offset += take;
      if (this.filled === this.size) {
        // The buffer goes to the page with the message, so a new one takes its place.
        this.port.postMessage(this.block, [this.block.buffer]);
        this.block = new Float32Array(this.size);
        this.filled = 0;
      }
    }
    return true;
  }
}

registerProcessor("codealta-pcm", PcmCollector);
