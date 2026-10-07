import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";

const edge = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe", "C:/Program Files/Microsoft/Edge/Application/msedge.exe"].find(existsSync);

// What a plugin writes for a dialog: its own markup around a block of Markdown, indented like the rest.
const fragment = `
  <div class="alta-column">
    <p>Before</p>
    <div class="alta-markdown">
      ## Report

      | Step | Time |
      |---|---|
      | Build | 4.8 s |

      \`\`\`csharp
      var answer = 42; // the answer
      \`\`\`

      \`\`\`mermaid
      flowchart LR
        build[Build] --> test[Test]
      \`\`\`

      &lt;script&gt;window.pluginFixture.state.executed++&lt;/script&gt;
      &lt;img src="x" onerror="window.pluginFixture.state.executed++"&gt;
      <script>window.pluginFixture.state.executed++</script>
    </div>
    <div class="alta-row"><input name="title" value="kept"> <button data-alta-action="save" data-alta-value="now">Save</button></div>
  </div>`;

test("a plugin fragment shows its Markdown with the renderer of the window: a table, highlighted code and a diagram", { skip: !edge, timeout: 180_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-plugin-html-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./pluginHtml.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    await writeFile(join(root, "style.css"), await readFile(new URL("./style.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, ["--headless=new", "--disable-gpu", "--no-first-run", "--disable-background-networking", "--disable-extensions", `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
    let port = "";
    for (let attempt = 0; attempt < 100 && !port; attempt++) {
      try { port = (await readFile(join(root, "profile", "DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; }
      catch { await new Promise(resolve => setTimeout(resolve, 50)); }
    }
    assert.match(port, /^\d+$/);
    const pages: { type?: string; webSocketDebuggerUrl?: string }[] = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10_000) })).json();
    socket = new WebSocket(pages.find(value => value.type === "page")!.webSocketDebuggerUrl!);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", () => reject(Error("Browser unavailable")), { once: true }); });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<any>((resolve, reject) => {
      const id = ++sequence;
      const timer = setTimeout(() => reject(Error(`${method} timed out`)), 30_000);
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
    assert.equal(await wait("window.pluginFixture"), true);
    const block = "document.querySelector('.plugin-html .alta-markdown .markdown-content')";

    await evaluate(`pluginFixture.render(${JSON.stringify(fragment)})`);
    // The block is drawn by the Markdown component of the window, from the text the plugin wrote in it.
    assert.equal(await wait(`${block}?.querySelector('h2')?.textContent==='Report'`), true);
    assert.equal(await evaluate(`[...${block}.querySelectorAll('td')].map(cell=>cell.textContent).join('|')`), "Build|4.8 s");
    assert.equal(await evaluate(`${block}.querySelector('pre[data-language="csharp"] code').textContent`), "var answer = 42; // the answer\n");
    assert.ok(await evaluate<number>(`${block}.querySelectorAll('pre[data-language="csharp"] code span[class^="hljs-"]').length`) >= 2, "The code has the colors of its language.");
    assert.equal(await wait(`${block}.querySelector('.markdown-diagram svg')`, 60_000), true, "The mermaid block becomes its diagram.");
    assert.equal(await evaluate(`${block}.querySelector('.markdown-diagram svg').textContent.includes('Build')`), true);
    // Nothing a plugin writes runs, in its markup or in its Markdown, and nothing of it is loaded.
    assert.equal(await evaluate("pluginFixture.state.executed"), 0);
    assert.deepEqual(await evaluate(`[...document.querySelectorAll('.plugin-html script, .plugin-html img, .plugin-html [onerror]')].map(element=>element.outerHTML)`), []);
    // The rest of the fragment is as before: its own elements, its fields and its actions.
    assert.equal(await evaluate("document.querySelector('.plugin-html > .alta-column > p').textContent"), "Before");
    assert.equal(await evaluate("document.querySelector('.plugin-html button[data-alta-action]').classList.contains('bp6-button')"), true);
    await evaluate("document.querySelector('.plugin-html button[data-alta-action]').click()");
    assert.deepEqual(await evaluate("pluginFixture.state.actions"), [{ action: "save", value: "now", values: { title: "kept" } }]);

    // The same fragment given again leaves what is shown in place: a field being edited, a diagram already drawn.
    await evaluate("window.keptDiagram=document.querySelector('.plugin-html .markdown-diagram'); document.querySelector('.plugin-html input').value='typed'");
    await evaluate(`pluginFixture.render(${JSON.stringify(fragment)})`);
    assert.equal(await evaluate("document.querySelector('.plugin-html input').value==='typed' && document.querySelector('.plugin-html .markdown-diagram')===window.keptDiagram"), true);

    // A new fragment replaces it, with its own Markdown; one without Markdown has none left.
    await evaluate(`pluginFixture.render(${JSON.stringify(`<div class="alta-markdown">\n  # Other\n\n  - one\n  - two\n</div><div class="alta-markdown">**second** block</div>`)})`);
    assert.equal(await wait("document.querySelectorAll('.plugin-html .alta-markdown .markdown-content').length===2 && document.querySelector('.plugin-html h1')?.textContent==='Other'"), true);
    assert.equal(await evaluate("[...document.querySelectorAll('.plugin-html li')].map(item=>item.textContent).join('|') + '/' + document.querySelector('.plugin-html strong').textContent"), "one|two/second");
    assert.equal(await evaluate("!document.querySelector('.plugin-html h2') && !document.querySelector('.plugin-html .markdown-diagram')"), true);
    await evaluate(`pluginFixture.render("<p>plain</p>")`);
    assert.equal(await wait("document.querySelector('.plugin-html').textContent==='plain' && !document.querySelector('.markdown-content')"), true);
    // A component that starts with a fragment, as a dialog does, runs its effects twice under StrictMode: the
    // Markdown is still read once.
    await evaluate("pluginFixture.clear()");
    await evaluate(`pluginFixture.render(${JSON.stringify(`<div class="alta-markdown">*again*</div>`)})`);
    assert.equal(await wait("document.querySelector('.plugin-html .alta-markdown em')?.textContent==='again'"), true);
    assert.equal(await evaluate("document.querySelector('.plugin-html').textContent.trim()"), "again");
    assert.equal(await evaluate("pluginFixture.state.executed"), 0);
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close();
    if (browser && browser.exitCode === null) { const closed = new Promise<void>(resolve => browser!.once("exit", () => resolve())); browser.kill(); await Promise.race([closed, new Promise(resolve => setTimeout(resolve, 5000))]); }
    await rm(root, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
  }
});
