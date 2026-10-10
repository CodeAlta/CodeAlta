import assert from "node:assert/strict";
import test from "node:test";
import type { DocumentationAskResponse, DocumentationImageResponse, DocumentationMenuResponse, DocumentationPageResponse, DocumentationSearchResponse } from "#neoastra";
import { createDocumentationHub, type DocumentationApi } from "./documentationHub";

const epoch = "6f1d2c3b-4a59-4e68-9b7a-0c1d2e3f4a5b", other = "00000000-1111-4222-8333-444444444444";
const menu: DocumentationMenuResponse = { status: "ok", home: "readme.md", canAsk: true,
  items: [{ path: "readme.md", title: "User Guide", icon: "book", depth: 0, parent: null }, { path: "sessions.md", title: "Sessions", icon: null, depth: 0, parent: null }],
  pages: [{ path: "readme.md", title: "User Guide" }, { path: "sessions.md", title: "Sessions" }, { path: "orphan.md", title: "Orphan" }] };

/** A host whose answers the test gives when it chooses, so that an answer can come late. */
function host() {
  const calls = { menu: [] as string[], page: [] as string[], image: [] as string[], search: [] as string[], ask: [] as { question: string | null; page: string | null }[] };
  const waiting = new Map<string, ((value: unknown) => void)[]>();
  const pending = <T,>(key: string) => new Promise<T>(resolve => { waiting.set(key, [...waiting.get(key) ?? [], resolve as (value: unknown) => void]); });
  const page = (path: string): DocumentationPageResponse => ({ status: "ok", path, title: path, blocks: [{ kind: "markdown", markdown: `# ${path}`, image: null, svg: null, alt: null, caption: null, width: null, height: null }] });
  const api: DocumentationApi = {
    menu: request => { calls.menu.push(request.expectedEpoch); return pending<DocumentationMenuResponse>("menu"); },
    page: request => { calls.page.push(request.path ?? ""); return pending<DocumentationPageResponse>(`page:${request.path}`); },
    image: request => { calls.image.push(request.name ?? ""); return pending<DocumentationImageResponse>(`image:${request.name}`); },
    search: request => { calls.search.push(request.text ?? ""); return pending<DocumentationSearchResponse>(`search:${request.text}`); },
    ask: request => { calls.ask.push({ question: request.question, page: request.page }); return pending<DocumentationAskResponse>("ask"); },
  };
  /** Answers the oldest request that waits under a key, then lets the hub run. */
  async function answer(key: string, value: unknown | Error) {
    const resolve = waiting.get(key)?.shift();
    assert.ok(resolve, `nothing waits for ${key}`);
    resolve(value instanceof Error ? Promise.reject(value) : value);
    await new Promise(resolved => setTimeout(resolved));
  }
  return { api, calls, answer, page, waits: (key: string) => (waiting.get(key)?.length ?? 0) > 0 };
}

test("the tab reads the navigation when it is shown, and opens the first page of the guide", async () => {
  const fake = host();
  const hub = createDocumentationHub(fake.api);
  let published = 0;
  hub.subscribe(() => { published++; });
  assert.equal(hub.getSnapshot().status, "idle");
  // Nothing is read before the tab is shown, and nothing without a host.
  hub.show();
  assert.deepEqual(fake.calls.menu, []);
  hub.connect(epoch);
  assert.deepEqual(fake.calls.menu, [epoch]);
  assert.equal(hub.getSnapshot().status, "loading");
  hub.show();
  assert.equal(fake.calls.menu.length, 1, "One read at a time.");

  await fake.answer("menu", menu);
  let view = hub.getSnapshot();
  assert.equal(view.status, "ready");
  assert.equal(view.canAsk, true);
  assert.deepEqual(view.items.map(entry => entry.path), ["readme.md", "sessions.md"]);
  assert.deepEqual([view.location?.page, view.location?.anchor, view.page?.status, view.page?.title], ["readme.md", null, "loading", "User Guide"]);
  assert.deepEqual(fake.calls.page, ["readme.md"]);
  await fake.answer("page:readme.md", fake.page("readme.md"));
  view = hub.getSnapshot();
  assert.deepEqual([view.page?.status, view.page?.path, view.page?.blocks.length], ["ready", "readme.md", 1]);
  assert.deepEqual([view.canBack, view.canForward], [false, false]);
  assert.ok(published >= 3);
  // Showing the tab again keeps the page it is at, and reads nothing.
  hub.show();
  assert.equal(hub.getSnapshot().location?.serial, view.location?.serial);
  assert.deepEqual(fake.calls.page, ["readme.md"]);
});

