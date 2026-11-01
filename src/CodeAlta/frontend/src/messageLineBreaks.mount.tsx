import { createElement } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import { TimelineMessage } from "./TimelineMessage";
import type { TimelineItem } from "./timeline";

// A message of the timeline, with the stylesheet of the application, to measure where its lines fall.
const root = createRoot(document.getElementById("root")!);
function show(category: TimelineItem["category"], source: string, summary: string | null = null) {
  flushSync(() => root.render(createElement(TimelineMessage, { item: { key: "literal", eventType: "contentCompleted", category, title: "You", icon: "user",
    timestamp: "2026-01-01T00:00:00Z", subtitle: null, summary, summaryIsCode: false, detailMarkdown: null, details: null,
    markdown: source, copyMarkdown: source, metadata: [], detailsLabel: "Details", truncated: false, bodyOmitted: false } })));
}
/** The top of each word that the text of the message holds, whether it is on a line of its own or not. */
function wordTops(...words: readonly string[]) {
  const content = document.querySelector(".markdown-content")!;
  const walker = document.createTreeWalker(content, NodeFilter.SHOW_TEXT);
  const tops: Record<string, number> = {};
  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
    for (const word of words) {
      const at = node.textContent!.indexOf(word);
      if (at < 0) continue;
      const range = document.createRange();
      range.setStart(node, at); range.setEnd(node, at + word.length);
      tops[word] = range.getBoundingClientRect().top;
    }
  }
  return words.map(word => tops[word]);
}
Object.assign(window, { lineBreaks: { show, wordTops } });
