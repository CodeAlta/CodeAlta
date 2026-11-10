import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, endBrowser, waitForBrowserPort } from "../browserTarget";
import { testLimit, testLimitRanOut } from "../testTime";

const edge = browserExecutable;

// A plan as the editor opens one: a front matter, text, and a diagram of each kind that plans draw.
const plan = `---
title: A plan
status: in-progress
---

# A plan

Before the diagrams.

\`\`\`mermaid
flowchart LR
  build[Build] --> test[Test]
\`\`\`

\`\`\`mermaid
sequenceDiagram
  participant S as Source page
  participant H as Host
  S->>H: dragMoved(tab, point in its window)
  Note over H: source window empty → close it
\`\`\`

After the diagrams.
`;

test("the Markdown preview of the code editor draws its mermaid blocks, with and without the lines of a review", { skip: !edge, timeout: testLimit(180_000) }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-editor-preview-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./markdownPreview.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    await writeFile(join(root, "style.css"), await readFile(new URL("../style.css", import.meta.url), "utf8") + await readFile(new URL("./editor.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, [...browserBaseArgs, `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(root, "profile"), browser);
    const pages: { type?: string; webSocketDebuggerUrl?: string }[] = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(testLimit(10_000)) })).json();
    socket = new WebSocket(pages.find(value => value.type === "page")!.webSocketDebuggerUrl!);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", () => reject(Error("Browser unavailable")), { once: true }); });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<any>((resolve, reject) => {
      const id = ++sequence;
      const timer = setTimeout(() => reject(Error(testLimitRanOut(testLimit(70_000), `the answer to ${method}`))), testLimit(70_000));
      const reply = (event: MessageEvent) => {
        const message = JSON.parse(String(event.data));
        if (message.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer);
        if (message.error) reject(Error(JSON.stringify(message.error))); else resolve(message.result);
      };
      socket!.addEventListener("message", reply); socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async <T,>(expression: string): Promise<T> => {
      const result = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails));
      return result.result?.value as T;
    };
    const wait = (expression: string, milliseconds = testLimit(8_000)) => evaluate<boolean>(
      `new Promise(resolve=>{const end=Date.now()+${milliseconds};const tick=()=>{let ok=false;try{ok=!!(${expression})}catch{}ok?resolve(true):Date.now()>end?resolve(false):setTimeout(tick,25)};tick()})`);
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait("window.previewFixture"), true);
    const content = "document.querySelector('.editor-markdown > .markdown-content')";
    const shown = () => evaluate<string[]>(`[...${content}.children].map(e=>e.tagName+(e.className?'.'+e.className:'')+(e.querySelector(':scope > svg')?' svg':''))`);

    for (const [sourceLines, text] of [[false, plan], [true, plan], [false, plan.replaceAll("\n", "\r\n")]] as const) {
      await evaluate("previewFixture.clear()");
      await evaluate(`previewFixture.render(${JSON.stringify(text)}, ${sourceLines})`);
      const label = `sourceLines: ${sourceLines}${text.includes("\r") ? ", CRLF" : ""}`;
      assert.equal(await wait(`${content}?.querySelectorAll('.markdown-rendered > .markdown-diagram > svg').length===2`, testLimit(60_000)), true, `${label}: ${JSON.stringify(await shown())}`);
      assert.deepEqual(await shown(), ["TABLE.markdown-front-matter", "H1", "P", "DIV.markdown-rendered", "DIV.markdown-rendered", "P"], label);
      // Each diagram keeps its source beside it, hidden until its Source button shows it.
      assert.equal(await evaluate(`${content}.querySelectorAll('.markdown-rendered > pre[data-language="mermaid"][hidden]').length`), 2, label);
      if (sourceLines)
        assert.deepEqual(await evaluate(`[...${content}.querySelectorAll('.markdown-rendered')].map(e=>e.getAttribute('data-source-lines'))`), ["10-13", "15-21"]);
    }

    // Source shows the code of a diagram in its place, and Preview the drawing again.
    const toggle = `${content}.querySelector('.markdown-rendered button.markdown-source-toggle')`;
    const view = () => evaluate<unknown>(`(() => { const block = ${content}.querySelector('.markdown-rendered'); return [block.querySelector('.markdown-diagram').hidden, block.querySelector('pre').hidden, ${toggle}.textContent, ${toggle}.getAttribute('aria-pressed')]; })()`);
    assert.deepEqual(await view(), [false, true, "Source", "false"]);
    await evaluate(`${toggle}.click()`);
    assert.deepEqual(await view(), [true, false, "Preview", "true"]);
    await evaluate(`${toggle}.click()`);
    assert.deepEqual(await view(), [false, true, "Source", "false"]);

    // Expand opens the diagram in a window of its own, as a picture in the colors of the page, fitted to the window;
    // + zooms it, Escape closes the window and gives the keyboard back to the button. Source hides the button.
    const expand = `${content}.querySelector('.markdown-rendered button.markdown-diagram-expand')`;
    assert.equal(await evaluate(`${expand}.getAttribute('aria-label')`), "Expand the diagram");
    await evaluate(`${toggle}.click()`);
    assert.equal(await evaluate(`getComputedStyle(${expand}).display`), "none", "a diagram shown as its source is not expanded");
    await evaluate(`${toggle}.click()`);
    await evaluate(`${expand}.click()`);
    const picture = "document.querySelector('dialog.diagram-viewer-dialog[open] .editor-image-view img')";
    assert.equal(await wait(`${picture}?.complete && ${picture}.naturalWidth > 0`), true, "the diagram is drawn as a picture");
    assert.deepEqual(await evaluate(`(() => { const image = ${picture}, src = decodeURIComponent(image.src), view = image.parentElement.getBoundingClientRect(), shown = image.getBoundingClientRect();
      return [src.startsWith('data:image/svg+xml'), src.includes('--pele-fg') && src.includes('Build'), document.activeElement === image.parentElement,
        view.width > innerWidth - 100, image.closest('.editor-image').dataset.fit]; })()`), [true, true, true, true, "true"]);
    const zoom = "parseInt(document.querySelector('dialog.diagram-viewer-dialog .editor-image-zoom').textContent)";
    const fitted = await evaluate<number>(zoom);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "+", code: "Equal", text: "+", windowsVirtualKeyCode: 187 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "+", code: "Equal", windowsVirtualKeyCode: 187 });
    assert.equal(await wait(`${picture}.closest('.editor-image').dataset.fit === 'false' && ${zoom} > ${fitted}
      && Math.abs(${picture}.getBoundingClientRect().width - ${picture}.naturalWidth * ${zoom} / 100) < 2`), true, "+ zooms in");
    // Its title bar copies the diagram as PNG, twice its size with a margin, on the color behind it; and its Mermaid text,
    // which it shows beside it (as text here: the window of the application gives its code editor).
    const header = (name: string) => `document.querySelector('dialog.diagram-viewer-dialog .app-window-titlebar button.${name}')`;
    await evaluate(`${header("diagram-viewer-copy-image")}.click()`);
    assert.equal(await wait("previewClipboard.images.length === 1"), true, "the picture is copied");
    const natural = await evaluate<{ width: number; height: number }>(`({ width: ${picture}.naturalWidth, height: ${picture}.naturalHeight })`);
    const copied = await evaluate<{ type: string; width: number; height: number; corner: number[] }>("previewClipboard.images[0]");
    assert.deepEqual([copied.type, copied.width, copied.height, copied.corner[3]], ["image/png", (natural.width + 32) * 2, (natural.height + 32) * 2, 255], JSON.stringify(copied));
    assert.equal(await wait(`${header("diagram-viewer-copy-image")}.getAttribute('aria-label') === 'Image copied'`), true);
    const mermaid = "flowchart LR\n  build[Build] --> test[Test]";
    await evaluate(`${header("diagram-viewer-copy-source")}.click()`);
    assert.equal(await wait("previewClipboard.text.length === 1"), true, "the source is copied");
    assert.equal(await evaluate("previewClipboard.text[0]"), mermaid);
    assert.equal(await evaluate("!!document.querySelector('.diagram-viewer-source')"), false);
    await evaluate(`${header("diagram-viewer-source-toggle")}.click()`);
    assert.equal(await wait("document.querySelector('.diagram-viewer-source pre')?.textContent === " + JSON.stringify(mermaid)), true, "the source is shown beside the diagram");
    assert.equal(await evaluate(`${header("diagram-viewer-source-toggle")}.getAttribute('aria-pressed')`), "true");
    await evaluate(`document.querySelector('dialog.diagram-viewer-dialog .editor-image-view').focus()`);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    assert.equal(await wait(`!document.querySelector('dialog.diagram-viewer-dialog') && document.activeElement === ${expand}`), true, "Escape closes the window");

    // A `;` ends a statement of a sequence diagram: the rest of the line is no statement, and Mermaid refuses the text.
    // The block stays code, labelled `mermaid`, with a note that says why; written `#59;`, the same text is drawn.
    const invalid = "```mermaid\nsequenceDiagram\n  H->>H: to screen units; hit-test every window\n```";
    await evaluate("previewFixture.clear()");
    await evaluate(`previewFixture.render(${JSON.stringify(`${invalid}\n\n${invalid.replace("; ", "#59; ")}`)}, false)`);
    assert.equal(await wait(`${content}?.querySelectorAll('.markdown-diagram > svg').length===1`, testLimit(60_000)), true, JSON.stringify(await shown()));
    await new Promise(resolve => setTimeout(resolve, 500));
    assert.equal(await wait(`${content}?.querySelector('.markdown-diagram-refused')`), true, JSON.stringify(await shown()));
    assert.deepEqual(await shown(), ["PRE", "P.markdown-diagram-refused", "DIV.markdown-rendered"]);
    assert.equal(await evaluate(`${content}.querySelector('pre').getAttribute('data-language')`), "mermaid");
    assert.match(await evaluate<string>(`${content}.querySelector('.markdown-diagram-refused').textContent`), /^Not drawn as a diagram: Parse error on line \d+$/);

    // A press in the page gives it the keyboard, and the keys move in it.
    await evaluate("previewFixture.clear()");
    await evaluate(`previewFixture.render(${JSON.stringify(`# Long\n\n${"A line.\n\n".repeat(200)}`)}, false)`);
    const box = "document.querySelector('.editor-markdown')";
    await evaluate(`${box}.style.height = '200px'`);
    const rect = await evaluate<{ x: number; y: number }>(`${box}.getBoundingClientRect().toJSON()`);
    for (const type of ["mousePressed", "mouseReleased"]) await command("Input.dispatchMouseEvent", { type, button: "left", clickCount: 1, x: rect.x + 40, y: rect.y + 100 });
    assert.equal(await evaluate(`document.activeElement === ${box}`), true, "The page has the keyboard");
    const top = () => evaluate<number>(`${box}.scrollTop`);
    const press = async (key: string, code: number) => {
      await command("Input.dispatchKeyEvent", { type: "rawKeyDown", key, code: key, windowsVirtualKeyCode: code, nativeVirtualKeyCode: code });
      await command("Input.dispatchKeyEvent", { type: "keyUp", key, code: key, windowsVirtualKeyCode: code, nativeVirtualKeyCode: code });
    };
    const settled = async () => { let last = -1, now = await top(); while (now !== last) { await new Promise(resolve => setTimeout(resolve, 150)); last = now; now = await top(); } return now; };
    await press("PageDown", 34);
    const paged = await settled();
    assert.ok(paged > 100, `Page Down moves a page: ${paged}`);
    await press("ArrowDown", 40);
    const below = await settled();
    assert.ok(below > paged, `Arrow Down moves a line: ${below} after ${paged}`);
    await press("ArrowUp", 38);
    assert.ok(await settled() < below, "Arrow Up moves back");
    await press("PageUp", 33);
    assert.ok(await settled() < paged, "Page Up moves back a page");
    await press("End", 35);
    assert.ok(await settled() > paged * 5, "End goes to the end");
    await press("Home", 36);
    assert.equal(await settled(), 0, "Home goes to the start");
  } finally {
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close();
    await endBrowser(browser);
    await rm(root, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
  }
});
