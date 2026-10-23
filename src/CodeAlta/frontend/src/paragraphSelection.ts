/** The cards whose paragraphs a triple click keeps to. */
const cardSelector = ".timeline-message";

/** The block that holds `node`: its nearest element inside `card` that is not laid out inline, or the card itself. */
function blockOf(node: Node, card: Element): Element {
  for (let element = node instanceof Element ? node : node.parentElement; element && element !== card && card.contains(element); element = element.parentElement) {
    const display = getComputedStyle(element).display;
    if (!display.startsWith("inline") && display !== "contents") return element;
  }
  return card;
}

/**
 * Ends `range` with the paragraph it starts in, when it runs past the block of that paragraph. A triple click selects
 * a paragraph up to the start of whatever follows it, which after the last paragraph of a card is the controls of the
 * card or the next card, and WebKit (macOS) paints all that is between as selected. Returns whether the range was changed.
 */
export function keepToParagraph(range: Range, card: Element): boolean {
  if (!card.contains(range.startContainer)) return false;
  const block = blockOf(range.startContainer, card);
  if (block.contains(range.endContainer)) return false;
  range.setEnd(block, block.childNodes.length);
  return true;
}

/**
 * Keeps the selection of a triple click in a timeline card to the paragraph clicked. The selection is fixed once, when
 * the browser has made it on the press, so that a drag that follows still extends it paragraph by paragraph.
 */
export function installParagraphSelection(target: Document): void {
  let card: Element | null = null;
  const keep = () => {
    const selection = target.getSelection();
    if (!card || !selection || selection.rangeCount !== 1) return;
    const range = selection.getRangeAt(0).cloneRange();
    if (!keepToParagraph(range, card)) return;
    card = null;
    selection.removeAllRanges();
    selection.addRange(range);
  };
  target.addEventListener("mousedown", event => {
    card = event.detail >= 3 && event.button === 0 && !event.shiftKey && event.target instanceof Element ? event.target.closest(cardSelector) : null;
  }, true);
  target.addEventListener("selectionchange", keep);
  // The first move of a drag comes before the selection is extended: the selection is still the one of the press.
  target.addEventListener("mousemove", () => { keep(); card = null; }, true);
  target.addEventListener("mouseup", () => { keep(); card = null; }, true);
}
