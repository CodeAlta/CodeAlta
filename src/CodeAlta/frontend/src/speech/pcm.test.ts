import assert from "node:assert/strict";
import test from "node:test";
import { chunkSamples, createChunkEncoder, createChunker, createResampler, int16Base64, speechSampleRate, toInt16, voiceLevel } from "./pcm";

const sine = (rate: number, seconds: number, frequency: number, amplitude = .5) =>
  Float32Array.from({ length: Math.round(rate * seconds) }, (_, index) => amplitude * Math.sin(2 * Math.PI * frequency * index / rate));
const total = (blocks: Float32Array[]) => blocks.reduce((sum, block) => sum + block.length, 0);

test("a second of audio becomes 16 000 samples, whatever the rate of the microphone and the size of its blocks", () => {
  for (const rate of [48_000, 44_100, 96_000, 32_000, 16_000, 8_000]) {
    const source = sine(rate, 1, 440);
    for (const block of [128, 441, 4_800]) {
      const resampler = createResampler(rate);
      const blocks: Float32Array[] = [];
      for (let offset = 0; offset < source.length; offset += block) blocks.push(resampler.push(source.subarray(offset, offset + block)).slice());
      // The last sample may still wait for the block that completes it.
      assert.ok(Math.abs(total(blocks) - speechSampleRate) <= 1, `${rate} Hz in blocks of ${block}: ${total(blocks)}`);
    }
  }
});

test("the blocks of a stream give the same samples as the stream in one piece", () => {
  const source = sine(44_100, .5, 300);
  const whole = createResampler(44_100).push(source);
  const split = createResampler(44_100);
  const pieces: number[] = [];
  for (let offset = 0; offset < source.length; offset += 128) pieces.push(...split.push(source.subarray(offset, offset + 128)));
  assert.equal(pieces.length, whole.length);
  for (let index = 0; index < whole.length; index++) assert.ok(Math.abs(pieces[index] - whole[index]) < 1e-5, `sample ${index}`);
});

test("a lower rate keeps the voice and its loudness, and averages what the new rate cannot hold", () => {
  // 48 kHz to 16 kHz: each sample is the mean of three.
  assert.deepEqual([...createResampler(48_000).push(Float32Array.from([0, .3, .6, 1, 1, 1, -1, 0, 1]))].map(value => Math.round(value * 1000) / 1000), [.3, 1, 0]);
  // The same rate changes nothing.
  assert.deepEqual([...createResampler(16_000).push(Float32Array.from([.25, -.5, 1]))], [.25, -.5, 1]);
  // A tone in the range of a voice keeps its level within a decibel.
  const level = (samples: Float32Array) => Math.sqrt(samples.reduce((sum, value) => sum + value * value, 0) / samples.length);
  const tone = sine(48_000, 1, 300);
  assert.ok(Math.abs(20 * Math.log10(level(createResampler(48_000).push(tone)) / level(tone))) < 1);
  // A tone above half the new rate would come back as a false low tone: it is much weaker instead.
  const high = sine(48_000, 1, 15_000);
  assert.ok(level(createResampler(48_000).push(high)) < level(high) / 3);
  // A microphone at a lower rate repeats its samples.
  assert.deepEqual([...createResampler(8_000).push(Float32Array.from([.5, -.5]))], [.5, .5, -.5, -.5]);
  assert.throws(() => createResampler(0), RangeError);
  assert.throws(() => createResampler(Number.NaN), RangeError);
});

test("samples are cut at the ends of the 16-bit scale and written low byte first", () => {
  assert.deepEqual([...toInt16(Float32Array.from([0, 1, -1, 2, -3, .5, -.5, Number.NaN, Number.POSITIVE_INFINITY, Number.NEGATIVE_INFINITY]))],
    [0, 32767, -32768, 32767, -32768, 16384, -16384, 0, 32767, -32768]);
  // 1 is 01 00, -2 is FE FF, 0x1234 is 34 12.
  assert.equal(int16Base64(Int16Array.from([1, -2, 0x1234])), Buffer.from([0x01, 0x00, 0xfe, 0xff, 0x34, 0x12]).toString("base64"));
  assert.equal(int16Base64(new Int16Array(0)), "");
  // More bytes than one call of String.fromCharCode takes.
  const long = Int16Array.from({ length: 40_000 }, (_, index) => (index * 7919) % 65536 - 32768);
  const bytes = Buffer.from(int16Base64(long), "base64");
  assert.equal(bytes.length, 80_000);
  for (const index of [0, 1, 16_383, 16_384, 39_999]) assert.equal(bytes.readInt16LE(index * 2), long[index], `sample ${index}`);
});

test("a stream is cut into tenths of a second, and what is left waits for the next block", () => {
  const chunker = createChunker();
  assert.deepEqual(chunker.push(new Float32Array(1_000)).length, 0);
  const first = chunker.push(Float32Array.from({ length: 2_500 }, (_, index) => index));
  assert.deepEqual(first.map(chunk => chunk.length), [chunkSamples, chunkSamples]);
  // The first chunk starts with the thousand samples that waited, then takes the first six hundred of the block.
  assert.deepEqual([first[0][999], first[0][1_000], first[0][1_599], first[1][0], first[1][1_599]], [0, 0, 599, 600, 2_199]);
  assert.equal(chunker.push(new Float32Array(1_299)).length, 0);
  const last = chunker.push(Float32Array.from([7]));
  assert.equal(last.length, 1);
  assert.equal(last[0][299], 2_499);
  assert.equal(last[0][1_599], 7);
  // A chunk that was given is not written again by the blocks that follow.
  chunker.push(Float32Array.from({ length: 3_200 }, () => 9));
  assert.equal(last[0][1_599], 7);
  assert.throws(() => createChunker(0), RangeError);
});

test("the level of a voice rises with it and stays between 0 and 1", () => {
  assert.equal(voiceLevel(new Float32Array(1_600)), 0);
  assert.equal(voiceLevel(new Float32Array(0)), 0);
  const quiet = voiceLevel(sine(16_000, .1, 300, .002)), speaking = voiceLevel(sine(16_000, .1, 300, .2)), loud = voiceLevel(sine(16_000, .1, 300, 1));
  assert.ok(quiet > 0 && quiet < speaking && speaking < loud && loud <= 1, `${quiet} ${speaking} ${loud}`);
  assert.ok(speaking > .5, "a voice at a normal distance is in the upper half");
  assert.equal(voiceLevel(Float32Array.from({ length: 100 }, () => 4)), 1);
});

test("the blocks of a microphone become chunks the host takes: 3 200 bytes of Base64 each", () => {
  const encoder = createChunkEncoder(48_000);
  const source = sine(48_000, .35, 300);
  const chunks = [];
  for (let offset = 0; offset < source.length; offset += 128) chunks.push(...encoder.push(source.subarray(offset, offset + 128)));
  assert.equal(chunks.length, 3, "0.35 s is three whole tenths of a second");
  for (const chunk of chunks) {
    assert.equal(Buffer.from(chunk.base64, "base64").length, chunkSamples * 2);
    assert.ok(chunk.level > .5 && chunk.level <= 1);
  }
  // What the host decodes is the tone that went in.
  const decoded = Buffer.from(chunks[0].base64, "base64");
  const peak = Math.max(...Array.from({ length: chunkSamples }, (_, index) => Math.abs(decoded.readInt16LE(index * 2))));
  assert.ok(peak > 15_500 && peak < 16_500, `peak ${peak}`);
});
