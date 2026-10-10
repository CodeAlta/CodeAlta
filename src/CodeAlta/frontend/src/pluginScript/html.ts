/**
 * `html`, the tagged template that stands for JSX in a script that has no build step: elements are written as in JSX and the values
 * go in `${...}` holes.
 *
 * ```js
 * html`<${Button} intent="primary" onClick=${save}>Save<//>`
 * ```
 *
 * It is the syntax of the `htm` library, in a small parser of our own so that the window takes no dependency for it: a tag is a name or a
 * hole (a component), `<//>` or `</>` closes the open element, `<>…</>` is a fragment, an attribute is `name="text"`, `name=${value}`,
 * `name` (true) or `...${props}`, text that holds a line break is trimmed, and a template with several roots gives an array.
 * A template is parsed once, whatever the values. On a page element (a tag that is a name) `class` and `for` are read as `className` and
 * `htmlFor`, as the HTML a model writes by habit has them.
 */
export type CreateElement = (type: unknown, props: Record<string, unknown> | null, ...children: unknown[]) => unknown;

// What stands for a `${…}` in the text a template is parsed from.
const hole = "\u0001";

type Part = string | Readonly<{ hole: number }>;
type Prop = Readonly<{ spread: number }> | Readonly<{ name: Part; value: readonly Part[] | true }>;
type Node = Readonly<{ tag: Part; props: readonly Prop[]; children: Child[] }>;
type Child = Node | Part;

/** A syntax error of a template, with what was read before it. */
export class HtmlTemplateError extends Error {
  constructor(message: string, near: string) {
    super(`${message} near "${near.replaceAll(hole, "${…}").slice(0, 40)}"`);
    this.name = "HtmlTemplateError";
  }
}

function parse(strings: readonly string[]): readonly Child[] {
  const source = strings.join(hole);
  let holes = 0;
  const holeAt = () => ({ hole: holes++ });
  let position = 0;
  const root: Child[] = [];
  const stack: Node[] = [];
  const children = (): Child[] => stack.length ? stack[stack.length - 1].children : root;
  // A hole in what is skipped (a comment, a closing tag) still has its number: the values are read in order.
  const skip = (text: string) => { holes += text.split(hole).length - 1; };
  const whitespace = /\s/u;
  const skipSpace = () => { while (position < source.length && whitespace.test(source[position])) position++; };

  // A run of text and holes becomes parts: the holes keep their numbers in the order they are read.
  function pieces(text: string): Part[] {
    const out: Part[] = [];
    for (const [index, chunk] of text.split(hole).entries()) {
      if (index > 0) out.push(holeAt());
      if (chunk) out.push(chunk);
    }
    return out;
  }

  function text(raw: string) {
    // A text with a line break is the layout of the template: its edges are trimmed, and what is blank goes.
    const trimmed = raw.includes("\n") ? raw.replace(/^\s*\n\s*|\s*\n\s*$/gu, "") : raw;
    if (trimmed) children().push(...pieces(trimmed));
  }

  while (position < source.length) {
    const next = source.indexOf("<", position);
    if (next < 0) { text(source.slice(position)); break; }
    if (next > position) text(source.slice(position, next));
    position = next + 1;
    if (source.startsWith("!--", position)) {
      const end = source.indexOf("-->", position);
      if (end < 0) throw new HtmlTemplateError("A comment is not closed", source.slice(position));
      skip(source.slice(position, end));
      position = end + 3;
      continue;
    }

    if (source[position] === "/") {
      const end = source.indexOf(">", position);
      if (end < 0) throw new HtmlTemplateError("A closing tag is not closed", source.slice(position));
      skip(source.slice(position, end));
      position = end + 1;
      if (!stack.length) throw new HtmlTemplateError("A closing tag closes nothing", source.slice(Math.max(0, position - 8), position));
      stack.pop();
      continue;
    }

    // An opening tag: its name (or a hole, or nothing for a fragment), then its attributes.
    const start = position;
    while (position < source.length && !whitespace.test(source[position]) && source[position] !== ">" && source[position] !== "/") position++;
    const name = source.slice(start, position);
    const tag: Part = name === hole ? holeAt() : name;
    const props: Prop[] = [];
    let selfClosing = false;
    for (;;) {
      skipSpace();
      if (position >= source.length) throw new HtmlTemplateError("A tag is not closed", source.slice(start));
      if (source[position] === ">") { position++; break; }
      if (source[position] === "/" && source[position + 1] === ">") { selfClosing = true; position += 2; break; }
      if (source.startsWith(`...${hole}`, position)) { position += 3 + hole.length; props.push({ spread: holeAt().hole }); continue; }
      const nameStart = position;
      while (position < source.length && !whitespace.test(source[position]) && source[position] !== "=" && source[position] !== ">" && !(source[position] === "/" && source[position + 1] === ">")) position++;
      const attribute = source.slice(nameStart, position);
      if (!attribute) throw new HtmlTemplateError("An attribute has no name", source.slice(nameStart));
      if (source[position] !== "=") { props.push({ name: attribute === hole ? holeAt() : attribute, value: true }); continue; }
      position++;
      let value: string;
      const quote = source[position];
      if (quote === "\"" || quote === "'") {
        const end = source.indexOf(quote, position + 1);
        if (end < 0) throw new HtmlTemplateError("A quoted value is not closed", source.slice(position));
        value = source.slice(position + 1, end);
        position = end + 1;
      } else {
        const valueStart = position;
        while (position < source.length && !whitespace.test(source[position]) && source[position] !== ">" && !(source[position] === "/" && source[position + 1] === ">")) position++;
        value = source.slice(valueStart, position);
      }

      props.push({ name: attribute === hole ? holeAt() : attribute, value: pieces(value) });
    }

    const node: Node = { tag, props, children: [] };
    children().push(node);
    if (!selfClosing) stack.push(node);
  }

  if (stack.length) throw new HtmlTemplateError("An element is not closed", source.slice(-30));
  return root;
}

