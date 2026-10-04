import assert from "node:assert/strict";
import test from "node:test";
import { createTimelineImageCache, imageDataUrl, inlineImageSource, projectTimelineImages, steppedImageIndex, viewerChrome, viewerWindowSize } from "./timelineImages";

const minimum = { width: 360, height: 240 };
const ok = (base64 = "AAAA", mediaType = "image/png") => ({ status: "ok", mediaType, base64 });

test("a row's image list is accepted only when its indexes are its positions", () => {
  assert.deepEqual(projectTimelineImages([{ index: 0, title: "First", mediaType: "image/png" }, { index: 1, title: "Second", mediaType: null }]),
    [{ index: 0, title: "First", mediaType: "image/png" }, { index: 1, title: "Second", mediaType: null }]);
  for (const bad of [null, undefined, [], "images", [null], [{ index: 1, title: "x", mediaType: null }], [{ index: 0, title: "", mediaType: null }],
    [{ index: 0, title: 7, mediaType: null }], [{ index: 0, title: "x", mediaType: 7 }], [{ index: 0, title: "x".repeat(257), mediaType: null }],
    Array.from({ length: 65 }, (_, index) => ({ index, title: "x", mediaType: null }))])
    assert.equal(projectTimelineImages(bad), undefined);
});

test("only the image types of the timeline become a data URL", () => {
  assert.equal(imageDataUrl("image/png", "AAAA"), "data:image/png;base64,AAAA");
  assert.equal(imageDataUrl("image/webp", "AA=="), "data:image/webp;base64,AA==");
  for (const [mediaType, base64] of [["image/svg+xml", "AAAA"], ["text/html", "AAAA"], [null, "AAAA"], ["image/png", ""], ["image/png", null], ["image/png", "AA\"A"]])
    assert.equal(imageDataUrl(mediaType, base64), null);
});

test("the viewer opens at the image's size, and at most 80% of the main window", () => {
  const viewport = { width: 2000, height: 1000 };
  // Not measured yet: the largest window.
  assert.deepEqual(viewerWindowSize(viewport, null, minimum), { width: 1600, height: 800 });
  assert.deepEqual(viewerWindowSize(viewport, { width: 0, height: 0 }, minimum), { width: 1600, height: 800 });
  // A small image is not enlarged; the window keeps its minimum size.
  assert.deepEqual(viewerWindowSize(viewport, { width: 600, height: 400 }, minimum), { width: 600 + viewerChrome.width, height: 400 + viewerChrome.height });
  assert.deepEqual(viewerWindowSize(viewport, { width: 40, height: 30 }, minimum), minimum);
  // A large image is scaled down by its tighter side and keeps its aspect ratio.
  const wide = viewerWindowSize(viewport, { width: 4000, height: 1000 }, minimum);
  assert.equal(wide.width, 1600);
  assert.equal(wide.height, Math.ceil(1000 * (1600 - viewerChrome.width) / 4000) + viewerChrome.height);
  const tall = viewerWindowSize(viewport, { width: 1000, height: 4000 }, minimum);
  assert.equal(tall.height, 800);
  assert.equal(tall.width, Math.max(minimum.width, Math.ceil(1000 * (800 - viewerChrome.height) / 4000) + viewerChrome.width));
  // A window smaller than the viewer's minimum still gets no more than 80% of it.
  assert.deepEqual(viewerWindowSize({ width: 300, height: 200 }, { width: 40, height: 30 }, minimum), { width: 240, height: 160 });
});

test("moving between the images of a message wraps around", () => {
  assert.equal(steppedImageIndex(0, 3, 1), 1);
  assert.equal(steppedImageIndex(2, 3, 1), 0);
  assert.equal(steppedImageIndex(0, 3, -1), 2);
  assert.equal(steppedImageIndex(0, 1, 1), 0);
  assert.equal(steppedImageIndex(0, 0, 1), 0);
});

test("the images of a pending prompt are answered from the page", async () => {
  const source = inlineImageSource("outgoing:1", [{ url: "data:image/png;base64,AAAA" }]);
  assert.deepEqual(source.peek(0), { status: "ready", url: "data:image/png;base64,AAAA" });
  assert.deepEqual(await source.load(0), { status: "ready", url: "data:image/png;base64,AAAA" });
  assert.deepEqual(source.peek(1), { status: "failed" });
});

