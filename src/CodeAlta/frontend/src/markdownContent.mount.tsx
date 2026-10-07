import { createElement } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import { MarkdownContent } from "./MarkdownContent";
import { TimelineMessage } from "./TimelineMessage";
import type { TimelineItem } from "./timeline";
import MarkdownIt from "markdown-it";

// Literal fixtures only; no application host, clipboard or bridge.
const root = createRoot(document.getElementById("app")!);
const state = { phase: "ready", executed: 0, opens: [] as string[], copies: [] as string[],
  policies: [] as { directive: string; uri: string; phase: string }[], parses: 0, parsedBases: [] as string[] };
window.open = ((url?: string | URL) => { state.opens.push(String(url)); return null; }) as typeof window.open;
Object.defineProperty(navigator, "clipboard", { value: { writeText: async (text: string) => { state.copies.push(text); } } });
document.addEventListener("securitypolicyviolation", e => state.policies.push({ directive: e.effectiveDirective, uri: e.blockedURI, phase: state.phase }));
const nativeParse = DOMParser.prototype.parseFromString;
DOMParser.prototype.parseFromString = function (text, type) {
  state.phase = "dom-parse"; state.parses++;
  const doc = nativeParse.call(this, text, type);
  state.parsedBases.push(doc.baseURI);
  state.phase = "sanitize";
  return doc;
};
const cases = [
  { id: "useful", source: '<div><b>bold</b><kbd>key</kbd><details open><summary>Inspect</summary><p>detail</p></details><table><caption>Values</caption><tr><th scope="col">A</th><td colspan="2">42</td></tr></table></div>', selectors: "b,kbd,details[open] summary,table caption,th[scope=col],td[colspan='2']" },
  { id: "mixed", source: 'Inline <span>**mixed**</span>\nnext\n\n<div>\n\n*blank boundary*\n\n</div>\n\n| A | B |\n| :- | -: |\n| 1 | 2 |', selectors: 'span strong,br,div em,th[align=left],td[align=right]' },
  { id: "code", source: '```unknown-tool\r\n<a>&\r\n```\r\n\r\n    indented\r\n\r\n<pre id="session-prompt" class="copy-markdown" tabindex="3" role="button" aria-label="spoof"><code class="language-ts">raw &lt;b&gt;</code></pre>', selectors: 'pre.timeline-code[tabindex="0"][role=region] code.language-unknown-tool' },
  { id: "links", source: '[safe](https://remote.invalid/safe) [relative](/path) [mail](mailto:x@y.invalid) [creds](https://u:p@remote.invalid/) <a href="jav&#x61;script:window.markdownFixture.state.executed++">bad</a> www.example.invalid x@y.invalid', selectors: 'a[href="https://remote.invalid/safe"]' },
  { id: "self-import", source: '<div><style>@import url("https://markdown-production.invalid/import.css");</style></div>' },
  { id: "remote-import", source: '<div><style>@import url("https://remote.invalid/import.css");</style></div>' },
  { id: "css-url", source: '<div style="background:url(https://markdown-production.invalid/image)">text</div><div style="background:url(https://remote.invalid/image)">text</div>' },
  { id: "images", source: '![alt <b>](https://markdown-production.invalid/md.png)\n\n<img src="https://markdown-production.invalid/raw.png" onerror="window.markdownFixture.state.executed++"><picture><source srcset="https://remote.invalid/source"><img srcset="https://markdown-production.invalid/one 1x, https://remote.invalid/two 2x"></picture>' },
  { id: "media", source: '<video autoplay src="https://markdown-production.invalid/video" poster="https://remote.invalid/poster"><track src="https://markdown-production.invalid/track"></video><audio src="https://remote.invalid/audio"></audio>' },
  { id: "links-frames", source: '<div><link rel=stylesheet href="https://markdown-production.invalid/style"><link rel=stylesheet href="https://remote.invalid/style"><iframe src="https://markdown-production.invalid/frame"></iframe><iframe src="https://remote.invalid/frame"></iframe></div>' },
  { id: "data", source: '<img src="data:image/svg+xml,%3Csvg xmlns=%22http://www.w3.org/2000/svg%22 onload=%22parent.markdownFixture.state.executed++%22%3E%3C/svg%3E"><iframe src="data:text/html,%3Cscript%3Eparent.markdownFixture.state.executed++%3C/script%3E"></iframe>' },
  { id: "base", source: '<div><base href="https://remote.invalid/base/"><meta http-equiv=refresh content="0;url=https://markdown-production.invalid/navigation"></div>' },
  { id: "foreign", source: '<svg><a xlink:href="jav&#x61;script:window.markdownFixture.state.executed++">x</a><foreignObject><iframe src="https://markdown-production.invalid/foreign"></iframe></foreignObject></svg><math><mtext><table><mglyph><style><!--</style><img title="--><img src=https://markdown-production.invalid/malformed onerror=window.markdownFixture.state.executed++>">' },
  { id: "front-matter", source: '---\nname: release-notes\ndescription: "Writes: <b>notes</b>"\ntags:\n  - docs\n  - release\nmetadata:\n  owner: me\n---\n\n# Title\n\ntext', selectors: 'table.markdown-front-matter th[scope=row],table.markdown-front-matter td ul li,table.markdown-front-matter td pre code.language-yaml,h1' },
  { id: "tasks", source: '- [ ] open\n- [x] done **now**\n- plain\n\n1. [X] numbered\n\nloose:\n\n- [ ] first\n\n- [x] second\n\n<input type="checkbox" checked> authored', selectors: 'ul.markdown-task-list li.markdown-task-item span.markdown-task[role=checkbox][aria-checked=false][aria-disabled=true],span.markdown-task[aria-checked=true],ol.markdown-task-list,li.markdown-task-item p span.markdown-task' },
  { id: "alerts", source: '> [!NOTE]\n> Useful to know.\n\n> [!warning]\n>\n> Careful.\n\n> [!UNKNOWN]\n> a plain quote', selectors: 'blockquote.markdown-alert[data-alert=note] p.markdown-alert-title,blockquote.markdown-alert[data-alert=warning] p.markdown-alert-title' },
  { id: "spoof", source: '<table class="markdown-front-matter"><tr><td>authored</td></tr></table><ul class="markdown-task-list"><li class="markdown-task-item"><span class="markdown-task" role="checkbox" aria-checked="true">x</span> authored</li></ul><blockquote class="markdown-alert" data-alert="caution"><p class="markdown-alert-title">Caution</p>authored</blockquote>', selectors: 'table td,ul li span,blockquote p' },
  { id: "authority", source: '<div id="session-prompt" class="timeline-code copy-markdown" data-persisted-message="true" tabindex="0" role="button" aria-label="Send"><button>Run</button><form action="https://markdown-production.invalid/post"><input name="neoastra"></form><object data="https://markdown-production.invalid/object"></object><embed src="https://remote.invalid/embed"><script>window.markdownFixture.state.executed++</script><span onclick="window.open(\'https://remote.invalid\')">text</span></div>' },
];
function item(source: string): TimelineItem {
  return { key: "literal", eventType: "contentCompleted", category: "assistant", title: "Assistant", icon: "assistant",
    timestamp: "2026-01-01T00:00:00Z", subtitle: null, summary: null, summaryIsCode: false, detailMarkdown: null, details: null,
    markdown: source, copyMarkdown: source, metadata: [], detailsLabel: "Details", truncated: false, bodyOmitted: false };
}
function render(source: string, timeline = true) {
  state.phase = "render";
  flushSync(() => root.render(timeline ? createElement(TimelineMessage, { item: item(source) }) : createElement(MarkdownContent, { source })));
  state.phase = "insert-deferred";
}
function snapshot() {
  const content = document.querySelector(".markdown-content")!;
  return { ...state, html: content?.innerHTML, text: content?.textContent, location: location.href, base: document.baseURI, frames: frames.length,
    prohibited: content ? Array.from(content.querySelectorAll("script,style,img,picture,source,video,audio,track,iframe,link,base,meta,object,embed,svg,math,form,input,button,textarea,select"))
      .filter(e => !copyButton(e)).map(e => e.outerHTML) : [],
    attributes: content ? Array.from(content.querySelectorAll("*")).flatMap(e => Array.from(e.attributes).filter(a => /^on|^(style|id|name|data-.*|src|srcset|srcdoc|target|download|ping|action|formaction)$/i.test(a.name)
        // The renderer names a fenced block's language on its pre and colors its tokens with highlight.js spans.
        && !((e.tagName === "PRE" || copyButton(e)) && a.name === "data-language" && /^[a-z0-9_-]{1,32}$/.test(a.value))
        // The button of a code block says for a moment that it copied.
        && !(copyButton(e) && a.name === "data-copied" && a.value === "true")
        // The renderer names the kind of an alert on its quote.
        && !(e.tagName === "BLOCKQUOTE" && e.className === "markdown-alert" && a.name === "data-alert" && /^(?:note|tip|important|warning|caution)$/.test(a.value))
      || /^(role|tabindex|aria-.*)$/.test(a.name) && !(e.tagName === "PRE" && e.className === "timeline-code"
        && (a.name === "role" && a.value === "region" || a.name === "tabindex" && a.value === "0" || a.name === "aria-label" && a.value === "Code block"))
        // The button of a code block says what it does.
        && !(copyButton(e) && a.name === "aria-label" && a.value === "Copy")
        // The box of a task says what it shows, and that it takes no input.
        && !(e.tagName === "SPAN" && e.className === "markdown-task" && !e.firstChild
          && (a.name === "role" && a.value === "checkbox" || a.name === "aria-checked" && /^(?:true|false)$/.test(a.value) || a.name === "aria-disabled" && a.value === "true"))
      || a.name === "class" && !(e.tagName === "PRE" && a.value === "timeline-code" || e.tagName === "CODE" && /^language-[a-zA-Z0-9_-]{1,32}$/.test(a.value)
        || e.tagName === "SPAN" && !!e.closest("pre > code") && /^(?:(?:hljs-[a-z_-]+|[a-z]+_)(?: |$))+$/.test(a.value)
        // The elements of the renderer itself: the front matter, the tasks and the alerts.
        || rendererClasses[e.tagName]?.includes(a.value))).map(a => `${e.tagName}:${a.name}=${a.value}`)) : [] };
}
// The classes that the renderer gives its own elements. Authored HTML keeps none: see the "spoof" case.
const rendererClasses: Record<string, readonly string[] | undefined> = { TABLE: ["markdown-front-matter"], DIV: ["markdown-front-matter"], LI: ["markdown-task-item"],
  UL: ["markdown-task-list"], OL: ["markdown-task-list"], SPAN: ["markdown-task"], BLOCKQUOTE: ["markdown-alert"], P: ["markdown-alert-title"], BUTTON: ["markdown-copy"] };
