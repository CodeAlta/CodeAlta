import createDOMPurify from "dompurify";

/** The attribute of an element that runs a plugin command, and of one that raises a dialog action. */
export const pluginCommandAttribute = "data-alta-command";
export const pluginActionAttribute = "data-alta-action";
export const pluginValueAttribute = "data-alta-value";

const tags = ["p", "br", "hr", "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "pre", "code",
  "ul", "ol", "li", "dl", "dt", "dd", "strong", "em", "s", "del", "b", "i", "u", "sub", "sup", "kbd", "samp", "var", "small", "mark",
  "abbr", "a", "span", "div", "section", "header", "footer", "table", "caption", "thead", "tbody", "tfoot", "tr", "th", "td", "details", "summary",
  "button", "input", "select", "option", "optgroup", "textarea", "label", "fieldset", "legend", "progress", "meter"];
// Every attribute is then checked for its element and its value by the hook below.
const attributes = ["title", "class", "href", "open", "scope", "align", "colspan", "rowspan", "type", "name", "value", "placeholder", "checked", "readonly",
  "required", "disabled", "selected", "multiple", "label", "min", "max", "step", "rows", "cols", "maxlength", "id", "for", "aria-label",
  "data-alta-command", "data-alta-action", "data-alta-value"];
const inputTypes = /^(text|search|number|checkbox|radio|password|email|url|date|time|range|color|hidden)$/u;
const fields = ["input", "select", "textarea"];
const name = /^[A-Za-z][A-Za-z0-9._:-]{0,63}$/u;
const number = /^-?\d{1,9}(\.\d{1,6})?$/u;
// What the application's look is made of: an intent or a layout, never an arbitrary class of the page.
const classes = /^alta-[a-z][a-z0-9-]{0,31}$/u;
const intents: Readonly<Record<string, string>> = { "alta-primary": "primary", "alta-success": "success", "alta-warning": "warning", "alta-danger": "danger" };

