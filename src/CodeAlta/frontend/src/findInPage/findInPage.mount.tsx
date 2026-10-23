// Disposable page; two rendered Markdown pages that each find in themselves (Ctrl+F), as the preview of the code
// editor and a file under review do, side by side in two tabs of the window.
import { StrictMode, createElement as h, useState } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import { MarkdownContent } from "../MarkdownContent";
import { findKeyKept } from "./findInPage";
import { useFindInPage } from "./useFindInPage";

function Page({ name, source }: { name: string; source: string }) {
  const [box, setBox] = useState<HTMLDivElement | null>(null);
  const finder = useFindInPage({ page: () => box, available: !!box });
  return h("section", { id: name, "data-find-keys": finder.available || undefined, tabIndex: -1, style: { position: "relative", display: "flex", height: "260px" },
    onKeyDown: event => { if (finder.keyDown(event)) event.stopPropagation(); } },
    finder.bar,
    h("div", { className: "editor-markdown", ref: setBox, tabIndex: 0, style: { flex: "1 1 auto", overflow: "auto" } },
      // The gutter of a review is not a word of the page.
      h("div", { className: "review-block-gutter" }, "plan gutter"),
      h(MarkdownContent, { source, document: true })));
}

const state = { searchedSessions: 0 };
// The window searches the sessions with Ctrl+F, except where a page keeps the key.
window.addEventListener("keydown", event => {
  if (event.key.toLowerCase() !== "f" || !event.ctrlKey) return;
  if (findKeyKept(event, event.target instanceof Element ? event.target : null)) return;
  event.preventDefault(); event.stopPropagation(); state.searchedSessions++;
}, true);

const root = createRoot(document.getElementById("root")!);
const fixture = {
  state,
  render(first: string, second: string) {
    flushSync(() => root.render(h(StrictMode, null, h(Page, { name: "first", source: first }), h(Page, { name: "second", source: second }))));
  },
  type(element: HTMLInputElement, value: string) {
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value")!.set!.call(element, value);
    element.dispatchEvent(new Event("input", { bubbles: true }));
  },
};
Object.assign(window, { findFixture: fixture });
