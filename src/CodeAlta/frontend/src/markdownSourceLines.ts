/**
 * The attribute that a rendered block of Markdown carries when it is rendered with its source lines: `S-E`, the first
 * and the last line of the block in the text that was rendered, 1-based and inclusive (front matter included).
 */
export const sourceLinesAttribute = "data-source-lines";

/** The lines of a block, read from its {@link sourceLinesAttribute}; null when the value is not `S-E` with `1 <= S <= E`. */
export function parseSourceLines(value: string | null): { start: number; end: number } | null {
  const match = value === null ? null : /^([1-9]\d{0,8})-([1-9]\d{0,8})$/.exec(value);
  if (!match) return null;
  const start = Number(match[1]), end = Number(match[2]);
  return start <= end ? { start, end } : null;
}

/** The blocks rendered in a container that say which lines they come from, in the order of the document. A block can hold others (a list item, its list). */
export function sourceBlocks(container: HTMLElement): Array<{ element: HTMLElement; start: number; end: number }> {
  const blocks: Array<{ element: HTMLElement; start: number; end: number }> = [];
  for (const element of Array.from(container.querySelectorAll<HTMLElement>(`[${sourceLinesAttribute}]`))) {
    const lines = parseSourceLines(element.getAttribute(sourceLinesAttribute));
    if (lines) blocks.push({ element, ...lines });
  }
  return blocks;
}
