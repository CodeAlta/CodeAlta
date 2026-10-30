import MarkdownIt from "markdown-it";
import { fileMarkdownHref } from "./markdownLinks";
import { markdownMath } from "./markdownMath";
import { sourceLinesAttribute } from "./markdownSourceLines";

/**
 * What a render writes on each block that it renders, as {@link sourceLinesAttribute}: `mark:S-E`, the lines of the
 * block counted from `firstLine`, the line of the text given to the parser in the whole document.
 */
export type MarkdownSourceLines = Readonly<{ mark: string; firstLine: number }>;

/**
 * Creates an instance-owned, browser-independent parser. Output is UNSANITIZED HTML:
 * only the MarkdownContent boundary may turn it into DOM, never inject it directly.
 *
 * A message breaks its lines where its text does. A document (a file, the instructions of a skill) is wrapped
 * in its source at any width: with `breaks: false` a line that follows another continues its paragraph.
 */
export function createMarkdownParser(options: Readonly<{ breaks?: boolean;
  /** Written on each link of the Markdown that names a file, as `data-file-link`: what tells it from an `<a>` that the text wrote in HTML. */
  fileLinkMark?: string;
  /** Written on the placeholders of the formulas (see `markdownMath`): what tells them from HTML that the text wrote. */
  placeholderMark?: string }> = {}) {
  const md = new MarkdownIt("commonmark", { html: true, breaks: options.breaks ?? true, linkify: true, typographer: false })
    .enable(["table", "strikethrough", "linkify"]);
  markdownMath(md, options.placeholderMark ?? "");
  md.linkify.set({ fuzzyLink: false, fuzzyEmail: false, fuzzyIP: false });
  // A `file:` address is a link too, written as a link or alone in the text: markdown-it refuses the scheme, and
  // the boundary decides what a link may name.
  const validateLink = md.validateLink.bind(md);
  md.validateLink = url => /^file:/i.test(url.trim()) || validateLink(url);
  md.linkify.add("file:", { validate: (text, position) => /^\/\/\/?[^\s<>"'`]*[^\s<>"'`.,;:!?)\]}]/.exec(text.slice(position))?.[0].length ?? 0 });
  const escape = md.utils.escapeHtml;
  const code = (text: string, info: string, lines: string | number | null) => {
    const language = md.utils.unescapeAll(info).trim().split(/\s+/)[0];
    const attr = /^[a-zA-Z0-9_-]{1,32}$/.test(language) ? ` class="language-${language}"` : "";
    const linesAttr = typeof lines === "string" ? ` ${sourceLinesAttribute}="${escape(lines)}"` : "";
    // markdown-it includes the syntax-terminating LF unlike the previous token.text.
    // Preserve the existing one-LF trimming/display convention after removing it.
    const tokenText = text.replace(/\n$/, "");
    return `<pre${linesAttr}><code${attr}>${escape(tokenText.replace(/\n$/, ""))}\n</code></pre>\n`;
  };
  md.renderer.rules.fence = (tokens, index) => code(tokens[index].content, tokens[index].info, tokens[index].attrGet(sourceLinesAttribute));
  md.renderer.rules.code_block = (tokens, index) => code(tokens[index].content, "", tokens[index].attrGet(sourceLinesAttribute));
  // Asked for, each block says which lines of the document it comes from: a paragraph, a heading, an item of a list, a
  // row of a table, a code block, a quote, a rule, a display formula. A block of HTML says nothing: its elements are the text's own.
  const sourceBlock = /^(?:paragraph_open|heading_open|list_item_open|tr_open|blockquote_open|hr|fence|code_block|math_block)$/;
  md.core.ruler.push("source_lines", state => {
    const sourceLines = (state.env as { sourceLines?: MarkdownSourceLines } | undefined)?.sourceLines;
    if (!sourceLines) return;
    // The text is normalized: its lines end with LF alone.
    const lines = state.src.split("\n");
    for (const token of state.tokens) {
      if (!token.map || !sourceBlock.test(token.type)) continue;
      // An item of a list ends where the next one starts: the blank lines between them are no part of it.
      let [start, end] = token.map;
      while (end > start + 1 && /^[ \t]*$/.test(lines[end - 1] ?? "")) end--;
      token.attrSet(sourceLinesAttribute, `${sourceLines.mark}:${sourceLines.firstLine + start}-${sourceLines.firstLine + end - 1}`);
    }
  });
  for (const rule of ["th_open", "td_open"]) md.renderer.rules[rule] = (tokens, index, options, _env, renderer) => {
    const token = tokens[index];
    const style = token.attrGet("style");
    const alignment = typeof style === "string" ? style.match(/^text-align:(left|right|center)$/)?.[1] : undefined;
    token.attrs = token.attrs?.filter(([name]) => name !== "style") ?? null;
    if (alignment) token.attrSet("align", alignment);
    return renderer.renderToken(tokens, index, options);
  };
  // The parser draws no picture. One of the disk, such as a chart a script made, is a link to its file, marked as a
  // picture as well: the window may draw it there (see `markdownBoundary`), or open the file. Another is its
  // alternative text.
  md.renderer.rules.image = (tokens, index) => {
    const token = tokens[index];
    const src = token.attrGet("src");
    if (!options.fileLinkMark || typeof src !== "string" || !fileMarkdownHref(src)) return escape(token.content);
    const mark = escape(options.fileLinkMark);
    return `<a href="${escape(src)}" data-file-link="${mark}" data-file-image="${mark}">${escape(token.content || fileName(src))}</a>`;
  };
  if (options.fileLinkMark) md.renderer.rules.link_open = (tokens, index, rendering, _env, renderer) => {
    const token = tokens[index];
    const href = token.attrGet("href");
    if (typeof href === "string" && fileMarkdownHref(href)) token.attrSet("data-file-link", options.fileLinkMark!);
    return renderer.renderToken(tokens, index, rendering);
  };
  return (source: string, sourceLines?: MarkdownSourceLines) => md.render(source, sourceLines ? { sourceLines } : {});
}

// The name of the file a target names, without its folder and the place in the file; the target itself when it has none.
function fileName(target: string): string {
  let path = target.split("#", 1)[0];
  try { path = decodeURIComponent(path); } catch { /* Not escapes: the path is as it is written. */ }
  return path.replaceAll("\\", "/").split("/").filter(Boolean).at(-1) || target;
}

/**
 * The lines that the front matter of a document takes, its two delimiters included.
 * @param source The document.
 * @param body The rest of the document after its front matter, as {@link splitFrontMatter} returns it.
 * @returns The number of lines, 0 when the document has no front matter; its body starts on the next line.
 */
export function frontMatterLineCount(source: string, body: string): number {
  const prefix = source.slice(0, source.length - body.length);
  if (!prefix) return 0;
  const breaks = prefix.split("\n").length - 1;
  return prefix.endsWith("\n") ? breaks : breaks + 1;
}

/** The longest front matter that is taken out of a document, in UTF-16 units; a longer one stays in its text. */
export const maximumFrontMatterLength = 16 * 1024;

const delimiter = /^---[ \t]*$/;
const closing = /^(?:---|\.\.\.)[ \t]*$/;
// A key of a mapping at the start of a line: a plain or a quoted name, then a colon that ends the line or is followed by a space.
const key = /^(?:"((?:[^"\\]|\\.)*)"|'((?:[^']|'')*)'|([^\s#:&*!|>'"%@`,[\]{}-][^:#]*?|-[^\s:#][^:#]*?))[ \t]*:(?:[ \t]+(.*))?$/;

/**
 * Takes the front matter off a document: the lines between a first line `---` and the next line `---` or `...`,
 * when they start with a key as a YAML mapping does. A text that only starts with a rule keeps it.
 * @returns The front matter without its two delimiters (null when the document has none), and the rest of the text.
 */
export function splitFrontMatter(source: string): Readonly<{ frontMatter: string | null; body: string }> {
  const none = { frontMatter: null, body: source };
  const text = source.charCodeAt(0) === 0xfeff ? source.slice(1) : source;
  const first = text.indexOf("\n");
  if (first < 0 || !delimiter.test(text.slice(0, first).replace(/\r$/, ""))) return none;
  let at = first + 1, started = false;
  while (at <= text.length && at - first <= maximumFrontMatterLength) {
    const end = text.indexOf("\n", at);
    const line = (end < 0 ? text.slice(at) : text.slice(at, end)).replace(/\r$/, "");
    if (closing.test(line)) {
      if (!started) return none;
      return { frontMatter: text.slice(first + 1, at).replace(/\r?\n$/, ""), body: end < 0 ? "" : text.slice(end + 1) };
    }
    // The first line that says something has to be a key: this is what tells a mapping from a paragraph under a rule.
    if (!started && line.trim() && !line.trimStart().startsWith("#")) {
      if (!key.test(line)) return none;
      started = true;
    }
    if (end < 0) break;
    at = end + 1;
  }
  return none;
}

/** One entry of a front matter: its key, and its value as a text, as the items of a list, or as the YAML it is written in. */
export type FrontMatterEntry = Readonly<{ key: string; text: string } | { key: string; items: readonly string[] } | { key: string; yaml: string }>;

const unquote = (value: string) => {
  const text = value.trim();
  if (text.length >= 2 && text.startsWith('"') && text.endsWith('"')) {
    try { const parsed: unknown = JSON.parse(text); if (typeof parsed === "string") return parsed; } catch { /* Not the escapes of JSON: shown as written. */ }
    return text.slice(1, -1);
  }
  if (text.length >= 2 && text.startsWith("'") && text.endsWith("'")) return text.slice(1, -1).replaceAll("''", "'");
  // A comment after a plain value is not part of it.
  return text.replace(/\s+#.*$/, "");
};

/**
 * Reads the entries of a front matter, in their order: each key at the start of a line, with what follows it.
 * A value on one line is a text; a block scalar (`|`, `>`) is its text; a list of plain items is its items; anything
 * else (a mapping, a list of mappings) is kept as the YAML it is written in.
 * @returns The entries, or null when a line is neither a key, nor a part of the value above it.
 */
export function frontMatterEntries(frontMatter: string): readonly FrontMatterEntry[] | null {
  const entries: { key: string; value: string; lines: string[] }[] = [];
  for (const raw of frontMatter.split(/\r?\n/)) {
    const line = raw.replace(/\s+$/, "");
    const current = entries.at(-1);
    // A line that is indented, empty, or an item of a list at the start of a line belongs to the key above it.
    if (current && (line === "" || /^[ \t]/.test(line) || /^-(?:[ \t]|$)/.test(line))) { current.lines.push(line); continue; }
    if (line === "" || line.startsWith("#")) continue;
    const match = key.exec(line);
    if (!match) return null;
    const name = match[1] !== undefined ? unquote(`"${match[1]}"`) : match[2] !== undefined ? match[2].replaceAll("''", "'") : match[3].trim();
    entries.push({ key: name, value: match[4] ?? "", lines: [] });
  }
  return entries.map(({ key: name, value, lines }): FrontMatterEntry => {
    while (lines.length && lines.at(-1) === "") lines.pop();
    const inline = value.trim();
    if (!lines.length) return { key: name, text: unquote(inline) };
    const indent = Math.min(...lines.filter(line => line !== "").map(line => /^[ \t]*/.exec(line)![0].length));
    const block = lines.map(line => line.slice(indent));
    // A block scalar: its lines are its text, folded into one line each paragraph when it is written with `>`.
    if (/^[|>][+-]?\d?$/.test(inline)) {
      return { key: name, text: inline.startsWith(">") ? block.join("\n").replace(/([^\n])\n(?=[^\n])/g, "$1 ") : block.join("\n") };
    }
    if (inline === "" && block.every(line => /^-[ \t]+\S/.test(line) && !/^-[ \t]+[^'"\s][^:]*:(?:[ \t]|$)/.test(line))) {
      return { key: name, items: block.map(line => unquote(line.replace(/^-[ \t]+/, ""))) };
    }
    // A plain value that continues on the next lines is one text.
    if (inline !== "" && !/^[[{&*!|>]/.test(inline) && block.every(line => line !== "" && !/^-[ \t]/.test(line) && !key.test(line))) {
      return { key: name, text: unquote([inline, ...block.map(line => line.trim())].join(" ")) };
    }
    return { key: name, yaml: (inline ? [inline, ...lines] : block).join("\n") };
  });
}
