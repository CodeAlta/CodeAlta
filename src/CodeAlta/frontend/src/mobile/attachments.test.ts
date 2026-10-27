import assert from "node:assert/strict";
import test from "node:test";
import { scaledSize } from "../promptImages";
import { addAttachments, createHeldFiles, hasImages, maximumAttachments, newSessionReadsImages, phoneImageSide } from "./attachments";
import type { Options } from "./contract";

const text = (name: string, bytes: string) => new File([bytes], name, { type: "text/plain" });

test("a file is kept, and an image is left out when the model does not read images", async () => {
  const added = await addAttachments([], [text("notes.txt", "hello"), new File([Uint8Array.from([1, 2, 3])], "photo.png", { type: "image/png" })], false);
  assert.deepEqual(added.files.map(file => file.title), ["notes.txt"]);
  assert.equal(added.files[0].mediaType, "text/plain");
  assert.match(added.notice ?? "", /does not read images/);
  assert.equal(hasImages(added.files), false);

  const both = await addAttachments([], [text("notes.txt", "hello")], true);
  assert.deepEqual(both.files.map(file => [file.title, file.mediaType]), [["notes.txt", "text/plain"]]);
  assert.equal(both.notice, null);
});

test("files already held stay, and a prompt stops at its size and its count", async () => {
  const held = await addAttachments([], [text("a.txt", "hello")], true);
  const more = await addAttachments(held.files, [text("b.txt", "world")], true);
  assert.deepEqual(more.files.map(file => file.title), ["a.txt", "b.txt"]);

  const huge = new File([new Uint8Array(21 * 1024 * 1024)], "big.bin", { type: "application/octet-stream" });
  const refused = await addAttachments(held.files, [huge], true);
  assert.deepEqual(refused.files.map(file => file.title), ["a.txt"]);
  assert.match(refused.notice ?? "", /1 byte to 20 MB/);

  const full = Array.from({ length: maximumAttachments }, (_, index) => ({ title: `f${index}.txt`, mediaType: "text/plain", base64: "aGVsbG8=" }));
  const capped = await addAttachments(full, [text("extra.txt", "x")], true);
  assert.equal(capped.files.length, maximumAttachments);
  assert.match(capped.notice ?? "", /64 attachments/);
});

test("a photo is checked when it is added: an image too is a file of at most 20 MB", async () => {
  const photo = new File([Uint8Array.from([1, 2, 3])], "IMG_0001.jpg", { type: "image/jpeg" });
  const big = { title: "IMG_0001.jpg", mediaType: "image/png", base64: "A".repeat(Math.ceil(21 * 1024 * 1024 / 3) * 4) };
  const refused = await addAttachments([], [photo], true, async () => big);
  assert.deepEqual(refused.files, []);
  assert.match(refused.notice ?? "", /1 byte to 20 MB/);
  const held = { title: "a.png", mediaType: "image/png", base64: "A".repeat(Math.ceil(19 * 1024 * 1024 / 3) * 4) };
  const total = await addAttachments([held, held, held], [photo], true, async () => held);
  assert.equal(total.files.length, 3, "the total counts the images");
  assert.match(total.notice ?? "", /64 MB in all/);
  // A file that is not an image is refused for its size before it is read.
  let read = 0;
  const large = new File([new Uint8Array(8 * 1024 * 1024)], "log.txt", { type: "text/plain" });
  const before = await addAttachments([held, held, held], [large], true, async file => { read++; return { title: file.name, mediaType: "text/plain", base64: "aGk=" }; });
  assert.equal(read, 0);
  assert.match(before.notice ?? "", /64 MB in all/);
});

test("a photo is scaled down to its longest side, its proportions kept; a small one keeps its size", () => {
  assert.equal(phoneImageSide, 2048);
  assert.deepEqual(scaledSize(4032, 3024, phoneImageSide), { width: 2048, height: 1536 });
  assert.deepEqual(scaledSize(3024, 4032, phoneImageSide), { width: 1536, height: 2048 });
  assert.deepEqual(scaledSize(12000, 10, phoneImageSide), { width: 2048, height: 2 });
  assert.deepEqual(scaledSize(800, 600, phoneImageSide), { width: 800, height: 600 });
  assert.deepEqual(scaledSize(4032, 3024, Infinity), { width: 4032, height: 3024 }, "the desktop keeps every image as it is");
});

test("a new session reads images when the model of its tier, or the one chosen, of its provider, says so", () => {
  const options = {
    models: [
      { provider: "codex", id: "gpt", name: "GPT", providerName: "Codex", efforts: [], defaultEffort: null, imageInput: false },
      { provider: "openrouter", id: "gpt", name: "GPT", providerName: "OpenRouter", efforts: [], defaultEffort: null, imageInput: true },
    ],
    prompts: [], tiers: [{ tier: "quick", provider: "codex", model: "gpt", reasoning: null, error: null }, { tier: "complex", provider: null, model: null, reasoning: null, error: "None" }],
    defaultTier: "quick", defaults: null, error: null,
  } satisfies Options;
  assert.equal(newSessionReadsImages(options, { tier: "quick", provider: "", model: "", effort: "" }), false, "the model of the tier, on the provider of the tier");
  assert.equal(newSessionReadsImages(options, { tier: null, provider: "openrouter", model: "gpt", effort: "" }), true);
  assert.equal(newSessionReadsImages(options, { tier: null, provider: "codex", model: "gpt", effort: "" }), false);
  assert.equal(newSessionReadsImages(options, { tier: "complex", provider: "", model: "", effort: "" }), null, "a tier without a model says nothing");
  assert.equal(newSessionReadsImages(null, { tier: "quick", provider: "", model: "", effort: "" }), null);
});

test("the files held for sessions are bounded: the least recent other session lets them go first", () => {
  const file = (megabytes: number) => ({ title: `${megabytes}.bin`, mediaType: "application/octet-stream", base64: "A".repeat(megabytes * 1024 * 1024 / 3 * 4) });
  const held = createHeldFiles(30 * 1024 * 1024);
  assert.deepEqual(held.set("a", [file(9)]), []);
  assert.deepEqual(held.set("b", [file(9)]), []);
  assert.deepEqual(held.set("c", [file(9)]), []);
  assert.deepEqual(held.set("new", [file(9)], ["new", "b"]), ["a"], "the oldest one that is not shown");
  assert.equal(held.get("a").length, 0);
  assert.equal(held.wasDropped("a"), true);
  assert.equal(held.wasDropped("a"), false, "said once");
  assert.ok(held.total() <= 30 * 1024 * 1024);
  assert.deepEqual(held.take("b").map(f => f.title), ["9.bin"]);
  assert.equal(held.get("b").length, 0);
});
