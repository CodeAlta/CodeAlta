import { marked, type Tokens } from "marked";

function escapeHtml(value: string) {
  return value
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");
}

export function renderMarkdownHtml(markdown: string, timelineCodeBlocks = false) {
  // Per-render configuration: timeline opt-in cannot mutate shared/default rendering.
  const renderer = new marked.Renderer();
  renderer.html = ({ text }) => escapeHtml(text);
  if (timelineCodeBlocks) renderer.code = ({ text, lang }: Tokens.Code) => {
    const language = lang?.match(/^\S*/)?.[0];
    const className = language ? ` class="language-${escapeHtml(language)}"` : "";
    return `<pre class="timeline-code" tabindex="0" role="region" aria-label="Code block"><code${className}>${escapeHtml(text.replace(/\n$/, ""))}\n</code></pre>\n`;
  };
  return marked.parse(markdown, {
    async: false,
    breaks: true,
    gfm: true,
    renderer,
  }) as string;
}