test("a page that is asked for before the navigation is read is the one that opens", async () => {
  const fake = host();
  const hub = createDocumentationHub(fake.api);
  hub.connect(epoch);
  hub.show("Sessions.md", "#queue");
  await fake.answer("menu", menu);
  // The page as the guide writes it, and the address of the heading without its mark.
  assert.deepEqual([hub.getSnapshot().location?.page, hub.getSnapshot().location?.anchor], ["sessions.md", "queue"]);
  assert.deepEqual(fake.calls.page, ["sessions.md"]);
  await fake.answer("page:sessions.md", fake.page("sessions.md"));
  // An address that names no heading is dropped; the same place again is a move of its own, so the tab scrolls to it again.
  const before = hub.getSnapshot().location!.serial;
  hub.go("sessions.md", "a b\"]");
  assert.equal(hub.getSnapshot().location?.anchor, null);
  hub.go("sessions.md", "queue");
  hub.go("sessions.md", "queue");
  assert.equal(hub.getSnapshot().location!.serial, before + 3);
  assert.deepEqual(fake.calls.page, ["sessions.md"], "A page that was read is not read again.");
});

test("an answer that comes after the tab moved on is dropped", async () => {
  const fake = host();
  const hub = createDocumentationHub(fake.api);
  hub.connect(epoch);
  hub.show();
  await fake.answer("menu", menu);
  await fake.answer("page:readme.md", fake.page("readme.md"));
  hub.go("sessions.md");
  hub.go("orphan.md");
  assert.deepEqual(fake.calls.page, ["readme.md", "sessions.md", "orphan.md"]);
  // The page that was asked for last answers first, the one before answers late.
  await fake.answer("page:orphan.md", fake.page("orphan.md"));
  assert.equal(hub.getSnapshot().page?.path, "orphan.md");
  await fake.answer("page:sessions.md", fake.page("sessions.md"));
  assert.deepEqual([hub.getSnapshot().page?.path, hub.getSnapshot().location?.page], ["orphan.md", "orphan.md"]);

  // Back and forward go through what was visited, without asking the host for a page it gave.
  assert.deepEqual([hub.getSnapshot().canBack, hub.getSnapshot().canForward], [true, false]);
  hub.back();
  assert.deepEqual([hub.getSnapshot().location?.page, hub.getSnapshot().location?.restore, hub.getSnapshot().page?.status], ["sessions.md", true, "loading"]);
  await fake.answer("page:sessions.md", fake.page("sessions.md"));
  hub.back();
  assert.deepEqual([hub.getSnapshot().location?.page, hub.getSnapshot().page?.status, hub.getSnapshot().canBack, hub.getSnapshot().canForward], ["readme.md", "ready", false, true]);
  hub.back();
  assert.equal(hub.getSnapshot().location?.page, "readme.md");
  hub.forward(); hub.forward(); hub.forward();
  assert.deepEqual([hub.getSnapshot().location?.page, hub.getSnapshot().canForward], ["orphan.md", false]);
  // Going somewhere from the middle drops what was ahead.
  hub.back();
  hub.go("readme.md");
  assert.deepEqual([hub.getSnapshot().canBack, hub.getSnapshot().canForward, hub.getSnapshot().location?.restore], [true, false, false]);
});

test("a page the guide does not have, and a host that does not answer, are said and can be asked again", async () => {
  const fake = host();
  const hub = createDocumentationHub(fake.api);
  hub.connect(epoch);
  hub.show();
  await fake.answer("menu", Error("timeout"));
  assert.equal(hub.getSnapshot().status, "failed");
  hub.retry();
  await fake.answer("menu", { ...menu, status: "unavailable", items: [], pages: [], home: null });
  assert.equal(hub.getSnapshot().status, "unavailable");
  hub.show();
  await fake.answer("menu", menu);
  await fake.answer("page:readme.md", { status: "not_found", path: null, title: null, blocks: [] });
  assert.deepEqual([hub.getSnapshot().status, hub.getSnapshot().page?.status], ["ready", "not_found"]);
  hub.go("sessions.md");
  await fake.answer("page:sessions.md", Error("timeout"));
  assert.equal(hub.getSnapshot().page?.status, "failed");
  hub.retry();
  await fake.answer("page:sessions.md", fake.page("sessions.md"));
  assert.equal(hub.getSnapshot().page?.status, "ready");
});

test("another host forgets what was read and shows the same place; an answer of the host before is dropped", async () => {
  const fake = host();
  const hub = createDocumentationHub(fake.api);
  const leave = hub.connect(epoch);
  hub.show("sessions.md", "queue");
  await fake.answer("menu", menu);
  await fake.answer("page:sessions.md", fake.page("sessions.md"));
  hub.go("orphan.md");
  leave();
  hub.connect(other);
  assert.deepEqual(fake.calls.menu, [epoch, other]);
  assert.deepEqual([hub.getSnapshot().status, hub.getSnapshot().page, hub.getSnapshot().location?.page], ["loading", null, "orphan.md"]);
  // The page the host before was asked for answers now: it is not shown.
  await fake.answer("page:orphan.md", fake.page("orphan.md"));
  assert.equal(hub.getSnapshot().page, null);
  await fake.answer("menu", menu);
  assert.deepEqual([hub.getSnapshot().location?.page, hub.getSnapshot().page?.status], ["orphan.md", "loading"]);
  await fake.answer("page:orphan.md", fake.page("orphan.md"));
  assert.equal(hub.getSnapshot().page?.status, "ready");
  // The same host named again (an effect that runs twice) changes nothing.
  const serial = hub.getSnapshot().location?.serial;
  hub.connect(other);
  assert.deepEqual([hub.getSnapshot().status, hub.getSnapshot().location?.serial, fake.calls.menu.length], ["ready", serial, 2]);
});

