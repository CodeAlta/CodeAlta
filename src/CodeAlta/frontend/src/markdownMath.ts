import type { MarkdownIt, StateBlock, StateInline } from "markdown-it";

// Math is written as models and people write it in Markdown: `$…$`, `\(…\)` and `` $`…`$ `` inline, `$$…$$`, `\[…\]` and
// the display environments of LaTeX on lines of their own (a `math` fenced block is a fence, dressed by the boundary).
// The rules of `$` are Pandoc's, as `@mdit/plugin-tex` (MIT) writes them: `$5 and $10` or `$HOME` stay text. A display
// block that is not closed stays text: a message being written does not show half a formula.

/** The environments of LaTeX that are display math by themselves, written alone on their lines. */
const displayEnvironments: ReadonlySet<string> = new Set(["equation", "equation*", "align", "align*", "alignat", "alignat*",
  "gather", "gather*", "multline", "multline*", "flalign", "flalign*", "CD"]);

const isWordCode = (code: number) => code >= 48 && code <= 57 || code >= 65 && code <= 90 || code >= 97 && code <= 122 || code === 95;
const isDigitCode = (code: number) => code >= 48 && code <= 57;
const isSpaceCode = (code: number) => code === 0x20 || code === 0x09 || code === 0x0a || code === 0x0d;

// The backslashes just before a place: an odd count escapes the character there.
function backslashesBefore(text: string, at: number, from = 0): number {
  let count = 0;
  while (at - count - 1 >= from && text.charCodeAt(at - count - 1) === 0x5c) count++;
  return count;
}

// The first `close` from `from` on that no backslash escapes, before `end`; -1 when there is none.
function unescapedIndex(text: string, close: string, from: number, end = text.length): number {
  for (let at = text.indexOf(close, from); at >= 0 && at + close.length <= end; at = text.indexOf(close, at + 1)) {
    if (backslashesBefore(text, at, from) % 2 === 0) return at;
  }
  return -1;
}

// A `$` opens when it follows the start, a space or a sign (not a letter, a digit or another `$`), and a space does not follow it.
function canOpenDollar(text: string, at: number, end: number): boolean {
  const before = text.charCodeAt(at - 1), after = text.charCodeAt(at + 1);
  return at + 1 < end && before !== 0x24 && (at === 0 || isSpaceCode(before) || !isWordCode(before)) && !isSpaceCode(after);
}

// A `$` closes when no space precedes it and neither a digit nor another `$` follows it.
function canCloseDollar(text: string, at: number, end: number): boolean {
  const before = text.charCodeAt(at - 1), after = at + 1 < end ? text.charCodeAt(at + 1) : NaN;
  return !isSpaceCode(before) && after !== 0x24 && !isDigitCode(after);
}

// The closing `$` of an inline formula opened at `at`: a `$` that cannot close ends the search, unless it stands alone
// between spaces (`costs $ 5`).
function closingDollar(text: string, at: number, end: number): number {
  for (let candidate = at + 1; ;) {
    candidate = text.indexOf("$", candidate);
    if (candidate < 0 || candidate >= end) return -1;
    if (backslashesBefore(text, candidate) % 2 === 1) { candidate++; continue; }
    if (canCloseDollar(text, candidate, end)) return candidate;
    const after = candidate + 1 < end ? text.charCodeAt(candidate + 1) : NaN;
    if (isSpaceCode(text.charCodeAt(candidate - 1)) && (Number.isNaN(after) || isSpaceCode(after))) { candidate++; continue; }
    return -1;
  }
}

/** A formula in a text: where it starts and ends, delimiters included, and its TeX between them. */
export type MathSpan = Readonly<{
  start: number; end: number; tex: string; display: boolean;
  /** The delimiters as written; empty for an environment, which is part of its TeX. */
  open: string; close: string;
  /** False for a formula still being written, which runs to the end of its line (inline) or of the text (display). */
  closed: boolean;
}>;

// A formula still being written is one only when it looks like TeX, not like a price.
const texLike = /[\\^_{}]/;

/**
 * The formulas of a text, as the parser reads them, skipping code. With `unclosed`, a formula that is not closed yet
 * is one too (see {@link MathSpan.closed}), as the prompt shows it while it is being written.
 */
