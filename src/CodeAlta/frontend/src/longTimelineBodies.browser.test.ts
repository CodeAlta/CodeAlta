import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { mkdtemp, readFile, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { locales, translate } from "./localization";
import { browserBaseArgs, browserExecutable } from "./browserTarget";

const edge = browserExecutable;
test("production long diagnostic bodies preserve literal preview, full Copy, notices and exact disclosure lifetime", { skip: !edge, timeout: 60_000 }, async t => {
  const root = await mkdtemp(join(tmpdir(), "codealta-long-bodies-"));
  t.diagnostic(`Literal mounted evidence: ${root}`);
  let browser: ReturnType<typeof spawn> | undefined, socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./longTimelineBodies.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    await writeFile(join(root, "style.css"), readFileSync(new URL("./style.css", import.meta.url)));
    await writeFile(join(root, "fixture.html"), '<!doctype html><html><head><link rel="stylesheet" href="style.css"></head><body><div id="app"></div><script src="fixture.js"></script></body></html>');
    const profile = join(root, "profile");
    browser = spawn(edge!, [...browserBaseArgs, `--user-data-dir=${profile}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
    let port = "";
    for (let attempt = 0; attempt < 100 && !port; attempt++) {
      try { port = (await readFile(join(profile, "DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; }
      catch { await new Promise(resolve => setTimeout(resolve, 50)); }
    }
    assert.match(port, /^\d+$/);
    const pages: { type?: string; webSocketDebuggerUrl?: string }[] = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10_000) })).json();
    socket = new WebSocket(pages.find(page => page.type === "page")!.webSocketDebuggerUrl!);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", () => reject(Error("browser unavailable")), { once: true }); });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<{ result?: { value?: unknown }; exceptionDetails?: unknown }>((resolve, reject) => {
      const id = ++sequence, timer = setTimeout(() => reject(Error(`${method} timed out`)), 12_000);
      const reply = (event: MessageEvent) => {
        const message = JSON.parse(String(event.data)); if (message.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer);
        if (message.error) reject(Error(`${method}: ${JSON.stringify(message.error)}`)); else resolve(message.result ?? {});
      };
      socket!.addEventListener("message", reply); socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async (expression: string) => {
      try { const result = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
        assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails)); return result.result?.value;
      } catch (cause) { throw Error(`expression=${expression}; failure=${cause instanceof Error ? cause.message : String(cause)}`, { cause }); }
    };
    const wait = (condition: string) => evaluate(`new Promise(resolve=>{const end=Date.now()+7000;function check(){if(${condition})resolve(true);else if(Date.now()>end)resolve(document.body.innerText.slice(-1200));else setTimeout(check,20)}check()})`);
    const frames = () => evaluate("new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))");
    const toggle = async () => { await evaluate("document.querySelector('.long-message-toggle').click()"); await frames(); };
    await command("Page.enable"); await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 720, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(join(root, "fixture.html")).href });
    assert.equal(await wait("!!window.bodyFixture && !!document.querySelector('.message-error')"), true);
    assert.equal(await evaluate("!!document.querySelector('.message-error .long-message-toggle')"), true, "Long diagnostic errors start collapsed");
    await evaluate("window.bodyNetwork=0;window.fetch=()=>{bodyNetwork++;throw Error('forbidden')};window.open=()=>{bodyNetwork++;throw Error('forbidden')}");
    assert.equal(await evaluate("!document.querySelector('.markdown-content') && document.querySelector('.long-message-preview').textContent==='Preview (plain text): '+ 'x'.repeat(239)+'…'"), true, "Surrogate-safe inert excerpt, no cut Markdown parse");
    assert.equal(await evaluate("document.querySelector('article').textContent.includes('Additional diagnostic details were omitted.') || document.querySelector('article').textContent.includes('Some details were shortened')"), false);
    await evaluate("document.querySelector('.copy-markdown').click()"); await frames();
    assert.equal(await evaluate("bodyFixture.copies[0]===bodyFixture.expectedCopy() && bodyFixture.copies[0].includes(' END')"), true);
    await evaluate("document.querySelector('.long-message-toggle').focus()");
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", text: "\r", windowsVirtualKeyCode: 13 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    assert.equal(await wait("document.querySelector('.long-message-toggle').getAttribute('aria-expanded')==='true'"), true);
    assert.equal(await evaluate("document.activeElement===document.querySelector('.long-message-toggle') && document.getElementById(document.activeElement.getAttribute('aria-controls')).querySelector('.markdown-content')!==null && !document.querySelector('article img,article script') && !window.injected"), true);
    for (const theme of ["light", "dark"]) for (const locale of locales) {
      await evaluate(`document.documentElement.dataset.theme='${theme}';bodyFixture.language('${locale}')`); await frames();
      assert.equal(await evaluate("document.querySelector('.long-message-toggle').textContent"), translate(locale, "Collapse message"));
      assert.equal(await evaluate("document.querySelectorAll('.markdown-content').length===1 && document.documentElement.scrollWidth<=390"), true, `${theme}/${locale}`);
    }
    await evaluate("bodyFixture.language('en')"); await toggle();
    for (const options of [{ repeat: true }, { isComposing: true }, { keyCode: 229 }]) {
      assert.equal(await evaluate(`!document.querySelector('.long-message-toggle').dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true,cancelable:true,...${JSON.stringify(options)}}))`), true);
    }
    await evaluate("{const b=document.querySelector('.long-message-toggle');b.addEventListener('click',e=>e.preventDefault(),{once:true});b.click()}"); await frames();
    assert.equal(await evaluate("document.querySelector('.long-message-toggle').getAttribute('aria-expanded')"), "false");
    for (const value of [{ eventType: "contentCompleted", kind: "Plan" }, { eventType: "planSnapshot", kind: "Updated" },
      { eventType: "error", kind: "Failure" }, { eventType: "sessionUpdate", kind: "Warning" }]) {
      await evaluate(`bodyFixture.replace(${JSON.stringify(value)})`);
      assert.equal(await evaluate("document.querySelector('.long-message-toggle').getAttribute('aria-expanded')"), "false", JSON.stringify(value));
      await toggle(); assert.equal(await evaluate("document.querySelectorAll('.markdown-content').length"), 1);
    }
    for (const item of [{ summary: "Critical outcome" }, { details: "new raw bytes" }, { detailMarkdown: "different supplied detail" },
      { metadata: ["Provider: different"] }, { title: "different" }, { subtitle: "Failed" }, { category: "notes" }]) {
      await evaluate(`bodyFixture.item(${JSON.stringify(item)})`);
      assert.equal(await evaluate("document.querySelector('.long-message-toggle').getAttribute('aria-expanded')"), "false", JSON.stringify(item));
      await evaluate("bodyFixture.item({})");
      assert.equal(await evaluate("document.querySelector('.long-message-toggle').getAttribute('aria-expanded')"), "false", "metadata A/B/A retires expansion");
      await toggle();
    }
    await evaluate("bodyFixture.scope()");
    assert.equal(await evaluate("document.querySelector('.long-message-toggle').getAttribute('aria-expanded')"), "false");
    await toggle();
    await evaluate("{const d=document.createElement('dialog');document.body.append(d);d.showModal();d.close();d.remove()}"); await frames();
    assert.equal(await evaluate("document.querySelector('.long-message-toggle').getAttribute('aria-expanded')"), "false");
    for (const text of [null, "", "TERSE FAILURE", "x".repeat(1200)]) {
      await evaluate(`bodyFixture.replace({eventType:'error',text:${JSON.stringify(text)}})`);
      assert.equal(await evaluate("!document.querySelector('.long-message-toggle')"), true);
      if (text) {
        assert.equal(await evaluate("document.querySelectorAll('.markdown-content > p').length===1 && document.querySelector('.markdown-content').children.length===1"), true);
        assert.equal(await evaluate("document.querySelector('.markdown-content > p').textContent"), text);
      } else assert.equal(await evaluate("!document.querySelector('.markdown-content')"), true);
    }
    for (const value of [{ eventType: "contentCompleted", kind: "Reasoning" },
      { eventType: "sessionUpdate", kind: "CompactionStarted" }, { eventType: "contentCompleted", kind: "CommandOutput" }]) {
      await evaluate(`bodyFixture.replace(${JSON.stringify(value)})`);
      assert.equal(await evaluate("!document.querySelector('.long-message-toggle') && !!document.querySelector('.timeline-detail-trigger')"), true, "Compact rows open details rather than nested expanders");
      await evaluate("document.querySelector('.timeline-detail-trigger').click()"); await frames();
      assert.equal(await evaluate("!!document.querySelector('dialog[open]') && document.querySelector('dialog[open]').textContent.includes(' END') && !document.querySelector('dialog script, dialog img')"), true);
      await evaluate("bodyFixture.scope()"); await frames();
      assert.equal(await evaluate("!document.querySelector('dialog[open]')"), true, "Scope replacement retires compact details");
    }
    await evaluate("bodyFixture.replace({});void(window.detachedBody=document.querySelector('.long-message-toggle'));bodyFixture.unmount();detachedBody.click()");
    assert.equal(await evaluate("document.querySelector('#app').childElementCount===0 && bodyNetwork===0"), true);
  } finally { socket?.close(); browser?.kill(); }
});
