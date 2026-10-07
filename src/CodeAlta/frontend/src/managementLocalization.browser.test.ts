import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { locales, translate } from "./localization";

const edge = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe", "C:/Program Files/Microsoft/Edge/Application/msedge.exe"].find(existsSync);
test("batch deletion locale changes retain exact confirmations, original owners and uncertainty", { skip: !edge, timeout: 120_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-management-language-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./managementLocalization.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    await writeFile(join(root, "style.css"), await readFile(new URL("./style.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, ["--headless=new", "--disable-gpu", "--no-first-run", "--disable-background-networking", "--disable-extensions", "--edge-skip-compat-layer-relaunch", `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
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
      const timer = setTimeout(() => reject(Error(`${method} timed out`)), 12_000);
      const reply = (event: MessageEvent) => {
        const message = JSON.parse(String(event.data));
        if (message.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer);
        if (message.error) reject(Error(JSON.stringify(message.error))); else resolve(message.result);
      };
      socket!.addEventListener("message", reply); socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async (expression: string) => {
      const result = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails));
      return result.result?.value;
    };
    const act = (script: string) => evaluate(`(async()=>{${script};await new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)));return true})()`);
    const check = async (expression: string, label = expression) => assert.equal(await evaluate(expression), true, label);
    const click = (key: string) => act(`management.click(${JSON.stringify(key)})`);
    const input = (selector: string, value: string) => act(`management.input(${JSON.stringify(selector)},${JSON.stringify(value)})`);
    const fresh = async () => {
      await command("Page.navigate", { url: pathToFileURL(page).href + `?nonce=${Date.now()}` });
      assert.equal(await evaluate(`new Promise(resolve=>{const end=Date.now()+5000;const tick=()=>window.management?resolve(true):Date.now()>end?resolve(false):setTimeout(tick,20);tick()})`), true);
    };
    const languages = async () => {
      await act(`window.kept={batch:management.batch.getSnapshot(),counts:JSON.stringify(management.counts()),nodes:Array.from(document.querySelectorAll('section,input,select,textarea'))}`);
      for (const locale of locales) {
        await act(`management.setLocale(${JSON.stringify(locale)})`);
        assert.equal(await evaluate("document.querySelector('.session-batch-delete').getAttribute('aria-label')"), translate(locale, "Batch session deletion"));
        await check(`document.querySelector('.session-batch-delete').textContent.includes(${JSON.stringify(translate(locale, "Select saved sessions for exact deletion"))})`, `${locale}: visible heading/control translates`);
        await check("kept.batch===management.batch.getSnapshot() && kept.counts===JSON.stringify(management.counts()) && kept.nodes.every(n=>n.isConnected)", `${locale} preserves mounted inputs, exact state references and RPC counts`);
        await check("Array.from(document.querySelectorAll('[role=status]')).some(e=>e.textContent===management.batch.getSnapshot().message)", `${locale}: controller message stays literal`);
        await check("management.batch.getSnapshot().items.every((item,index)=>{const labels=document.querySelectorAll('.session-batch-report li')[index].querySelectorAll('strong');return labels[0].textContent===item.request.confirmedTitle && labels[1].textContent===management.text(item.outcome)})", `${locale}: literal titles, translated closed outcome presentation`);
      }
    };
    const narrow = async () => {
      await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 500, deviceScaleFactor: 1, mobile: false });
      for (const locale of ["de", "ja"]) for (const theme of ["light", "dark"]) {
        await act(`management.setLocale('${locale}');document.documentElement.dataset.theme='${theme}'`);
        await check("(()=>{const m=document.querySelector('main');return m.scrollWidth<=m.clientWidth+1 && m.getBoundingClientRect().height<=innerHeight+1 && [...m.querySelectorAll('input,select,textarea,button')].every(e=>e.getBoundingClientRect().width<=m.clientWidth)})()", `${locale}/${theme}: short narrow management controls fit horizontal viewport; vertical scrolling remains available`);
      }
    };
    await fresh();
    await act("document.querySelector('.session-batch-delete details').open=true");
    await click("Select visible eligible");
    await languages();
    await click("Review exact deletion targets");
    await input(".session-batch-report input", "削除 3");
    await check("document.querySelector('.session-batch-report button').disabled && management.deletion.calls.length===0");
    await input(".session-batch-report input", "DELETE 3 ");
    await check("document.querySelector('.session-batch-report button').disabled");
    await input(".session-batch-report input", "DELETE 3");
    await languages();
    await check("document.querySelector('.session-batch-report input').value==='DELETE 3' && !document.querySelector('.session-batch-report button').disabled && document.querySelector('.session-batch-report label').textContent.includes('DELETE 3')");
    await narrow();
    await click("Delete reviewed sessions");
    await languages();
    await check("management.deletion.calls.length===1 && management.deletion.calls[0].confirmedTitle==='Unknown'");
    await act("management.deleteReply('ok')");
    await check("management.deletion.calls.length===2 && management.deletion.calls[1].confirmedTitle==='Description'");
    await languages();
    await act("management.deletion.reject()");
    await languages();
    await check("management.batch.getSnapshot().items.map(i=>i.outcome).join('|')==='deleted|uncertain|not-started' && management.batch.getSnapshot().items[0].request===management.deletion.calls[0] && management.batch.getSnapshot().items[1].request===management.deletion.calls[1] && management.deletion.calls.length===2 && !management.batch.clearSettled() && document.querySelector('[role=alert]')!==null");
    await fresh();
    await act("document.querySelector('.session-batch-delete details').open=true");
    await click("Select visible eligible"); await click("Review exact deletion targets");
    await input(".session-batch-report input", "DELETE 3"); await click("Delete reviewed sessions");
    await languages();
    await click("Stop after pending original");
    await act("management.deleteReply('ok')");
    await languages();
    await check("management.deletion.calls.length===1 && management.batch.getSnapshot().items.map(i=>i.outcome).join('|')==='deleted|not-started|not-started'");
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close();
    if (browser && browser.exitCode === null) { const closed = new Promise<void>(resolve => browser!.once("exit", () => resolve())); browser.kill(); await Promise.race([closed, new Promise(resolve => setTimeout(resolve, 5000))]); }
    await rm(root, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
  }
});