test("an image is read once and then answered from the cache", async () => {
  const requests: unknown[] = [];
  const cache = createTimelineImageCache(async request => { requests.push(request); return ok(); });
  const source = cache.reader("epoch", "session")("120");
  assert.equal(source.peek(0), undefined);
  const [first, second] = await Promise.all([source.load(0), source.load(0)]);
  assert.deepEqual(first, { status: "ready", url: "data:image/png;base64,AAAA" });
  assert.equal(second, first);
  assert.deepEqual(source.peek(0), first);
  assert.equal(await cache.reader("epoch", "session")("120").load(0), first);
  assert.deepEqual(requests, [{ expectedEpoch: "epoch", sessionId: "session", offset: "120", index: 0 }]);
  // Another image, message, session or host epoch is another read.
  await source.load(1);
  await cache.reader("epoch", "session")("300").load(0);
  await cache.reader("epoch", "other")("120").load(0);
  await cache.reader("later", "session")("120").load(0);
  assert.equal(requests.length, 5);
  assert.equal(source.key, JSON.stringify(["epoch", "session", "120"]));
});

test("the cache keeps the newest images within its entry and size limits", async () => {
  const cache = createTimelineImageCache(async request => ok("A".repeat(4 * (request.index + 1))), { maximumEntries: 2, maximumCharacters: 80 });
  const source = cache.reader("epoch", "session")("1");
  const url = (index: number) => "data:image/png;base64,".length + 4 * (index + 1);
  await source.load(0);
  await source.load(1);
  assert.deepEqual(cache.usage(), { entries: 2, characters: url(0) + url(1) });
  // A hit makes an entry the newest: the other one leaves first.
  source.peek(0);
  await source.load(2);
  assert.equal(source.peek(1), undefined);
  assert.ok(source.peek(0) && source.peek(2));
  assert.deepEqual(cache.usage(), { entries: 2, characters: url(0) + url(2) });
  // Entries leave until the size fits, and an image larger than the whole budget is shown but not kept.
  await source.load(9);
  assert.deepEqual(cache.usage(), { entries: 1, characters: url(9) });
  assert.equal((await source.load(20)).status, "ready");
  assert.equal(source.peek(20), undefined);
  assert.deepEqual(cache.usage(), { entries: 1, characters: url(9) });
});

test("a definite refusal is remembered and a passing one is asked again", async () => {
  let status = "missing_file";
  let reads = 0;
  const cache = createTimelineImageCache(async () => { reads++; return { status, mediaType: null, base64: null }; }, { retryDelayMilliseconds: 0 });
  const source = cache.reader("epoch", "session")("1");
  for (const [index, refusal] of ["missing_file", "outside_store", "unsupported_type", "too_large", "missing_record", "missing_image", "missing_session", "invalid"].entries()) {
    status = refusal;
    assert.deepEqual(await source.load(index), { status: "failed" });
    assert.deepEqual(source.peek(index), { status: "failed" });
  }
  reads = 0;
  for (const [index, refusal] of ["read_failed", "closed", "stale_epoch", "unavailable"].entries()) {
    status = refusal;
    assert.deepEqual(await source.load(100 + index), { status: "failed" });
    assert.equal(source.peek(100 + index), undefined);
  }
  assert.equal(reads, 4);
  // A full host is asked again, three times in all.
  status = "capacity"; reads = 0;
  assert.deepEqual(await source.load(200), { status: "failed" });
  assert.equal(reads, 3);
  assert.equal(source.peek(200), undefined);
});

test("a full host that frees up, a failed transport and a malformed answer", async () => {
  let reads = 0;
  const busy = createTimelineImageCache(async () => ++reads < 3 ? { status: "capacity" } : ok(), { retryDelayMilliseconds: 0 });
  assert.equal((await busy.reader("epoch", "session")("1").load(0)).status, "ready");
  const broken = createTimelineImageCache(async () => { throw new Error("transport"); });
  const source = broken.reader("epoch", "session")("1");
  assert.deepEqual(await source.load(0), { status: "failed" });
  assert.equal(source.peek(0), undefined);
  for (const reply of [null, {}, { status: "ok" }, ok("AAAA", "image/svg+xml"), ok("not base64!")]) {
    const cache = createTimelineImageCache(async () => reply);
    assert.deepEqual(await cache.reader("epoch", "session")("1").load(0), { status: "failed" });
  }
});

test("at most the configured number of reads run together", async () => {
  let running = 0, peak = 0;
  const releases: (() => void)[] = [];
  const cache = createTimelineImageCache(async () => {
    peak = Math.max(peak, ++running);
    await new Promise<void>(resolve => releases.push(resolve));
    running--;
    return ok();
  }, { concurrency: 2 });
  const source = cache.reader("epoch", "session")("1");
  const loads = [0, 1, 2, 3, 4].map(index => source.load(index));
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.equal(releases.length, 2);
  while (releases.length) { releases.shift()!(); await new Promise(resolve => setTimeout(resolve, 0)); }
  assert.deepEqual((await Promise.all(loads)).map(state => state.status), Array(5).fill("ready"));
  assert.equal(peak, 2);
});
