import test from "node:test";
import assert from "node:assert/strict";
import { createImageDrafts, freezeImages, pngHeader } from "./promptImages";
import { captureSubmission } from "./sessionOperations";

test("image-only capture preserves truly empty text and titles without admitting blank text-only or whitespace", () => {
  const selection = { providerKey: "p", agentPromptId: "default", modelId: "image", reasoningEffort: null };
  const images = [{ title: "Settings / image", mediaType: "image/png", base64: "AA==" }];
  const captured = captureSubmission("e", "s", "", "k", selection, null, images);
  assert.ok(captured);
  assert.equal(captured.text, "");
  images[0].title = "changed";
  assert.equal(captured.images![0].title, "Settings / image");
  assert.equal(captureSubmission("e", "s", "", "k", selection, null, []), null);
  assert.equal(captureSubmission("e", "s", " \n\t", "k", selection, null, images), null);
  assert.equal(captureSubmission("e", "s", "", "k", null, null, images), null);
});

test("image draft replacement is bounded, immutable and exact-revision fenced", () => {
  const owner = createImageDrafts();
  const empty = owner.get("a");
  const image = { title: "Image 1", mediaType: "image/png", base64: "AA==" };
  const frozen = freezeImages([image]);
  assert.equal(owner.replace("a", empty, frozen), true);
  image.title = "mutated";
  assert.equal(owner.get("a")[0].title, "Image 1");
  assert.equal(owner.replace("a", empty, []), false);
  assert.deepEqual(owner.get("b"), []);
  for (let index = 0; index < 7; index++) assert.equal(owner.replace(`s${index}`, owner.get(`s${index}`), frozen), true);
  assert.equal(owner.replace("overflow", owner.get("overflow"), frozen), false);
  assert.equal(owner.replace("a", owner.get("a"), []), true);
  assert.equal(owner.replace("overflow", owner.get("overflow"), frozen), true);
});

test("PNG preview header refuses arbitrary, oversized and non-raster payloads", () => {
  for (const bytes of [new Uint8Array(), new Uint8Array(65_537), new TextEncoder().encode("<svg/>")])
    assert.throws(() => pngHeader(bytes));
});

test("pending image reads are bounded and cannot overlap the same draft", () => {
  const owner = createImageDrafts();
  const releases = Array.from({ length: 8 }, (_, index) => owner.beginRead(String(index))!);
  assert.ok(releases.every(Boolean));
  assert.equal(owner.beginRead("0"), null);
  assert.equal(owner.beginRead("overflow"), null);
  releases[0](); assert.ok(owner.beginRead("overflow"));
});

test("title replacement uses exact snapshots, rejects invalid metadata and fences ABA", () => {
  const owner = createImageDrafts();
  const key = "scope";
  owner.replace(key, owner.get(key), [{ title: "Original", mediaType: "image/png", base64: "AA==" }]);
  const original = owner.get(key);
  for (const title of ["", "  ", "x".repeat(81), "bad\nname", "bad\u0085name"])
    assert.equal(owner.replace(key, original, [{ ...original[0], title }]), false);
  assert.equal(owner.replace(key, original, [{ ...original[0], title: "日本語 / local" }]), true);
  assert.equal(original[0].title, "Original");
  assert.equal(owner.replace(key, owner.get(key), original), true);
  assert.notEqual(owner.get(key), original, "returning to the same title never revives an old paste snapshot");
  assert.equal(owner.replace(key, original, []), false);
  assert.equal(owner.replace("replacement scope", original, []), false);
});

test("worst-case bounded image request fits the unchanged bridge frame and freezes all fields", () => {
  const images = [65_536, 32_768].map(size => ({ title: "漢".repeat(80), mediaType: "image/png", base64: btoa("a".repeat(size)) }));
  const selection = { providerKey: "p".repeat(256), agentPromptId: "a".repeat(256), modelId: "m".repeat(256), reasoningEffort: null };
  const captured = captureSubmission("e".repeat(64), "s".repeat(256), "\u0001".repeat(4096), "k".repeat(256), selection,
    { projectId: "p".repeat(256), projectPath: "\u0001".repeat(4096) }, images)!;
  assert.ok(captured);
  assert.ok(new TextEncoder().encode(JSON.stringify(captured)).length < 200 * 1024, "leave at least 8 KiB for the existing bridge envelope");
  images[0].title = "changed";
  assert.notEqual(captured.images![0].title, images[0].title);
  assert.equal(captureSubmission("e", "s", "x".repeat(4097), "k", selection, null, images), null);
  assert.equal(captureSubmission("e", "s", "x", "k", selection, null, [...images, images[1]]), null);
});
