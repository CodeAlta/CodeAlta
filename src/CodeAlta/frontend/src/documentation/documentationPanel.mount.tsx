// Disposable page: the Documentation tab over a guide the test plays, under StrictMode as in the application.
import { StrictMode, createElement } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import type { DocumentationBlock, DocumentationMenuItem, DocumentationPageResponse } from "#neoastra";
import { MarkdownLinksContext } from "../MarkdownContent";
import { DocumentationPanel } from "./DocumentationPanel";
import { createDocumentationHub, type DocumentationApi } from "./documentationHub";

const epoch = "6f1d2c3b-4a59-4e68-9b7a-0c1d2e3f4a5b";
const container = document.getElementById("root")!;
const root = createRoot(container);
const text = (markdown: string): DocumentationBlock => ({ kind: "markdown", markdown, image: null, svg: null, alt: null, caption: null, width: null, height: null });
const item = (path: string, title: string, icon: string | null = null, depth = 0, parent: string | null = null): DocumentationMenuItem => ({ path, title, icon, depth, parent });
// One pixel, as a picture the window shows.
const pixel = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";
const filler = Array.from({ length: 40 }, (_, index) => `Paragraph ${index + 1} of the page, long enough to make the page taller than the tab so that a heading further down is out of sight.`).join("\n\n");

const state = {
  executed: 0, web: [] as string[], notices: [] as string[], sessions: [] as string[], providers: 0, activated: 0,
  calls: { menu: 0, page: [] as string[], image: [] as string[], search: [] as string[], ask: [] as { question: string | null; page: string | null }[] },
  /** What the next question gets. */
  askReply: { status: "ok", sessionId: "chat-1", problem: null } as { status: string; sessionId: string | null; problem: string | null },
};

const pages: Record<string, DocumentationPageResponse> = {
  "readme.md": { status: "ok", path: "readme.md", title: "User Guide", blocks: [
    text([
      "# User Guide",
      "",
      "See [Sessions](sessions.md#queue), [the web](https://example.com/guide), [outside](../outside.md), [a file](file:///C:/secret.txt) and [nowhere](nowhere.md).",
      "",
      "<img src=\"https://remote.invalid/x.png\" onerror=\"documentationFixture.state.executed++\"><script>documentationFixture.state.executed++</script>",
      "<a href=\"javascript:documentationFixture.state.executed++\">script link</a> <iframe src=\"https://remote.invalid/\"></iframe>",
      "",
      "## Start here",
      "",
      "First steps.",
      "",
      "## Start here",
      "",
      "### Details, and *more*",
      "",
      "| Feature | Desktop |",
      "| --- | --- |",
      "| Sessions | ✓ |",
    ].join("\n")),
    { kind: "figure", markdown: null, image: "alta-desktop-home.webp", svg: null, alt: "The workspace", caption: "The main <code>workspace</code>, see [Git](plugins/git.md).", width: 1280, height: 800 },
    { kind: "figure", markdown: null, image: null, alt: "Prompt flow", caption: "How a prompt is composed.", width: null, height: null,
      svg: "<svg viewBox=\"0 0 960 430\" xmlns=\"http://www.w3.org/2000/svg\" onload=\"parent.documentationFixture.state.executed++\"><script>parent.documentationFixture.state.executed++</script><rect width=\"960\" height=\"430\" fill=\"#123\"/></svg>" },
    { kind: "figure", markdown: null, image: "missing.webp", svg: null, alt: "Not there", caption: null, width: 640, height: 400 },
    text("Last words of the first page."),
  ] },
  "sessions.md": { status: "ok", path: "sessions.md", title: "Sessions", blocks: [text(`# Sessions\n\n${filler}\n\n## Queue\n\nA session keeps a **prompt queue** while it runs.\n\n${filler}\n\n## More\n\nThe end of the page.`)] },
  "plugins/readme.md": { status: "ok", path: "plugins/readme.md", title: "Plugins", blocks: [text("# Plugins\n\nWhat plugins add.")] },
  "plugins/git.md": { status: "ok", path: "plugins/git.md", title: "Git", blocks: [text("# Git\n\n## Sign in\n\nSign in for issues.")] },
};

const api: DocumentationApi = {
  async menu() {
    state.calls.menu++;
    return { status: "ok", home: "readme.md", canAsk: true,
      items: [item("readme.md", "User Guide", "book"), item("sessions.md", "Sessions", "diagram-3"), item("plugins/readme.md", "Plugins", "puzzle"), item("plugins/git.md", "Git", null, 1, "plugins/readme.md")],
      pages: Object.values(pages).map(page => ({ path: page.path!, title: page.title! })) };
  },
  async page(request) {
    state.calls.page.push(request.path ?? "");
    return pages[request.path ?? ""] ?? { status: "not_found", path: null, title: null, blocks: [] };
  },
  async image(request) {
    state.calls.image.push(request.name ?? "");
    return request.name === "alta-desktop-home.webp" ? { status: "ok", mediaType: "image/png", base64: pixel } : { status: "not_found", mediaType: null, base64: null };
  },
  async search(request) {
    state.calls.search.push(request.text ?? "");
    return { status: "ok", hits: (request.text ?? "").toLowerCase().includes("queue") ? [{ path: "sessions.md", title: "Sessions", heading: "Queue", text: "A session keeps a prompt queue while it runs." }] : [] };
  },
  async ask(request) {
    state.calls.ask.push({ question: request.question, page: request.page });
    return { ...state.askReply };
  },
};

// The hub is the window's: it lasts while the tab is closed and opened again.
const hub = createDocumentationHub(api);
hub.connect(epoch);

const fixture = {
  state, hub,
  /** Shows the tab in a pane of a width. */
  mount(options: { width?: number; visible?: boolean } = {}) {
    container.style.width = `${options.width ?? 1320}px`;
    flushSync(() => root.render(createElement(StrictMode, null, createElement(MarkdownLinksContext.Provider, { value: (address: string) => { state.web.push(address); } },
      createElement(DocumentationPanel, {
        hub, visible: options.visible ?? true,
        onActivate: () => { state.activated++; },
        onOpenSession: (id: string) => { state.sessions.push(id); },
        onProviders: () => { state.providers++; },
        onNotice: (message: string) => { state.notices.push(message); },
      })))));
  },
  /** Closes the tab. */
  unmount() { flushSync(() => root.render(null)); },
  /** The colors of the window. */
  theme(name: "dark" | "light") {
    document.documentElement.dataset.theme = name;
    document.body.classList.toggle("bp6-dark", name === "dark");
  },
  /** Types in a field as a user does, for React to see it. */
  type(element: HTMLInputElement | HTMLTextAreaElement, value: string) {
    const prototype = element instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
    Object.getOwnPropertyDescriptor(prototype, "value")!.set!.call(element, value);
    element.dispatchEvent(new Event("input", { bubbles: true }));
  },
};
Object.assign(window, { documentationFixture: fixture });
