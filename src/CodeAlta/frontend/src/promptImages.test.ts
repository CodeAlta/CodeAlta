import test from "node:test";
import assert from "node:assert/strict";
import { createImageDrafts, freezeImages, pngHeader } from "./promptImages";
import { captureSubmission } from "./sessionOperations";

test("local handoff reserves capacity before persistence and never consumes source images", () => {
  const owner = createImageDrafts();
  const image = { title: "Original", mediaType: "image/png", base64: "AA==" };
  owner.replace("source", owner.get("source"), [image]);
  const original = owner.get("source");
  let writes = 0;
  assert.equal(owner.copyToEmpty("source", original, "target", () => { writes++; return false; }), false);
  assert.equal(owner.get("source"), original);
  assert.equal(owner.get("target").length, 0);
  assert.equal(owner.copyToEmpty("source", original, "target", () => {
    writes++;
    assert.equal(owner.replace("target", owner.get("target"), [image]), false, "reservation excludes reentrant destination writes");
    return true;
  }), true);
  assert.deepEqual(owner.get("target"), original);
  assert.equal(owner.get("source"), original);
  assert.equal(owner.copyToEmpty("source", original, "target", () => { writes++; return true; }), false);
  for (let i = 0; i < 6; i++) owner.replace(`full${i}`, owner.get(`full${i}`), [image]);
  assert.equal(owner.copyToEmpty("source", original, "overflow", () => { writes++; return true; }), false);
  assert.equal(writes, 2, "occupied/capacity refusal precedes text storage");
});

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

test("local image copy rejects source ABA, pending destination reads and storage uncertainty", () => {
  const owner = createImageDrafts();
  const image = { title: "original", mediaType: "image/png", base64: "AA==" };
  owner.replace("source", owner.get("source"), [image]);
  const captured = owner.get("source");
  const finish = owner.beginRead("destination")!;
  assert.equal(owner.copyToEmpty("source", captured, "destination", () => assert.fail("pending read cannot persist text")), false);
  finish();
  assert.equal(owner.copyToEmpty("source", captured, "destination", () => {
    assert.equal(owner.beginRead("destination"), null);
    throw Error("text storage may have written before throwing");
  }), false);
  assert.equal(owner.get("source"), captured);
  assert.equal(owner.get("destination").length, 0);
  owner.replace("source", captured, [{ ...image, title: "new" }]);
  owner.replace("source", owner.get("source"), captured);
  assert.equal(owner.copyToEmpty("source", captured, "destination", () => assert.fail("stale capture cannot persist")), false);
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

test("PNG preview header refuses malformed and non-raster payloads regardless of size", () => {
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

test("image requests preserve metadata without the former text and total-byte limits", () => {
  const images = [65_536, 32_768].map(size => ({ title: "漢".repeat(80), mediaType: "image/png", base64: btoa("a".repeat(size)) }));
  const selection = { providerKey: "p".repeat(256), agentPromptId: "a".repeat(256), modelId: "m".repeat(256), reasoningEffort: null };
  const captured = captureSubmission("e".repeat(64), "s".repeat(256), "\u0001".repeat(4096), "k".repeat(256), selection,
    { projectId: "p".repeat(256), projectPath: "\u0001".repeat(4096) }, images)!;
  assert.ok(captured);
  assert.ok(new TextEncoder().encode(JSON.stringify(captured)).length < 200 * 1024, "leave at least 8 KiB for the existing bridge envelope");
  images[0].title = "changed";
  assert.notEqual(captured.images![0].title, images[0].title);
  assert.ok(captureSubmission("e", "s", "x".repeat(32768), "k", selection, null, images));
  assert.ok(captureSubmission("e", "s", "x", "k", selection, null, [...images, images[1]]));
  assert.equal(captureSubmission("e", "s", "x".repeat(32769), "k", selection, null, images), null);
});

test("ordinary multi-megabyte images and more than three attachments remain in the draft", () => {
  const owner = createImageDrafts();
  const image = { title: "Screenshot", mediaType: "image/png", base64: btoa("a".repeat(2_000_000)) };
  assert.equal(owner.replace("draft", owner.get("draft"), [image, image, image, image]), true);
  assert.equal(owner.get("draft").length, 4);
});
