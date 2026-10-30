import createDOMPurify from "dompurify";
import { highlightCode } from "./codeHighlight";
import { diagramAppearance, diagrams, type DiagramAppearance } from "./diagrams";
import { loadedMath } from "./math";
import { createMarkdownParser, frontMatterEntries, frontMatterLineCount, splitFrontMatter } from "./markdown";
import { markdownHrefKind } from "./markdownLinks";
import { displayMathAttribute, mathAttribute } from "./markdownMath";
import { sourceLinesAttribute } from "./markdownSourceLines";

/** The alerts of GitHub: a quote whose first line is `[!NOTE]`, `[!TIP]`, `[!IMPORTANT]`, `[!WARNING]` or `[!CAUTION]`. */
export const alertKinds = ["note", "tip", "important", "warning", "caution"] as const;
export type AlertKind = typeof alertKinds[number];
/** The titles of the alerts, in the language of the window. */
export type MarkdownLabels = Readonly<Record<AlertKind, string> & {
  /** What the button of a code block says it does. */ copy?: string;
  /** What the note under a `mermaid` block that could not be drawn says before its reason. */ notDrawn?: string;
  /** What the buttons of a displayed equation say they do. */ copyLatex?: string; copyMathMl?: string;
}>;
const englishLabels: MarkdownLabels = { note: "Note", tip: "Tip", important: "Important", warning: "Warning", caution: "Caution", copy: "Copy",
  notDrawn: "Not drawn as a diagram:", copyLatex: "Copy LaTeX", copyMathMl: "Copy MathML" };

/**
 * What a render could not draw yet: the texts of the `mermaid` blocks to draw, and whether its formulas wait for the
 * math library (`loadMath`). The owner draws or loads them, then renders again.
 */
export type MarkdownPending = { diagrams: string[]; math: boolean };

/** A new, empty {@link MarkdownPending}. */
export const markdownPending = (): MarkdownPending => ({ diagrams: [], math: false });

/**
 * What a button of a displayed equation copies: its LaTeX between `$$` lines, or its MathML (which Word pastes as an
 * equation); null when the element is no such button or the equation is not drawn.
 */
export function equationCopy(button: Element): string | null {
  const block = button.closest(".markdown-math-block");
  const kind = button.closest("button.markdown-math-copy")?.getAttribute("data-copy");
  if (!block || !kind) return null;
  if (kind === "mathml") return block.querySelector(".katex-mathml math")?.outerHTML ?? null;
  const tex = block.querySelector('annotation[encoding="application/x-tex"]')?.textContent;
  return tex ? `$$\n${tex.trim()}\n$$` : null;
}

// A text whose formulas define macros is drawn whole each time: a formula depends on the ones before it.
const definesMacros = /\\(?:newcommand|renewcommand|providecommand|def|gdef|edef|xdef|let|DeclareMathOperator)(?![a-zA-Z])/;
// The formulas a renderer keeps drawn, so that a message being written draws only its new one.
const formulaCapacity = 512;

/** The image of the renderer in a link to a picture of the disk, without a source until the window draws it. */
export const localImageClass = "markdown-local-image";
/** What such a link shows while its picture is not drawn: the alternative text of the picture. */
export const localImageAltClass = "markdown-local-image-alt";
// What the parser writes on a link to a picture of the disk (see `createMarkdownParser`).
const fileImageAttribute = "data-file-image";

/**
 * How a text is rendered: a document is a file or a text written as one, whose lines are wrapped in its source.
 * With `sourceLines`, each block says which lines of the text it comes from, in `data-source-lines` (see `markdownSourceLines`).
 */
export type MarkdownRenderOptions = Readonly<{ document?: boolean; sourceLines?: boolean }>;

/**
 * Instance-owned browser boundary; parser output must never bypass this sanitizer. Its diagrams are those drawn
 * for `appearance`: the window's, or one no wider than a phone.
 */