/**
 * Makes the `html` tag of a React: `createElement` of the window's React, and its `Fragment` for `<>…</>`.
 * @throws HtmlTemplateError When a template is not well formed (when it is first used).
 */
export function createHtml(createElement: CreateElement, fragment: unknown): (strings: TemplateStringsArray, ...values: unknown[]) => unknown {
  const cache = new WeakMap<TemplateStringsArray, readonly Child[]>();
  return (strings, ...values) => {
    let tree = cache.get(strings);
    if (!tree) { tree = parse(strings); cache.set(strings, tree); }
    const read = (part: Part) => typeof part === "string" ? part : values[part.hole];
    const build = (node: Node): unknown => {
      const type = node.tag === "" ? fragment : read(node.tag);
      let props: Record<string, unknown> | null = null;
      for (const entry of node.props) {
        props ??= {};
        if ("spread" in entry) { Object.assign(props, values[entry.spread] as object); continue; }
        const key = String(read(entry.name));
        const raw = entry.value;
        // A value that is one hole keeps the type of its value; mixed with text it is a string.
        props[key] = raw === true ? true : raw.length === 1 ? read(raw[0]) : raw.map(part => String(read(part))).join("");
      }

      // A page element written with the names of HTML (`class`, `for`) gets the names of React.
      if (props && typeof type === "string") {
        if ("class" in props) { props.className ??= props.class; delete props.class; }
        if ("for" in props) { props.htmlFor ??= props.for; delete props.for; }
      }

      return createElement(type, props, ...node.children.map(child => typeof child === "object" && "tag" in child ? build(child) : read(child as Part)));
    };
    const out = tree.map(child => typeof child === "object" && "tag" in child ? build(child) : read(child as Part));
    return out.length === 1 ? out[0] : out;
  };
}
