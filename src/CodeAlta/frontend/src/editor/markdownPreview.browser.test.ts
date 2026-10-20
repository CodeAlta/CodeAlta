import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, endBrowser, waitForBrowserPort } from "../browserTarget";

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

test("the Markdown preview of the code editor draws its mermaid blocks, with and without the lines of a review", { skip: !edge, timeout: 180_000 }, async () => {
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
    const pages: { type?: string; webSocketDebuggerUrl?: string }[] = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10_000) })).json();
    socket = new WebSocket(pages.find(value => value.type === "page")!.webSocketDebuggerUrl!);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", () => reject(Error("Browser unavailable")), { once: true }); });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<any>((resolve, reject) => {
      const id = ++sequence;
      const timer = setTimeout(() => reject(Error(`${method} timed out`)), 70_000);
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
    const wait = (expression: string, milliseconds = 8_000) => evaluate<boolean>(
      `new Promise(resolve=>{const end=Date.now()+${milliseconds};const tick=()=>{let ok=false;try{ok=!!(${expression})}catch{}ok?resolve(true):Date.now()>end?resolve(false):setTimeout(tick,25)};tick()})`);
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait("window.previewFixture"), true);
    const content = "document.querySelector('.editor-markdown > .markdown-content')";
    const shown = () => evaluate<string[]>(`[...${content}.children].map(e=>e.tagName+(e.className?'.'+e.className:'')+(e.querySelector(':scope > svg')?' svg':''))`);

    for (const [sourceLines, text] of [[false, plan], [true, plan], [false, plan.replaceAll("\n", "\r\n")]] as const) {
      await evaluate("previewFixture.clear()");
      await evaluate(`previewFixture.render(${JSON.stringify(text)}, ${sourceLines})`);
      const label = `sourceLines: ${sourceLines}${text.includes("\r") ? ", CRLF" : ""}`;
      assert.equal(await wait(`${content}?.querySelectorAll('.markdown-diagram > svg').length===2`, 60_000), true, `${label}: ${JSON.stringify(await shown())}`);
      assert.deepEqual(await shown(), ["TABLE.markdown-front-matter", "H1", "P", "DIV.markdown-diagram svg", "DIV.markdown-diagram svg", "P"], label);
      assert.equal(await evaluate(`${content}.querySelector('pre[data-language="mermaid"]')`), null, label);
      if (sourceLines)
        assert.deepEqual(await evaluate(`[...${content}.querySelectorAll('.markdown-diagram')].map(e=>e.getAttribute('data-source-lines'))`), ["10-13", "15-21"]);
    }

    // A `;` ends a statement of a sequence diagram: the rest of the line is no statement, and Mermaid refuses the text.
    // The block stays code, labelled `mermaid`, with a note that says why; written `#59;`, the same text is drawn.
    const invalid = "```mermaid\nsequenceDiagram\n  H->>H: to screen units; hit-test every window\n```";
    await evaluate("previewFixture.clear()");
    await evaluate(`previewFixture.render(${JSON.stringify(`${invalid}\n\n${invalid.replace("; ", "#59; ")}`)}, false)`);
    assert.equal(await wait(`${content}?.querySelectorAll('.markdown-diagram > svg').length===1`, 60_000), true, JSON.stringify(await shown()));
    await new Promise(resolve => setTimeout(resolve, 500));
    assert.equal(await wait(`${content}?.querySelector('.markdown-diagram-refused')`), true, JSON.stringify(await shown()));
    assert.deepEqual(await shown(), ["PRE", "P.markdown-diagram-refused", "DIV.markdown-diagram svg"]);
    assert.equal(await evaluate(`${content}.querySelector('pre').getAttribute('data-language')`), "mermaid");
    assert.match(await evaluate<string>(`${content}.querySelector('.markdown-diagram-refused').textContent`), /^Not drawn as a diagram: Parse error on line \d+$/);
  } finally {
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close();
    await endBrowser(browser);
    await rm(root, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
  }
});
