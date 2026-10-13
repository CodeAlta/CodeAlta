import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { runInNewContext } from "node:vm";
import { createChunkEncoder } from "./pcm";
import { workletFile, workletProcessor } from "./workletFile";

const source = readFileSync(new URL("./pcm-worklet.js", import.meta.url), "utf8");

// Runs the file as an audio thread would: its own globals, and nothing of the page.
function worklet(sampleRate: number) {
  const posted: { block: Float32Array; transfer: unknown[] }[] = [];
  let registered: { name: string; create: new () => { process(inputs: Float32Array[][]): boolean } } | undefined;
  class AudioWorkletProcessor { port = { postMessage: (block: Float32Array, transfer: unknown[]) => { posted.push({ block, transfer }); } }; }
  runInNewContext(source, { AudioWorkletProcessor, sampleRate, Float32Array, Math,
    registerProcessor: (name: string, create: new () => { process(inputs: Float32Array[][]): boolean }) => { registered = { name, create }; } });
  assert.ok(registered);
  return { name: registered.name, processor: new registered.create(), posted };
}

test("the worklet is one file that imports nothing: the build copies it as it is", () => {
  assert.doesNotMatch(source, /^\s*(import|export)\b/m);
  assert.doesNotMatch(source, /\b(require|importScripts)\s*\(/);
  // The name the capture makes its node with.
  assert.equal(worklet(48_000).name, workletProcessor);
  assert.match(workletFile, /^[\w-]+\.js$/, "a file beside the entry document");
});

test("the worklet hands the page a tenth of a second at a time, and keeps what a block leaves over", () => {
  for (const rate of [48_000, 44_100, 16_000]) {
    const { processor, posted } = worklet(rate);
    const size = Math.round(rate / 10);
    let next = 0;
    // The audio thread gives 128 samples at a time: a second and a bit.
    for (let block = 0; block < Math.ceil(rate * 1.05 / 128); block++) {
      const channel = Float32Array.from({ length: 128 }, () => next++);
      assert.equal(processor.process([[channel]]), true, "the processor stays alive");
    }
    assert.equal(posted.length, 10, `${rate} Hz`);
    let expected = 0;
    for (const { block, transfer } of posted) {
      assert.equal(block.length, size);
      // The buffer goes with the message: the thread does not write into what the page reads.
      assert.ok(transfer.length === 1 && transfer[0] === block.buffer);
      for (let index = 0; index < block.length; index += 97) assert.equal(block[index], expected + index);
      expected += size;
    }
    assert.equal(new Set(posted.map(message => message.block.buffer)).size, posted.length);
  }
});

test("a microphone that is silent or absent for a moment stops nothing", () => {
  const { processor, posted } = worklet(48_000);
  assert.equal(processor.process([]), true);
  assert.equal(processor.process([[]]), true);
  assert.equal(posted.length, 0);
});

test("what the worklet gives becomes the chunks of the host", () => {
  const { processor, posted } = worklet(48_000);
  for (let block = 0; block < 400; block++) processor.process([[Float32Array.from({ length: 128 }, (_, index) => .5 * Math.sin(2 * Math.PI * 300 * (block * 128 + index) / 48_000))]]);
  const encoder = createChunkEncoder(48_000);
  const chunks = posted.flatMap(message => encoder.push(message.block));
  // 51 200 samples at 48 kHz are ten whole tenths of a second, each a tenth of a second at 16 kHz.
  assert.equal(posted.length, 10);
  assert.equal(chunks.length, 10);
  for (const chunk of chunks) assert.equal(Buffer.from(chunk.base64, "base64").length, 3_200);
});
