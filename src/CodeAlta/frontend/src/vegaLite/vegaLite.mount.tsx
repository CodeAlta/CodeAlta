// Disposable page; the charts of `vega-lite` blocks in a message of a session and in the Markdown preview of the code editor,
// as the window renders them, with a host that reads the data files of a project from memory.
import { StrictMode, createElement } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import { MarkdownChartDataContext, MarkdownContent, MarkdownLinkScopeContext } from "../MarkdownContent";
import { MarkdownPreviewBox } from "../editor/MarkdownPreviewBox";
import type { MarkdownLinkScope } from "../markdownLinks";
import type { ChartFile } from "./vegaLite";

// A clipboard that keeps what is written to it: the text, and the size of a picture.
const clipboard = { text: [] as string[], images: [] as { type: string; width: number; height: number }[] };
Object.defineProperty(navigator, "clipboard", { configurable: true, value: {
  async writeText(text: string) { clipboard.text.push(text); },
  async write(items: ClipboardItem[]) {
    for (const item of items) {
      const blob = await item.getType("image/png");
      const bitmap = await createImageBitmap(blob);
      clipboard.images.push({ type: blob.type, width: bitmap.width, height: bitmap.height });
      bitmap.close();
    }
  },
} });

// The files of the project, as the host would read them below the folder of a session or a document; and what was asked.
const files = new Map<string, ChartFile>();
const reads: { path: string; scope: MarkdownLinkScope }[] = [];
const readChartData = async (path: string, scope: MarkdownLinkScope): Promise<ChartFile> => {
  reads.push({ path, scope });
  const folder = scope.sessionId ? "" : scope.directory ? `${scope.directory}/` : "";
  return files.get(folder + path) ?? { status: "not_found" };
};

const root = createRoot(document.getElementById("root")!);
const fixture = {
  // Under StrictMode, as in the application: a message of a session, or a document of the project (in the folder `docs`).
  render(source: string, surface: "message" | "document") {
    const scope: MarkdownLinkScope = surface === "message" ? { sessionId: "s1" } : { projectId: "p1", directory: "docs" };
    const content = surface === "message" ? createElement(MarkdownContent, { source, timelineCodeBlocks: true })
      : createElement(MarkdownPreviewBox, { label: "report.md", review: false, children: createElement(MarkdownContent, { source, document: true }) });
    flushSync(() => root.render(createElement(StrictMode, null, createElement(MarkdownChartDataContext.Provider, { value: readChartData },
      createElement(MarkdownLinkScopeContext.Provider, { value: scope }, content)))));
  },
  clear() { flushSync(() => root.render(null)); },
  setFile(path: string, text: string | null) { if (text === null) files.delete(path); else files.set(path, { status: "ok", text }); },
};
Object.assign(window, { chartFixture: fixture, chartClipboard: clipboard, chartReads: reads });
