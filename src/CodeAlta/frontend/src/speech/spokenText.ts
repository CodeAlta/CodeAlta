// What a dictation writes into a prompt. The host tells the whole text heard so far each time, so the page
// keeps how much of it is written and writes what comes after.

/** The part of the final text of a dictation that is not in the prompt yet, given how much of it was written. */
export function unwritten(finalText: string, written: number): string {
  return written >= finalText.length ? "" : finalText.slice(Math.max(0, written));
}

// What is written against the word before it: the punctuation that ends or continues a phrase.
const closing = /^[.,;:!?…)\]}%»”’]/u;
// What the next word is written against: an opening bracket or quote, a slash, a dash that joins.
const opening = /[([{«“‘/]$/u;

/**
 * A spoken piece as it goes into a prompt at the caret, after `before` (the text that precedes the caret, or
 * its last characters). It is separated by a space from a word it follows, and not from the start of the
 * prompt, of a line, or from the space that is already there.
 */
export function spokenInsertion(before: string, piece: string): string {
  const text = piece.trim();
  if (!text) return "";
  return before !== "" && !/\s$/u.test(before) && !opening.test(before) && !closing.test(text) ? ` ${text}` : text;
}
