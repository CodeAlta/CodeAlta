import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { mkdtemp, readFile, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";

const edge = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe", "C:/Program Files/Microsoft/Edge/Application/msedge.exe"].find(existsSync);
test("actual App mounts against an isolated bridge and records parity baselines", { skip: !edge, timeout: 90_000 }, async t => {
  const root = await mkdtemp(join(tmpdir(), "codealta-parity-app-"));
  t.diagnostic(`Actual-App baseline artifacts (not visual acceptance): ${root}`);
  let browser: ReturnType<typeof spawn> | undefined, socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./main.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife",
      alias: { "#neoastra": fileURLToPath(new URL("./parityApp.bridge.ts", import.meta.url)) },
      define: { "import.meta.env.VITE_DEMO_MODE": '"false"' }, metafile: true,
    }).then(result => {
      assert.ok(!Object.keys(result.metafile!.inputs).some(path => path.includes("obj/neoastra") || path.includes("@neoastra/client")), "No native bridge may enter the fixture bundle");
    });
    await writeFile(join(root, "fixture.html"), '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="fixture.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
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
    const exceptions: unknown[] = [], requests: string[] = [];
    socket.addEventListener("message", event => {
      const message = JSON.parse(String(event.data));
      if (message.method === "Runtime.exceptionThrown") exceptions.push(message.params.exceptionDetails);
      if (message.method === "Network.requestWillBeSent") requests.push(message.params.request.url);
    });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<any>((resolve, reject) => {
      const id = ++sequence;
      const reply = (event: MessageEvent) => {
        const message = JSON.parse(String(event.data)); if (message.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer);
        if (message.error) reject(Error(`${method}: ${JSON.stringify(message.error)}`)); else resolve(message.result ?? {});
      };
      const timer = setTimeout(() => { socket!.removeEventListener("message", reply); reject(Error(`${method} timed out`)); }, 12_000);
      socket!.addEventListener("message", reply); socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async (expression: string) => {
      const result = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails)); return result.result?.value;
    };
    await command("Page.enable"); await command("Runtime.enable"); await command("Network.enable");
    // Block resource attempts outside the isolated fixture even if a regression introduces one.
    await command("Network.setBlockedURLs", { urls: ["http://*", "https://*", "ws://*", "wss://*"] });
    await command("Page.navigate", { url: pathToFileURL(join(root, "fixture.html")).href });
    assert.equal(await evaluate("new Promise(resolve=>{const end=Date.now()+7000;function check(){if(document.body.textContent.includes('Create a usable desktop workspace'))resolve(true);else if(Date.now()>end)resolve(false);else setTimeout(check,20)}check()})"), true, JSON.stringify(exceptions));
    const measurements = [];
    for (const width of [390, 1280]) for (const theme of ["light", "dark"]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 900, deviceScaleFactor: 1, mobile: false });
      await evaluate(`document.documentElement.dataset.theme='${theme}';new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))`);
      assert.equal(await evaluate("document.body.textContent.includes('The workspace shell is ready.')"), true);
      assert.equal(await evaluate("document.querySelectorAll('.timeline-tool-group .tool-tile-title').length"), 3);
      for (let index = 0; index < 3; index++) {
        await evaluate(`document.querySelectorAll('.tool-tile-title')[${index}].click();new Promise(r=>requestAnimationFrame(r))`);
        assert.equal(await evaluate("!!document.querySelector('dialog[open]')"), true);
        assert.equal(await evaluate(`document.querySelector('dialog[open]').textContent.includes(${JSON.stringify(["Read source", "Search references", "Inspect changes"][index])})`), true);
        await evaluate("document.querySelector('dialog[open]').dispatchEvent(new Event('cancel',{cancelable:true}));new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))");
        assert.equal(await evaluate("!document.querySelector('dialog[open]')"), true);
        assert.equal(await evaluate(`document.activeElement===document.querySelectorAll('.tool-tile-title')[${index}]`), true);
      }
      const shot = await command("Page.captureScreenshot", { format: "png", captureBeyondViewport: false });
      await writeFile(join(root, `${width}-${theme}.png`), Buffer.from(shot.data, "base64"));
      measurements.push({ width, theme, scrollWidth: await evaluate("document.documentElement.scrollWidth"), text: await evaluate("document.body.innerText") });
    }
    await writeFile(join(root, "baseline.json"), JSON.stringify(measurements, null, 2));
    assert.deepEqual(await evaluate("parityFixture.unexpected"), []);
    assert.deepEqual(exceptions, []);
    assert.deepEqual(requests.filter(url => !url.startsWith(pathToFileURL(root).href + "/") && !url.startsWith("data:")), []);
    assert.ok((await evaluate("parityFixture.calls")).includes("workspace.historyTimeline:demo-active"));
  } finally { socket?.close(); browser?.kill(); }
});
