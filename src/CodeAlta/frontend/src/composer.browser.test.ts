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

// Mounts the actual OwnedSessionPanel with production style.css, not an OS select popup or native WebView2.
test("mounted composer stays compact and its controls remain legible in both themes", { skip: !edge, timeout: 60_000 }, async () => {
  const app = readFileSync(fileURLToPath(new URL("./main.tsx", import.meta.url)), "utf8");
  assert.match(app, /action === "context"\) activateContextShortcut\(workspaceShell\.current\)/,
    "the mounted shortcut dispatcher must remain wired to the production app");
  const root = await mkdtemp(join(tmpdir(), "codealta-composer-mounted-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./composer.mount.tsx", import.meta.url))],
      outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife",
      plugins: [{ name: "isolated-choices", setup(build) {
        build.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./composer.neoastra.mount.ts", import.meta.url)) }));
      } }] });
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
      socket!.addEventListener("error", () => reject(new Error("test browser debugger unavailable")), { once: true });
    });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<{ result?: { value?: string } }>((resolve, reject) => {
      const id = ++sequence;
      const timer = setTimeout(() => reject(new Error(`test browser ${method} timed out`)), 12_000);
      const reply = (event: MessageEvent) => {
        const message = JSON.parse(String(event.data)) as { id?: number; result?: { result?: { value?: string } }; error?: object };
        if (message.id !== id) return;
        socket!.removeEventListener("message", reply);
        clearTimeout(timer);
        if (message.error) reject(new Error(`test browser ${method} failed`));
        else resolve(message.result ?? {});
      };
      socket!.addEventListener("message", reply);
      socket!.send(JSON.stringify({ id, method, params }));
    });
    await command("Page.enable");
    const loaded = new Promise<void>((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("test page did not load")), 10_000);
      const listener = (event: MessageEvent) => {
        if ((JSON.parse(String(event.data)) as { method?: string }).method !== "Page.loadEventFired") return;
        clearTimeout(timer); socket!.removeEventListener("message", listener); resolve();
      };
      socket!.addEventListener("message", listener);
    });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    await loaded;
    await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    const evaluate = async (expression: string) => (await command("Runtime.evaluate", { expression, returnByValue: true,
      awaitPromise: true })).result?.value;
    const ready = await evaluate(`new Promise(resolve => { const end = Date.now() + 9000; const check = () => {
      if (document.querySelector('select[aria-label="Agent prompt"]:not(:disabled)')) resolve('ready');
      else if (Date.now() > end) resolve(document.body.innerText.slice(0, 300)); else setTimeout(check, 35); }; check(); })`);
    assert.equal(ready, "ready");
    type Sample = { panel: number; editor: number; scroll: number; client: number; overflow: string; padding: string;
      controls: Record<string, { color: string; background: string; border: string; outline: string; opacity: string }>;
      pageWidth: number; viewWidth: number; panelWidth: number; panelScrollWidth: number };
    const sample = async (): Promise<Sample> => JSON.parse((await evaluate(`JSON.stringify((() => {
      const editor = document.querySelector('.prompt-input');
      const controls = Object.fromEntries(['.prompt-options select', '.prompt-options option', '.project-rename input', '.session-rename input']
        .map(selector => { const s = getComputedStyle(document.querySelector(selector)); return [selector, { color: s.color,
          background: s.backgroundColor, border: s.borderColor, outline: s.outlineStyle, opacity: s.opacity }]; }));
      return { panel: document.querySelector('.owned-session').getBoundingClientRect().height,
        editor: editor.getBoundingClientRect().height, scroll: editor.scrollHeight, client: editor.clientHeight,
        overflow: getComputedStyle(editor).overflowY, padding: getComputedStyle(document.querySelector('.owned-session')).paddingTop,
        controls, pageWidth: document.documentElement.scrollWidth, viewWidth: document.documentElement.clientWidth,
        panelWidth: document.querySelector('.owned-session').clientWidth,
        panelScrollWidth: document.querySelector('.owned-session').scrollWidth };
    })())`))!) as Sample;
    const contrast = (a: string, b: string) => {
      const luminance = (value: string) => {
        const channels = value.match(/[\d.]+/g)!.slice(0, 3).map(Number).map(v => v / 255);
        return channels.reduce((sum, channel, index) => sum + [0.2126, 0.7152, 0.0722][index]
          * (channel <= .04045 ? channel / 12.92 : ((channel + .055) / 1.055) ** 2.4), 0);
      };
      const light = Math.max(luminance(a), luminance(b));
      return (light + .05) / (Math.min(luminance(a), luminance(b)) + .05);
    };
    for (const width of [390, 1120]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 800, deviceScaleFactor: 1, mobile: false });
      for (const theme of ["dark", "light"]) {
        await evaluate(`document.documentElement.dataset.theme = '${theme}'`);
        let result = await sample();
        assert.ok(result.panel <= (width === 390 ? 235 : 210), `${theme} ${width}px idle composer: ${JSON.stringify(result)}`);
        assert.equal(result.padding, "11px", "owned-session padding must override generic section chrome");
        assert.ok(result.pageWidth <= result.viewWidth + 2, `${theme} ${width}px controls cause horizontal overflow`);
        assert.ok(result.panelScrollWidth <= result.panelWidth + 2, `${theme} ${width}px composer controls overflow their panel`);
        assert.equal(result.editor, 50, "idle textarea is single-line-height");
        for (const [selector, control] of Object.entries(result.controls)) {
          assert.ok(contrast(control.color, control.background) >= 4.5, `${theme} ${width}px ${selector} text contrast: ${JSON.stringify(control)}`);
          if (selector.endsWith("input")) assert.notEqual(control.background, "rgba(0, 0, 0, 0)");
        }
        assert.equal(result.controls[".session-rename input"].opacity, "1", "disabled text must not be faded by browser defaults");
        assert.equal(await evaluate(`(() => { const field = document.querySelector('.prompt-options select');
          field.focus(); return getComputedStyle(field).outlineStyle; })()`), "solid", "select focus must be visible");
        assert.equal(await evaluate(`(() => { const field = document.querySelector('.project-rename input');
          field.focus(); return getComputedStyle(field).outlineStyle; })()`), "solid", "rename focus must be visible");
        await evaluate(`(() => { const el = document.querySelector('.prompt-input');
          Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(el, 'A normal request about a project\\nWith a little context\\nAnd one more detail');
          el.dispatchEvent(new Event('input', { bubbles: true })); })()`);
        result = await sample();
        assert.ok(result.editor > 50 && result.editor < 140, `${theme} ${width}px ordinary prompt must auto-grow: ${JSON.stringify(result)}`);
        assert.ok(result.scroll <= result.client + 2, `${theme} ${width}px ordinary prompt overflowed: ${JSON.stringify(result)}`);
        await evaluate(`(() => { const el = document.querySelector('.prompt-input');
          Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(el, ('Long line of text.\\n').repeat(90));
          el.dispatchEvent(new Event('input', { bubbles: true })); })()`);
        result = await sample();
        assert.ok(result.editor <= 241 && result.scroll > result.client, `${theme} ${width}px long prompt must scroll inside capped textarea: ${JSON.stringify(result)}`);
        await evaluate(`(() => { const el = document.querySelector('.prompt-input');
          Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(el, '');
          el.dispatchEvent(new Event('input', { bubbles: true })); })()`);
      }
    }
    assert.equal(await evaluate(`document.querySelector('.owned-session > .composer-toolbar button[aria-label="Expand prompt editor"]')?.title`),
      "Edit prompt in a large window (F6)");
    assert.equal(await evaluate(`document.querySelector('.composer-toolbar button.primary-button')?.textContent`), "Send");
    assert.equal(await evaluate(`document.querySelector('.composer-toolbar')?.textContent.includes('Refresh receipts')`), false);
    const beforeChord = Number(await evaluate("window.fixture.refreshes"));
    assert.equal(await evaluate(`document.querySelector('.advanced-session-controls').open`), false);
    await evaluate(`(() => { const prompt = document.querySelector('#session-prompt'); prompt.focus();
      for (const key of ['g', 'u']) prompt.dispatchEvent(new KeyboardEvent('keydown', { key, ctrlKey: true, bubbles: true, cancelable: true })); })()`);
    assert.equal(await evaluate(`new Promise(resolve => { const end = Date.now() + 4000; const check = () => {
      if (window.fixture.refreshes > ${beforeChord}) resolve('refreshed');
      else if (Date.now() > end) resolve('not refreshed'); else setTimeout(check, 25); }; check(); })`), "refreshed",
    "Ctrl+G, Ctrl+U must invoke the mounted composer runtime reader through production shortcut dispatch");
    assert.equal(await evaluate(`document.querySelector('.advanced-session-controls').open`), true,
      "shortcut must reveal the hidden context control and diagnostics");
    assert.equal(await evaluate(`document.activeElement?.id`), "refresh-session-context", "revealed refresh must be discoverable by focus");
    await evaluate(`(() => { const dialog = document.createElement('dialog'); dialog.setAttribute('open', '');
      const button = document.createElement('button'); dialog.append(button); document.body.append(dialog);
      for (const key of ['g', 'u']) button.dispatchEvent(new KeyboardEvent('keydown', { key, ctrlKey: true, bubbles: true, cancelable: true }));
      dialog.remove(); })()`);
    assert.equal(Number(await evaluate("window.fixture.refreshes")), beforeChord + 1, "modal chord cannot refresh context");
    await evaluate(`(() => { window.fixtureChoicesFail = true; document.querySelector('.advanced-session-controls').open = true;
      [...document.querySelectorAll('.advanced-session-controls button')].find(button => button.textContent.includes('Refresh choices')).click(); })()`);
    assert.equal(await evaluate(`new Promise(resolve => { const end = Date.now() + 4000; const check = () => {
      if (document.querySelector('.composer-notice[role="alert"]')?.textContent.includes('unavailable')) resolve('visible');
      else if (Date.now() > end) resolve('missing'); else setTimeout(check, 25); }; check(); })`), "visible",
    "choice failure and explicit retry must remain visible outside advanced controls");
    assert.equal(await evaluate(`document.querySelector('.composer-notice[role="alert"] button')?.textContent`), "Retry choices");
    await evaluate(`(() => { document.querySelector('.owned-session').remove();
      const catalog = document.createElement('section'); catalog.className = 'composer catalog-composer';
      const button = document.createElement('button'); button.className = 'prompt-state';
      button.onclick = () => window.fixture.catalogOpens++;
      catalog.append(button); document.querySelector('.session-workspace').append(catalog);
      for (const key of ['g', 'u']) button.dispatchEvent(new KeyboardEvent('keydown', { key, ctrlKey: true, bubbles: true, cancelable: true })); })()`);
    assert.equal(Number(await evaluate("window.fixture.catalogOpens")), 1, "catalog-only context chord retains its provider-configuration action");
  } finally {
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 999, method: "Browser.close" }));
    socket?.close();
    if (browser?.pid) {
      const killer = spawn("taskkill", ["/F", "/T", "/PID", String(browser.pid)], { stdio: "ignore", windowsHide: true });
      await new Promise(resolve => killer.once("exit", resolve));
    }
    await rm(root, { recursive: true, force: true, maxRetries: 20, retryDelay: 100 });
  }
});
