// Disposable page; the Markdown preview of the code editor, as `ProjectEditor` renders it.
import { StrictMode, createElement } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import { MarkdownContent } from "../MarkdownContent";
import { MarkdownPreviewBox } from "./MarkdownPreviewBox";

// A clipboard that keeps what is written to it: the text, and the size of a picture.
const clipboard = { text: [] as string[], images: [] as { type: string; width: number; height: number; corner: number[] }[] };
Object.defineProperty(navigator, "clipboard", { configurable: true, value: {
  async writeText(text: string) { clipboard.text.push(text); },
  async write(items: ClipboardItem[]) {
    for (const item of items) {
      const blob = await item.getType("image/png");
      const bitmap = await createImageBitmap(blob);
      const canvas = new OffscreenCanvas(bitmap.width, bitmap.height), context = canvas.getContext("2d")!;
      context.drawImage(bitmap, 0, 0);
      clipboard.images.push({ type: blob.type, width: bitmap.width, height: bitmap.height, corner: [...context.getImageData(0, 0, 1, 1).data] });
      bitmap.close();
    }
  },
} });

// The page has the styles of the application, not Blueprint's: the color behind a diagram is the one Blueprint gives it.
document.documentElement.style.setProperty("--bp-palette-dark-gray-3", "#2f343c");

const root = createRoot(document.getElementById("root")!);
const fixture = {
  // Under StrictMode, as in the application. A review shows the lines of the blocks (`sourceLines`).
  render(source: string, sourceLines: boolean) {
    flushSync(() => root.render(createElement(StrictMode, null,
      createElement(MarkdownPreviewBox, { label: "plan.md", review: sourceLines, children: createElement(MarkdownContent, { source, document: true, sourceLines }) }))));
  },
  clear() { flushSync(() => root.render(null)); },
};
Object.assign(window, { previewFixture: fixture, previewClipboard: clipboard });
