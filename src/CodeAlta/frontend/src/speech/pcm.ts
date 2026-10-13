// What the microphone gives (blocks of floating-point samples at the rate of the system) becomes what the
// host takes: 16 kHz mono signed 16-bit little-endian samples, a tenth of a second at a time, in Base64.

/** The sample rate of the audio the host takes. */
export const speechSampleRate = 16_000;
/** The samples of one chunk: a tenth of a second. */
export const chunkSamples = 1_600;

/**
 * One chunk of audio for the host, how loud it is from 0 to 1 for a meter, and its loudest sample (of 1),
 * which tells a microphone that gives nothing from the quiet of a room.
 */
export type AudioChunk = Readonly<{ base64: string; level: number; peak: number }>;

/**
 * Changes the sample rate of a stream given block by block. Each sample it gives is the mean of the part of
 * the source it covers, which also keeps what is above the new rate from folding into the voice; what a
 * block leaves unfinished waits for the next one.
 */
export function createResampler(sourceRate: number, targetRate = speechSampleRate): { push(input: Float32Array): Float32Array } {
  if (!(sourceRate > 0) || !(targetRate > 0) || !Number.isFinite(sourceRate) || !Number.isFinite(targetRate)) throw new RangeError("A sample rate is a positive number.");
  // The source samples one sample of the result covers.
  const span = sourceRate / targetRate;
  let covered = 0, sum = 0;
  return {
    push(input) {
      const output = new Float32Array(Math.ceil((covered + input.length) / span) + 1);
      let count = 0;
      for (let index = 0; index < input.length; index++) {
        const sample = input[index];
        let rest = 1;
        while (covered + rest >= span) {
          const part = span - covered;
          output[count++] = (sum + sample * part) / span;
          sum = 0; covered = 0; rest -= part;
        }
        sum += sample * rest; covered += rest;
      }
      return output.subarray(0, count);
    },
  };
}

/** Cuts a stream given block by block into pieces of one size; the samples left over wait for the next block. */
export function createChunker(size = chunkSamples): { push(samples: Float32Array): Float32Array[] } {
  if (!Number.isInteger(size) || size <= 0) throw new RangeError("The size of a chunk is a positive whole number.");
  let pending = new Float32Array(size), filled = 0;
  return {
    push(samples) {
      const chunks: Float32Array[] = [];
      let offset = 0;
      while (offset < samples.length) {
        const take = Math.min(size - filled, samples.length - offset);
        pending.set(samples.subarray(offset, offset + take), filled);
        filled += take; offset += take;
        if (filled === size) { chunks.push(pending); pending = new Float32Array(size); filled = 0; }
      }
      return chunks;
    },
  };
}

/** Samples from -1 to 1 as signed 16-bit integers; what is louder than the scale is cut at its ends. */
export function toInt16(samples: Float32Array): Int16Array {
  const output = new Int16Array(samples.length);
  for (let index = 0; index < samples.length; index++) {
    const sample = samples[index];
    // A sample that is not a number is silence.
    const value = sample >= 1 ? 1 : sample <= -1 ? -1 : sample === sample ? sample : 0;
    output[index] = Math.round(value < 0 ? value * 0x8000 : value * 0x7fff);
  }
  return output;
}

/** 16-bit samples as Base64 of their bytes, the low byte first, whatever the byte order of the computer. */
export function int16Base64(samples: Int16Array): string {
  const bytes = new Uint8Array(samples.length * 2), view = new DataView(bytes.buffer);
  for (let index = 0; index < samples.length; index++) view.setInt16(index * 2, samples[index], true);
  let binary = "";
  for (let offset = 0; offset < bytes.length; offset += 0x8000) binary += String.fromCharCode(...bytes.subarray(offset, offset + 0x8000));
  return btoa(binary);
}

/**
 * How loud samples are, from 0 (silence) to 1: their root mean square on a scale of decibels, where a voice
 * at a normal distance sits in the upper half and the noise of a quiet room near the bottom.
 */
export function voiceLevel(samples: Float32Array): number {
  if (samples.length === 0) return 0;
  let squares = 0;
  for (let index = 0; index < samples.length; index++) squares += samples[index] * samples[index];
  const rms = Math.sqrt(squares / samples.length);
  if (!(rms > 0)) return 0;
  const floor = -60;
  return Math.min(1, Math.max(0, (20 * Math.log10(rms) - floor) / -floor));
}

/** The loudest sample of a chunk, as a part of the scale: 0 is digital silence. */
export function peakLevel(samples: Float32Array): number {
  let peak = 0;
  for (let index = 0; index < samples.length; index++) { const value = Math.abs(samples[index]); if (value > peak) peak = value; }
  return Math.min(1, peak);
}

/** Turns the blocks of a microphone, at its own rate, into the chunks the host takes. */
export function createChunkEncoder(sourceRate: number): { push(input: Float32Array): AudioChunk[] } {
  const resampler = createResampler(sourceRate), chunker = createChunker();
  return { push: input => chunker.push(resampler.push(input)).map(chunk => ({ base64: int16Base64(toInt16(chunk)), level: voiceLevel(chunk), peak: peakLevel(chunk) })) };
}