test("a picture is read once, kept, and a few at a time", async () => {
  const fake = host();
  const hub = createDocumentationHub(fake.api, { imageConcurrency: 2, maximumImages: 2 });
  assert.deepEqual(await hub.images.load("a.webp"), { status: "failed" }, "No host, no picture.");
  hub.connect(epoch);
  assert.equal(hub.images.peek("a.webp"), undefined);
  const first = hub.images.load("a.webp"), again = hub.images.load("a.webp"), second = hub.images.load("b.webp"), third = hub.images.load("c.webp");
  await new Promise(resolve => setTimeout(resolve));
  assert.deepEqual(fake.calls.image, ["a.webp", "b.webp"], "The third waits for a place.");
  await fake.answer("image:a.webp", { status: "ok", mediaType: "image/webp", base64: "UklGRg==" });
  assert.deepEqual(await first, { status: "ready", url: "data:image/webp;base64,UklGRg==" });
  assert.equal(await again, await first);
  assert.deepEqual(fake.calls.image, ["a.webp", "b.webp", "c.webp"]);
  assert.deepEqual(hub.images.peek("a.webp"), { status: "ready", url: "data:image/webp;base64,UklGRg==" });
  assert.equal(await hub.images.load("a.webp"), await first);
  // What is no picture the window shows, and a picture the guide does not have, are remembered as missing.
  await fake.answer("image:b.webp", { status: "ok", mediaType: "text/html", base64: "PGh0bWw+" });
  assert.deepEqual(await second, { status: "failed" });
  await fake.answer("image:c.webp", { status: "not_found", mediaType: null, base64: null });
  assert.deepEqual(await third, { status: "failed" });
  assert.deepEqual(hub.images.peek("c.webp"), { status: "failed" });
  // Only the newest are kept.
  assert.equal(hub.images.peek("a.webp"), undefined);
  // A read that did not answer is asked again.
  const lost = hub.images.load("d.webp");
  await fake.answer("image:d.webp", Error("timeout"));
  assert.deepEqual(await lost, { status: "failed" });
  assert.equal(hub.images.peek("d.webp"), undefined);
});

test("only the newest search is answered", async () => {
  const fake = host();
  const hub = createDocumentationHub(fake.api);
  hub.connect(epoch);
  assert.deepEqual(await hub.search(" a "), []);
  assert.deepEqual(fake.calls.search, []);
  const first = hub.search("queue"), second = hub.search("  prompt queue  ");
  assert.deepEqual(fake.calls.search, ["queue", "prompt queue"]);
  const hit = { path: "sessions.md", title: "Sessions", heading: "Queue", text: "A prompt queue." };
  await fake.answer("search:prompt queue", { status: "ok", hits: [hit] });
  assert.deepEqual(await second, [hit]);
  await fake.answer("search:queue", { status: "ok", hits: [hit, hit] });
  assert.equal(await first, null);
  const failed = hub.search("nothing");
  await fake.answer("search:nothing", Error("timeout"));
  assert.deepEqual(await failed, []);
});

test("a question is asked once at a time, and its chat is the one of the host that was asked", async () => {
  const fake = host();
  const hub = createDocumentationHub(fake.api);
  assert.deepEqual(await hub.ask("Why?", null), { status: "unavailable", sessionId: null, problem: null });
  const leave = hub.connect(epoch);
  const first = hub.ask("How do I queue a prompt?", "sessions.md");
  assert.deepEqual(await hub.ask("And then?", null), { status: "busy", sessionId: null, problem: null });
  assert.deepEqual(fake.calls.ask, [{ question: "How do I queue a prompt?", page: "sessions.md" }]);
  await fake.answer("ask", { status: "ok", sessionId: "chat-1", problem: null });
  assert.deepEqual(await first, { status: "ok", sessionId: "chat-1", problem: null });

  const refused = hub.ask("Why?", null);
  await fake.answer("ask", { status: "no_provider", sessionId: null, problem: null });
  assert.equal((await refused).status, "no_provider");
  const lost = hub.ask("Why?", null);
  await fake.answer("ask", Error("timeout"));
  assert.equal((await lost).status, "failed");

  // The host changes while the question is asked: the chat of the host before is not this window's to show.
  const late = hub.ask("Why?", null);
  leave();
  hub.connect(other);
  await fake.answer("ask", { status: "ok", sessionId: "chat-2", problem: null });
  assert.deepEqual(await late, { status: "stale", sessionId: null, problem: null });
});

test("the tab keeps how far each page was read, and which move it showed last", () => {
  const hub = createDocumentationHub(host().api);
  assert.equal(hub.scroll.get("readme.md"), 0);
  assert.equal(hub.scroll.has("readme.md"), false);
  hub.scroll.set("readme.md", 420);
  assert.deepEqual([hub.scroll.get("readme.md"), hub.scroll.has("readme.md")], [420, true]);
  assert.equal(hub.scroll.shown(), 0);
  hub.scroll.show(7);
  assert.equal(hub.scroll.shown(), 7);
});