// The one control of the renderer: the button of a code block, first in its block, with nothing in it.
const copyButton = (e: Element) => e.tagName === "BUTTON" && e.className === "markdown-copy" && !e.firstChild && e.getAttribute("type") === "button"
  && e.parentElement?.tagName === "PRE" && e.parentElement.firstElementChild === e && e.nextElementSibling?.tagName === "CODE";
Object.assign(window, { markdownFixture: { state, cases, render, snapshot,
  // A document, as the code editor and the Skills page show one.
  renderDocument(source: string) { state.phase = "render"; flushSync(() => root.render(createElement(MarkdownContent, { key: "document", source, document: true }))); state.phase = "insert-deferred"; },
  texts(selector: string) { return Array.from(document.querySelectorAll(`.markdown-content ${selector}`)).map(e => e.textContent); },
  run(index: number) { render(cases[index].source); return (cases[index].selectors?.split(",") ?? []).every(s => document.querySelector(`.markdown-content ${s}`)); },
  linkRect() { const a = document.querySelector<HTMLAnchorElement>(".markdown-content a[href]")!; a.focus(); return a.getBoundingClientRect().toJSON(); },
  codeCheck() {
    const texts = Array.from(document.querySelectorAll(".markdown-content pre code")).map(e => e.textContent);
    (document.querySelector(".copy-markdown") as HTMLButtonElement).click();
    return texts;
  },
  // Every code block has the button of the renderer; pressing one copies the text of its block alone.
  copyCheck() {
    const before = state.copies.length;
    const buttons = Array.from(document.querySelectorAll<HTMLButtonElement>(".markdown-content pre > button.markdown-copy"));
    buttons[0]?.click();
    return new Promise(resolve => setTimeout(() => resolve({ blocks: document.querySelectorAll(".markdown-content pre").length, buttons: buttons.length,
      languages: buttons.map(button => button.getAttribute("data-language")), copied: state.copies.slice(before), marked: buttons.map(button => button.hasAttribute("data-copied")) }), 50));
  },
  memoCheck() {
    const source = '```txt\n' + 'line\n'.repeat(45) + '```'; render(source);
    const toggle = document.querySelector<HTMLButtonElement>(".long-message-toggle"); if (toggle) flushSync(() => toggle.click());
    const pre = document.querySelector<HTMLElement>("pre.timeline-code")!;
    pre.style.height = "40px"; pre.style.overflow = "auto"; pre.focus(); pre.scrollTop = 30;
    const text = pre.querySelector("code")!.firstChild!; const range = document.createRange(); range.setStart(text, 0); range.setEnd(text, 4);
    const selection = getSelection()!; selection.removeAllRanges(); selection.addRange(range);
    const parses = state.parses; render(source);
    return { identity: document.querySelector("pre") === pre, focused: document.activeElement === pre, selection: selection.toString(), scroll: pre.scrollTop, reparsed: state.parses !== parses };
  },
  fallback(kind: "parser" | "sanitizer") {
    const source = '<img src="https://markdown-production.invalid/fallback" onerror="window.markdownFixture.state.executed++"> original';
    const parse = MarkdownIt.prototype.render, fragment = Document.prototype.createDocumentFragment;
    try {
      if (kind === "parser") MarkdownIt.prototype.render = () => { throw new Error("private parser detail"); };
      else Document.prototype.createDocumentFragment = () => { throw new Error("private sanitizer detail"); };
      flushSync(() => root.render(createElement(MarkdownContent, { key: kind, source, timelineCodeBlocks: true })));
      return { source, ...snapshot() };
    } finally { MarkdownIt.prototype.render = parse; Document.prototype.createDocumentFragment = fragment; }
  },
} });
