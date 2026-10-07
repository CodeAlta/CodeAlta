import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";

const edge = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe", "C:/Program Files/Microsoft/Edge/Application/msedge.exe"].find(existsSync);

test("production MarkdownContent retains useful HTML without resource or app authority under production CSP", { skip: !edge, timeout: 60_000 }, async () => {
  const directory = await mkdtemp(join(tmpdir(), "codealta-markdown-boundary-"));
  let browser: ReturnType<typeof spawn> | undefined, socket: WebSocket | undefined;
  try {
    const config = JSON.parse(await readFile(new URL("../../neoastra.json", import.meta.url), "utf8")) as { assets: { csp: string } };
    const bundle = await build({ entryPoints: [fileURLToPath(new URL("./markdownContent.mount.tsx", import.meta.url))],
      bundle: true, platform: "browser", format: "iife", write: false });
    const origin = "https://markdown-production.invalid", page = origin + "/index.html", script = origin + "/fixture.js";
    const assets = new Map([[page, '<!doctype html><html><head><link rel="icon" href="data:,"></head><body><div id="app"></div><script src="/fixture.js"></script></body></html>'], [script, bundle.outputFiles[0].text]]);
    browser = spawn(edge!, ["--headless=new", "--disable-gpu", "--no-first-run", "--disable-background-networking", "--disable-extensions", "--edge-skip-compat-layer-relaunch",
      "--host-resolver-rules=MAP * ~NOTFOUND", `--user-data-dir=${join(directory, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
    let port = "";
    for (let i = 0; i < 100 && !port; i++) {
      try { port = (await readFile(join(directory, "profile/DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; }
      catch { await new Promise(resolve => setTimeout(resolve, 50)); }
    }
    assert.match(port, /^\d+$/);
    // Loopback is solely the Node-to-CDP control channel, never a page asset or application bridge.
    const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(5000) })).json() as { type: string; url: string; webSocketDebuggerUrl: string }[];
    socket = new WebSocket(pages.find(p => p.type === "page" && p.url === "about:blank")!.webSocketDebuggerUrl);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", reject, { once: true }); });
    let sequence = 0;
    type Reply = { result?: { value?: unknown }; exceptionDetails?: unknown };
    const pending = new Map<number, (reply: { result?: Reply; error?: unknown }) => void>();
    const command = (method: string, params: object = {}) => new Promise<Reply>((resolve, reject) => {
      const id = ++sequence;
      const timer = setTimeout(() => { pending.delete(id); reject(new Error(`${method} timeout`)); }, 8000);
      pending.set(id, reply => { clearTimeout(timer); reply.error ? reject(new Error(JSON.stringify(reply.error))) : resolve(reply.result ?? {}); });
      socket!.send(JSON.stringify({ id, method, params }));
    });
    const attempts: string[] = [], blocked: string[] = [], failures: string[] = [], baseDiagnostics: string[] = [];
    socket.addEventListener("message", event => {
      const m = JSON.parse(String(event.data));
      if (pending.has(m.id)) { pending.get(m.id)!(m); pending.delete(m.id); }
      if (m.method === "Fetch.requestPaused") {
        const { requestId, request } = m.params; attempts.push(request.url);
        const body = assets.get(request.url);
        const action = body === undefined
          ? (blocked.push(request.url), command("Fetch.failRequest", { requestId, errorReason: "BlockedByClient" }))
          : command("Fetch.fulfillRequest", { requestId, responseCode: 200, responseHeaders: [
            { name: "Content-Type", value: request.url === page ? "text/html" : "text/javascript" },
            { name: "Content-Security-Policy", value: config.assets.csp }], body: Buffer.from(body).toString("base64") });
        void action.catch(error => failures.push(String(error)));
      }
      if (m.method === "Network.requestWillBeSent" && !assets.has(m.params.request.url)) failures.push(`request attempt: ${m.params.request.url}`);
      if (["Page.windowOpen", "Page.frameAttached", "Page.frameRequestedNavigation", "Runtime.exceptionThrown"].includes(m.method)) failures.push(JSON.stringify(m));
      if (m.method === "Page.frameNavigated" && m.params.frame.url !== page) failures.push(JSON.stringify(m));
      if (m.method === "Log.entryAdded" && m.params.entry.source === "security") {
        const text = String(m.params.entry.text);
        if (text.includes("base-uri 'none'") && text.includes("https://remote.invalid/base/")) baseDiagnostics.push(text);
        else failures.push(text);
      }
    });
    await command("Page.enable"); await command("Runtime.enable"); await command("Network.enable"); await command("Log.enable");
    await command("Fetch.enable", { patterns: [{ urlPattern: "*", requestStage: "Request" }] });
    await command("Page.navigate", { url: page });
    const evaluate = async <T,>(expression: string): Promise<T> => {
      const reply = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(reply.exceptionDetails, undefined, JSON.stringify(reply.exceptionDetails)); return reply.result?.value as T;
    };
    let ready = false;
    for (let i = 0; i < 100 && !ready; i++) { ready = await evaluate<boolean>("!!window.markdownFixture"); if (!ready) await new Promise(resolve => setTimeout(resolve, 40)); }
    assert.ok(ready);
    const cases = await evaluate<{ id: string; source: string }[]>("markdownFixture.cases");
    const check = async () => {
      // Render is synchronous; this observes bounded deferred parse/sanitize/insertion effects together.
      await new Promise(resolve => setTimeout(resolve, 600));
      const s = await evaluate<{ location: string; base: string; parsedBases: string[]; frames: number; executed: number; opens: string[]; prohibited: string[]; attributes: string[]; policies: { directive: string; uri: string }[] }>("markdownFixture.snapshot()");
      assert.deepEqual(blocked, [], "intercepted attempts are failures, not safe loads"); assert.deepEqual(failures, []);
      assert.equal(s.location, page); assert.equal(s.base, page); assert.equal(s.frames, 0); assert.equal(s.executed, 0); assert.deepEqual(s.opens, []);
      assert.ok(s.parsedBases.every(base => base === page));
      assert.deepEqual(s.prohibited, []); assert.deepEqual(s.attributes, []);
      assert.ok(s.policies.every(p => p.directive === "base-uri" && p.uri === "https://remote.invalid/base/"));
    };
    for (let i = 0; i < cases.length; i++) {
      assert.equal(await evaluate(`markdownFixture.run(${i})`), true, `useful HTML control: ${cases[i].id}`);
      await check();
      if (cases[i].id === "code") {
        assert.deepEqual(await evaluate("markdownFixture.codeCheck()"), ["<a>&\n", "indented\n", "raw <b>"]);
        assert.deepEqual(await evaluate("markdownFixture.state.copies"), [cases[i].source], "Copy retains CRLF and original markup");
        // A block has a button of its own, which copies the block as it is written; an authored button is not one.
        assert.deepEqual(await evaluate("markdownFixture.copyCheck()"), { blocks: 3, buttons: 3, languages: ["unknown-tool", null, "ts"], copied: ["<a>&"], marked: [true, false, false] });
        await check();
      }
      if (cases[i].id === "front-matter") {
        // The entries are a table of names and values, built from the text: nothing of it is Markdown or HTML.
        assert.deepEqual(await evaluate("markdownFixture.texts('table.markdown-front-matter th')"), ["name", "description", "tags", "metadata"]);
        assert.deepEqual(await evaluate("markdownFixture.texts('table.markdown-front-matter td').slice(0, 2)"), ["release-notes", "Writes: <b>notes</b>"]);
        assert.deepEqual(await evaluate("markdownFixture.texts('table.markdown-front-matter td li')"), ["docs", "release"]);
        assert.deepEqual(await evaluate("markdownFixture.texts('table.markdown-front-matter td pre code')"), ["owner: me\n"]);
        // The text below is the document, without a rule or a heading made of the delimiters.
        assert.deepEqual(await evaluate("[markdownFixture.texts('h1'), document.querySelectorAll('.markdown-content hr, .markdown-content h2, .markdown-content table b').length]"), [["Title"], 0]);
      }
      if (cases[i].id === "tasks") {
        assert.deepEqual(await evaluate("Array.from(document.querySelectorAll('.markdown-content .markdown-task')).map(e => e.getAttribute('aria-checked'))"), ["false", "true", "true", "false", "true"]);
        assert.deepEqual(await evaluate("markdownFixture.texts('li')"), ["open", "done now", "plain", "numbered", "first", "second"].map((text, index) => index < 4 ? text : `\n${text}\n`));
        assert.equal(await evaluate("document.querySelectorAll('.markdown-content li:not(.markdown-task-item)').length"), 1, "An item without a mark is an item of the list.");
        assert.equal(await evaluate("document.querySelector('.markdown-content li strong')?.textContent"), "now");
      }
      if (cases[i].id === "alerts") {
        assert.deepEqual(await evaluate("markdownFixture.texts('.markdown-alert-title')"), ["Note", "Warning"]);
        assert.deepEqual(await evaluate("markdownFixture.texts('blockquote.markdown-alert > p:not(.markdown-alert-title)')"), ["Useful to know.", "Careful."]);
        assert.equal(await evaluate("document.querySelector('.markdown-content blockquote:not(.markdown-alert)')?.textContent.trim()"), "[!UNKNOWN]\na plain quote");
      }
      if (cases[i].id === "spoof") {
        // What the renderer gives its own elements cannot be authored: the classes, the role and the kind are removed.
        assert.equal(await evaluate("document.querySelectorAll('.markdown-content [class], .markdown-content [role], .markdown-content [data-alert]').length"), 0);
      }
      if (cases[i].id === "links") {
        assert.equal(await evaluate("document.querySelectorAll('.markdown-content a[href]').length"), 1);
        const rect = await evaluate<{ x: number; y: number; width: number; height: number }>("markdownFixture.linkRect()");
        for (const button of ["left", "middle"]) for (const type of ["mousePressed", "mouseReleased"]) await command("Input.dispatchMouseEvent", { type, button, x: rect.x + rect.width / 2, y: rect.y + rect.height / 2, clickCount: 1 });
        await evaluate("markdownFixture.linkRect()");
        await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13, text: "\r" });
        await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 }); await check();
      }
    }
    assert.deepEqual(await evaluate("markdownFixture.memoCheck()"), { identity: true, focused: true, selection: "line", scroll: 30, reparsed: false });
    // A document is wrapped in its source: its lines follow each other, where a message breaks them.
    await evaluate("markdownFixture.render('one\\ntwo', false)");
    assert.equal(await evaluate("document.querySelectorAll('.markdown-content br').length"), 1);
    await evaluate("markdownFixture.renderDocument('---\\ntitle: Guide\\n---\\n\\none\\ntwo\\n\\n- [x] done\\n\\n> [!TIP]\\n> Wrapped\\n> text.')"); await check();
    assert.deepEqual(await evaluate("[document.querySelectorAll('.markdown-content br').length, markdownFixture.texts('table.markdown-front-matter td'), markdownFixture.texts('p:not(.markdown-alert-title)')]"),
      [0, ["Guide"], ["one\ntwo", "Wrapped\ntext."]]);
    assert.deepEqual(await evaluate("[markdownFixture.texts('.markdown-alert[data-alert=tip] .markdown-alert-title'), document.querySelectorAll('.markdown-content .markdown-task[aria-checked=true]').length]"), [["Tip"], 1]);
    // Same shared component used by Notes/live rendering, without timeline region opt-in.
    await evaluate("markdownFixture.render(markdownFixture.cases[0].source, false)"); await check();
    assert.equal(await evaluate("!!document.querySelector('.markdown-content details summary')"), true);
    for (const kind of ["parser", "sanitizer"]) {
      const fallback = await evaluate<{ source: string; text: string; html: string }>(`markdownFixture.fallback('${kind}')`);
      assert.equal(fallback.text, fallback.source, `${kind} error retains inert source`);
      assert.ok(fallback.html.startsWith("<pre>&lt;img")); assert.doesNotMatch(fallback.html, /private .* detail/);
      await check();
    }
    assert.deepEqual(attempts, [page, script]);
    assert.ok(baseDiagnostics.length > 0, "explicitly retain base-uri enforcement evidence");
    console.log(`Markdown production boundary: ${cases.length} cases, 2 fulfilled assets, 0 unexpected requests; ${baseDiagnostics.length} allowed base-uri diagnostics; 600ms deferred windows. HTTPS is not app://codealta.`);
  } finally {
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close();
    if (browser?.pid) { const killer = spawn("taskkill", ["/F", "/T", "/PID", String(browser.pid)], { stdio: "ignore", windowsHide: true }); await new Promise(resolve => killer.once("exit", resolve)); }
    await rm(directory, { recursive: true, force: true, maxRetries: 20, retryDelay: 100 });
  }
});
