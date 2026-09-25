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

test("mounted selected-project Details uses current bounded catalog identity without writes", { skip: !edge, timeout: 60_000 }, async () => {
  const app = readFileSync(fileURLToPath(new URL("./main.tsx", import.meta.url)), "utf8");
  assert.match(app, /<ProjectDetailsEntry context=\{projectDetailsContext\} getCurrent=\{currentProjectDetailsContext\}/);
  assert.match(app, /async function refreshProjects\(signal: AbortSignal\) \{\s*markProjectInspection\(false\)/);
  assert.match(app, /active: view === "workspace" && detailsPaneVisible/);
  const root = await mkdtemp(join(tmpdir(), "codealta-project-details-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./projectDetails.mount.tsx", import.meta.url))],
      outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    await writeFile(join(root, "style.css"), readFileSync(fileURLToPath(new URL("./style.css", import.meta.url))));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><link rel="stylesheet" href="style.css"></head><body><div id="app"></div><script src="fixture.js"></script></body></html>');
    const profile = join(root, "profile");
    browser = spawn(edge!, ["--headless=new", "--disable-gpu", "--no-first-run", "--disable-background-networking", "--disable-extensions",
      `--user-data-dir=${profile}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
    let port = "";
    for (let attempt = 0; attempt < 100 && !port; attempt++) {
      try { port = (await readFile(join(profile, "DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; }
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
    const wait = (condition: string) => evaluate(`new Promise(resolve=>{const end=Date.now()+7000;function check(){if(${condition})resolve(true);
      else if(Date.now()>end)resolve(document.body.innerText.slice(0,350));else setTimeout(check,20)}check()})`);
    const click = (selector: string) => evaluate(`document.querySelector(${JSON.stringify(selector)}).click()`);
    const press = async (key: string, code: string, virtual: number) => {
      await command("Input.dispatchKeyEvent", { type: "keyDown", key, code, windowsVirtualKeyCode: virtual });
      await command("Input.dispatchKeyEvent", { type: "keyUp", key, code, windowsVirtualKeyCode: virtual });
    };
    assert.equal(await wait("!!document.querySelector('.project-details-trigger')"), true);
    await evaluate(`(() => { window.fixtureWrites=0; window.fixtureRpc=0;
      const original=Storage.prototype.setItem; Storage.prototype.setItem=function(...args){window.fixtureWrites++; return original.apply(this,args)};
      window.fetch=()=>{window.fixtureRpc++; return Promise.reject(new Error('fixture forbids network'))}; })()`);
    const open = async () => { await click(".project-details-trigger"); assert.equal(await wait("!!document.querySelector('.project-details-dialog[open]')"), true); };
    await evaluate("document.querySelector('.project-details-trigger').focus()");
    await open();
    assert.equal(await evaluate("document.activeElement.getAttribute('aria-label')"), "Close project details");
    await evaluate("window.fixtureShellKeys=0;window.addEventListener('keydown',()=>window.fixtureShellKeys++)");
    await press("Tab", "Tab", 9);
    assert.equal(await evaluate("document.querySelector('.project-details-dialog').contains(document.activeElement)"), true);
    assert.equal(await evaluate("window.fixtureShellKeys"), 0);
    assert.equal(await evaluate("[...document.querySelectorAll('.project-details-fields dd')].map(x=>x.textContent).join('|')"), "one|Alpha|C:/catalog/alpha|No");
    assert.equal(await evaluate("document.querySelector('.project-details-dialog').textContent.includes('Project list is partial')"), true);
    assert.equal(await evaluate("document.querySelector('.project-details-dialog').textContent.includes('Session list is partial')"), true);
    assert.equal(await evaluate("document.querySelector('.project-details-dialog').textContent.includes('this name may be shortened')"), true);
    assert.equal(await evaluate("/Total sessions:|Default branch:|Tags:/.test(document.querySelector('.project-details-fields').textContent)"), false);
    await evaluate("document.querySelector('.project-details-dialog header button').dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',isComposing:true,bubbles:true,cancelable:true}))");
    assert.equal(await evaluate("!!document.querySelector('.project-details-dialog[open]')"), true);
    await press("Escape", "Escape", 27);
    assert.equal(await wait("!document.querySelector('.project-details-dialog')"), true);
    assert.equal(await wait("document.activeElement?.classList.contains('project-details-trigger')"), true);
    await evaluate("document.querySelector('.project-details-trigger').focus()"); await press(" ", "Space", 32);
    assert.equal(await wait("!!document.querySelector('.project-details-dialog[open]')"), true);
    await click(".project-details-dialog footer button:first-of-type");
    assert.equal(await wait("document.querySelector('.project-details-dialog [role=status]')?.textContent.includes('Project ID copied')"), true);
    assert.equal(await evaluate("window.projectDetailsFixture.copied.join('|')"), "one");
    await evaluate("window.projectDetailsFixture.copyMode('reject')");
    await click(".project-details-dialog footer button:nth-of-type(2)");
    assert.equal(await wait("document.querySelector('.project-details-dialog [role=alert]')?.textContent.includes('Could not copy project path')"), true);
    assert.equal(await evaluate("document.body.innerText.includes('private clipboard failure')"), false);
    await evaluate("window.projectDetailsFixture.copyMode('unavailable')");
    await click(".project-details-dialog footer button:first-of-type");
    assert.equal(await wait("document.querySelector('.project-details-dialog [role=alert]')?.textContent.includes('Clipboard unavailable')"), true);
    await evaluate("window.projectDetailsFixture.copyMode('pending')");
    await click(".project-details-dialog footer button:nth-of-type(2)");
    assert.equal(await wait("window.projectDetailsFixture.pending===1"), true);
    assert.equal(await evaluate("document.querySelector('.project-details-dialog footer button:first-of-type').disabled"), true);
    await click(".project-details-dialog footer button:first-of-type");
    assert.equal(await evaluate("window.projectDetailsFixture.pending"), 1);
    await click(".project-details-dialog footer button:last-child");
    assert.equal(await wait("!document.querySelector('.project-details-dialog')"), true);
    await evaluate("window.projectDetailsFixture.reject()");
    assert.equal(await evaluate("!!document.querySelector('.project-details-dialog [role=alert]')"), false);
    await open();
    await evaluate("window.projectDetailsFixture.copyMode('pending')");
    await click(".project-details-dialog footer button:first-of-type");
    assert.equal(await wait("window.projectDetailsFixture.pending===1"), true);
    await evaluate("window.projectDetailsFixture.select('two')");
    assert.equal(await wait("!document.querySelector('.project-details-dialog')"), true);
    await evaluate("window.projectDetailsFixture.resolve()");
    assert.equal(await evaluate("document.querySelector('#selection').textContent"), "two / session-two");
    await open();
    assert.equal(await evaluate("document.querySelector('.project-details-fields').textContent.includes('C:/catalog/alpha')"), false);
    await evaluate("window.projectDetailsFixture.copyMode('success')");
    await click(".project-details-dialog footer button:nth-of-type(2)");
    assert.equal(await wait("document.querySelector('.project-details-dialog [role=status]')?.textContent.includes('Project path copied')"), true);
    assert.equal(await evaluate("window.projectDetailsFixture.copied.at(-1)"), "C:/catalog/beta");
    await click(".project-details-dialog footer button:last-child");
    await click(".project-list li:last-child > button");
    assert.equal(await evaluate("document.querySelector('#selection').textContent"), "old / session-old");
    await open();
    assert.equal(await evaluate("document.querySelector('.project-details-fields').textContent.includes('Yes (read-only project)')"), true);
    await click(".project-details-dialog footer button:last-child");
    await click(".project-root-list button");
    assert.equal(await evaluate("document.querySelector('.project-details-trigger').disabled"), true);
    await evaluate("window.projectDetailsFixture.select('one')");
    assert.equal(await wait("!document.querySelector('.project-details-trigger').disabled"), true);
    await evaluate("window.projectDetailsFixture.stale({...window.projectDetailsFixture.current.snapshot,projects:[{id:'other',name:'Fake',path:'C:/catalog/alpha',archived:false}]})");
    await click(".project-details-trigger");
    assert.equal(await evaluate("!!document.querySelector('.project-details-dialog')"), false);
    await evaluate("window.projectDetailsFixture.snapshot({...window.projectDetailsFixture.current.snapshot,projects:[{id:'one',name:'Alpha',path:'C:/catalog/alpha',archived:false},{id:'other',name:'Fake',path:'C:/catalog/alpha',archived:false}]})");
    assert.equal(await wait("document.querySelector('.project-details-trigger').disabled"), true);
    await evaluate("window.projectDetailsFixture.snapshot({...window.projectDetailsFixture.current.snapshot,projects:[{id:'one',name:'Alpha',path:'C:/catalog/alpha',archived:false},{id:'one',name:'Fake',path:'C:/catalog/other',archived:false}]})");
    assert.equal(await wait("document.querySelector('.project-details-trigger').disabled"), true);
    await evaluate("window.projectDetailsFixture.snapshot({...window.projectDetailsFixture.current.snapshot,projects:[]})");
    assert.equal(await wait("document.querySelector('.project-details-trigger').disabled"), true);
    await evaluate("window.projectDetailsFixture.snapshot({...window.projectDetailsFixture.current.snapshot,projects:[{id:'one',name:'Alpha',path:' ',archived:false}]})");
    assert.equal(await wait("document.querySelector('.project-details-trigger').disabled"), true);
    await evaluate("window.projectDetailsFixture.refresh(true)");
    assert.equal(await wait("!document.querySelector('.project-details-trigger').disabled"), true);
    await open();
    await evaluate("window.projectDetailsFixture.snapshot({...window.projectDetailsFixture.current.snapshot,projects:[{id:'one',name:'Renamed',path:'C:/catalog/alpha',archived:false}]})");
    assert.equal(await wait("!document.querySelector('.project-details-dialog')"), true);
    await open();
    assert.equal(await evaluate("document.querySelector('.project-details-fields').textContent.includes('Renamed')"), true);
    await evaluate("window.projectDetailsFixture.refresh(false)");
    assert.equal(await wait("!document.querySelector('.project-details-dialog') && document.querySelector('.project-details-trigger').disabled"), true);
    await evaluate("window.projectDetailsFixture.refresh(true)");
    assert.equal(await wait("!document.querySelector('.project-details-trigger').disabled"), true);
    await open();
    await evaluate("window.projectDetailsFixture.session('other-session')");
    assert.equal(await wait("!document.querySelector('.project-details-dialog')"), true);
    await open();
    await evaluate("window.projectDetailsFixture.host('different-host',true)");
    assert.equal(await wait("!document.querySelector('.project-details-dialog')"), true);
    await evaluate("window.projectDetailsFixture.host(null,false)"); // Catalog-only inspection remains available.
    await open();
    await evaluate("window.projectDetailsFixture.active(false)");
    assert.equal(await wait("!document.querySelector('.project-details-dialog')"), true);
    await evaluate("window.projectDetailsFixture.active(true)");
    for (const width of [390, 1120]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 720, deviceScaleFactor: 1, mobile: false });
      if (width === 390) await evaluate("document.querySelector('.workspace-shell').classList.remove('project-rail-open')");
      else await evaluate("document.querySelector('.workspace-shell').classList.add('project-rail-open')");
      const backgrounds: string[] = [];
      for (const theme of ["dark", "light"]) {
        await evaluate(`document.documentElement.dataset.theme=${JSON.stringify(theme)}`);
        await open();
        const layout = await evaluate(`({right:document.querySelector('.project-details-dialog').getBoundingClientRect().right,
          bottom:document.querySelector('.project-details-dialog footer').getBoundingClientRect().bottom,
          overflow:document.documentElement.scrollWidth,background:getComputedStyle(document.querySelector('.project-details-dialog')).backgroundColor})`) as {right:number;bottom:number;overflow:number;background:string};
        assert.ok(layout.right <= width+2 && layout.bottom <= 722 && layout.overflow <= width+2 && layout.background !== "rgba(0, 0, 0, 0)", `${width} ${theme}: ${JSON.stringify(layout)}`);
        backgrounds.push(layout.background);
        await press("Escape", "Escape", 27);
        assert.equal(await wait("!document.querySelector('.project-details-dialog')"), true);
      }
      assert.notEqual(backgrounds[0], backgrounds[1]);
    }
    assert.equal(await evaluate("window.fixtureWrites"), 0);
    assert.equal(await evaluate("window.fixtureRpc"), 0);
  } finally {
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
