import type { DocumentationAskResponse, DocumentationBlock, DocumentationImageResponse, DocumentationMenuItem, DocumentationMenuResponse, DocumentationPageInfo,
  DocumentationPageResponse, DocumentationSearchHit, DocumentationSearchResponse } from "#neoastra";
import { imageDataUrl } from "../timelineImages";
import { emptyHistory, moveHistory, pushHistory, validAnchor, type DocumentationHistory, type DocumentationTarget } from "./documentation";

/** What the hub asks of the host: the `documentation` service, or a fixture of it. */
export type DocumentationApi = Readonly<{
  menu(request: { expectedEpoch: string }, options?: { timeoutMilliseconds?: number }): Promise<DocumentationMenuResponse>;
  page(request: { expectedEpoch: string; path: string | null }, options?: { timeoutMilliseconds?: number }): Promise<DocumentationPageResponse>;
  image(request: { expectedEpoch: string; name: string | null }, options?: { timeoutMilliseconds?: number }): Promise<DocumentationImageResponse>;
  search(request: { expectedEpoch: string; text: string | null }, options?: { timeoutMilliseconds?: number }): Promise<DocumentationSearchResponse>;
  ask(request: { expectedEpoch: string; question: string | null; page: string | null }, options?: { timeoutMilliseconds?: number }): Promise<DocumentationAskResponse>;
}>;

/** The page the tab shows: what was asked for, and what the host answered. */
export type DocumentationPageState = Readonly<{
  /** `loading` until the host answers; `not_found` for a page the guide does not have; `failed` when the host could not be asked. */
  status: "loading" | "ready" | "not_found" | "failed";
  path: string; title: string | null; blocks: readonly DocumentationBlock[];
}>;

/**
 * Where the tab is. `serial` grows with every move, so going again to the heading the tab is at scrolls to it again;
 * `restore` says that the reader came back to a place (Back, Forward), which is shown where it was left.
 */
export type DocumentationLocation = DocumentationTarget & Readonly<{ serial: number; restore: boolean }>;

/** What the Documentation tab shows. */
export type DocumentationView = Readonly<{
  /** `idle` before the guide was asked for; `unavailable` when the application ships none; `failed` when the host could not be asked. */
  status: "idle" | "loading" | "ready" | "unavailable" | "failed";
  home: string | null; items: readonly DocumentationMenuItem[]; pages: readonly DocumentationPageInfo[]; canAsk: boolean;
  location: DocumentationLocation | null; page: DocumentationPageState | null; canBack: boolean; canForward: boolean;
}>;

/** A picture of the guide as a `data:` URL (the page's policy allows no `blob:` image), or a failed read. */
export type DocumentationImageState = Readonly<{ status: "ready"; url: string } | { status: "failed" }>;

/** What asking a question gave: the chat to show, or why there is none. `stale` when the host changed meanwhile. */
export type DocumentationAskResult = Readonly<{ status: string; sessionId: string | null; problem: string | null }>;

const failedImage: DocumentationImageState = Object.freeze({ status: "failed" });
const initial: DocumentationView = Object.freeze({ status: "idle", home: null, items: [], pages: [], canAsk: false, location: null, page: null, canBack: false, canForward: false });

/**
 * The state of the Documentation tab, kept by the window so that closing the tab and opening it again shows the
 * page it was at. It reads the navigation once for a host, a page when the tab goes to it, and a picture when a
 * figure comes to the screen; an answer that comes after the tab moved on, or after the host changed, is dropped.
 */
