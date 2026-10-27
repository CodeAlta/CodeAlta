import assert from "node:assert/strict";
import test from "node:test";
import { createMarkdownImageCache, markdownImageRequest } from "./markdownImages";

const epoch = "3b0a7c1e-52c9-4f0b-8a55-6d0c2f9e1b77";
const png = "iVBORw0KGgo=";

test("a picture of the disk is read once, kept, and named by its link and where the link starts from", async () => {
  const requests: unknown[] = [];
  const cache = createMarkdownImageCache(async request => {
    requests.push(request);
    return { status: "ok", hostEpoch: epoch, mediaType: "image/png", base64: png, fullPath: "C:\\out\\chart.png" };
  });
  const request = markdownImageRequest(epoch, "out/chart.png", { sessionId: "s1" });
  assert.deepEqual(request, { expectedHostEpoch: epoch, address: "out/chart.png", sessionId: "s1", projectId: null, directory: null });
  assert.equal(cache.peek(request), undefined);
  const [first, second] = await Promise.all([cache.load(request), cache.load(request)]);
  assert.deepEqual(first, { url: `data:image/png;base64,${png}`, path: "C:\\out\\chart.png" });
  assert.equal(second, first);
  assert.deepEqual(cache.peek(request), first);
  assert.equal(requests.length, 1);
  // The same address in another session names another file.
  await cache.load(markdownImageRequest(epoch, "out/chart.png", { sessionId: "s2" }));
  assert.equal(requests.length, 2);
});

test("a picture that cannot be drawn stays a link: a settled refusal is kept, a failed read is asked again", async () => {
  const replies: Record<string, unknown> = {
    "missing.png": { status: "not_found", hostEpoch: epoch, mediaType: null, base64: null, fullPath: null },
    "notes.txt": { status: "not_image", hostEpoch: epoch, mediaType: null, base64: null, fullPath: null },
    "busy.png": { status: "failed", hostEpoch: epoch, mediaType: null, base64: null, fullPath: null },
    "svg.png": { status: "ok", hostEpoch: epoch, mediaType: "image/svg+xml", base64: png, fullPath: "C:\\a.svg" },
    "other.png": { status: "ok", hostEpoch: "4b0a7c1e-52c9-4f0b-8a55-6d0c2f9e1b77", mediaType: "image/png", base64: png, fullPath: "C:\\a.png" },
    "nopath.png": { status: "ok", hostEpoch: epoch, mediaType: "image/png", base64: png, fullPath: null },
  };
  let reads = 0;
  const cache = createMarkdownImageCache(async request => {
    reads++;
    if (request.address === "throws.png") throw new Error("transport");
    return replies[request.address];
  });
  for (const address of [...Object.keys(replies), "throws.png"]) assert.equal(await cache.load(markdownImageRequest(epoch, address, null)), null, address);
  assert.equal(reads, 7);
  assert.equal(cache.peek(markdownImageRequest(epoch, "missing.png", null)), null);
  assert.equal(cache.peek(markdownImageRequest(epoch, "notes.txt", null)), null);
  for (const address of ["busy.png", "svg.png", "other.png", "nopath.png", "throws.png"]) assert.equal(cache.peek(markdownImageRequest(epoch, address, null)), undefined, address);
});

test("the newest pictures are kept, within a number and a size, and reads wait for a free slot", async () => {
  let running = 0, most = 0;
  const cache = createMarkdownImageCache(async request => {
    running++; most = Math.max(most, running);
    await new Promise(resolve => setTimeout(resolve, 5));
    running--;
    return { status: "ok", hostEpoch: epoch, mediaType: "image/png", base64: request.address === "big.png" ? "A".repeat(200) : png, fullPath: `/${request.address}` };
  }, { maximumEntries: 2, maximumCharacters: 150, concurrency: 2 });
  await Promise.all(["a.png", "b.png", "c.png"].map(address => cache.load(markdownImageRequest(epoch, address, null))));
  assert.equal(most, 2);
  assert.equal(cache.usage().entries, 2);
  assert.equal(cache.peek(markdownImageRequest(epoch, "a.png", null)), undefined);
  // A picture larger than the whole cache is drawn, and not kept.
  assert.ok(await cache.load(markdownImageRequest(epoch, "big.png", null)));
  assert.equal(cache.peek(markdownImageRequest(epoch, "big.png", null)), undefined);
});
