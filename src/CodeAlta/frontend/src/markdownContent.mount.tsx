import { createElement } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import { MarkdownContent, MarkdownImagesContext, MarkdownLinkScopeContext } from "./MarkdownContent";
import type { MarkdownImage, MarkdownImages } from "./markdownImages";
import { sourceBlocks } from "./markdownSourceLines";
import { MessageLinksContext, TimelineMessage } from "./TimelineMessage";
import type { TimelineItem } from "./timeline";
import MarkdownIt from "markdown-it";

// Literal fixtures only; no application host, clipboard or bridge.
const root = createRoot(document.getElementById("app")!);
const state = { phase: "ready", executed: 0, opens: [] as string[], copies: [] as string[],
  external: [] as string[], pictures: [] as string[], reveals: [] as string[], sourceLines: false,
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
  { id: "links", source: '[safe](https://remote.invalid/safe) [relative](/path) [file](src/Program.cs#L3) [mail](mailto:x@y.invalid) [creds](https://u:p@remote.invalid/) [share](//remote.invalid/share/a.cs) <a href="jav&#x61;script:window.markdownFixture.state.executed++">bad</a> <a href="src/authored.cs" data-file-link="spoof">authored</a> <a href="https://remote.invalid/authored">page</a> www.example.invalid x@y.invalid', selectors: 'a[href="https://remote.invalid/safe"],a[href="/path"],a[href="src/Program.cs#L3"],a[href="https://remote.invalid/authored"]' },
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
  { id: "file-images", source: '![chart <b>](out/chart.png) ![](C:/pics/my%20plot.png) ![web](https://remote.invalid/a.png) <img src="out/raw.png" class="markdown-local-image"> <a href="out/forged.png" data-file-link="spoof" data-file-image="spoof">forged</a> <span class="markdown-local-image-alt">authored</span>', selectors: 'a[href="out/chart.png"] > span.markdown-local-image-alt + img.markdown-local-image[alt="chart <b>"],a[href="C:/pics/my%20plot.png"] > img.markdown-local-image[alt="my plot.png"]' },
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
      .filter(e => !copyButton(e) && !localImage(e)).map(e => e.outerHTML) : [],
    attributes: content ? Array.from(content.querySelectorAll("*")).flatMap(e => Array.from(e.attributes).filter(a => /^on|^(style|id|name|data-.*|src|srcset|srcdoc|target|download|ping|action|formaction)$/i.test(a.name)
        // A picture of the disk is drawn from the `data:` URL the window read.
        && !(localImage(e) && a.name === "src" && a.value.startsWith("data:image/png;base64,"))
        // The renderer names a fenced block's language on its pre and colors its tokens with highlight.js spans.
        && !((e.tagName === "PRE" || copyButton(e)) && a.name === "data-language" && /^[a-z0-9_-]{1,32}$/.test(a.value))
        // The button of a code block says for a moment that it copied.
        && !(copyButton(e) && a.name === "data-copied" && a.value === "true")
        // The renderer names the kind of an alert on its quote.
        && !(e.tagName === "BLOCKQUOTE" && e.className === "markdown-alert" && a.name === "data-alert" && /^(?:note|tip|important|warning|caution)$/.test(a.value))
        // A render that asks for them gives each block its lines.
        && !(state.sourceLines && a.name === "data-source-lines" && /^\d+-\d+$/.test(a.value))
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
  UL: ["markdown-task-list"], OL: ["markdown-task-list"], SPAN: ["markdown-task", "markdown-local-image-alt"], BLOCKQUOTE: ["markdown-alert"], P: ["markdown-alert-title"], BUTTON: ["markdown-copy"],
  IMG: ["markdown-local-image"] };
// The image of the renderer in a link to a picture of the disk: last in its link, after the alternative text.
const localImage = (e: Element) => e.tagName === "IMG" && e.className === "markdown-local-image" && !e.hasAttribute("srcset")
  && e.parentElement?.tagName === "A" && e.parentElement.hasAttribute("href") && e.parentElement.lastElementChild === e
  && e.previousElementSibling?.className === "markdown-local-image-alt";
// What the window gives the pictures of the disk: one picture, drawn by the page, for one link; any other is not drawn.
function pictures(): MarkdownImages {
  const canvas = document.createElement("canvas");
  canvas.width = 48; canvas.height = 32;
  const url = canvas.toDataURL("image/png");
  const read = (address: string): MarkdownImage | null => address === "out/chart.png" ? { url, path: "C:\\out\\chart.png" } : null;
  return { platform: "windows", peek: () => undefined,
    load: async (address, scope) => { state.pictures.push(`${address}@${scope?.sessionId ?? ""}`); return read(address); },
    reveal: (address, scope) => { state.reveals.push(`${address}@${scope?.sessionId ?? ""}`); } };
}
// The one control of the renderer: the button of a code block, first in its block, with nothing in it.
const copyButton = (e: Element) => e.tagName === "BUTTON" && e.className === "markdown-copy" && !e.firstChild && e.getAttribute("type") === "button"
  && e.parentElement?.tagName === "PRE" && e.parentElement.firstElementChild === e && e.nextElementSibling?.tagName === "CODE";
Object.assign(window, { markdownFixture: { state, cases, render, snapshot,
  // An explicit, inert recording grant, never a real bridge or system-browser launch.
  renderGranted(category: TimelineItem["category"] = "assistant", current = true, source = '[safe](https://remote.invalid/safe)') {
    flushSync(() => root.render(createElement(MessageLinksContext.Provider, { value: address => state.external.push(address) },
      createElement(TimelineMessage, { item: { ...item(source), category }, canInspect: () => current }))));
  },
  // A message of a session whose window reads the pictures of the disk.
  renderPictures(source: string) {
    flushSync(() => root.render(createElement(MessageLinksContext.Provider, { value: address => state.external.push(address) },
      createElement(MarkdownImagesContext.Provider, { value: pictures() }, createElement(MarkdownLinkScopeContext.Provider, { value: { sessionId: "s1" } },
        createElement(TimelineMessage, { item: item(source), canInspect: () => true }))))));
  },
  pictureRect() { return document.querySelector(".markdown-content img.markdown-local-image[src]")?.getBoundingClientRect().toJSON() ?? null; },
  viewer() {
    const dialog = document.querySelector<HTMLDialogElement>("dialog.timeline-image-dialog");
    return dialog && { open: dialog.open, title: dialog.getAttribute("aria-label"), path: dialog.querySelector(".timeline-image-file code")?.textContent,
      image: dialog.querySelector<HTMLImageElement>(".timeline-image-view img")?.src.startsWith("data:image/png;base64,") ?? false,
      buttons: Array.from(dialog.querySelectorAll(".timeline-image-file button"), button => button.getAttribute("title")) };
  },
  revealPicture() { document.querySelector<HTMLButtonElement>("dialog.timeline-image-dialog .timeline-image-file button[title='Reveal in File Explorer']")!.click(); },
  // A document, as the code editor and the Skills page show one.
  renderDocument(source: string) { state.phase = "render"; flushSync(() => root.render(createElement(MarkdownContent, { key: "document", source, document: true }))); state.phase = "insert-deferred"; },
  // A document whose blocks say which lines they come from, as a review shows one.
  renderSourceLines(source: string, sourceLines = true) {
    state.phase = "render"; state.sourceLines = sourceLines;
    flushSync(() => root.render(createElement(MarkdownContent, { key: "source-lines", source, document: true, sourceLines })));
    state.phase = "insert-deferred";
    const content = document.querySelector<HTMLElement>(".markdown-content")!;
    const stripped = content.cloneNode(true) as HTMLElement;
    for (const element of Array.from(stripped.querySelectorAll("[data-source-lines]"))) element.removeAttribute("data-source-lines");
    return { html: content.innerHTML, stripped: stripped.innerHTML, attributes: content.querySelectorAll("[data-source-lines]").length,
      blocks: sourceBlocks(content).map(({ element, start, end }) => `${element.tagName}${element.className ? `.${element.className}` : ""} ${start}-${end}`) };
  },
  texts(selector: string) { return Array.from(document.querySelectorAll(`.markdown-content ${selector}`)).map(e => e.textContent); },
  // Formulas: the source first, then KaTeX's drawing once it is loaded.
  math() {
    const content = document.querySelector(".markdown-content")!;
    return { sources: Array.from(content.querySelectorAll(".markdown-math-source, pre > code.language-math"), e => e.textContent),
      inline: content.querySelectorAll("span.markdown-math > .katex").length,
      display: content.querySelectorAll(".markdown-math-block > .markdown-math-display > .katex-display").length,
      chemistry: Array.from(content.querySelectorAll("span.markdown-math"))[1]?.querySelector(".katex-html")?.textContent?.replace(/​/g, "") ?? "",
      errors: Array.from(content.querySelectorAll(".katex-error"), e => e.getAttribute("title")?.startsWith("ParseError") ?? false),
      tex: Array.from(content.querySelectorAll('annotation[encoding="application/x-tex"]'), e => e.textContent),
      buttons: Array.from(content.querySelectorAll(".markdown-math-block button.markdown-math-copy"), e => e.getAttribute("aria-label")),
      authored: Array.from(content.querySelectorAll("[data-math], [data-math-display]")).length,
      paragraphs: Array.from(content.querySelectorAll("p"), e => e.textContent) };
  },
  // Footnotes: the references, the notes and their ways back; a click moves between them.
  footnotes() {
    const content = document.querySelector(".markdown-content")!;
    return { references: Array.from(content.querySelectorAll("sup.markdown-footnote > button.markdown-footnote-ref"), e => [e.textContent, e.getAttribute("title"), e.getAttribute("aria-label")]),
      notes: Array.from(content.querySelectorAll(".markdown-footnotes[role=note] li.markdown-footnote-note"), e => e.textContent?.trim()),
      back: content.querySelectorAll("li.markdown-footnote-note button.markdown-footnote-back").length,
      authored: content.querySelectorAll("sup:not(.markdown-footnote), [data-footnote]:not(.markdown-footnote-ref):not(.markdown-footnote-back):not(.markdown-footnote):not(.markdown-footnote-note):not(.markdown-footnotes)").length };
  },
  followFootnote(selector: string) {
    document.querySelector<HTMLButtonElement>(`.markdown-content ${selector}`)!.click();
    const active = document.activeElement;
    return { focused: `${active?.className}:${active?.getAttribute("data-footnote")}`, lit: Array.from(document.querySelectorAll(".markdown-content [data-flash]"), e => e.className) };
  },
  copyEquation(kind: "latex" | "mathml") {
    const before = state.copies.length;
    document.querySelector<HTMLButtonElement>(`.markdown-content button.markdown-math-copy[data-copy=${kind}]`)!.click();
    return new Promise(resolve => setTimeout(() => resolve(state.copies.slice(before)), 50));
  },
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
