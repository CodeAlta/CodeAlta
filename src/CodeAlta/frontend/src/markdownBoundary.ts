import createDOMPurify from "dompurify";
import { createMarkdownParser } from "./markdown";

/** Instance-owned browser boundary; parser output must never bypass this sanitizer. */
export function createMarkdownRenderer(view: Window & typeof globalThis) {
  const parse = createMarkdownParser();
  const purifier = createDOMPurify(view);
  const tags = ["p", "br", "hr", "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "pre", "code",
    "ul", "ol", "li", "dl", "dt", "dd", "strong", "em", "s", "del", "b", "i", "u", "sub", "sup", "kbd", "samp", "var",
    "abbr", "a", "span", "div", "table", "caption", "thead", "tbody", "tfoot", "tr", "th", "td", "details", "summary"];
  function safeHref(value: string) {
    if (!/^https?:\/\//i.test(value) || /[\u0000-\u0020\u007f\\]/.test(value)) return false;
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
  return (source: string, timelineCodeBlocks: boolean) => {
    try {
      const fragment = purifier.sanitize(parse(source), {
        ALLOWED_TAGS: tags,
        ALLOWED_ATTR: ["href", "title", "class", "open", "scope", "align", "colspan", "rowspan", "start", "value", "reversed"],
        ALLOW_ARIA_ATTR: false, ALLOW_DATA_ATTR: false, RETURN_DOM_FRAGMENT: true,
      });
      // Only constant renderer-owned attributes may be added to sanitized nodes.
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