export function createDocumentationHub(api: DocumentationApi, limits: { maximumPages?: number; maximumImages?: number; maximumImageCharacters?: number; imageConcurrency?: number } = {}) {
  const maximumPages = limits.maximumPages ?? 16, maximumImages = limits.maximumImages ?? 48;
  const maximumImageCharacters = limits.maximumImageCharacters ?? 40 * 1024 * 1024, imageConcurrency = limits.imageConcurrency ?? 3;
  const listeners = new Set<() => void>();
  let view = initial;
  let epoch: string | null = null;
  let history: DocumentationHistory = emptyHistory;
  let serial = 0;
  // Each read carries the number of its request: only the answer to the newest one is kept.
  let menuRequest = 0, pageRequest = 0, searchRequest = 0;
  let asking = false;
  // What the user asked to see before the navigation was read, and whether the guide was asked for at all: the tab
  // can be shown before the window names its host.
  let wanted: DocumentationTarget | null = null;
  let asked = false;
  const pages = new Map<string, DocumentationPageState>();
  const images = new Map<string, DocumentationImageState>();
  const imageReads = new Map<string, Promise<DocumentationImageState>>();
  const imageWaiting: (() => void)[] = [];
  let imageRunning = 0, imageCharacters = 0;
  const scrolls = new Map<string, number>();
  let placedSerial = 0;

  function publish(next: Partial<DocumentationView>) {
    view = Object.freeze({ ...view, ...next, canBack: history.index > 0, canForward: history.index >= 0 && history.index < history.entries.length - 1 });
    for (const listener of [...listeners]) listener();
  }
  const sameEpoch = (expected: string) => epoch === expected;

  async function readMenu() {
    const expected = epoch;
    if (!expected) return;
    const request = ++menuRequest;
    publish({ status: "loading" });
    let reply: DocumentationMenuResponse | null = null;
    try { reply = await api.menu({ expectedEpoch: expected }, { timeoutMilliseconds: 20_000 }); } catch { /* The host did not answer. */ }
    if (request !== menuRequest || !sameEpoch(expected)) return;
    if (reply?.status === "ok") {
      publish({ status: "ready", home: reply.home, items: reply.items, pages: reply.pages, canAsk: reply.canAsk });
      const target = wanted ?? (view.location ? { page: view.location.page, anchor: view.location.anchor } : reply.home ? { page: reply.home, anchor: null } : null);
      wanted = null;
      if (target) move(target, "push");
    } else {
      publish({ status: reply?.status === "unavailable" ? "unavailable" : "failed" });
    }
  }

  // The page a caller names, as the guide writes it; a name the navigation does not know is asked of the host as it is.
  const canonical = (page: string) => view.pages.find(known => known.path.toLowerCase() === page.toLowerCase())?.path ?? page;

  function move(target: DocumentationTarget, how: "push" | "keep") {
    const page = canonical(target.page), anchor = validAnchor(target.anchor);
    if (how === "push") history = pushHistory(history, { page, anchor });
    publish({ location: { page, anchor, serial: ++serial, restore: how === "keep" } });
    void readPage(page);
  }

  async function readPage(path: string) {
    const expected = epoch;
    if (!expected) return;
    const request = ++pageRequest;
    const known = pages.get(path);
    if (known) { pages.delete(path); pages.set(path, known); publish({ page: known }); return; }
    // The page before stays until the new one is there only when it is the same page: another page shows that it is loading.
    publish({ page: { status: "loading", path, title: view.pages.find(page => page.path === path)?.title ?? null, blocks: [] } });
    let reply: DocumentationPageResponse | null = null;
    try { reply = await api.page({ expectedEpoch: expected, path }, { timeoutMilliseconds: 20_000 }); } catch { /* The host did not answer. */ }
    if (request !== pageRequest || !sameEpoch(expected)) return;
    if (reply?.status === "ok" && reply.path) {
      const state: DocumentationPageState = Object.freeze({ status: "ready", path: reply.path, title: reply.title, blocks: reply.blocks });
      pages.set(path, state);
      for (const oldest of pages.keys()) { if (pages.size <= maximumPages) break; pages.delete(oldest); }
      publish({ page: state });
    } else {
      publish({ page: { status: reply?.status === "not_found" ? "not_found" : "failed", path, title: null, blocks: [] } });
    }
  }

  function ensure(target: DocumentationTarget | null) {
    asked = true;
    if (view.status === "ready") {
      if (target) move(target, "push");
      else if (!view.location && view.home) move({ page: view.home, anchor: null }, "push");
      return;
    }
    if (target) wanted = target;
    if (view.status !== "loading") void readMenu();
  }

  function rememberImage(name: string, state: DocumentationImageState) {
    const size = (value: DocumentationImageState) => value.status === "ready" ? value.url.length : 0;
    if (size(state) > maximumImageCharacters) return;
    images.set(name, state);
    imageCharacters += size(state);
    for (const [oldest, value] of images) {
      if (images.size <= maximumImages && imageCharacters <= maximumImageCharacters) break;
      images.delete(oldest);
      imageCharacters -= size(value);
    }
  }
  async function imageSlot<T>(work: () => Promise<T>): Promise<T> {
    if (imageRunning >= imageConcurrency) await new Promise<void>(resolve => imageWaiting.push(resolve));
    else imageRunning++;
    // A finished read hands its slot to the next waiting one.
    try { return await work(); }
    finally { const next = imageWaiting.shift(); if (next) next(); else imageRunning--; }
  }
  async function readImage(name: string, expected: string): Promise<DocumentationImageState> {
    let status = "failed";
    try {
      const reply = await api.image({ expectedEpoch: expected, name }, { timeoutMilliseconds: 30_000 });
      status = reply.status;
      const url = status === "ok" ? imageDataUrl(reply.mediaType, reply.base64) : null;
      if (!sameEpoch(expected)) return failedImage;
      if (url) { const state: DocumentationImageState = Object.freeze({ status: "ready", url }); rememberImage(name, state); return state; }
    } catch { /* A read that did not answer can be asked again. */ return failedImage; }
    // A picture the guide does not have is not asked for again.
    if (status === "not_found" || status === "ok") rememberImage(name, failedImage);
    return failedImage;
  }

  return {
    subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
    getSnapshot: () => view,
    /** Names the host the tab talks to. Another host than before forgets what was read; the place the tab is at stays. */
    connect(next: string) {
      if (epoch !== next) {
        epoch = next;
        menuRequest++; pageRequest++; searchRequest++;
        pages.clear(); images.clear(); imageReads.clear(); imageCharacters = 0;
        const location = view.location;
        if (location) wanted = { page: location.page, anchor: location.anchor };
        view = Object.freeze({ ...initial, location, canBack: view.canBack, canForward: view.canForward });
        if (asked) void readMenu(); else publish({});
      }
      return () => { if (epoch === next) { epoch = null; menuRequest++; pageRequest++; searchRequest++; } };
    },
    /**
     * Shows the guide: at a page and a heading when they are named, otherwise where the tab was, or at the first page.
     * This is what the tab does when it is opened, and what a request of the host (`alta documentation open`) asks for.
     */
    show(page: string | null = null, anchor: string | null = null) { ensure(page ? { page, anchor } : null); },
    /** Goes to a page of the guide, as a click on the navigation or on a link does. */
    go(page: string, anchor: string | null = null) { ensure({ page, anchor }); },
    back() { const next = moveHistory(history, -1); if (next !== history) { history = next; move(next.entries[next.index], "keep"); } },
    forward() { const next = moveHistory(history, 1); if (next !== history) { history = next; move(next.entries[next.index], "keep"); } },
    /** Asks the host again for what it did not answer: the navigation, or the page the tab is at. */
    retry() {
      if (view.status !== "ready") { if (view.location) wanted = { page: view.location.page, anchor: view.location.anchor }; void readMenu(); }
      else if (view.location) void readPage(view.location.page);
    },
    /** The pictures of the guide, by file name: `peek` answers without reading. */
    images: {
      peek(name: string): DocumentationImageState | undefined {
        const state = images.get(name);
        if (state) { images.delete(name); images.set(name, state); }
        return state;
      },
      load(name: string): Promise<DocumentationImageState> {
        const known = images.get(name);
        if (known) return Promise.resolve(known);
        const expected = epoch;
        if (!expected) return Promise.resolve(failedImage);
        let pending = imageReads.get(name);
        if (!pending) {
          pending = imageSlot(() => readImage(name, expected)).finally(() => { if (imageReads.get(name) === pending) imageReads.delete(name); });
          imageReads.set(name, pending);
        }
        return pending;
      },
    },
    /** Finds a text in the guide. Null when a newer search, or another host, took its place: the answer is not shown. */
    async search(text: string): Promise<readonly DocumentationSearchHit[] | null> {
      const expected = epoch, request = ++searchRequest;
      if (!expected || text.trim().length < 2) return [];
      try {
        const reply = await api.search({ expectedEpoch: expected, text: text.trim().slice(0, 100) }, { timeoutMilliseconds: 20_000 });
        return request !== searchRequest || !sameEpoch(expected) ? null : reply.status === "ok" ? reply.hits : [];
      } catch { return request !== searchRequest ? null : []; }
    },
    /** Asks an agent a question about the guide, or about a page of it. One question is asked at a time. */
    async ask(question: string, page: string | null): Promise<DocumentationAskResult> {
      const expected = epoch;
      if (!expected) return { status: "unavailable", sessionId: null, problem: null };
      if (asking) return { status: "busy", sessionId: null, problem: null };
      asking = true;
      try {
        const reply = await api.ask({ expectedEpoch: expected, question, page }, { timeoutMilliseconds: 120_000 });
        // A chat the host created is shown even when the tab moved on; one of another host is not this window's.
        return sameEpoch(expected) ? { status: reply.status, sessionId: reply.sessionId, problem: reply.problem } : { status: "stale", sessionId: null, problem: null };
      } catch { return { status: "failed", sessionId: null, problem: null }; }
      finally { asking = false; }
    },
    /**
     * How far each page was scrolled, so that coming back to a page shows where the reader was, and the move the tab
     * last showed: a tab that is opened again finds its place already shown, and keeps where the reader was.
     */
    scroll: {
      get: (page: string) => scrolls.get(page) ?? 0,
      has: (page: string) => scrolls.has(page),
      shown: () => placedSerial,
      show(move: number) { placedSerial = move; },
      set(page: string, top: number) { scrolls.delete(page); scrolls.set(page, top); for (const oldest of scrolls.keys()) { if (scrolls.size <= 64) break; scrolls.delete(oldest); } },
    },
  };
}

export type DocumentationHub = ReturnType<typeof createDocumentationHub>;
