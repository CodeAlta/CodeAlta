import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";

const edge = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
  "C:/Program Files/Microsoft/Edge/Application/msedge.exe"].find(existsSync);

test("mounted live Copy feedback follows source, latest request and row lifetime", { skip: !edge, timeout: 60_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-live-copy-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./liveCopy.mount.tsx", import.meta.url))],
      outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    await writeFile(join(root, "style.css"), readFileSync(fileURLToPath(new URL("./style.css", import.meta.url))));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><link rel="stylesheet" href="style.css"></head><body><div id="app"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, ["--headless=new", "--disable-gpu", "--no-first-run", "--disable-background-networking", "--disable-extensions",
      `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
    let port = "";
    for (let attempt = 0; attempt < 100 && !port; attempt++) {
      try { port = (await readFile(join(root, "profile", "DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; }
      catch { await new Promise(resolve => setTimeout(resolve, 50)); }
    }
    assert.match(port, /^\d+$/);
    const pages: { type?: string; url?: string; webSocketDebuggerUrl?: string }[] =
      await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10_000) })).json();
    const tab = pages.find(value => value.type === "page" && value.url === "about:blank");
    assert.ok(tab?.webSocketDebuggerUrl);
    socket = new WebSocket(tab.webSocketDebuggerUrl);
    await new Promise<void>((resolve, reject) => {
      socket!.addEventListener("open", () => resolve(), { once: true });
      socket!.addEventListener("error", () => reject(new Error("test browser unavailable")), { once: true });
    });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<{ result?: { value?: unknown } }>((resolve, reject) => {
      const id = ++sequence;
      const timer = setTimeout(() => reject(new Error(`browser ${method} timed out`)), 12_000);
      const reply = (event: MessageEvent) => {
        const message = JSON.parse(String(event.data)) as { id?: number; result?: { result?: { value?: unknown } }; error?: object };
        if (message.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer);
        if (message.error) reject(new Error(`browser ${method} failed`)); else resolve(message.result ?? {});
      };
      socket!.addEventListener("message", reply);
      socket!.send(JSON.stringify({ id, method, params }));
    });
    await command("Page.enable");
    await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    const evaluate = async (expression: string) => (await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true })).result?.value;
    const wait = (condition: string) => evaluate(`new Promise(resolve=>{const end=Date.now()+4000;function check(){if(${condition})resolve(true);
      else if(Date.now()>end)resolve(false);else setTimeout(check,25)}check()})`);
    const drain = () => evaluate("new Promise(resolve=>setTimeout(resolve,0))");
    const click = () => evaluate("document.querySelector('.copy-markdown').click()");
    const label = () => evaluate("document.querySelector('.copy-markdown')?.getAttribute('aria-label')");
    assert.equal(await wait("document.querySelector('.history')?.dataset.windowReady==='true' && !!document.querySelector('.copy-markdown')"), true);
    await evaluate(`(() => {
      const savedSet=window.setTimeout.bind(window),savedClear=window.clearTimeout.bind(window);
      window.copyResets=[];window.pendingCopies=[];
      window.setTimeout=(fn,ms,...args)=>{if(ms!==1600)return savedSet(fn,ms,...args);
        const id=savedSet(()=>{},60000);window.copyResets.push({id,fn,cleared:false});return id};
      window.clearTimeout=id=>{const reset=window.copyResets.find(v=>v.id===id);if(reset)reset.cleared=true;savedClear(id)};
      Object.defineProperty(navigator,'clipboard',{configurable:true,value:{writeText:text=>new Promise((resolve,reject)=>{
        window.pendingCopies.push({text,resolve,reject})})}});
      window.originalCopyNode=document.querySelector('.copy-markdown');
      window.originalText=window.liveCopyFixture.text();
    })()`);
    await click();
    await evaluate("window.liveCopyFixture.update({text:window.originalText+'\\nSTREAMED NEW END'})");
    assert.equal(await evaluate("window.originalCopyNode===document.querySelector('.copy-markdown')"), true,
      "production History must reuse the actual live row key while its streaming text changes");
    await click();
    assert.equal(await evaluate("window.pendingCopies[0].text===window.originalText && window.pendingCopies[1].text===window.liveCopyFixture.text()"), true,
      "each click captures the full retained Markdown, including prefix omissions and Unicode");
    await evaluate("window.pendingCopies[1].resolve()");
    assert.equal(await wait("window.copyResets.length===1"), true);
    await evaluate("window.pendingCopies[0].reject(Error('private stale failure'))");
    await drain();
    const staleResultLabel = await label();
    await click();
    await evaluate("window.pendingCopies[2].resolve()");
    assert.equal(await wait("window.copyResets.length===2"), true);
    await evaluate("window.copyResets[0].fn()"); // Already queued timer can still run despite clearTimeout.
    await drain();
    assert.deepEqual({ staleResultLabel, staleTimerLabel: await label(), canceledTimer: await evaluate("window.copyResets[0].cleared") },
      { staleResultLabel: "Copied", staleTimerLabel: "Copied", canceledTimer: true },
      "old streaming-source results and queued canceled timers must not overwrite newer copy feedback");
    assert.equal(await evaluate("document.querySelector('.markdown-content strong').textContent"), "Retained");
    assert.equal(await evaluate("!!document.querySelector('.markdown-content script') || !!window.liveCopyInjected"), false);
    assert.equal(await evaluate("document.querySelector('.timeline-message').textContent.includes('Text prefix truncated.') && document.querySelector('.timeline-message').textContent.includes('Earlier text may be missing.')"), true);
    assert.equal(await evaluate("window.liveCopyFixture.reads()"), 1, "streaming copy and presentation must not read history again");

    const request = async () => {
      const index = Number(await evaluate("window.pendingCopies.length"));
      await click();
      assert.equal(await evaluate("window.pendingCopies.length"), index + 1);
      return index;
    };
    const settle = async (index: number, failure = false) => {
      await evaluate(`window.pendingCopies[${index}].${failure ? "reject(Error('private clipboard failure'))" : "resolve()"}`);
      await drain();
    };
    const lastTimer = () => evaluate("window.copyResets.length-1");
    await evaluate("window.liveCopyFixture.render()");
    assert.equal(await label(), "Copied", "an equivalent live replacement does not reset current feedback");
    const older = await request();
    const latest = await request();
    await settle(latest, true);
    assert.equal(await label(), "Copy failed", "current clipboard failures must not silently look idle");
    assert.equal(await evaluate("document.querySelector('.copy-markdown').title==='Copy failed' && document.querySelector('.copy-markdown').classList.contains('copy-failed') && document.querySelector('.copy-markdown [aria-live=polite]').textContent==='Copy failed'"), true);
    assert.equal(await evaluate("document.body.textContent.includes('private clipboard failure')"), false);
    await settle(older);
    assert.equal(await label(), "Copy failed", "older success on the same source cannot erase newer failure");
    await evaluate("window.copyResets.at(-1).fn()");
    await drain();
    assert.equal(await label(), "Copy Assistant as Markdown", "only the current reset returns feedback to idle");

    // All these sources keep History's key (kind is normalized for that key), but change its semantics.
    for (const [field, before, after] of [
      ["isComplete", false, true], ["isTruncated", true, false], ["startedWithDelta", true, false],
      ["kind", "Assistant", "assistant"], ["text", await evaluate("window.liveCopyFixture.text()"), "Same row, replaced retained body 😀"],
    ] as const) {
      await settle(await request());
      const timer = await lastTimer();
      await evaluate(`window.semanticNode=document.querySelector('.copy-markdown');window.liveCopyFixture.update({${field}:${JSON.stringify(after)}})`);
      assert.equal(await evaluate("window.semanticNode===document.querySelector('.copy-markdown')"), true);
      assert.equal(await evaluate(`window.copyResets[${timer}].cleared`), true, `${field} change clears the owned timer`);
      assert.equal(await evaluate("document.querySelector('.copy-markdown').classList.contains('copy-idle')"), true);
      await evaluate(`window.copyResets[${timer}].fn()`);
      await drain();
      assert.equal(await evaluate("document.querySelector('.copy-markdown').classList.contains('copy-idle')"), true);
      const oldSource = await request();
      await evaluate(`window.liveCopyFixture.update({${field}:${JSON.stringify(before)}});window.liveCopyFixture.update({${field}:${JSON.stringify(after)}})`);
      await settle(oldSource);
      assert.equal(await evaluate("document.querySelector('.copy-markdown').classList.contains('copy-idle')"), true,
        `${field} ABA cannot revive an earlier request even when text is unchanged`);
    }
    assert.equal(await evaluate("document.querySelector('.message-heading small').textContent"), "Complete");
    assert.equal(await evaluate("document.querySelector('.timeline-message').textContent.includes('Text prefix truncated.') || document.querySelector('.timeline-message').textContent.includes('Earlier text may be missing.')"), false);

    // Run/content identity changes really remount under the production History key.
    for (const change of [{ runId: "run-two" }, { contentId: "content-two" }, { kind: "Reasoning" }]) {
      const obsolete = await request();
      await evaluate(`window.identityNode=document.querySelector('.copy-markdown');window.liveCopyFixture.update(${JSON.stringify(change)})`);
      assert.equal(await evaluate("window.identityNode.isConnected"), false);
      await settle(await request());
      const count = await evaluate("window.copyResets.length");
      await settle(obsolete, true);
      assert.equal(await label(), "Copied", "unmounted identity cannot overwrite a different live row");
      assert.equal(await evaluate("window.copyResets.length"), count, "stale settlements cannot allocate timers");
    }
    const unmountTimer = await lastTimer();
    await evaluate("window.liveCopyFixture.show(false)");
    assert.equal(await evaluate(`window.copyResets[${unmountTimer}].cleared`), true);
    assert.equal(await evaluate("document.querySelector('.copy-markdown')===null"), true);
    await evaluate("window.liveCopyFixture.show(true)");
    const detachedRequest = await request();
    await evaluate("window.liveCopyFixture.show(false);window.liveCopyFixture.show(true)");
    await settle(await request(), true);
    const timersBeforeDetachedResult = await evaluate("window.copyResets.length");
    await settle(detachedRequest);
    assert.equal(await evaluate("window.copyResets.length"), timersBeforeDetachedResult, "detached settlement must not allocate a reset timer");
    await evaluate(`window.copyResets[${unmountTimer}].fn()`);
    await drain();
    assert.equal(await label(), "Copy failed", "late request and canceled timer cannot touch a remounted row");
    assert.equal(await evaluate("window.liveCopyFixture.reads()"), 1, "neither row removal nor copy recovery adds reads");

    const previousSessionRequest = await request();
    await evaluate("window.liveCopyFixture.select('fixture-other')");
    assert.equal(await wait("document.querySelector('.history')?.dataset.windowReady==='true'"), true);
    await settle(previousSessionRequest);
    assert.equal(await label(), "Copy Reasoning as Markdown", "same row fields in a new mounted session start idle");
    await evaluate("Object.defineProperty(navigator,'clipboard',{configurable:true,value:undefined})");
    await click();
    assert.equal(await wait("document.querySelector('.copy-markdown').getAttribute('aria-label')==='Copy failed'"), true,
      "missing clipboard capability is also an accessible failure");
    await evaluate("Object.defineProperty(navigator,'clipboard',{configurable:true,value:{writeText:text=>{window.keyboardCopy=text;return Promise.resolve()}}});document.querySelector('.copy-markdown').focus()");
    await command("Page.bringToFront");
    assert.equal(await evaluate("document.activeElement===document.querySelector('.copy-markdown')"), true);
    for (const [key, code, virtual] of [["Enter", "Enter", 13], [" ", "Space", 32]] as const) {
      await evaluate("window.keyboardCopy=null");
      await command("Input.dispatchKeyEvent", { type: "keyDown", key, code, text: key === "Enter" ? "\r" : " ", windowsVirtualKeyCode: virtual });
      await command("Input.dispatchKeyEvent", { type: "keyUp", key, code, windowsVirtualKeyCode: virtual });
      assert.equal(await wait("document.querySelector('.copy-markdown').getAttribute('aria-label')==='Copied'"), true,
        `${code} should activate the focused Copy control`);
      assert.equal(await evaluate("window.keyboardCopy===window.liveCopyFixture.text()"), true);
    }
    for (const width of [390, 1120]) for (const theme of ["light", "dark"]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 720, deviceScaleFactor: 1, mobile: false });
      await evaluate(`document.documentElement.dataset.theme=${JSON.stringify(theme)}`);
      const layout = await evaluate("({outer:document.documentElement.scrollWidth,right:document.querySelector('.copy-markdown').getBoundingClientRect().right,outline:getComputedStyle(document.querySelector('.copy-markdown')).outlineStyle})") as {outer:number;right:number;outline:string};
      assert.ok(layout.outer <= width + 2 && layout.right <= width + 2 && layout.outline === "solid", `${width}/${theme}: ${JSON.stringify(layout)}`);
    }
  } finally {
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
