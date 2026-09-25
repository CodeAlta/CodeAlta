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
  assert.match(app, /compactTrigger: compactTrigger\.current/,
    "the mounted compaction shortcut trigger must remain wired to the production dispatcher");
  assert.match(app, /action === "compact"[\s\S]*?trigger\.dataset\.epoch === status\.hostEpoch[\s\S]*?trigger\.click\(\)/,
    "production shortcut dispatch must recheck the selected epoch and live trigger");
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
    type Sample = { panel: number; toolbar: number; editor: number; scroll: number; client: number; overflow: string; padding: string;
      controls: Record<string, { color: string; background: string; border: string; outline: string; opacity: string }>;
      pageWidth: number; viewWidth: number; panelWidth: number; panelScrollWidth: number };
    const sample = async (): Promise<Sample> => JSON.parse((await evaluate(`JSON.stringify((() => {
      const editor = document.querySelector('.prompt-input');
      const controls = Object.fromEntries(['.prompt-options select', '.prompt-options option', '.project-rename input', '.session-rename input']
        .map(selector => { const s = getComputedStyle(document.querySelector(selector)); return [selector, { color: s.color,
          background: s.backgroundColor, border: s.borderColor, outline: s.outlineStyle, opacity: s.opacity }]; }));
      return { panel: document.querySelector('.owned-session').getBoundingClientRect().height,
        toolbar: document.querySelector('.composer-toolbar').getBoundingClientRect().height,
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
    assert.equal(await evaluate(`document.querySelector('.owned-session .composer-icon-button[aria-label="Reminders for selected session"]')?.title`),
      "Reminders for selected session (Ctrl+G, Ctrl+D)");
    assert.equal(await evaluate(`document.querySelector('.composer-toolbar button.primary-button')?.textContent`), "Send");
    assert.equal(await evaluate(`document.querySelector('.composer-toolbar')?.textContent.includes('Refresh receipts')`), false);
    assert.equal(await evaluate(`!!document.querySelector('.composer-toolbar [aria-label="Cancel observed run"]')`), false,
      "host availability without an observed run cannot expose cancellation");
    const waitFor = (condition: string) => evaluate(`new Promise(resolve => { const end = Date.now() + 4000; const check = () => {
      if (${condition}) resolve('ready'); else if (Date.now() > end) resolve('timed out'); else setTimeout(check, 25); }; check(); })`);
    assert.equal(await evaluate(`!!document.querySelector('.composer-toolbar [aria-label="Compact observed idle attachment"]')`), false,
      "host availability without an observed entry cannot imply idle");
    await evaluate(`window.fixture.observe(null, 12); document.querySelector('#refresh-session-context').click()`);
    assert.equal(await waitFor(`!!document.querySelector('.composer-toolbar [aria-label="Compact observed idle attachment"]')`), "ready");
    for (const width of [390, 1120]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 800, deviceScaleFactor: 1, mobile: false });
      for (const theme of ["dark", "light"]) {
        await evaluate(`document.documentElement.dataset.theme = '${theme}'`);
        const layout = await sample();
        assert.ok(layout.pageWidth <= layout.viewWidth + 2 && layout.panelScrollWidth <= layout.panelWidth + 2,
          `${theme} ${width}px compaction toolbar overflows: ${JSON.stringify(layout)}`);
        assert.ok(layout.toolbar <= (width === 390 ? 100 : 80), `${theme} ${width}px compaction toolbar grew: ${JSON.stringify(layout)}`);
        await evaluate(`document.querySelector('.composer-toolbar [aria-label="Compact observed idle attachment"]').focus()`);
        assert.equal(await evaluate(`getComputedStyle(document.activeElement).outlineStyle`), "solid");
      }
    }
    const compactKey = (target: string, extras = "") => evaluate(`(() => { const el = document.querySelector(${JSON.stringify(target)});
      const key = new KeyboardEvent('keydown', { key: 'F11', ctrlKey: true, bubbles: true, cancelable: true, ${extras} });
      el.dispatchEvent(key); return key.defaultPrevented; })()`);
    for (const extra of ["altKey: true", "shiftKey: true", "metaKey: true", "repeat: true", "isComposing: true", "keyCode: 229"]) {
      assert.equal(await compactKey('#session-prompt', extra), false);
    }
    assert.equal(await compactKey('.project-rename input'), false, "another editor must retain its keyboard shortcut");
    await evaluate(`window.fixture.workspaceActive(false)`);
    assert.equal(await compactKey('#session-prompt'), false, "unrelated screens do not intercept Ctrl+F11");
    await evaluate(`window.fixture.workspaceActive(true); window.fixture.shortcutSelection(null)`);
    assert.equal(await compactKey('#session-prompt'), false, "unverified selection does not intercept Ctrl+F11");
    await evaluate(`window.fixture.shortcutSelection({ epoch: 'fixture-epoch', sessionId: 'fixture-other', projectId: null })`);
    assert.equal(await compactKey('#session-prompt'), false, "different selected session cannot dispatch compaction");
    await evaluate(`window.fixture.shortcutSelection({ epoch: 'wrong', sessionId: 'fixture-session', projectId: null })`);
    assert.equal(await compactKey('#session-prompt'), false, "stale selection cannot dispatch compaction");
    await evaluate(`window.fixture.shortcutSelection({ epoch: 'fixture-epoch', sessionId: 'fixture-session', projectId: 'other' })`);
    assert.equal(await compactKey('#session-prompt'), false, "wrong project scope cannot dispatch compaction");
    await evaluate(`window.fixture.shortcutSelection({ epoch: 'fixture-epoch', sessionId: 'fixture-session', projectId: null })`);
    assert.equal(await evaluate(`(() => { const modal = document.createElement('dialog'); modal.setAttribute('open', '');
      const button = document.createElement('button'); modal.append(button); document.body.append(modal);
      const key = new KeyboardEvent('keydown', { key: 'F11', ctrlKey: true, bubbles: true, cancelable: true });
      button.dispatchEvent(key);
      const composerKey = new KeyboardEvent('keydown', { key: 'F11', ctrlKey: true, bubbles: true, cancelable: true });
      document.querySelector('#session-prompt').dispatchEvent(composerKey);
      modal.remove(); return key.defaultPrevented || composerKey.defaultPrevented; })()`), false);
    assert.equal(await evaluate(`window.fixture.compactCalls.length`), 0);
    await evaluate(`window.fixture.flags({retiring:true}); document.querySelector('#refresh-session-context').click()`);
    assert.equal(await waitFor(`[...document.querySelectorAll('.advanced-session-controls dt')].some(el => el.textContent.includes('Attachment retiring') && el.nextElementSibling?.textContent === 'yes')`), "ready");
    assert.equal(await waitFor(`!document.querySelector('.composer-toolbar [aria-label="Compact observed idle attachment"]')`), "ready");
    await evaluate(`window.fixture.flags({transitioning:true}); document.querySelector('#refresh-session-context').click()`);
    assert.equal(await waitFor(`[...document.querySelectorAll('.advanced-session-controls .detail')].some(el => el.textContent.includes('coordinator transition recorded: yes'))`), "ready");
    assert.equal(await evaluate(`!!document.querySelector('.composer-toolbar [aria-label="Compact observed idle attachment"]')`), false);
    await evaluate(`window.fixture.flags({draining:true}); document.querySelector('#refresh-session-context').click()`);
    assert.equal(await waitFor(`[...document.querySelectorAll('.advanced-session-controls dt')].some(el => el.textContent.includes('Queue drain in progress') && el.nextElementSibling?.textContent === 'yes')`), "ready");
    assert.equal(await evaluate(`!!document.querySelector('.composer-toolbar [aria-label="Compact observed idle attachment"]')`), false);
    await evaluate(`window.fixture.flags({}); document.querySelector('#refresh-session-context').click()`);
    assert.equal(await waitFor(`!!document.querySelector('.composer-toolbar [aria-label="Compact observed idle attachment"]')`), "ready");
    await evaluate(`(() => { const el = document.querySelector('.prompt-input');
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(el, 'Draft for compaction');
      el.dispatchEvent(new Event('input', { bubbles: true })); window.fixture.compactMode('uncertain'); })()`);
    assert.equal(await compactKey('#session-prompt'), true, "production shortcut dispatcher routes Ctrl+F11 from composer");
    assert.equal(await waitFor(`!!document.querySelector('.composer-toolbar [aria-label^="Retry exact compaction"]:not(:disabled)')`), "ready");
    assert.equal(await evaluate(`JSON.stringify(window.fixture.compactCalls[0])`),
      JSON.stringify({ expectedEpoch: "fixture-epoch", clientRequestId: await evaluate(`window.fixture.compactCalls[0].clientRequestId`),
        sessionId: "fixture-session", expectedRuntimeInstanceId: "fixture-runtime", expectedAttachmentGeneration: "gen-12" }));
    assert.equal(await evaluate(`document.querySelector('.prompt-input').value`), "Draft for compaction");
    assert.equal(await evaluate(`document.querySelector('.composer-notice:not([role])')?.textContent.includes('gen-12')`), true);
    await evaluate(`window.fixture.observe('new-run', 13); document.querySelector('#refresh-session-context').click()`);
    assert.equal(await waitFor(`document.querySelector('.advanced-session-controls dd')?.textContent === 'gen-13'`), "ready");
    assert.equal(await evaluate(`document.querySelector('.composer-toolbar [aria-label^="Retry exact compaction"]')?.title.includes('gen-12')`), true);
    assert.equal(await evaluate(`!!document.querySelector('.composer-toolbar [aria-label="Cancel observed run"]')`), true,
      "a new observed run does not displace the older retained compaction target or cancellation control");
    for (const width of [390, 1120]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 800, deviceScaleFactor: 1, mobile: false });
      for (const theme of ["dark", "light"]) {
        await evaluate(`document.documentElement.dataset.theme = '${theme}'`);
        const layout = await sample();
        assert.ok(layout.pageWidth <= layout.viewWidth + 2 && layout.panelScrollWidth <= layout.panelWidth + 2
          && layout.toolbar <= (width === 390 ? 100 : 80), `${theme} ${width}px retained compaction plus cancellation toolbar: ${JSON.stringify(layout)}`);
      }
    }
    await evaluate(`window.fixture.compactMode('hold'); document.querySelector('.composer-toolbar [aria-label^="Retry exact compaction"]').click();
      document.querySelector('.composer-toolbar [aria-label^="Retry exact compaction"]').click()`);
    assert.equal(await evaluate(`window.fixture.compactCalls.length`), 2, "original waiter excludes repeated retry");
    assert.equal(await evaluate(`window.fixture.compactCalls[0].clientRequestId === window.fixture.compactCalls[1].clientRequestId &&
      window.fixture.compactCalls[1].expectedAttachmentGeneration === 'gen-12'`), true);
    assert.equal(await compactKey('#session-prompt'), false, "in-flight control cannot intercept Ctrl+F11");
    await evaluate(`window.fixture.switchSession('fixture-other')`);
    assert.equal(await waitFor(`!!document.querySelector('.composer-toolbar [aria-label="Cancel observed run"]')`), "ready");
    assert.equal(await evaluate(`!!document.querySelector('.composer-toolbar [aria-label^="Retry exact compaction"]')`), false);
    await evaluate(`window.fixture.settleCompact(); window.fixture.switchSession('fixture-session')`);
    assert.equal(await waitFor(`!!document.querySelector('.composer-toolbar [aria-label^="Retry exact compaction"]:not(:disabled)')`), "ready");
    assert.equal(await evaluate(`document.querySelector('.prompt-input').value`), "Draft for compaction");
    await evaluate(`window.fixture.compactMode('busy'); document.querySelector('.composer-toolbar [aria-label^="Retry exact compaction"]').click()`);
    assert.equal(await waitFor(`[...document.querySelectorAll('.advanced-session-controls [role="status"]')].some(el => el.textContent.includes('busy'))`), "ready");
    assert.equal(await evaluate(`[...document.querySelectorAll('.composer-notice[role="status"]')].some(el => el.textContent.includes('busy') && el.textContent.includes('cannot be retried'))`), true);
    assert.equal(await evaluate(`window.fixture.compactCalls[2].expectedAttachmentGeneration`), "gen-12");
    assert.equal(await evaluate(`!!document.querySelector('.composer-toolbar [aria-label="Compact observed idle attachment"]')`), false,
      "busy ends the old intent but an active observation does not authorize a fresh attempt");
    await evaluate(`window.fixture.observe(null, 13); document.querySelector('#refresh-session-context').click()`);
    assert.equal(await waitFor(`!!document.querySelector('.composer-toolbar [aria-label="Compact observed idle attachment"]')`), "ready",
      "a new idle observation permits a separate explicit attempt after busy");
    await evaluate(`window.fixture.observe('run-one', 12)`);
    await evaluate(`document.querySelector('#refresh-session-context').click()`);
    assert.equal(await waitFor(`!document.querySelector('.composer-toolbar [aria-label="Compact observed idle attachment"]')`), "ready");
    assert.equal(await waitFor(`!!document.querySelector('.composer-toolbar [aria-label="Cancel observed run"]')`), "ready");
    assert.equal(await evaluate(`document.querySelector('.composer-toolbar .send-button')?.textContent`), "Send");
    for (const width of [390, 1120]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 800, deviceScaleFactor: 1, mobile: false });
      for (const theme of ["dark", "light"]) {
        await evaluate(`document.documentElement.dataset.theme = '${theme}'`);
        const result = await sample();
        assert.ok(result.pageWidth <= result.viewWidth + 2 && result.panelScrollWidth <= result.panelWidth + 2,
          `${theme} ${width}px observed-run controls overflow: ${JSON.stringify(result)}`);
        assert.ok(result.toolbar <= (width === 390 ? 100 : 80), `${theme} ${width}px observed-run toolbar grew: ${JSON.stringify(result)}`);
        await evaluate(`document.querySelector('.composer-toolbar [aria-label="Cancel observed run"]').focus()`);
        assert.equal(await evaluate(`JSON.stringify([document.activeElement?.getAttribute('aria-label'), getComputedStyle(document.activeElement).outlineStyle])`),
          '["Cancel observed run","solid"]');
      }
    }
    await evaluate(`(() => { const el = document.querySelector('.prompt-input');
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(el, 'Keep this unsent text');
      el.dispatchEvent(new Event('input', { bubbles: true })); window.fixture.mode('uncertain'); })()`);
    assert.equal(await waitFor(`document.querySelector('.prompt-input').value === 'Keep this unsent text'`), "ready");
    await evaluate(`document.querySelector('.composer-toolbar [aria-label="Cancel observed run"]').focus()`);
    assert.equal(await evaluate(`document.activeElement?.getAttribute('aria-label')`), "Cancel observed run");
    await command("Page.bringToFront");
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", text: "\r", windowsVirtualKeyCode: 13, nativeVirtualKeyCode: 13 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    assert.equal(await waitFor(`!!document.querySelector('.composer-toolbar [aria-label^="Retry exact cancellation"]')`), "ready",
      `keyboard activation: ${await evaluate(`JSON.stringify([window.fixture.abortCalls.length,document.activeElement?.outerHTML?.slice(0,300)])`)}`);
    assert.equal(await evaluate(`document.querySelector('.composer-notice[role="status"]')?.textContent.includes('uncertain')`), true);
    assert.equal(await evaluate(`document.querySelector('.composer-notice:not([role])')?.textContent.includes('run run-one')`), true);
    assert.equal(await evaluate(`JSON.stringify(window.fixture.abortCalls.map(({expectedEpoch,sessionId,expectedRuntimeInstanceId,expectedAttachmentGeneration,expectedRunId}) =>
      ({expectedEpoch,sessionId,expectedRuntimeInstanceId,expectedAttachmentGeneration,expectedRunId})))`),
      JSON.stringify([{ expectedEpoch: "fixture-epoch", sessionId: "fixture-session", expectedRuntimeInstanceId: "fixture-runtime",
        expectedAttachmentGeneration: "gen-12", expectedRunId: "run-one" }]));
    assert.match((await evaluate(`window.fixture.abortCalls[0].clientRequestId`))!, /^[0-9a-f-]{36}$/i);
    assert.equal(await evaluate(`document.querySelector('.prompt-input').value`), "Keep this unsent text");
    await evaluate(`window.fixture.observe('run-two', 13); document.querySelector('#refresh-session-context').click()`);
    assert.equal(await waitFor(`document.querySelector('.advanced-session-controls dd')?.textContent === 'gen-13'`), "ready");
    assert.equal(await evaluate(`document.querySelector('.composer-toolbar [aria-label^="Retry exact cancellation"]')?.getAttribute('aria-label').includes('run-one')`), true);
    assert.equal(await evaluate(`document.querySelector('.composer-toolbar [aria-label^="Retry exact cancellation"]')?.title.includes('gen-12')`), true);
    assert.equal(await evaluate(`document.querySelector('.composer-toolbar .send-button')?.textContent`), "Send");
    await evaluate(`window.fixture.mode('hold'); document.querySelector('.composer-toolbar [aria-label^="Retry exact cancellation"]').click();
      document.querySelector('.composer-toolbar [aria-label^="Retry exact cancellation"]').click()`);
    assert.equal(await evaluate(`window.fixture.abortCalls.length`), 2, "in-flight retry excludes repeated admission");
    assert.equal(await evaluate(`window.fixture.abortCalls[0].clientRequestId === window.fixture.abortCalls[1].clientRequestId &&
      window.fixture.abortCalls[1].expectedRunId === 'run-one'`), true);
    await evaluate(`window.fixture.switchSession('fixture-other')`);
    assert.equal(await waitFor(`!!document.querySelector('.composer-toolbar [aria-label="Cancel observed run"]')`), "ready");
    assert.equal(await evaluate(`document.querySelector('.composer-toolbar [aria-label^="Retry exact cancellation"]') === null`), true);
    await evaluate(`window.fixture.settle(); window.fixture.switchSession('fixture-session')`);
    assert.equal(await waitFor(`!!document.querySelector('.composer-toolbar [aria-label^="Retry exact cancellation"]')`), "ready");
    assert.equal(await evaluate(`document.querySelector('.prompt-input').value`), "Keep this unsent text");
    await evaluate(`window.fixture.mode('fail'); document.querySelector('.composer-toolbar [aria-label^="Retry exact cancellation"]').click()`);
    assert.equal(await waitFor(`document.querySelector('.advanced-session-controls [role="status"]')?.textContent.includes('busy')`), "ready");
    assert.equal(await evaluate(`window.fixture.abortCalls[2].expectedRunId`), "run-one");
    assert.equal(await evaluate(`!!document.querySelector('.composer-toolbar [aria-label="Cancel observed run"]')`), true,
      "a settled failure permits only a new explicit observation-targeted action");
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
    await evaluate(`window.fixture.retainSend('Pending exact Send text')`);
    assert.equal(await waitFor(`document.querySelector('.composer-toolbar .send-button')?.textContent === 'Retry exact request'`), "ready");
    assert.equal(await evaluate(`document.querySelector('.prompt-input').value`), "Pending exact Send text");
    assert.equal(await evaluate(`!!document.querySelector('.composer-toolbar [aria-label="Cancel observed run"]')`), true,
      "pending exact Send recovery does not disappear when observed-run cancellation is available");
    await evaluate(`window.fixture.observe(null, 13); document.querySelector('#refresh-session-context').click()`);
    assert.equal(await waitFor(`!!document.querySelector('.composer-toolbar [aria-label="Compact observed idle attachment"]')`), "ready");
    assert.equal(await evaluate(`document.querySelector('.composer-toolbar .send-button')?.textContent`), "Retry exact request",
      "eligible compaction cannot hide exact Send recovery");
    assert.equal(await evaluate(`document.querySelector('.prompt-input').value`), "Pending exact Send text");
    await evaluate(`window.fixture.observe('run-three', 14, 'replacement-runtime'); document.querySelector('#refresh-session-context').click()`);
    assert.equal(await waitFor(`[...document.querySelectorAll('.owned-session [role="alert"]')].some(el => el.textContent.includes('Reload required'))`), "ready");
    assert.equal(await evaluate(`document.querySelector('.composer-toolbar [aria-label="Cancel observed run"]') === null`), true,
      "stale runtime identity cannot authorize another observed-run action");
    assert.equal(await evaluate(`document.querySelector('.composer-toolbar [aria-label="Compact observed idle attachment"]') === null`), true,
      "stale runtime identity cannot authorize an idle compaction attempt");
    assert.equal(await compactKey('.project-rename input'), false);
    assert.equal(await compactKey('.composer-toolbar .send-button'), false);
    assert.equal(await evaluate(`document.querySelector('.composer-toolbar .send-button')?.disabled`), true);
    assert.equal(await evaluate(`window.fixture.abortCalls.length`), 3);
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
