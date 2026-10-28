import assert from "node:assert/strict";
import test from "node:test";
import { isShownImage, linkedImageUrl, matchLinkedImages, savedImageUrl, splitFiles, steppedIndex } from "./imageModel";

test("an image is read by an id of the session, never by a path", () => {
  assert.equal(savedImageUrl("s 1", "20261027-01-shot.png"), "/api/sessions/s%201/images/20261027-01-shot.png");
  assert.equal(savedImageUrl("s1", "../x.png"), "/api/sessions/s1/images/..%2Fx.png", "an odd id is one segment, which the server refuses");
  assert.equal(linkedImageUrl("s1", 12, "0"), "/api/sessions/s1/linked-images/12/0");
});

test("the files of a message: images with an id are pictures, the others chips", () => {
  const { images, others } = splitFiles([
    { title: "shot.png", mediaType: "image/png", id: "a.png" }, { title: "notes.txt", mediaType: "text/plain", id: "b.txt" },
    { title: "pending.png", mediaType: "image/png", id: null }, { title: "raw.svg", mediaType: "image/svg+xml", id: "c.svg" },
  ]);
  assert.deepEqual(images.map(file => file.title), ["shot.png"]);
  assert.deepEqual(others.map(file => file.title), ["notes.txt", "pending.png", "raw.svg"], "an image not saved yet, and a type the server does not serve");
  assert.ok(isShownImage("image/jpeg") && isShownImage("image/*") && !isShownImage("application/pdf"));
});

test("the pictures of an answer are matched to its images by target, else by place", () => {
  const images = [{ id: "0", title: "A", src: "shots/a b.png" }, { id: "1", title: "B", src: "C:\\shots\\b.png" }];
  assert.deepEqual(matchLinkedImages(["C:%5Cshots%5Cb.png", "shots/a%20b.png"], images).map(image => image?.id), ["1", "0"]);
  assert.deepEqual(matchLinkedImages(["other.png", "shots/a%20b.png"], images).map(image => image?.id ?? null), ["1", "0"], "as many as listed: by place");
  assert.deepEqual(matchLinkedImages(["other.png"], images).map(image => image?.id ?? null), [null], "one the event does not list stays a link");
});

test("a swipe past the last image comes to the first", () => {
  assert.deepEqual([steppedIndex(2, 3, 1), steppedIndex(0, 3, -1), steppedIndex(0, 0, 1)], [0, 2, 0]);
});
