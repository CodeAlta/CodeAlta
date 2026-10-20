// Disposable page; the Markdown preview of the code editor, as `ProjectEditor` renders it.
import { StrictMode, createElement } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import { MarkdownContent } from "../MarkdownContent";

const root = createRoot(document.getElementById("root")!);
const fixture = {
  // Under StrictMode, as in the application. A review shows the lines of the blocks (`sourceLines`).
  render(source: string, sourceLines: boolean) {
    flushSync(() => root.render(createElement(StrictMode, null,
      createElement("div", { className: "editor-markdown" }, createElement(MarkdownContent, { source, document: true, sourceLines })))));
  },
  clear() { flushSync(() => root.render(null)); },
};
Object.assign(window, { previewFixture: fixture });
