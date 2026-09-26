import MarkdownIt from "markdown-it";

/** Creates an instance-owned, browser-independent parser. Output is UNSANITIZED HTML:
 * only the MarkdownContent boundary may turn it into DOM, never inject it directly. */
export function createMarkdownParser() {
  const md = new MarkdownIt("commonmark", { html: true, breaks: true, linkify: true, typographer: false })
    .enable(["table", "strikethrough", "linkify"]);
  md.linkify.set({ fuzzyLink: false, fuzzyEmail: false, fuzzyIP: false });
  const escape = md.utils.escapeHtml;
  const code = (text: string, info: string) => {
    const language = md.utils.unescapeAll(info).trim().split(/\s+/)[0];
    const attr = /^[a-zA-Z0-9_-]{1,32}$/.test(language) ? ` class="language-${language}"` : "";
    // markdown-it includes the syntax-terminating LF unlike the previous token.text.
    // Preserve the existing one-LF trimming/display convention after removing it.
    const tokenText = text.replace(/\n$/, "");
    return `<pre><code${attr}>${escape(tokenText.replace(/\n$/, ""))}\n</code></pre>\n`;
  };
  md.renderer.rules.fence = (tokens, index) => code(tokens[index].content, tokens[index].info);
  md.renderer.rules.code_block = (tokens, index) => code(tokens[index].content, "");
  for (const rule of ["th_open", "td_open"]) md.renderer.rules[rule] = (tokens, index, options, _env, renderer) => {
    const token = tokens[index];
    const style = token.attrGet("style");
    const alignment = typeof style === "string" ? style.match(/^text-align:(left|right|center)$/)?.[1] : undefined;
    token.attrs = token.attrs?.filter(([name]) => name !== "style") ?? null;
    if (alignment) token.attrSet("align", alignment);
    return renderer.renderToken(tokens, index, options);
  };
  md.renderer.rules.image = (tokens, index) => escape(tokens[index].content);
  return (source: string) => md.render(source);
}