export function createMarkdownRenderer(view: Window & typeof globalThis, labels: MarkdownLabels = englishLabels,
  appearance: () => DiagramAppearance = () => diagramAppearance()) {
  // A link to a file is one that the Markdown of the text wrote (`[name](path)`): the parsers mark theirs with
  // a value that no text knows. An `<a>` written in HTML opens a page of the web, and nothing else.
  const fileLinkMark = view.crypto?.randomUUID?.() ?? `${Math.random()}${Math.random()}`;
  const fileLinks = new WeakSet<Node>();
  // The placeholders of the parser (formulas) carry a mark of their own, which no text knows either.
  const placeholderMark = view.crypto?.randomUUID?.() ?? `${Math.random()}${Math.random()}`;
  const parsers = { message: createMarkdownParser({ fileLinkMark, placeholderMark }),
    document: createMarkdownParser({ breaks: false, fileLinkMark, placeholderMark }) };
  // The formulas drawn by this renderer, most recently used last.
  const drawnFormulas = new Map<string, string>();
  // The lines of a block are the parser's when they carry the mark of the render being sanitized, which no text knows:
  // they are kept without it. Any other `data-source-lines`, written in HTML, is removed.
  const randomMark = () => view.crypto?.randomUUID?.() ?? `${Math.random()}${Math.random()}`;
  let sourceLinesMark: string | null = null;
  const ownSourceLines = (value: string) => {
    const lines = sourceLinesMark !== null && value.startsWith(`${sourceLinesMark}:`) ? value.slice(sourceLinesMark.length + 1) : null;
    return lines !== null && /^\d{1,9}-\d{1,9}$/.test(lines) ? lines : null;
  };
  const purifier = createDOMPurify(view);
  const tags = ["p", "br", "hr", "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "pre", "code",
    "ul", "ol", "li", "dl", "dt", "dd", "strong", "em", "s", "del", "b", "i", "u", "sub", "sup", "kbd", "samp", "var",
    "abbr", "a", "span", "div", "table", "caption", "thead", "tbody", "tfoot", "tr", "th", "td", "details", "summary"];
  purifier.addHook("uponSanitizeElement", node => {
    if (node.nodeName === "A" && (node as Element).getAttribute("data-file-link") === fileLinkMark) fileLinks.add(node);
  });
  // A picture of the disk that the Markdown names is such a link, marked as a picture too: it keeps `data-file-image`
  // without the mark, for `dressFileImages` alone. An `<img>` and a `data-file-image` written in HTML are removed.
  purifier.addHook("uponSanitizeAttribute", (node, data) => {
    const tag = node.nodeName.toLowerCase();
    const { attrName: name, attrValue: value } = data;
    // The target of a link is a page of the web or, for a link of the Markdown, a file of this computer. The
    // sanitizer has a rule of its own for an address, which knows neither a drive nor `file:`: what this
    // boundary accepts is kept as it is.
    const kind = name === "href" && tag === "a" ? markdownHrefKind(value) : null;
    const link = kind === "web" || kind === "file" && fileLinks.has(node);
    if (link) data.forceKeepAttr = true;
    const lines = name === sourceLinesAttribute ? ownSourceLines(value) : null;
    if (lines !== null) data.attrValue = lines;
    const image = name === fileImageAttribute && tag === "a" && fileLinks.has(node) && value === fileLinkMark;
    if (image) data.attrValue = "";
    // A formula of the parser keeps its attribute, without the mark: one written in HTML is text.
    const formula = (name === mathAttribute || name === displayMathAttribute) && (tag === "span" || tag === "div") && value === placeholderMark;
    if (formula) data.attrValue = "";
    data.keepAttr = name === "title"
      || link
      || lines !== null
      || image
      || formula
      || name === "class" && tag === "code" && /^language-[a-zA-Z0-9_-]{1,32}$/.test(value)
      || name === "open" && tag === "details"
      || name === "scope" && tag === "th" && /^(col|row|colgroup|rowgroup)$/.test(value)
      || name === "align" && (tag === "td" || tag === "th") && /^(left|right|center)$/.test(value)
      || (name === "colspan" || name === "rowspan") && (tag === "td" || tag === "th") && /^(?:[1-9]|[1-9][0-9]|100)$/.test(value)
      || name === "start" && tag === "ol" && /^-?\d{1,6}$/.test(value)
      || name === "value" && tag === "li" && /^-?\d{1,6}$/.test(value)
      || name === "reversed" && tag === "ol";
  });
  // Fenced blocks are dressed after sanitization, from their text alone: the sanitizer keeps accepting no
  // authored class, style or SVG. A `mermaid` block that has been drawn becomes its diagram; one that has
  // not is reported in `undrawn` and stays a code block. A `math` block is drawn by `dressMath`. Any other known language is highlighted.
  function dressFences(fragment: DocumentFragment, undrawn: string[] | undefined) {
    for (const code of Array.from(fragment.querySelectorAll<HTMLElement>("pre > code[class^='language-']"))) {
      const pre = code.parentElement!;
      if (pre.children.length !== 1) continue;
      const language = code.className.slice("language-".length);
      const text = code.textContent ?? "";
      if (language === "mermaid") {
        const look = appearance();
        const svg = diagrams.get(text, look);
        if (typeof svg === "string") {
          const figure = view.document.createElement("div");
          figure.className = "markdown-diagram";
          figure.innerHTML = svg;
          // The diagram comes from the lines of its block.
          const lines = pre.getAttribute(sourceLinesAttribute);
          if (lines !== null) figure.setAttribute(sourceLinesAttribute, lines);
          pre.replaceWith(figure);
        } else {
          if (svg === undefined) undrawn?.push(text);
          pre.setAttribute("data-language", "mermaid");
          // A block that could not be drawn says why under it, or it reads as a diagram that failed to show.
          const refused = svg === null ? diagrams.refusal(text, look) : null;
          if (refused !== null) {
            const note = view.document.createElement("p");
            note.className = "markdown-diagram-refused";
            note.textContent = `${labels.notDrawn ?? englishLabels.notDrawn!} ${refused}`;
            pre.after(note);
          }
        }
        continue;
      }
      const highlighted = highlightCode(text, language);
      if (highlighted !== null) code.innerHTML = highlighted;
      // The block names its language in a corner (drawn by the stylesheet, so never selected or copied).
      pre.setAttribute("data-language", language.toLowerCase());
    }
  }
  // The formulas of the parser, and the `math` fenced blocks, are drawn with KaTeX once it is loaded; before, they show
  // their source and the render says it waits for it (`pending.math`). A displayed equation has buttons that copy its
  // LaTeX and its MathML: elements of the renderer, like the copy button of a code block.
  function dressMath(fragment: DocumentFragment, pending: MarkdownPending | undefined) {
    const placeholders = Array.from(fragment.querySelectorAll<HTMLElement>(`[${mathAttribute}], [${displayMathAttribute}], pre > code.language-math`));
    if (!placeholders.length) return;
    const library = loadedMath();
    const formulas = placeholders.map(element => ({ element, display: !element.hasAttribute(mathAttribute),
      block: element.tagName !== "SPAN", tex: element.tagName === "CODE" ? (element.textContent ?? "").replace(/\n$/, "") : element.textContent ?? "" }));
    if (!library) {
      if (pending) pending.math = true;
      for (const { element, display, block, tex } of formulas) {
        if (element.tagName === "CODE") continue; // A `math` block stays a code block until it is drawn.
        element.removeAttribute(mathAttribute); element.removeAttribute(displayMathAttribute);
        element.className = block ? "markdown-math-source markdown-math-source-block" : "markdown-math-source";
        element.textContent = display ? `$$${tex}$$` : `$${tex}$`;
      }
      return;
    }
    const drawing = library.formulas();
    const fresh = formulas.some(({ tex }) => definesMacros.test(tex));
    const draw = (tex: string, display: boolean) => {
      if (fresh) return drawing.render(tex, display);
      const key = `${display ? "d" : "i"}${tex}`;
      let html = drawnFormulas.get(key);
      if (html === undefined) html = drawing.render(tex, display);
      else drawnFormulas.delete(key);
      drawnFormulas.set(key, html);
      while (drawnFormulas.size > formulaCapacity) drawnFormulas.delete(drawnFormulas.keys().next().value!);
      return html;
    };
    for (const { element, display, block, tex } of formulas) {
      const html = draw(tex, display);
      if (!block) {
        const formula = view.document.createElement("span");
        formula.className = display ? "markdown-math markdown-math-display" : "markdown-math";
        formula.innerHTML = html;
        element.replaceWith(formula);
        continue;
      }
      const figure = view.document.createElement("div");
      figure.className = "markdown-math-block";
      const host = element.tagName === "CODE" ? element.parentElement! : element;
      const lines = host.getAttribute(sourceLinesAttribute);
      if (lines !== null) figure.setAttribute(sourceLinesAttribute, lines);
      const tools = view.document.createElement("span");
      tools.className = "markdown-math-tools";
      for (const [kind, label, text] of [["latex", labels.copyLatex ?? englishLabels.copyLatex!, "LaTeX"], ["mathml", labels.copyMathMl ?? englishLabels.copyMathMl!, "MathML"]]) {
        const button = view.document.createElement("button");
        button.type = "button";
        button.className = "markdown-math-copy";
        button.setAttribute("data-copy", kind);
        button.title = label;
        button.setAttribute("aria-label", label);
        button.textContent = text;
        tools.append(button);
      }
      const formula = view.document.createElement("div");
      formula.className = "markdown-math-display";
      formula.innerHTML = html;
      figure.append(tools, formula);
      host.replaceWith(figure);
    }
  }
  // The front matter of a document is shown as what it is, a few named values: a table of its entries, built
  // from its text alone (never parsed as Markdown or HTML). A front matter that is no list of keys is shown as
  // the YAML it is written in.
  function frontMatterBlock(frontMatter: string) {
    const document = view.document;
    const yaml = (text: string) => {
      const pre = document.createElement("pre"), code = document.createElement("code");
      code.className = "language-yaml";
      code.textContent = `${text}\n`;
      pre.append(code);
      return pre;
    };
    const entries = frontMatterEntries(frontMatter);
    if (!entries?.length) {
      const block = document.createElement("div");
      block.className = "markdown-front-matter";
      block.append(yaml(frontMatter));
      return block;
    }
    const table = document.createElement("table");
    table.className = "markdown-front-matter";
    const body = table.createTBody();
    for (const entry of entries) {
      const row = body.insertRow();
      const name = document.createElement("th");
      name.scope = "row";
      name.textContent = entry.key;
      const value = document.createElement("td");
      if ("text" in entry) value.textContent = entry.text;
      else if ("yaml" in entry) value.append(yaml(entry.yaml));
      else {
        const list = document.createElement("ul");
        for (const item of entry.items) list.appendChild(document.createElement("li")).textContent = item;
        value.append(list);
      }
      row.append(name, value);
    }
    return table;
  }
  // An item of a list that starts with `[ ]` or `[x]` is a task, as on GitHub. Its mark becomes a box that shows
  // whether the task is done: an element of the renderer, which is no form control and takes no input.
  function dressTasks(fragment: DocumentFragment) {
    for (const item of Array.from(fragment.querySelectorAll("li"))) {
      let first = item.firstChild;
      while (first?.nodeType === 3 && !first.nodeValue?.trim()) first = first.nextSibling;
      // A list with blank lines between its items wraps the text of each in a paragraph.
      const host = first instanceof view.Element && first.tagName === "P" ? first : item;
      const text = host === item ? first : host.firstChild;
      if (text?.nodeType !== 3) continue;
      const mark = /^\s*\[([ xX])\](?:\s+|$)/.exec(text.nodeValue ?? "");
      if (!mark) continue;
      text.nodeValue = text.nodeValue!.slice(mark[0].length);
      const box = view.document.createElement("span");
      box.className = "markdown-task";
      box.setAttribute("role", "checkbox");
      box.setAttribute("aria-checked", mark[1] === " " ? "false" : "true");
      box.setAttribute("aria-disabled", "true");
      host.insertBefore(box, text);
      item.className = "markdown-task-item";
      if (item.parentElement) item.parentElement.className = "markdown-task-list";
    }
  }
  // A quote whose first line is `[!NOTE]`, `[!TIP]`, `[!IMPORTANT]`, `[!WARNING]` or `[!CAUTION]` is an alert, as
  // on GitHub: the mark becomes the title of the quote, in the language of the window.
  function dressAlerts(fragment: DocumentFragment) {
    for (const quote of Array.from(fragment.querySelectorAll("blockquote"))) {
      const paragraph = quote.firstElementChild;
      const text = paragraph?.tagName === "P" ? paragraph.firstChild : null;
      if (!paragraph || text?.nodeType !== 3) continue;
      const mark = /^\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\][ \t]*(?:\n|$)/i.exec(text.nodeValue ?? "");
      if (!mark) continue;
      const kind = mark[1].toLowerCase() as AlertKind;
      text.nodeValue = text.nodeValue!.slice(mark[0].length);
      if (!text.nodeValue) {
        // A message breaks its lines: the break after the mark goes with it.
        const next = text.nextSibling;
        text.remove();
        if (next?.nodeName === "BR") next.remove();
        const lead = paragraph.firstChild;
        if (lead?.nodeType === 3) lead.nodeValue = lead.nodeValue!.replace(/^\n/, "");
      }
      if (!paragraph.firstChild) paragraph.remove();
      const title = view.document.createElement("p");
      title.className = "markdown-alert-title";
      title.textContent = labels[kind];
      quote.prepend(title);
      quote.className = "markdown-alert";
      quote.setAttribute("data-alert", kind);
    }
  }
  // A picture of the disk stays a link to its file, which shows its alternative text, with an image of the renderer
  // in it that has no source: the window that can read the file draws it there (`MarkdownContent`). Authored HTML keeps
  // no `<img>` and no class, so such an image is always the renderer's.
  function dressFileImages(fragment: DocumentFragment) {
    for (const link of Array.from(fragment.querySelectorAll(`a[${fileImageAttribute}]`))) {
      link.removeAttribute(fileImageAttribute);
      if (!link.hasAttribute("href")) continue;
      const alt = view.document.createElement("span");
      alt.className = localImageAltClass;
      alt.append(...Array.from(link.childNodes));
      const image = view.document.createElement("img");
      image.className = localImageClass;
      image.alt = alt.textContent ?? "";
      image.draggable = false;
      link.append(alt, image);
    }
  }
  // A code block has a button that copies its text: an element of the renderer, with constant attributes and no
  // content of its own. Authored HTML keeps no button and no class, so this one is always the renderer's. It names
  // the language of the block, which it shows beside its icon.
  function dressCopy(fragment: DocumentFragment) {
    for (const pre of Array.from(fragment.querySelectorAll("pre"))) {
      if (pre.children.length !== 1 || pre.firstElementChild?.tagName !== "CODE" || pre.closest(".markdown-front-matter")) continue;
      const button = view.document.createElement("button");
      button.type = "button";
      button.className = "markdown-copy";
      button.title = labels.copy ?? englishLabels.copy!;
      button.setAttribute("aria-label", button.title);
      const language = pre.getAttribute("data-language");
      if (language) button.setAttribute("data-language", language);
      pre.prepend(button);
    }
  }
  return (source: string, timelineCodeBlocks: boolean, pending?: MarkdownPending, options: MarkdownRenderOptions = {}) => {
    try {
      const { frontMatter, body } = splitFrontMatter(source);
      // The body of a document keeps the lines it has in the whole text, after its front matter.
      const frontMatterLines = frontMatterLineCount(source, body);
      const mark = options.sourceLines ? randomMark() : null;
      const parse = options.document ? parsers.document : parsers.message;
      const html = mark === null ? parse(body) : parse(body, { mark, firstLine: frontMatterLines + 1 });
      let fragment: DocumentFragment;
      sourceLinesMark = mark;
      try {
        fragment = purifier.sanitize(html, {
          ALLOWED_TAGS: tags,
          ALLOWED_ATTR: ["href", "title", "class", "open", "scope", "align", "colspan", "rowspan", "start", "value", "reversed", sourceLinesAttribute, fileImageAttribute,
            mathAttribute, displayMathAttribute],
          ALLOW_ARIA_ATTR: false, ALLOW_DATA_ATTR: false, RETURN_DOM_FRAGMENT: true,
        });
      } finally { sourceLinesMark = null; }
      // What follows only adds elements and constant attributes of the renderer to sanitized nodes, from text.
      if (frontMatter !== null) {
        const block = frontMatterBlock(frontMatter);
        if (mark !== null) block.setAttribute(sourceLinesAttribute, `1-${frontMatterLines}`);
        fragment.prepend(block);
      }
      dressFileImages(fragment);
      dressFences(fragment, pending?.diagrams);
      dressMath(fragment, pending);
      dressTasks(fragment);
      dressAlerts(fragment);
      // Authored pre/code and fenced code share a harmless region, never app controls.
      if (timelineCodeBlocks) for (const pre of Array.from(fragment.querySelectorAll("pre"))) {
        if (pre.children.length !== 1 || pre.firstElementChild?.tagName !== "CODE") continue;
        pre.className = "timeline-code"; pre.tabIndex = 0;
        pre.setAttribute("role", "region"); pre.setAttribute("aria-label", "Code block");
      }
      dressCopy(fragment);
      const container = view.document.createElement("div");
      container.append(fragment);
      return container.innerHTML;
    } catch {
      const pre = view.document.createElement("pre");
      pre.textContent = source;
      return pre.outerHTML; // Inert original source, never exception details or active HTML.
    }
  };
}
