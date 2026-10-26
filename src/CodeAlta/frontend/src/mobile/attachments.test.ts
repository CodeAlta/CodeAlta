import assert from "node:assert/strict";
import test from "node:test";
import { addAttachments, hasImages, maximumAttachments } from "./attachments";

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
