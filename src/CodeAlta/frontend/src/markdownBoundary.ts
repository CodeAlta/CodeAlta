import createDOMPurify from "dompurify";
import { highlightCode } from "./codeHighlight";
import { diagramAppearance, diagrams } from "./diagrams";
import { createMarkdownParser, frontMatterEntries, splitFrontMatter } from "./markdown";

/** The alerts of GitHub: a quote whose first line is `[!NOTE]`, `[!TIP]`, `[!IMPORTANT]`, `[!WARNING]` or `[!CAUTION]`. */
export const alertKinds = ["note", "tip", "important", "warning", "caution"] as const;
export type AlertKind = typeof alertKinds[number];
/** The titles of the alerts, in the language of the window. */
export type MarkdownLabels = Readonly<Record<AlertKind, string>>;
const englishLabels: MarkdownLabels = { note: "Note", tip: "Tip", important: "Important", warning: "Warning", caution: "Caution" };

/** How a text is rendered: a document is a file or a text written as one, whose lines are wrapped in its source. */
export type MarkdownRenderOptions = Readonly<{ document?: boolean }>;

/** Instance-owned browser boundary; parser output must never bypass this sanitizer. */
export function createMarkdownRenderer(view: Window & typeof globalThis, labels: MarkdownLabels = englishLabels) {
  const parsers = { message: createMarkdownParser(), document: createMarkdownParser({ breaks: false }) };
  const purifier = createDOMPurify(view);
  const tags = ["p", "br", "hr", "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "pre", "code",
    "ul", "ol", "li", "dl", "dt", "dd", "strong", "em", "s", "del", "b", "i", "u", "sub", "sup", "kbd", "samp", "var",
    "abbr", "a", "span", "div", "table", "caption", "thead", "tbody", "tfoot", "tr", "th", "td", "details", "summary"];
  function safeHref(value: string) {
    if (!/^https?:\/\//i.test(value) || /[\u0000- \u007f\\]/.test(value)) return false;
    try { const url = new URL(value); return !url.username && !url.password && !!url.hostname; } catch { return false; }
  }
  purifier.addHook("uponSanitizeAttribute", (node, data) => {
    const tag = node.nodeName.toLowerCase();
    const { attrName: name, attrValue: value } = data;
    data.keepAttr = name === "title"
      || name === "href" && tag === "a" && safeHref(value)
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
  // not is reported in `undrawn` and stays a code block. Any other known language is highlighted.
  function dressFences(fragment: DocumentFragment, undrawn: string[] | undefined) {
    for (const code of Array.from(fragment.querySelectorAll<HTMLElement>("pre > code[class^='language-']"))) {
      const pre = code.parentElement!;
      if (pre.children.length !== 1) continue;
      const language = code.className.slice("language-".length);
      const text = code.textContent ?? "";
      if (language === "mermaid") {
        const svg = diagrams.get(text, diagramAppearance());
        if (typeof svg === "string") {
          const figure = view.document.createElement("div");
          figure.className = "markdown-diagram";
          figure.innerHTML = svg;
          pre.replaceWith(figure);
        } else {
          if (svg === undefined) undrawn?.push(text);
          pre.setAttribute("data-language", "mermaid");
        }
        continue;
      }
      const highlighted = highlightCode(text, language);
      if (highlighted !== null) code.innerHTML = highlighted;
      // The block names its language in a corner (drawn by the stylesheet, so never selected or copied).
      pre.setAttribute("data-language", language.toLowerCase());
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
  return (source: string, timelineCodeBlocks: boolean, undrawn?: string[], options: MarkdownRenderOptions = {}) => {
    try {
      const { frontMatter, body } = splitFrontMatter(source);
      const fragment = purifier.sanitize((options.document ? parsers.document : parsers.message)(body), {
        ALLOWED_TAGS: tags,
        ALLOWED_ATTR: ["href", "title", "class", "open", "scope", "align", "colspan", "rowspan", "start", "value", "reversed"],
        ALLOW_ARIA_ATTR: false, ALLOW_DATA_ATTR: false, RETURN_DOM_FRAGMENT: true,
      });
      // What follows only adds elements and constant attributes of the renderer to sanitized nodes, from text.
      if (frontMatter !== null) fragment.prepend(frontMatterBlock(frontMatter));
      dressFences(fragment, undrawn);
      dressTasks(fragment);
      dressAlerts(fragment);
      // Authored pre/code and fenced code share a harmless region, never app controls.
      if (timelineCodeBlocks) for (const pre of Array.from(fragment.querySelectorAll("pre"))) {
        if (pre.children.length !== 1 || pre.firstElementChild?.tagName !== "CODE") continue;
        pre.className = "timeline-code"; pre.tabIndex = 0;
        pre.setAttribute("role", "region"); pre.setAttribute("aria-label", "Code block");
      }
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