export function mathSpans(text: string, unclosed = false): MathSpan[] {
  const spans: MathSpan[] = [];
  const length = text.length;
  // Where the paragraph that holds a place ends: an inline formula never crosses a blank line.
  const paragraphEnd = (from: number) => { const blank = /\n[ \t]*\n/g; blank.lastIndex = from; return blank.exec(text)?.index ?? length; };
  const lineEnd = (from: number) => { const at = text.indexOf("\n", from); return at < 0 ? length : at; };
  let at = 0;
  while (at < length) {
    const code = text.charCodeAt(at);
    const lineStart = at === 0 || text.charCodeAt(at - 1) === 0x0a;
    // A fenced code block runs to its closing fence, or to the end of the text.
    if (lineStart) {
      const fence = /^[ \t]{0,3}(`{3,}|~{3,})/.exec(text.slice(at, at + 64));
      if (fence) {
        const marker = fence[1];
        const closing = new RegExp(`^[ \\t]{0,3}${marker[0] === "`" ? "`" : "~"}{${marker.length},}[ \\t]*$`, "m");
        const rest = text.slice(lineEnd(at) + 1);
        const found = closing.exec(rest);
        at = found ? lineEnd(at) + 1 + found.index + found[0].length : length;
        continue;
      }
    }
    if (code === 0x60) { // A code span: as many backticks close it.
      let run = 1;
      while (text.charCodeAt(at + run) === 0x60) run++;
      const close = text.indexOf("`".repeat(run), at + run);
      at = close < 0 ? at + run : close + run;
      continue;
    }
    if (code === 0x5c) {
      const next = text.charCodeAt(at + 1);
      if (next === 0x28 || next === 0x5b) { // \( or \[
        const display = next === 0x5b, close = display ? "\\]" : "\\)";
        const end = display ? length : paragraphEnd(at);
        const found = unescapedIndex(text, close, at + 2, end);
        if (found > at + 2) {
          spans.push({ start: at, end: found + 2, tex: text.slice(at + 2, found), display, open: display ? "\\[" : "\\(", close, closed: true });
          at = found + 2;
          continue;
        }
        if (found < 0 && unclosed) {
          const stop = display ? length : lineEnd(at);
          spans.push({ start: at, end: stop, tex: text.slice(at + 2, stop), display, open: display ? "\\[" : "\\(", close, closed: false });
          at = stop;
          continue;
        }
      }
      const environment = next === 0x62 ? /^\\begin\{([a-zA-Z]+\*?)\}/.exec(text.slice(at, at + 32)) : null;
      if (environment && displayEnvironments.has(environment[1])) {
        const close = `\\end{${environment[1]}}`, found = text.indexOf(close, at);
        if (found >= 0 || unclosed) {
          const end = found >= 0 ? found + close.length : length;
          spans.push({ start: at, end, tex: text.slice(at, end), display: true, open: "", close: "", closed: found >= 0 });
          at = end;
          continue;
        }
      }
      at += 2; // An escaped character.
      continue;
    }
    if (code === 0x24) {
      if (text.charCodeAt(at + 1) === 0x24) { // $$…$$
        const found = unescapedIndex(text, "$$", at + 2);
        if (found >= 0 && text.slice(at + 2, found).trim()) {
          spans.push({ start: at, end: found + 2, tex: text.slice(at + 2, found), display: true, open: "$$", close: "$$", closed: true });
          at = found + 2;
          continue;
        }
        if (found < 0 && unclosed) {
          spans.push({ start: at, end: length, tex: text.slice(at + 2), display: true, open: "$$", close: "$$", closed: false });
          break;
        }
        at += 2;
        continue;
      }
      if (text.charCodeAt(at + 1) === 0x60) { // $`…`$
        const found = text.indexOf("`$", at + 2);
        if (found > at + 2 && found < paragraphEnd(at)) {
          spans.push({ start: at, end: found + 2, tex: text.slice(at + 2, found), display: false, open: "$`", close: "`$", closed: true });
          at = found + 2;
          continue;
        }
      }
      const end = paragraphEnd(at);
      if (canOpenDollar(text, at, end)) {
        const found = closingDollar(text, at, end);
        if (found > at + 1) {
          spans.push({ start: at, end: found + 1, tex: text.slice(at + 1, found), display: false, open: "$", close: "$", closed: true });
          at = found + 1;
          continue;
        }
        const stop = lineEnd(at);
        if (found < 0 && unclosed && texLike.test(text.slice(at + 1, stop))) {
          spans.push({ start: at, end: stop, tex: text.slice(at + 1, stop), display: false, open: "$", close: "$", closed: false });
          at = stop;
          continue;
        }
      }
    }
    at++;
  }
  return spans;
}

/** The formula that holds a place of a text (its delimiters included), formulas being written included; null when none does. */
export function mathSpanAt(text: string, offset: number): MathSpan | null {
  return mathSpans(text, true).find(span => offset >= span.start && offset <= span.end) ?? null;
}

function pushInline(state: StateInline, content: string, markup: string, display: boolean) {
  const token = state.push("math_inline", "math", 0);
  token.content = content;
  token.markup = markup;
  token.meta = { display };
}

// `$…$`, `` $`…`$ `` and `$$…$$` inside a paragraph (a display formula on its line of text).
function dollarRule(state: StateInline, silent: boolean): boolean {
  const { src, pos, posMax: end } = state;
  if (src.charCodeAt(pos) !== 0x24) return false;
  if (src.charCodeAt(pos + 1) === 0x24) {
    const found = unescapedIndex(src, "$$", pos + 2, end);
    if (found < 0 || !src.slice(pos + 2, found).trim()) return false;
    if (!silent) pushInline(state, src.slice(pos + 2, found), "$$", true);
    state.pos = found + 2;
    return true;
  }
  if (src.charCodeAt(pos + 1) === 0x60) {
    const found = src.indexOf("`$", pos + 2);
    if (found > pos + 2 && found + 2 <= end) {
      if (!silent) pushInline(state, src.slice(pos + 2, found), "$`", false);
      state.pos = found + 2;
      return true;
    }
  }
  if (!canOpenDollar(src, pos, end)) return false;
  const found = closingDollar(src, pos, end);
  if (found <= pos + 1) return false;
  if (!silent) pushInline(state, src.slice(pos + 1, found), "$", false);
  state.pos = found + 1;
  return true;
}

// `\(…\)`, read before the escapes of Markdown take the backslash.
function bracketRule(state: StateInline, silent: boolean): boolean {
  const { src, pos, posMax: end } = state;
  if (src.charCodeAt(pos) !== 0x5c || src.charCodeAt(pos + 1) !== 0x28) return false;
  const found = unescapedIndex(src, "\\)", pos + 2, end);
  if (found <= pos + 2) return false;
  if (!silent) pushInline(state, src.slice(pos + 2, found), "\\(", false);
  state.pos = found + 2;
  return true;
}

// `$$`, `\[` or `\begin{environment}` at the start of a line, closed on that line or on a later one of the same block.
function blockRule(state: StateBlock, startLine: number, endLine: number, silent: boolean): boolean {
  if (state.sCount[startLine] - state.blkIndent >= 4) return false;
  const lineText = (line: number) => state.src.slice(state.bMarks[line] + state.tShift[line], state.eMarks[line]);
  const first = lineText(startLine);
  let open: string, close: string;
  if (first.startsWith("$$")) { open = "$$"; close = "$$"; }
  else if (first.startsWith("\\[")) { open = "\\["; close = "\\]"; }
  else {
    const environment = /^\\begin\{([a-zA-Z]+\*?)\}/.exec(first);
    if (!environment || !displayEnvironments.has(environment[1])) return false;
    open = ""; close = `\\end{${environment[1]}}`;
  }
  // An environment keeps its delimiters: they are part of what KaTeX reads.
  const keep = open === "";
  const take = (text: string, found: number) => keep ? text.slice(0, found + close.length) : text.slice(0, found);
  const rest = first.slice(open.length);
  const sameLine = unescapedIndex(rest, close, 0);
  let content: string, closingLine = startLine;
  if (sameLine >= 0) {
    // Text after the closing delimiter makes the line a paragraph, where the formula is read inline.
    if (rest.slice(sameLine + close.length).trim()) return false;
    content = take(rest, sameLine);
  } else {
    let last: string | null = null;
    for (let line = startLine + 1; line < endLine; line++) {
      const empty = state.bMarks[line] + state.tShift[line] >= state.eMarks[line];
      if (!empty && state.sCount[line] < state.blkIndent) break; // The container of the block ended.
      const text = lineText(line), found = unescapedIndex(text, close, 0);
      if (found >= 0 && !text.slice(found + close.length).trim()) { closingLine = line; last = take(text, found); break; }
    }
    // Not closed: the lines stay text until the formula is complete.
    if (last === null) return false;
    const middle = state.getLines(startLine + 1, closingLine, state.blkIndent, false);
    content = [rest.trim() ? rest : "", middle, last.trim() ? last : ""].filter(Boolean).join("\n");
  }
  if (!content.trim()) return false;
  if (silent) return true;
  state.line = closingLine + 1;
  const token = state.push("math_block", "math", 0);
  token.block = true;
  token.content = content;
  token.markup = open || "\\begin";
  token.map = [startLine, state.line];
  return true;
}

/** The attribute of an inline formula of the parser; its value is the mark of the renderer. */
export const mathAttribute = "data-math";
/** The attribute of a display formula of the parser; its value is the mark of the renderer. */
export const displayMathAttribute = "data-math-display";

/**
 * Reads math in Markdown. A formula becomes a placeholder that holds its TeX, marked with `mark` (a value no text knows):
 * `<span data-math="mark">` inline, `<div data-math-display="mark">` for a display block, `<span data-math-display="mark">`
 * for a display formula inside a paragraph. The renderer draws it after sanitization.
 */
export function markdownMath(md: MarkdownIt, mark: string): void {
  const escape = md.utils.escapeHtml;
  md.inline.ruler.before("escape", "math_bracket", bracketRule);
  md.inline.ruler.after("escape", "math_dollar", dollarRule);
  md.block.ruler.after("blockquote", "math_block", blockRule, { alt: ["paragraph", "reference", "blockquote", "list"] });
  md.renderer.rules.math_inline = (tokens, index) => {
    const token = tokens[index];
    return `<span ${token.meta?.display ? displayMathAttribute : mathAttribute}="${escape(mark)}">${escape(token.content)}</span>`;
  };
  md.renderer.rules.math_block = (tokens, index, _options, _env, renderer) => {
    const token = tokens[index];
    token.attrSet(displayMathAttribute, mark);
    return `<div${renderer.renderAttrs(token)}>${escape(token.content)}</div>\n`;
  };
}
