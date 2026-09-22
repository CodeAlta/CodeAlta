import { marked } from "marked";

function escapeHtml(value: string) {
  return value
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");
}

const renderer = new marked.Renderer();
renderer.html = ({ text }) => escapeHtml(text);

export function renderMarkdownHtml(markdown: string) {
  return marked.parse(markdown, {
    async: false,
    breaks: true,
    gfm: true,
    renderer,
  }) as string;
}
