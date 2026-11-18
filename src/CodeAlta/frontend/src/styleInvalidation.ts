// A selector whose ancestor part holds a negated `:hover` or `:focus-within` costs WebKit a walk of the whole subtree of
// every element whose state changes, and the focus moves through every ancestor up to the document: with the transcript
// of a long session in the window (20,000 elements), each change of the focus took 50 to 75 ms instead of 3, and the
// change of a tab moves the focus three times. The positive form of the same selector is found by the class of its
// ancestor and costs nothing.

const negated = /:not\((?:[^()]|\([^()]*\))*:(?:hover|focus-within)/;

// The parts of a selector between its descendant combinators: the other combinators (`>`, `+`, `~`) and what is in
// parentheses or brackets stay inside a part.
function descendantParts(selector: string): string[] {
  const parts: string[] = [];
  let depth = 0, start = 0;
  for (let index = 0; index < selector.length; index++) {
    const char = selector[index];
    if (char === "(" || char === "[") depth++;
    else if (char === ")" || char === "]") depth--;
    else if (depth === 0 && /\s/.test(char)) {
      let next = index;
      while (next < selector.length && /\s/.test(selector[next])) next++;
      // A space around another combinator is not a descendant combinator.
      if (!/[>+~]/.test(selector[next] ?? "") && !/[>+~]/.test(selector.slice(start, index).trimEnd().slice(-1)) && selector.slice(start, index).trim()) {
        parts.push(selector.slice(start, index));
        start = next;
      }
      index = next - 1;
    }
  }
  parts.push(selector.slice(start));
  return parts;
}

function selectors(list: string): string[] {
  const found: string[] = [];
  let depth = 0, start = 0;
  for (let index = 0; index < list.length; index++) {
    const char = list[index];
    if (char === "(" || char === "[") depth++;
    else if (char === ")" || char === "]") depth--;
    else if (char === "," && depth === 0) { found.push(list.slice(start, index)); start = index + 1; }
  }
  found.push(list.slice(start));
  return found.map(selector => selector.trim()).filter(Boolean);
}

/**
 * The selectors of a stylesheet that negate `:hover` or `:focus-within` on an ancestor of what they style, as
 * `.row:not(:hover) .action` does: each one makes every change of the hover or of the focus walk whole subtrees.
 */
export function negatedStateAncestors(css: string): string[] {
  const found: string[] = [];
  for (const match of css.replace(/\/\*[\s\S]*?\*\//g, "").matchAll(/([^{}]+)\{/g)) {
    const prelude = match[1].trim();
    if (!prelude || prelude.startsWith("@")) continue;
    for (const selector of selectors(prelude)) {
      if (descendantParts(selector).slice(0, -1).some(part => negated.test(part))) found.push(selector);
    }
  }
  return found;
}

const monacoLockedHover = ".workbench-hover-container:not(:focus-within).locked .workbench-hover-pointer:after";

/**
 * Monaco's stylesheet of its hovers without the one selector of that kind it holds. The rule that follows it in the
 * file styles the same element when the hover has the focus, with the same specificity once the negation is gone, so
 * the hover looks the same.
 */
export function monacoHoverStyle(css: string): string {
  return css.replace(monacoLockedHover, ".workbench-hover-container.locked .workbench-hover-pointer:after");
}
