import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { locales, translate } from "./localization";

const edge = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe", "C:/Program Files/Microsoft/Edge/Application/msedge.exe"].find(existsSync);
test("provider presentation preserves literal decisions and input owners across languages and scope ABA", { skip: !edge, timeout: 60_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-provider-language-"));
  let browser: ReturnType<typeof spawn> | undefined; let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./providerLocalization.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    await writeFile(join(root, "style.css"), readFileSync(fileURLToPath(new URL("./style.css", import.meta.url))));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><link rel="stylesheet" href="style.css"></head><body><div id="app"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, ["--headless=new", "--disable-gpu", "--no-first-run", "--disable-background-networking", "--disable-extensions", `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
    let port = "";
    for (let attempt = 0; attempt < 100 && !port; attempt++) {
      try { port = (await readFile(join(root, "profile", "DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; }
      catch { await new Promise(resolve => setTimeout(resolve, 50)); }
    }
    assert.match(port, /^\d+$/);
    const pages: { type?: string; webSocketDebuggerUrl?: string }[] = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10_000) })).json();
    const tab = pages.find(value => value.type === "page"); assert.ok(tab?.webSocketDebuggerUrl);
    socket = new WebSocket(tab.webSocketDebuggerUrl);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", () => reject(Error("browser unavailable")), { once: true }); });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<{ result?: { value?: unknown }; exceptionDetails?: unknown }>((resolve, reject) => {
      const id = ++sequence; const timer = setTimeout(() => reject(Error(`browser ${method} timed out`)), 12_000);
      const reply = (event: MessageEvent) => {
        const message = JSON.parse(String(event.data)); if (message.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer);
        if (message.error || message.result?.exceptionDetails) reject(Error(`browser ${method}: ${JSON.stringify(message)}`)); else resolve(message.result ?? {});
      };
      socket!.addEventListener("message", reply); socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async (expression: string) => (await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true })).result?.value;
    const wait = (condition: string) => evaluate(`new Promise(resolve=>{const end=Date.now()+4000;const check=()=>{if(${condition})resolve(true);else if(Date.now()>end)resolve(false);else setTimeout(check,20)};check()})`);
    const paint = () => evaluate("new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(()=>resolve(true))))");
    const click = (text: string) => evaluate(`[...document.querySelectorAll('button')].find(b=>b.textContent===${JSON.stringify(text)}).click()`);
    const languages = async () => {
      await paint();
      await evaluate(`void(window.kept={nodes:[...document.querySelectorAll('input,textarea')].map(node=>({node,value:node.value,disabled:node.disabled})),focus:document.activeElement,
        calls:JSON.stringify([providerFixture.permissions.length,providerFixture.decisions.length,providerFixture.inputs.length,providerFixture.answers.length,providerFixture.cancels.length]),
        original:providerFixture.input.readOriginal()?.request,decision:providerFixture.permission.readOriginal()?.origin,capability:providerFixture.capability.canMutate()})`);
      for (const locale of locales) {
        await evaluate(`providerFixture.language('${locale}')`); await paint();
        assert.equal(await evaluate(`document.querySelector('main > section:last-child h3').textContent===${JSON.stringify(translate(locale, "Nonsecret provider input"))}`), true);
        assert.equal(await evaluate(`document.querySelector('main > section').getAttribute('aria-label')===${JSON.stringify(translate(locale, "Pending command permissions"))}`), true);
        assert.equal(await evaluate(`kept.calls===JSON.stringify([providerFixture.permissions.length,providerFixture.decisions.length,providerFixture.inputs.length,providerFixture.answers.length,providerFixture.cancels.length]) && kept.original===providerFixture.input.readOriginal()?.request && kept.decision===providerFixture.permission.readOriginal()?.origin && kept.capability===providerFixture.capability.canMutate() && kept.focus===document.activeElement && kept.nodes.every(v=>v.node.isConnected&&v.value===v.node.value&&v.disabled===v.node.disabled)`), true, `${locale}: no reads, grants, owner replacement, reset or focus loss`);
        assert.equal(await evaluate(`(()=>{const pre=document.querySelector('ol.history-records > li > pre');return !pre||pre.textContent===providerFixture.permissionPage.entries[0].command})()`), true);
        assert.equal(await evaluate(`(()=>{const option=document.querySelector('[aria-pressed]');return !option||option.textContent==='Settings — Complete'})()`), true);
      }
      await evaluate("providerFixture.language('en')"); await paint();
    };
    await command("Page.enable"); await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait("!!window.providerFixture && document.querySelectorAll('h3').length===2"), true);
    await languages();
    await click("Refresh input"); await click("Refresh pending commands"); await languages();
    assert.equal(await evaluate("providerFixture.inputs.length===1 && providerFixture.permissions.length===1"), true);
    await evaluate("providerFixture.inputs[0].resolve(providerFixture.inputPage);providerFixture.permissions[0].resolve(providerFixture.permissionPage)");
    assert.equal(await wait("!!document.querySelector('textarea') && !!document.querySelector('.history-controls')"), true);
    await evaluate(`document.querySelector('[aria-pressed]').click();const field=document.querySelector('textarea');field.focus();Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(field,'  Settings 日本語  ');field.dispatchEvent(new Event('input',{bubbles:true}))`);
    await languages();
    assert.equal(await evaluate("document.querySelector('textarea').value"), "  Settings 日本語  ");
    await evaluate("document.querySelector('textarea').dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',ctrlKey:true,isComposing:true,bubbles:true,cancelable:true}))");
    assert.equal(await evaluate("providerFixture.answers.length"), 0);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9 });
    assert.equal(await evaluate("document.activeElement.textContent"), "Deliberately answer with empty text");
    for (const locale of ["de", "ja"] as const) for (const theme of ["light", "dark"]) {
      await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 500, deviceScaleFactor: 1, mobile: false });
      await evaluate(`providerFixture.language('${locale}');document.documentElement.dataset.theme='${theme}'`); await paint();
      assert.equal(await evaluate("document.documentElement.scrollWidth<=innerWidth+2"), true, `${locale}/${theme}: provider controls fit`);
    }
    await evaluate("providerFixture.language('en')"); await paint();
    await click("Submit literal answers"); await click("Allow once"); await languages();
    assert.deepEqual(await evaluate("providerFixture.answers[0].request.answers"), [{ promptId: "choice", value: "Settings" }, { promptId: "free", value: "  Settings 日本語  " }]);
    assert.equal(await evaluate("providerFixture.decisions[0].request.decision"), "allow_once");
    await evaluate("providerFixture.answers[0].resolve({status:'rejected',hostEpoch:providerFixture.epoch,handle:providerFixture.handle});providerFixture.decisions[0].resolve({status:'rejected',hostEpoch:providerFixture.epoch,handle:providerFixture.handle})");
    assert.equal(await wait("providerFixture.input.readOriginal()?.kind==='terminal' && providerFixture.permission.readOriginal()?.state==='rejected'"), true);
    await languages(); await click("Observe original locally (no RPC)"); await click("Acknowledge observed terminal original"); await click("Observe retained decision");
    await click("Refresh input"); await click("Refresh pending commands");
    await evaluate("providerFixture.inputs[1].resolve(providerFixture.inputPage);providerFixture.permissions[1].resolve(providerFixture.permissionPage)");
    assert.equal(await wait("!!document.querySelector('textarea') && !!document.querySelector('.history-controls')"), true);
    await click("Cancel this attempt only"); await click("Deny"); await languages();
    await evaluate("providerFixture.select('other')"); await paint(); await evaluate("providerFixture.select('Settings')"); await paint();
    await languages();
    await evaluate("providerFixture.cancels[0].reject(Error('literal uncertain input'));providerFixture.decisions[1].reject(Error('literal uncertain command'))");
    assert.equal(await wait("providerFixture.input.readOriginal()?.kind==='uncertain' && providerFixture.permission.readOriginal()?.state==='error'"), true);
    await click("Observe retained decision"); await languages();
    assert.equal(await evaluate("providerFixture.input.acknowledge()"), false);
    assert.equal(await evaluate("providerFixture.decisions.length===2 && providerFixture.answers.length===1 && providerFixture.cancels.length===1"), true);
    // Fresh disposable owners: late old-selection reads must still revoke, not regain authority through ABA/locale.
    await command("Page.navigate", { url: pathToFileURL(page).href }); assert.equal(await wait("!!window.providerFixture && document.querySelectorAll('h3').length===2"), true);
    await click("Refresh input"); await click("Refresh pending commands");
    await evaluate("providerFixture.select('other')"); await paint(); await evaluate("providerFixture.select('Settings')"); await paint();
    await evaluate("providerFixture.inputs[0].resolve({...providerFixture.inputPage,status:'stale_epoch'});providerFixture.permissions[0].resolve({...providerFixture.permissionPage,status:'stale_epoch'})");
    assert.equal(await wait("!providerFixture.capability.canMutate()"), true); await languages();
    assert.equal(await evaluate("providerFixture.inputs.length===1 && providerFixture.permissions.length===1 && providerFixture.decisions.length===0 && providerFixture.answers.length===0"), true);
  } finally { socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 }); }
});
