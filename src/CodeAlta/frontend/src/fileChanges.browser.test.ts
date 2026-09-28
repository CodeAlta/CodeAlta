import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { mkdtemp, readFile, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { translate, type Locale } from "./localization";

const edge = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe", "C:/Program Files/Microsoft/Edge/Application/msedge.exe"].find(existsSync);
test("production file records preserve literal data, raw Copy, bounded fallbacks and exact disclosure lifetime", { skip: !edge, timeout: 60_000 }, async t => {
  const root = await mkdtemp(join(tmpdir(), "codealta-file-records-"));
  t.diagnostic(`File record evidence: ${root}`);
  let browser: ReturnType<typeof spawn> | undefined, socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./fileChanges.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    await writeFile(join(root, "style.css"), readFileSync(new URL("./style.css", import.meta.url)));
    await writeFile(join(root, "fixture.html"), '<!doctype html><html><head><link rel="stylesheet" href="style.css"></head><body><div id="app"></div><script src="fixture.js"></script></body></html>');
    const profile = join(root, "profile");
    browser = spawn(edge!, ["--headless=new", "--disable-gpu", "--no-first-run", "--disable-background-networking", "--disable-extensions", `--user-data-dir=${profile}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
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
    const wait = (condition: string) => evaluate(`new Promise(resolve=>{const end=Date.now()+7000;function check(){if(${condition})resolve(true);else if(Date.now()>end)resolve(document.body.innerText);else setTimeout(check,20)}check()})`);
    const frames = () => evaluate("new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))");
    await command("Page.enable"); await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 720, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(join(root, "fixture.html")).href });
    assert.equal(await wait("document.querySelectorAll('[data-file-record]').length===3"), true);
    await evaluate("window.fileNetwork=0;window.fetch=()=>{fileNetwork++;throw Error('forbidden')};window.open=()=>{fileNetwork++;throw Error('forbidden')}");
    assert.equal(await evaluate("!document.querySelector('[data-file-diff],.file-change-inspection img,.file-change-inspection script,.file-change-inspection a')"), true);
    assert.equal(await evaluate("document.querySelector('.file-counts').textContent"), "+1 −1");
    await evaluate("document.querySelector('[data-file-record]').focus()");
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", text: "\r", windowsVirtualKeyCode: 13 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    await evaluate("fileFixture.visibleDiff=fileFixture.diff.split('\\n').filter(line=>! /^(?:@@|diff --git |index |--- |\\+\\+\\+ )/.test(line)).join('\\n')");
    assert.equal(await wait("document.querySelector('[data-file-diff]')?.textContent===fileFixture.visibleDiff"), true);
    assert.equal(await evaluate("document.activeElement===document.querySelector('dialog[open] header button')"), true);
    for (const theme of ["light", "dark"]) for (const locale of ["en", "es", "fr", "de", "ja", "zh-CN"] as Locale[]) {
      await evaluate(`document.documentElement.dataset.theme='${theme}';fileFixture.language('${locale}')`); await frames();
      assert.equal(await evaluate("document.querySelector('.file-change-inspection').getAttribute('aria-label')"), translate(locale, "Supplied file records"));
      assert.equal(await evaluate("document.querySelector('[data-file-diff]').textContent===fileFixture.visibleDiff && document.querySelector('[data-file-record] strong').textContent==='<img src=x onerror=alert(1)>.ts' && document.documentElement.scrollWidth<=390"), true, `${theme}/${locale}`);
    }
    await evaluate("fileFixture.language('en');document.querySelector('dialog[open] header button').click()"); await frames();
    assert.equal(await evaluate("document.activeElement===document.querySelector('[data-file-record]')"), true);
    await evaluate("document.querySelector('.copy-markdown').click()"); await frames();
    assert.equal(await evaluate("fileFixture.copies[0]===JSON.stringify(JSON.parse(fileFixture.details),null,2)"), true);
    await evaluate("document.querySelector('[data-file-record=\"1\"]').click()"); await frames();
    assert.equal(await evaluate("document.querySelectorAll('[data-file-diff]').length===1 && document.querySelector('[data-file-diff]').textContent==='unsupported binary' && document.querySelector('[data-file-record=\"0\"]').getAttribute('aria-expanded')==='false'"), true);
    await evaluate("document.querySelector('dialog[open] header button').click()"); await frames();
    await evaluate("document.querySelector('[data-file-record=\"2\"]').click()"); await frames();
    assert.equal(await evaluate("!document.querySelector('[data-file-diff]') && document.querySelector('[data-file-record=\"2\"]').getAttribute('aria-expanded')==='true'"), true);
    await evaluate("document.querySelector('dialog[open] header button').click()"); await frames();
    for (const options of [{ repeat: true }, { isComposing: true }, { keyCode: 229 }]) {
      assert.equal(await evaluate(`!document.querySelector('[data-file-record]').dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true,cancelable:true,...${JSON.stringify(options)}}))`), true);
    }
    await evaluate("{const b=document.querySelector('[data-file-record]');b.addEventListener('click',e=>e.preventDefault(),{once:true});b.click()}"); await frames();
    assert.equal(await evaluate("document.querySelector('[data-file-record]').getAttribute('aria-expanded')"), "false");
    await evaluate("document.querySelector('[data-file-record]').click()"); await frames();
    await evaluate("void(window.oldFile=document.querySelector('[data-file-record]'));fileFixture.replace({details:JSON.stringify({path:'replacement'})});oldFile.click()"); await frames();
    assert.equal(await evaluate("!oldFile.isConnected && !document.querySelector('[data-file-record][aria-expanded=true]')"), true);
    await evaluate("fileFixture.replace({details:fileFixture.details});oldFile.click()"); await frames();
    assert.equal(await evaluate("!document.querySelector('[data-file-record][aria-expanded=true]')"), true, "same-offset A/B/A cannot revive old disclosure");
    await evaluate("document.querySelector('[data-file-record]').click()"); await frames();
    await evaluate("fileFixture.scope()"); await frames();
    assert.equal(await evaluate("!document.querySelector('[data-file-record][aria-expanded=true]')"), true);
    await evaluate("document.querySelector('[data-file-record]').click()"); await frames();
    await evaluate("{const d=document.createElement('dialog');document.body.append(d);d.showModal();d.close();d.remove()}"); await frames();
    assert.equal(await evaluate("!document.querySelector('[data-file-record][aria-expanded=true]')"), true, "native modal ABA retires inspection");
    for (const patch of [{ details: null }, { details: "{" }, { details: " ".repeat(8193) }, { details: '{"diff":"aggregate"}' }, { details: '{"path":"truncated"}', detailsTruncated: true }]) {
      await evaluate(`fileFixture.replace(${JSON.stringify(patch)})`);
      assert.equal(await evaluate("document.querySelectorAll('[data-file-record]').length===0 && !document.querySelector('dialog[open]')"), true, JSON.stringify(patch));
    }
    assert.equal(await evaluate("fileNetwork"), 0);
    // Tool inspection uses the same production record/lifetime fixture, after all file checks.
    await evaluate("window.toolRaw=JSON.stringify({arguments:{command:'<script>literal</script>'},result:{content:'  literal output\\r\\n',detailedContent:'x'.repeat(5000)},error:{message:'reported error'}});fileFixture.replace({kind:'ToolCall',name:'Settings',details:toolRaw,detailsTruncated:false,bodyOmitted:true})");
    assert.equal(await wait("!!document.querySelector('.tool-record-trigger')"), true);
    await evaluate("document.querySelector('.tool-record-trigger').focus()");
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", text: "\r", windowsVirtualKeyCode: 13 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    assert.equal(await wait("document.querySelector('.tool-record-dialog')?.open"), true);
    assert.equal(await evaluate("document.querySelector('[data-tool-field=\"result.content\"]').textContent==='  literal output\\r\\n' && document.querySelector('[data-tool-field=\"result.detailedContent\"]').textContent.length===4096 && !document.querySelector('.tool-record-dialog script')"), true);
    for (const theme of ["light", "dark"]) for (const locale of ["en", "es", "fr", "de", "ja", "zh-CN"] as Locale[]) {
      await evaluate(`document.documentElement.dataset.theme='${theme}';fileFixture.language('${locale}')`); await frames();
      assert.equal(await evaluate("document.querySelector('.tool-record-dialog h2').textContent"), translate(locale, "Inspect supplied tool record"));
      assert.equal(await evaluate("document.querySelector('[data-tool-field=\"result.content\"]').textContent==='  literal output\\r\\n' && document.documentElement.scrollWidth<=390"), true,
        String(await evaluate("JSON.stringify({width:document.documentElement.scrollWidth,overflow:[...document.querySelectorAll('body *')].filter(e=>e.getBoundingClientRect().right>390).slice(0,8).map(e=>[e.tagName,e.className,e.getBoundingClientRect().right])})")));
    }
    await evaluate("fileFixture.language('en');document.querySelector('.tool-record-copy').click()"); await frames();
    assert.equal(await evaluate("fileFixture.copies.at(-1)===toolRaw"), true);
    await evaluate("{const d=document.querySelector('.tool-record-dialog');d.dispatchEvent(new CompositionEvent('compositionstart',{bubbles:true}));d.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true,cancelable:true,isComposing:true}));d.dispatchEvent(new CompositionEvent('compositionend',{bubbles:true}))}");
    assert.equal(await evaluate("document.querySelector('.tool-record-dialog').open"), true);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    assert.equal(await wait("!document.querySelector('.tool-record-dialog') && document.activeElement.classList.contains('tool-record-trigger')"), true);
    for (const mode of ["replace", "scope", "native", "own"]) {
      await evaluate("document.querySelector('.tool-record-trigger').click()");
      assert.equal(await wait("document.querySelector('.tool-record-dialog')?.open"), true);
      await evaluate("void(window.oldToolCopy=document.querySelector('.tool-record-copy'));window.toolCopyCount=fileFixture.copies.length");
      if (mode === "replace") await evaluate("fileFixture.replace({details:'{\"arguments\":{}}'});fileFixture.replace({details:toolRaw})");
      if (mode === "scope") await evaluate("fileFixture.scope()");
      if (mode === "native") await evaluate("{const d=document.createElement('dialog');document.body.append(d);d.showModal();d.close();d.remove()}");
      if (mode === "own") await evaluate("{const d=document.querySelector('.tool-record-dialog');d.close();d.showModal();oldToolCopy.click()}");
      await evaluate("oldToolCopy.click()"); await frames();
      assert.equal(await evaluate("fileFixture.copies.length===toolCopyCount"), true, mode);
      await evaluate("document.querySelector('.tool-record-dialog header button')?.click()"); await frames();
    }
    await evaluate("fileFixture.replace({details:'{\"arguments\":1,\"arguments\":2}'})");
    assert.equal(await evaluate("!document.querySelector('.tool-record-trigger') && !!document.querySelector('.timeline-detail-trigger') && !!document.querySelector('.copy-markdown') && fileNetwork===0"), true);
    await evaluate("fileFixture.unmount()"); assert.equal(await evaluate("document.querySelector('#app').childElementCount"), 0);
  } finally { socket?.close(); browser?.kill(); }
});