function safeHref(value: string) {
  if (!/^https?:\/\//iu.test(value) || /[\u0000- \u007f\\]/u.test(value)) return false;
  try { const url = new URL(value); return !url.username && !url.password && !!url.hostname; } catch { return false; }
}

/**
 * Turns an HTML fragment given by a plugin into markup the window can show. Scripts, styles, event handler
 * attributes, frames, images and unknown elements are removed; what remains is text, structure, form
 * fields and the `data-alta-*` attributes the window acts on. Buttons, fields, tables and tags then get the
 * classes of the application's own components, so a fragment looks like the rest of the window.
 */
export function createPluginHtmlSanitizer(view: Window & typeof globalThis) {
  const purifier = createDOMPurify(view);
  purifier.addHook("uponSanitizeAttribute", (node, data) => {
    const tag = node.nodeName.toLowerCase();
    const { attrName: attribute, attrValue: value } = data;
    const field = fields.includes(tag);
    data.keepAttr = attribute === "title" && value.length <= 512
      || attribute === "class" && value.split(/\s+/u).filter(Boolean).every(item => classes.test(item))
      || attribute === "href" && tag === "a" && safeHref(value)
      || attribute === "open" && tag === "details"
      || attribute === "scope" && tag === "th" && /^(col|row|colgroup|rowgroup)$/u.test(value)
      || attribute === "align" && (tag === "td" || tag === "th") && /^(left|right|center)$/u.test(value)
      || (attribute === "colspan" || attribute === "rowspan") && (tag === "td" || tag === "th") && /^(?:[1-9]|[1-9][0-9]|100)$/u.test(value)
      || attribute === "type" && (tag === "input" && inputTypes.test(value) || tag === "button" && value === "button")
      || attribute === "name" && (field || tag === "button") && name.test(value)
      || attribute === "value" && (field || tag === "option" || tag === "button" || tag === "progress" || tag === "meter") && value.length <= 65536
      || attribute === "placeholder" && field && value.length <= 256
      || (attribute === "checked" || attribute === "readonly" || attribute === "required") && field
      || attribute === "disabled" && (field || tag === "button" || tag === "option" || tag === "fieldset")
      || attribute === "selected" && tag === "option"
      || attribute === "multiple" && tag === "select"
      || attribute === "label" && (tag === "option" || tag === "optgroup") && value.length <= 256
      || (attribute === "min" || attribute === "max" || attribute === "step") && (tag === "input" || tag === "progress" || tag === "meter") && number.test(value)
      || (attribute === "rows" || attribute === "cols" || attribute === "maxlength") && field && /^[1-9]\d{0,4}$/u.test(value)
      // Labels name their field with an identifier of the fragment's own namespace.
      || (attribute === "id" && (field || tag === "button") || attribute === "for" && tag === "label") && /^alta-[A-Za-z0-9_-]{1,64}$/u.test(value)
      || attribute === "aria-label" && value.length <= 256
      || (attribute === pluginCommandAttribute || attribute === pluginActionAttribute) && name.test(value)
      || attribute === pluginValueAttribute && value.length <= 4096;
  });

  function dress(fragment: DocumentFragment) {
    for (const element of Array.from(fragment.querySelectorAll<HTMLElement>("*"))) {
      const tag = element.nodeName.toLowerCase();
      const own = Array.from(element.classList);
      const intent = own.map(item => intents[item]).find(Boolean);
      const add = (...items: string[]) => element.classList.add(...items);
      if (tag === "button") { element.setAttribute("type", "button"); add("bp6-button", "bp6-small"); if (intent) add(`bp6-intent-${intent}`); }
      else if (tag === "input") {
        const type = element.getAttribute("type") ?? "text";
        if (!element.hasAttribute("type")) element.setAttribute("type", "text");
        if (!["checkbox", "radio", "range", "color", "hidden"].includes(type)) add("bp6-input", "bp6-small");
        element.setAttribute("autocomplete", "off");
      } else if (tag === "textarea") { add("bp6-input", "bp6-small"); element.setAttribute("spellcheck", "false"); }
      else if (tag === "table") add("bp6-html-table", "bp6-compact");
      else if (tag === "a") { element.setAttribute("rel", "noreferrer noopener"); element.setAttribute("target", "_blank"); }
      if (own.includes("alta-tag")) { add("bp6-tag", "bp6-minimal", "bp6-round"); if (intent) add(`bp6-intent-${intent}`); }
      if (own.includes("alta-callout")) { add("bp6-callout", "bp6-compact"); if (intent) add(`bp6-intent-${intent}`); }
      if (own.includes("alta-muted")) add("bp6-text-muted");
    }
  }

  return (html: string): string => {
    try {
      // Names stay as the plugin wrote them, "title" and "body" included: a name is kept on fields and buttons
      // only, which the document does not expose by name, and an identifier always starts with "alta-".
      const fragment = purifier.sanitize(html, { ALLOWED_TAGS: tags, ALLOWED_ATTR: attributes, ALLOW_DATA_ATTR: false, ALLOW_ARIA_ATTR: false, SANITIZE_DOM: false,
        RETURN_DOM_FRAGMENT: true, FORBID_TAGS: ["style", "script", "iframe", "img", "svg", "math", "form", "object", "embed", "link", "meta"] });
      dress(fragment);
      const container = view.document.createElement("div");
      container.appendChild(fragment);
      return container.innerHTML;
    } catch {
      return "";
    }
  };
}

/** A structural view of an element that carries values, so this stays testable without a browser. */
type FieldElement = { name?: string; type?: string; value?: string; checked?: boolean; disabled?: boolean; multiple?: boolean;
  selectedOptions?: ArrayLike<{ value: string }>; nodeName: string };

/**
 * The values of the named fields of a fragment: the text of an input, `true` or `false` for a checkbox,
 * the value of the chosen radio button, and the chosen options of a multiple select joined by commas.
 */
export function collectPluginFields(root: { querySelectorAll(selector: string): ArrayLike<unknown> }): Record<string, string> {
  const values: Record<string, string> = {};
  for (const element of Array.from(root.querySelectorAll("input[name], select[name], textarea[name]")) as FieldElement[]) {
    if (!element.name || element.disabled || Object.keys(values).length >= 128 && !(element.name in values)) continue;
    const type = (element.type ?? "").toLowerCase();
    if (type === "checkbox") values[element.name] = element.checked ? "true" : "false";
    else if (type === "radio") { if (element.checked) values[element.name] = element.value ?? ""; else if (!(element.name in values)) values[element.name] = ""; }
    else if (element.nodeName.toLowerCase() === "select" && element.multiple) values[element.name] = Array.from(element.selectedOptions ?? []).map(option => option.value).join(",");
    else values[element.name] = (element.value ?? "").slice(0, 65536);
  }
  return values;
}
