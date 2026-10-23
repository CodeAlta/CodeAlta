import { installParagraphSelection, keepToParagraph } from "./paragraphSelection";

// Two cards of a timeline, as the application lays them out, then a third one that is all on one line. With `webKit`
// set, a triple click selects as WebKit does on macOS: from the start of the paragraph to the start of what follows.
document.getElementById("root")!.innerHTML = `<div class="messages" style="width: 640px">
  <div class="timeline-single-row"><article class="message timeline-message message-assistant"><div class="message-body">
    <div class="message-heading"><span><strong>Assistant</strong></span><span class="message-actions"><button type="button">Copy</button></span></div>
    <div class="markdown-content"><p id="first">The first paragraph.</p><pre id="code">line one\nline two</pre><p id="last">The <strong>last</strong> paragraph of the card.</p></div>
  </div></article></div>
  <div class="timeline-single-row"><article class="message timeline-message timeline-compact"><div class="message-body">
    <div class="message-heading"><span id="next"><strong>Turn statistics</strong> · 1m 26s</span></div>
  </div></article></div>
</div>`;

const state = { webKit: false };
installParagraphSelection(document);
document.addEventListener("mousedown", event => {
  if (!state.webKit || event.detail < 3 || !(event.target instanceof Element)) return;
  event.preventDefault();
  const paragraph = event.target.closest("p")!, next = event.target.closest(".timeline-single-row")!.nextElementSibling!.querySelector("#next strong")!.firstChild!;
  getSelection()!.setBaseAndExtent(paragraph.firstChild!, 0, next, 0);
});

/** Runs `keepToParagraph` on a range from `start` to `end`, each a node of the page and an offset, and returns what it then selects. */
function keep(start: [Node, number], end: [Node, number]) {
  const range = document.createRange();
  range.setStart(...start);
  range.setEnd(...end);
  const changed = keepToParagraph(range, document.querySelector(".timeline-message")!);
  return { changed, text: range.toString() };
}

Object.assign(window, { paragraphFixture: { state, keep } });
