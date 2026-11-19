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

// A `:has()` is checked again when something changes in what it looks at, and the browser then computes again the style
// of what the selectors that hold one can style. Chromium keeps one list of that for all of them: the classes, the
// attributes and the tags of what each selector styles behind its `:has()`. A selector that styles there an element
// named by its tag alone (`dialog:has(> .window) pre`), or any element (`> *`), puts every such element of the page on
// the list, and a `:has()` on the element that holds the whole window makes every element put in the page or taken out
// of it a change to check. With both, drawing one more row in a long timeline computed the style of the whole timeline
// (8 ms with 23,000 elements in the window, 0.1 ms without), and showing another tab took 20 ms more.

// The compounds of a selector, in order: what its combinators separate.
function compounds(selector: string): string[] {
  const parts: string[] = [];
  let depth = 0, start = 0;
  for (let index = 0; index < selector.length; index++) {
    const char = selector[index];
    if (char === "(" || char === "[") depth++;
    else if (char === ")" || char === "]") depth--;
    else if (depth === 0 && /[\s>+~]/.test(char)) {
      if (selector.slice(start, index).trim()) parts.push(selector.slice(start, index).trim());
      start = index + 1;
    }
  }
  if (selector.slice(start).trim()) parts.push(selector.slice(start).trim());
  return parts;
}

// Whether a compound names what it matches by a class, an id or an attribute, in each of its forms.
function named(compound: string): boolean {
  let outside = "", depth = 0;
  for (const char of compound.replace(/::[\w-]+(\([^)]*\))?/g, "")) {
    if (char === "(") depth++;
    else if (char === ")") depth--;
    else if (depth === 0) outside += char;
  }
  if (/[.#[]/.test(outside)) return true;
  const any = /:(?:is|where)\(((?:[^()]|\([^()]*\))*)\)/.exec(compound);
  return !!any && selectors(any[1]).every(alternative => named(compounds(alternative).at(-1) ?? ""));
}

const windowShell = /^(?:html|body|:root|#root|\.app-shell|\.ide-shell|\.workspace-shell)(?![\w-])/;

/**
 * The selectors of a stylesheet whose `:has()` costs every change of the page: the ones that style, behind a `:has()`,
 * elements they name by their tag alone or not at all, and the ones that look, from the element that holds the whole
 * window, at everything in it (`.ide-shell:has(.tag)`; a `:has()` that names a child, `:has(> .controls)`, does not).
 */
export function costlyHasSelectors(css: string): string[] {
  const found: string[] = [];
  for (const match of css.replace(/\/\*[\s\S]*?\*\//g, "").matchAll(/([^{}]+)\{/g)) {
    const prelude = match[1].trim();
    if (!prelude || prelude.startsWith("@")) continue;
    for (const selector of selectors(prelude)) {
      if (!selector.includes(":has(")) continue;
      const parts = compounds(selector);
      const ancestors = parts.slice(0, -1);
      if (ancestors.some(part => part.includes(":has(")) && !named(parts.at(-1)!)
        || parts.some(part => windowShell.test(part) && /:has\(\s*[^\s>+~]/.test(part))) found.push(selector);
    }
  }
  return found;
}

const monacoLockedHover =".workbench-hover-container:not(:focus-within).locked .workbench-hover-pointer:after";

/**
 * Monaco's stylesheet of its hovers without the one selector of that kind it holds. The rule that follows it in the
 * file styles the same element when the hover has the focus, with the same specificity once the negation is gone, so
 * the hover looks the same.
 */
export function monacoHoverStyle(css: string): string {
  return css.replace(monacoLockedHover, ".workbench-hover-container.locked .workbench-hover-pointer:after");
}
