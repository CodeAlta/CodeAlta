// Disposable page: the Documentation tab over a guide the test plays, under StrictMode as in the application.
import { Fragment, StrictMode, createElement, useLayoutEffect, useRef, useState } from "react";
import { createPortal, flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import type { DocumentationBlock, DocumentationMenuItem, DocumentationPageResponse } from "#neoastra";
import { MarkdownLinksContext } from "../MarkdownContent";
import { SessionTabStrip } from "../SessionTabStrip";
import { closeFileTab, documentationTab, emptyFileTabs, openFileTab, type FileTabs } from "../fileTabs";
import { emptySessionTabs } from "../sessionTabs";
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

/**
 * A pane as the layout of the window makes one: the content of a tab is drawn into an element that is in no document
 * yet, and the pane takes that element in once it is itself drawn. The content is there before it has a size.
 */
function Pane({ children }: { children: React.ReactNode }) {
  const moveable = useRef<HTMLDivElement | null>(null);
  if (!moveable.current) { moveable.current = document.createElement("div"); moveable.current.style.height = "100%"; }
  return createElement(Fragment, null, createPortal(children, moveable.current), createElement(PaneSlot, { content: moveable.current }));
}
function PaneSlot({ content }: { content: HTMLElement }) {
  const self = useRef<HTMLDivElement>(null);
  useLayoutEffect(() => { if (self.current && content.parentElement !== self.current) self.current.appendChild(content); }, [content]);
  return createElement("div", { ref: self, className: "fixture-pane", style: { height: "100%" } });
}

// The hub is the window's: it lasts while the tab is closed and opened again.
const hub = createDocumentationHub(api);
hub.connect(epoch);

// The production dock, not an imitation of its portal: opening/closing changes its model while the hub survives.
function Dock() {
  const [files, setFiles] = useState<FileTabs>(() => openFileTab(emptyFileTabs(), documentationTab));
  return <div style={{ height: "100%", position: "relative" }} onKeyDown={event => {
    if (event.ctrlKey && event.key.toLowerCase() === "w") {
      event.preventDefault();
      setFiles(value => closeFileTab(value, documentationTab));
    }
  }}>
    <button className="fixture-book" style={{ position: "absolute", right: 0, top: 0, zIndex: 5 }} onClick={() => {
      hub.show(); setFiles(value => openFileTab(value, documentationTab));
    }}>Documentation</button>
    <SessionTabStrip state={emptySessionTabs()} select={() => setFiles(value => ({ ...value, active: null }))} close={() => { }} reopen={() => { }} capture={() => () => true}
      files={files} selectFile={tab => setFiles(value => ({ ...value, active: tab }))} closeFile={tab => setFiles(value => closeFileTab(value, tab))}
      renderFile={(_tab, visible) => <DocumentationPanel hub={hub} visible={visible} onActivate={() => { }} onOpenSession={() => { }} onProviders={null} onNotice={() => { }} />}>
      <div>Another tab</div>
    </SessionTabStrip>
  </div>;
}

const fixture = {
  state, hub,
  /** This shipped page only: preserve the Catalog's text/figure blocks, including the split at its unshipped screenshot. */
  prompts(source: string) {
    const blocks: DocumentationBlock[] = [];
    const body = source.replace(/^---\r?\n[\s\S]*?\r?\n---\r?\n/, "").replace(/^\{\.table\}\s*$/gm, "");
    let start = 0;
    for (const match of body.matchAll(/<figure\b[\s\S]*?<\/figure>/g)) {
      blocks.push(text(body.slice(start, match.index)));
      const figure = new DOMParser().parseFromString(match[0], "text/html");
      const svg = figure.querySelector("svg");
      if (svg) blocks.push({ kind: "figure", markdown: null, image: null, svg: svg.outerHTML, alt: svg.querySelector("title")?.textContent ?? "",
        caption: figure.querySelector("figcaption")?.textContent ?? null, width: null, height: null });
      start = match.index + match[0].length;
    }
    blocks.push(text(body.slice(start)));
    pages["prompts.md"] = { status: "ok", path: "prompts.md", title: "Agent Prompts", blocks };
  },
  /** Shows the real FlexLayout dock; Ctrl+W closes just Documentation and the book opens it again. */
  dock() {
    container.style.width = "1320px";
    container.style.height = "1000px";
    container.style.display = "";
    flushSync(() => root.render(<StrictMode><Dock /></StrictMode>));
  },
  /** Shows the tab in a pane of a width. */
  mount(options: { width?: number; visible?: boolean; pane?: boolean; hidden?: boolean } = {}) {
    container.style.width = `${options.width ?? 1320}px`;
    container.style.display = options.hidden ? "none" : "";
    const panel = createElement(MarkdownLinksContext.Provider, { value: (address: string) => { state.web.push(address); } },
      createElement(DocumentationPanel, {
        hub, visible: options.visible ?? true,
        onActivate: () => { state.activated++; },
        onOpenSession: (id: string) => { state.sessions.push(id); },
        onProviders: () => { state.providers++; },
        onNotice: (message: string) => { state.notices.push(message); },
      }));
    flushSync(() => root.render(createElement(StrictMode, null, options.pane ? createElement(Pane, null, panel) : panel)));
  },
  /** Shows a pane that was drawn hidden. */
  reveal() { container.style.display = ""; },
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
