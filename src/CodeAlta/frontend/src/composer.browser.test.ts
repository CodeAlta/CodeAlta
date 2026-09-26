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
  assert.match(app, /action === "context"\)[\s\S]*?activateContextShortcut\(workspaceShell\.current\)/,
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
    const waitFor = (condition: string) => evaluate(`new Promise(resolve => { const end = Date.now() + 4000; const check = () => {
      if (${condition}) resolve('ready'); else if (Date.now() > end) resolve('timed out'); else setTimeout(check, 25); }; check(); })`);
    const countLabel = () => evaluate(`document.querySelector('.composer-toolbar [data-reminder-count]')?.getAttribute('aria-label')`);
    assert.match((await countLabel()) ?? "", /active count unknown/i, "an unsettled list is not zero");
    assert.equal(await evaluate(`document.querySelector('.composer-toolbar .reminder-count')?.textContent`), "?");
    assert.equal(await evaluate(`JSON.stringify(window.fixture.reminderReads[0])`),
      JSON.stringify({ expectedEpoch: "fixture-epoch", sessionId: "fixture-session" }));
    await evaluate(`window.fixture.settleReminder('matching')`);
    const countReady = async (text: string) => evaluate(`new Promise(resolve => { const end = Date.now() + 4000; const check = () => {
      if (document.querySelector('.composer-toolbar [data-reminder-count]')?.getAttribute('aria-label')?.includes(${JSON.stringify(text)})) resolve('ready');
      else if (Date.now() > end) resolve('timed out'); else setTimeout(check, 25); }; check(); })`);
    assert.equal(await countReady("2 active at last observation"), "ready");
    assert.match((await countLabel()) ?? "", /may have changed/i, "completion since observation is unknown");
    assert.equal(await evaluate(`document.querySelector('.composer-toolbar .reminder-count')?.textContent`), "2");
    await evaluate(`window.fixture.startReminderMutation()`);
    assert.equal(await countReady("active count unknown"), "ready", "pending mutation invalidates the count");
    await evaluate(`window.fixture.settleReminderMutation()`);
    assert.equal(await countLabel(), "Reminders for selected session: active count unknown",
      "an accepted mutation cannot resurrect the old observation");
    await evaluate(`document.querySelector('[aria-label="Refresh observed reminder count"]').click()`);
    assert.equal(await evaluate(`window.fixture.reminderReads.length`), 2);
    await evaluate(`window.fixture.settleReminder('completed')`);
    assert.equal(await countReady("1 active at last observation"), "ready", "completed rows are not active");
    await evaluate(`document.querySelector('[aria-label="Refresh observed reminder count"]').click()`);
    await evaluate(`window.fixture.settleReminder('mismatch')`);
    assert.equal(await countReady("active count unknown"), "ready", "mismatched response cannot assert zero");
    await evaluate(`document.querySelector('[aria-label="Refresh observed reminder count"]').click()`);
    await evaluate(`window.fixture.settleReminder('error')`);
    assert.equal(await countReady("active count unknown"), "ready", "error-path zeros are not an observation");
    await evaluate(`document.querySelector('[aria-label="Refresh observed reminder count"]').click()`);
    await evaluate(`window.fixture.settleReminder('empty')`);
    assert.equal(await countReady("0 active at last observation"), "ready", "only an exact successful empty list proves zero");
    await evaluate(`document.querySelector('[aria-label="Refresh observed reminder count"]').click()`);
    await evaluate(`window.fixture.settleReminder('invalid')`);
    assert.equal(await countReady("active count unknown"), "ready", "inconsistent active totals cannot assert zero");
    await evaluate(`document.querySelector('.composer-toolbar [data-reminder-count]').click()`);
    assert.equal(await evaluate(`window.fixture.catalogOpens`), 1, "count retains the reminder navigation action");
    await evaluate(`window.fixture.catalogOpens = 0`);
    await evaluate(`document.querySelector('[aria-label="Refresh observed reminder count"]').click()`);
    assert.equal(await waitFor(`window.fixture.reminderReads.length === 7`), "ready");
    await evaluate(`window.fixture.switchSession('fixture-other')`);
    assert.equal(await waitFor(`window.fixture.reminderReads.length === 8`), "ready");
    assert.match((await countLabel()) ?? "", /active count unknown/i, "switching sessions clears the prior observation");
    await evaluate(`window.fixture.settleReminder('matching')`);
    assert.match((await countLabel()) ?? "", /active count unknown/i, "late old-session list cannot certify new session");
    await evaluate(`window.fixture.settleReminder('completed')`);
    assert.equal(await countReady("1 active at last observation"), "ready");
    await evaluate(`window.fixture.switchSession('fixture-session')`);
    assert.equal(await waitFor(`window.fixture.reminderReads.length === 9`), "ready");
    assert.match((await countLabel()) ?? "", /active count unknown/i, "returning does not reuse an old observation");
    await evaluate(`window.fixture.settleReminder('empty')`);
    assert.equal(await countReady("0 active at last observation"), "ready");
    await evaluate(`window.fixture.startReminderMutation()`);
    assert.equal(await countReady("active count unknown"), "ready");
    await evaluate(`window.fixture.settleReminderMutation(true)`);
    await evaluate(`document.querySelector('[aria-label="Refresh observed reminder count"]').click()`);
    assert.equal(await waitFor(`window.fixture.reminderReads.length === 10`), "ready");
    await evaluate(`window.fixture.settleReminder('matching')`);
    assert.match((await countLabel()) ?? "", /active count unknown/i,
      "an uncertain admission cannot be certified by a later read");
    type Sample = { panel: number; toolbar: number; editor: number; scroll: number; client: number; overflow: string; padding: string;
      controls: Record<string, { color: string; background: string; border: string; outline: string; opacity: string }>;
      pageWidth: number; viewWidth: number; panelWidth: number; panelScrollWidth: number };
    const sample = async (): Promise<Sample> => JSON.parse((await evaluate(`JSON.stringify((() => {
      const editor = document.querySelector('.prompt-input');
      const controls = Object.fromEntries(['.prompt-options select', '.prompt-options option', '.project-rename input', '.session-rename input']
        .map(selector => { const s = getComputedStyle(document.querySelector(selector)); return [selector, { color: s.color,
          background: s.backgroundColor, border: s.borderColor, outline: s.outlineStyle, opacity: s.opacity }]; }));
      const reminder = document.querySelector('.reminder-count');
      const badge = getComputedStyle(reminder); const button = getComputedStyle(reminder.closest('button'));
      controls['.reminder-count'] = { color: badge.color, background: button.backgroundColor,
        border: button.borderColor, outline: button.outlineStyle, opacity: button.opacity };
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
    assert.match((await evaluate(`document.querySelector('.owned-session [data-reminder-count]')?.title`)) ?? "", /Ctrl\+G, Ctrl\+D/);
    assert.equal(await evaluate(`document.querySelector('.composer-toolbar button.primary-button')?.textContent`), "Send");
    assert.equal(await evaluate(`document.querySelector('.composer-toolbar')?.textContent.includes('Refresh receipts')`), false);
    assert.equal(await evaluate(`!!document.querySelector('.composer-toolbar [aria-label="Cancel observed run"]')`), false,
      "host availability without an observed run cannot expose cancellation");
    assert.equal(await evaluate(`!!document.querySelector('.composer-toolbar [aria-label="Steer current composer to observed run"]')`), false,
      "host availability without an observed run cannot expose steering");
    assert.equal(await evaluate(`!!document.querySelector('.composer-toolbar [aria-label="Queue current composer in this host"]')`), false,
      "host availability without an observed attachment cannot expose queueing");
    assert.equal(await evaluate(`!!document.querySelector('.composer-toolbar [aria-label="Compact observed idle attachment"]')`), false,
      "host availability without an observed entry cannot imply idle");
    await evaluate(`window.fixture.observe(null, 12); document.querySelector('#refresh-session-context').click()`);
    assert.equal(await waitFor(`!!document.querySelector('.composer-toolbar [aria-label="Compact observed idle attachment"]')`), "ready");
    assert.equal(await evaluate(`!!document.querySelector('.composer-toolbar [aria-label="Queue current composer in this host"]')`), true,
      "an idle attachment with a valid identity permits a host-only queue attempt without a run");
    assert.equal(await evaluate(`document.querySelector('.composer-toolbar [aria-label="Queue current composer in this host"]')?.disabled`), true,
      "empty text is not a queueable composer draft");
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
    assert.equal(await evaluate(`!!document.querySelector('.composer-toolbar [aria-label="Queue current composer in this host"]')`), false);
    assert.equal(await evaluate(`!!document.querySelector('.composer-toolbar [aria-label="Steer current composer to observed run"]')`), false);
    await evaluate(`window.fixture.flags({transitioning:true}); document.querySelector('#refresh-session-context').click()`);
    assert.equal(await waitFor(`[...document.querySelectorAll('.advanced-session-controls .detail')].some(el => el.textContent.includes('coordinator transition recorded: yes'))`), "ready");
    assert.equal(await evaluate(`!!document.querySelector('.composer-toolbar [aria-label="Compact observed idle attachment"]')`), false);
    assert.equal(await evaluate(`!!document.querySelector('.composer-toolbar [aria-label="Queue current composer in this host"]')`), false);
    assert.equal(await evaluate(`!!document.querySelector('.composer-toolbar [aria-label="Steer current composer to observed run"]')`), false);
    await evaluate(`window.fixture.flags({draining:true}); document.querySelector('#refresh-session-context').click()`);
    assert.equal(await waitFor(`[...document.querySelectorAll('.advanced-session-controls dt')].some(el => el.textContent.includes('Queue drain in progress') && el.nextElementSibling?.textContent === 'yes')`), "ready");
    assert.equal(await evaluate(`!!document.querySelector('.composer-toolbar [aria-label="Compact observed idle attachment"]')`), false);
    assert.equal(await evaluate(`!!document.querySelector('.composer-toolbar [aria-label="Queue current composer in this host"]')`), true,
      "draining blocks idle compaction but permits an attachment-targeted queue attempt");
    assert.equal(await evaluate(`!!document.querySelector('.composer-toolbar [aria-label="Steer current composer to observed run"]')`), false);
    await evaluate(`window.fixture.flags({}); document.querySelector('#refresh-session-context').click()`);
    assert.equal(await waitFor(`!!document.querySelector('.composer-toolbar [aria-label="Compact observed idle attachment"]')`), "ready");
    assert.equal(await waitFor(`!!document.querySelector('.composer-toolbar [aria-label="Queue current composer in this host"]')`), "ready",
      "queueing is permitted with either idle or active observations");
    await evaluate(`(() => { const el = document.querySelector('.prompt-input');
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(el, 'Draft for compaction');
      el.dispatchEvent(new Event('input', { bubbles: true })); window.fixture.compactMode('uncertain'); })()`);
    assert.equal(await compactKey('#session-prompt'), true, "production shortcut dispatcher routes Ctrl+F11 from composer");
    assert.equal(await waitFor(`!!document.querySelector('.composer-toolbar [aria-label^="Retry exact compaction"]:not(:disabled)')`), "ready");
    assert.equal(await evaluate(`JSON.stringify(window.fixture.compactCalls[0])`),
      JSON.stringify({ expectedEpoch: "fixture-epoch", clientRequestId: await evaluate(`window.fixture.compactCalls[0].clientRequestId`),
        sessionId: "fixture-session", expectedRuntimeInstanceId: "11111111-1111-4111-8111-111111111111", expectedAttachmentGeneration: "12" }));
    assert.equal(await evaluate(`document.querySelector('.prompt-input').value`), "Draft for compaction");
    assert.equal(await evaluate(`document.querySelector('.composer-notice:not([role])')?.textContent.includes('12')`), true);
    await evaluate(`window.presentationSend = document.querySelector('.send-button');
      window.presentationEditor = document.querySelector('#session-prompt');
      window.presentationSend.focus(); window.fixture.observe('new-run', 13);
      document.querySelector('#refresh-session-context').click()`);
    assert.equal(await waitFor(`document.querySelector('.advanced-session-controls dd')?.textContent === '13'`), "ready");
    assert.equal(await evaluate(`document.querySelector('.composer-toolbar [aria-label^="Retry exact compaction"]')?.title.includes('12')`), true);
    assert.equal(await evaluate(`!!document.querySelector('.composer-toolbar [aria-label="Cancel observed run"]')`), true,
      "a new observed run does not displace the older retained compaction target or cancellation control");
    assert.equal(await evaluate(`document.querySelector('.composer-toolbar .primary-button')?.textContent`), "Cancel observed run",
      "eligible cancellation needs a truthful visible primary label, not an icon beside primary Send");
    assert.equal(await evaluate(`window.presentationSend === document.querySelector('.send-button') &&
      document.activeElement === window.presentationSend && window.presentationEditor === document.querySelector('#session-prompt')`), true,
      "emphasis changes must retain the focused Send node and editor, never turn Send into Abort");
    assert.equal(await evaluate(`document.querySelector('#session-prompt').value`), "Draft for compaction");
    assert.equal(await evaluate(`window.fixture.abortCalls.length`), 0, "presentation must not admit cancellation");
    assert.equal(await evaluate(`window.fixture.sendCalls.length`), 0, "presentation must not admit Send");
    for (const width of [390, 1120]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 800, deviceScaleFactor: 1, mobile: false });
      for (const theme of ["dark", "light"]) {
        await evaluate(`document.documentElement.dataset.theme = '${theme}'`);
        const layout = await sample();
        assert.ok(layout.pageWidth <= layout.viewWidth + 2 && layout.panelScrollWidth <= layout.panelWidth + 2
          && layout.toolbar <= (width === 390 ? 135 : 80), `${theme} ${width}px retained compaction plus labelled cancellation toolbar: ${JSON.stringify(layout)}`);
      }
    }
    await evaluate(`window.fixture.compactMode('hold'); document.querySelector('.composer-toolbar [aria-label^="Retry exact compaction"]').click();
      document.querySelector('.composer-toolbar [aria-label^="Retry exact compaction"]').click()`);
    assert.equal(await evaluate(`window.fixture.compactCalls.length`), 2, "original waiter excludes repeated retry");
    assert.equal(await evaluate(`window.fixture.compactCalls[0].clientRequestId === window.fixture.compactCalls[1].clientRequestId &&
      window.fixture.compactCalls[1].expectedAttachmentGeneration === '12'`), true);
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
    assert.equal(await evaluate(`window.fixture.compactCalls[2].expectedAttachmentGeneration`), "12");
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
    const presentationReads = Number(await evaluate(`window.fixture.refreshes`));
    await evaluate(`window.fixture.holdRuntimeRead(); document.querySelector('#refresh-session-context').click()`);
    assert.equal(await waitFor(`!document.querySelector('.cancel-run-button')`), "ready", "loading cannot retain new cancellation authority");
    assert.equal(await evaluate(`document.querySelector('.composer-toolbar .primary-button')?.textContent`), "Send");
    await evaluate(`window.fixture.settleRuntimeRead()`);
    assert.equal(await waitFor(`document.querySelector('.cancel-run-button')?.textContent === 'Cancel observed run'`), "ready");
    await evaluate(`window.fixture.failRuntimeRead(true); document.querySelector('#refresh-session-context').click()`);
    assert.equal(await waitFor(`!document.querySelector('.cancel-run-button')`), "ready", "failed observation cannot invent a running state");
    assert.equal(await evaluate(`document.querySelector('.composer-toolbar .primary-button')?.textContent`), "Send");
    await evaluate(`window.fixture.failRuntimeRead(false); document.querySelector('#refresh-session-context').click()`);
    assert.equal(await waitFor(`!!document.querySelector('.cancel-run-button')`), "ready");
    assert.equal(await evaluate(`window.fixture.refreshes`), presentationReads + 3, "only the three explicit refreshes may read");
    assert.equal(await evaluate(`window.fixture.abortCalls.length + window.fixture.sendCalls.length`), 0);
    for (const flag of ['retiring', 'transitioning', 'draining']) {
      await evaluate(`window.fixture.flags({${flag}:true});document.querySelector('#refresh-session-context').click()`);
      assert.equal(await waitFor(`!document.querySelector('.cancel-run-button')`), "ready", `${flag} active observations cannot offer cancellation`);
      assert.equal(await evaluate(`document.querySelector('.composer-toolbar .primary-button')?.textContent`), "Send");
    }
    await evaluate(`window.fixture.flags({});document.querySelector('#refresh-session-context').click()`);
    assert.equal(await waitFor(`!!document.querySelector('.cancel-run-button')`), "ready");
    assert.equal(await evaluate(`!!document.querySelector('.composer-toolbar [aria-label="Steer current composer to observed run"]')`), true);
    const steerButton = '.composer-toolbar [aria-label="Steer current composer to observed run"]';
    const writePrompt = (value: string) => evaluate(`(() => {const el=document.querySelector('#session-prompt');
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(el,${JSON.stringify(value)});
      el.dispatchEvent(new Event('input',{bubbles:true}));})()`);
    const steerKey = () => evaluate(`(() => {const el=document.querySelector('#session-prompt');
      const event=new KeyboardEvent('keydown',{key:'Enter',ctrlKey:true,bubbles:true,cancelable:true});
      el.dispatchEvent(event);return event.defaultPrevented;})()`);
    await writePrompt("  ");
    assert.equal(await waitFor(`document.querySelector(${JSON.stringify(steerButton)})?.disabled`), "ready");
    await writePrompt("Toolbar instruction");
    assert.equal(await waitFor(`!document.querySelector(${JSON.stringify(steerButton)})?.disabled`), "ready");
    for (const width of [390, 1120]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 800, deviceScaleFactor: 1, mobile: false });
      for (const theme of ["dark", "light"]) {
        await evaluate(`document.documentElement.dataset.theme = '${theme}'`);
        const layout = await sample();
        const icon = JSON.parse((await evaluate(`JSON.stringify((() => {const b=document.querySelector(${JSON.stringify(steerButton)}),s=getComputedStyle(b);
          b.focus();return {label:b.getAttribute('aria-label'),title:b.title,svg:!!b.querySelector('svg[aria-hidden="true"]'),
            color:s.color,background:s.backgroundColor,outline:getComputedStyle(b).outlineStyle};})())`))!) as
          {label:string;title:string;svg:boolean;color:string;background:string;outline:string};
        assert.equal(icon.label, "Steer current composer to observed run");
        assert.ok(icon.title.includes("run-one") && icon.svg && icon.outline === "solid");
        assert.ok(contrast(icon.color, icon.background) >= 4.5, `${width}/${theme} steer contrast: ${JSON.stringify(icon)}`);
        assert.ok(layout.pageWidth <= layout.viewWidth + 2 && layout.panelScrollWidth <= layout.panelWidth + 2
          && layout.toolbar <= (width === 390 ? 135 : 80), `${width}/${theme} steer and labelled cancellation controls overflow: ${JSON.stringify(layout)}`);
      }
    }
    const refreshBeforeSteer = await evaluate(`window.fixture.refreshes`);
    await evaluate(`(() => {window.fixture.steerMode('hold');const b=document.querySelector(${JSON.stringify(steerButton)});
      b.click();b.click();const event=new KeyboardEvent('keydown',{key:'Enter',ctrlKey:true,bubbles:true,cancelable:true});
      document.querySelector('#session-prompt').dispatchEvent(event);})()`);
    assert.equal(await evaluate(`window.fixture.steerCalls.length`), 1, "click, duplicate click and Ctrl+Enter share the in-flight owner latch");
    assert.equal(await evaluate(`window.fixture.refreshes`), refreshBeforeSteer, "toolbar uses the observed target without a hidden refresh");
    assert.equal(await evaluate(`JSON.stringify((({text,expectedEpoch,sessionId,expectedRuntimeInstanceId,expectedAttachmentGeneration,expectedRunId})=>
      ({text,expectedEpoch,sessionId,expectedRuntimeInstanceId,expectedAttachmentGeneration,expectedRunId}))(window.fixture.steerCalls[0]))`),
      JSON.stringify({text:"Toolbar instruction",expectedEpoch:"fixture-epoch",sessionId:"fixture-session",
        expectedRuntimeInstanceId:"11111111-1111-4111-8111-111111111111",expectedAttachmentGeneration:"12",expectedRunId:"run-one"}));
    await writePrompt("Edited after toolbar click");
    await evaluate(`window.fixture.settleSteer()`);
    assert.equal(await waitFor(`document.querySelector('#session-prompt').value === 'Edited after toolbar click' &&
      !document.querySelector(${JSON.stringify(steerButton)})?.disabled`), "ready", "accepted steering cannot erase newer Send draft edits");
    await evaluate(`window.fixture.steerMode('uncertain')`);
    assert.equal(await steerKey(), true);
    assert.equal(await waitFor(`!!document.querySelector('.context-actions button')?.textContent.includes('Retry exact steering request')`), "ready");
    assert.equal(await evaluate(`window.fixture.steerCalls.length`), 2);
    assert.equal(await evaluate(`window.fixture.steerCalls[1].text`), "Edited after toolbar click");
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(steerButton)})?.disabled`), true);
    await writePrompt("Newer independent draft");
    await evaluate(`window.fixture.observe('run-two',13); document.querySelector('#refresh-session-context').click()`);
    assert.equal(await waitFor(`document.querySelector('.advanced-session-controls dd')?.textContent === '13'`), "ready");
    assert.equal(await steerKey(), true);
    await evaluate(`document.querySelector(${JSON.stringify(steerButton)})?.click()`);
    assert.equal(await evaluate(`window.fixture.steerCalls.length`), 2, "retained steering never retries or retargets from composer");
    await evaluate(`window.fixture.switchSession('fixture-other')`);
    assert.equal(await waitFor(`!!document.querySelector('#session-prompt')`), "ready");
    assert.equal(await evaluate(`!!document.querySelector('.context-actions button')?.textContent.includes('Retry exact steering request')`), false);
    await evaluate(`window.fixture.switchSession('fixture-session')`);
    assert.equal(await waitFor(`!!document.querySelector('.context-actions button')?.textContent.includes('Retry exact steering request')`), "ready");
    assert.equal(await evaluate(`document.querySelector('#session-prompt').value`), "Newer independent draft");
    await evaluate(`window.fixture.steerMode('hold');document.querySelector('.context-actions button').click();
      document.querySelector('.context-actions button').click()`);
    assert.equal(await evaluate(`window.fixture.steerCalls.length`), 3);
    assert.equal(await evaluate(`window.fixture.steerCalls[2].clientRequestId === window.fixture.steerCalls[1].clientRequestId &&
      window.fixture.steerCalls[2].expectedRunId === 'run-one' && window.fixture.steerCalls[2].text === 'Edited after toolbar click'`), true);
    await evaluate(`window.fixture.settleSteer()`);
    assert.equal(await waitFor(`!document.querySelector(${JSON.stringify(steerButton)})?.disabled`), "ready");
    assert.equal(await evaluate(`document.querySelector('#session-prompt').value`), "Newer independent draft");
    await evaluate(`document.querySelector(${JSON.stringify(steerButton)})?.click()`);
    assert.equal(await evaluate(`window.fixture.steerCalls.length`), 4);
    assert.equal(await evaluate(`JSON.stringify([window.fixture.steerCalls[3].text,window.fixture.steerCalls[3].expectedRunId,
      window.fixture.steerCalls[3].expectedAttachmentGeneration,window.fixture.steerCalls[3].clientRequestId !== window.fixture.steerCalls[1].clientRequestId])`),
      '["Newer independent draft","run-two","13",true]');
    await evaluate(`window.fixture.settleSteer()`);
    assert.equal(await waitFor(`document.querySelector('#session-prompt').value === ''`), "ready");
    await evaluate(`window.fixture.observe('run-one',12);document.querySelector('#refresh-session-context').click()`);
    assert.equal(await waitFor(`document.querySelector('.advanced-session-controls dd')?.textContent === '12'`), "ready");
    for (const width of [390, 1120]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 800, deviceScaleFactor: 1, mobile: false });
      for (const theme of ["dark", "light"]) {
        await evaluate(`document.documentElement.dataset.theme = '${theme}'`);
        const result = await sample();
        assert.ok(result.pageWidth <= result.viewWidth + 2 && result.panelScrollWidth <= result.panelWidth + 2,
          `${theme} ${width}px observed-run controls overflow: ${JSON.stringify(result)}`);
        assert.ok(result.toolbar <= (width === 390 ? 135 : 80), `${theme} ${width}px labelled observed-run toolbar grew: ${JSON.stringify(result)}`);
        await evaluate(`document.querySelector('.composer-toolbar [aria-label="Cancel observed run"]').focus()`);
        assert.equal(await evaluate(`JSON.stringify([document.activeElement?.getAttribute('aria-label'), getComputedStyle(document.activeElement).outlineStyle])`),
          '["Cancel observed run","solid"]');
        const colors = JSON.parse((await evaluate(`JSON.stringify((() => {const s=getComputedStyle(document.activeElement);
          return [s.color,s.backgroundColor];})())`))!) as [string, string];
        assert.ok(contrast(...colors) >= 4.5, `${theme} cancellation label must be legible: ${colors}`);
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
    assert.equal(await evaluate(`document.querySelector('.cancel-run-button')?.textContent`), "Retry exact cancellation",
      "retained intent must visibly disclose recovery, not offer a new live-run action");
    assert.equal(await evaluate(`[...document.querySelectorAll('.composer-notice[role="status"]')].some(el=>el.textContent.includes('uncertain'))`), true);
    assert.equal(await evaluate(`[...document.querySelectorAll('.composer-notice:not([role])')].some(el=>el.textContent.includes('run run-one'))`), true);
    assert.equal(await evaluate(`JSON.stringify(window.fixture.abortCalls.map(({expectedEpoch,sessionId,expectedRuntimeInstanceId,expectedAttachmentGeneration,expectedRunId}) =>
      ({expectedEpoch,sessionId,expectedRuntimeInstanceId,expectedAttachmentGeneration,expectedRunId})))`),
      JSON.stringify([{ expectedEpoch: "fixture-epoch", sessionId: "fixture-session", expectedRuntimeInstanceId: "11111111-1111-4111-8111-111111111111",
        expectedAttachmentGeneration: "12", expectedRunId: "run-one" }]));
    assert.match((await evaluate(`window.fixture.abortCalls[0].clientRequestId`))!, /^[0-9a-f-]{36}$/i);
    assert.equal(await evaluate(`document.querySelector('.prompt-input').value`), "Keep this unsent text");
    await evaluate(`window.fixture.observe('run-two', 13); document.querySelector('#refresh-session-context').click()`);
    assert.equal(await waitFor(`document.querySelector('.advanced-session-controls dd')?.textContent === '13'`), "ready");
    assert.equal(await evaluate(`document.querySelector('.composer-toolbar [aria-label^="Retry exact cancellation"]')?.getAttribute('aria-label').includes('run-one')`), true);
    assert.equal(await evaluate(`document.querySelector('.composer-toolbar [aria-label^="Retry exact cancellation"]')?.title.includes('12')`), true);
    assert.equal(await evaluate(`document.querySelector('.composer-toolbar .send-button')?.textContent`), "Send");
    await evaluate(`window.fixture.mode('hold'); document.querySelector('.composer-toolbar [aria-label^="Retry exact cancellation"]').focus()`);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: " ", code: "Space", text: " ", windowsVirtualKeyCode: 32 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: " ", code: "Space", windowsVirtualKeyCode: 32 });
    await evaluate(`document.querySelector('.composer-toolbar [aria-label^="Retry exact cancellation"]').click()`);
    assert.equal(await evaluate(`window.fixture.abortCalls.length`), 2, "in-flight retry excludes repeated admission");
    assert.equal(await evaluate(`window.fixture.abortCalls[0].clientRequestId === window.fixture.abortCalls[1].clientRequestId &&
      window.fixture.abortCalls[1].expectedRunId === 'run-one'`), true);
    await evaluate(`window.fixture.failRuntimeRead(true);document.querySelector('#refresh-session-context').click()`);
    assert.equal(await waitFor(`[...document.querySelectorAll('[role="alert"]')].some(el=>el.textContent.includes('Runtime observation unavailable'))`), "ready");
    assert.equal(await evaluate(`document.querySelector('.cancel-run-button')?.textContent`), "Retry exact cancellation");
    assert.equal(await evaluate(`document.querySelector('.cancel-run-button')?.disabled`), true, "failed refresh does not unlock the live original waiter");
    assert.equal(await evaluate(`document.querySelector('.cancel-run-button')?.title.includes('run-one')`), true);
    await evaluate(`window.fixture.failRuntimeRead(false)`);
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
    const queueButton = '.composer-toolbar [aria-label="Queue current composer in this host"]';
    const retryQueue = () => evaluate(`(() => {const b=[...document.querySelectorAll('.context-actions button')]
      .find(el=>el.textContent.includes('Retry exact host-only queue request'));b?.click();return !!b;})()`);
    await writePrompt("  ");
    assert.equal(await waitFor(`document.querySelector(${JSON.stringify(queueButton)})?.disabled`), "ready");
    await writePrompt("Queue exact first\n");
    assert.equal(await waitFor(`!document.querySelector(${JSON.stringify(queueButton)})?.disabled`), "ready");
    await evaluate(`(() => {const el=[...document.querySelectorAll('.context-actions label')]
      .find(x=>x.textContent.includes('Host-only queued text')).querySelector('textarea');
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(el,'Separate secondary queue text');
      el.dispatchEvent(new Event('input',{bubbles:true}));})()`);
    for (const width of [390, 1120]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 800, deviceScaleFactor: 1, mobile: false });
      for (const theme of ["dark", "light"]) {
        await evaluate(`document.documentElement.dataset.theme = '${theme}'`);
        const layout = await sample();
        const icon = JSON.parse((await evaluate(`JSON.stringify((() => {const b=document.querySelector(${JSON.stringify(queueButton)}),s=getComputedStyle(b);
          b.focus();return {label:b.getAttribute('aria-label'),description:document.getElementById(b.getAttribute('aria-describedby'))?.textContent,
            title:b.title,svg:!!b.querySelector('svg[aria-hidden="true"]'),color:s.color,background:s.backgroundColor,
            outline:getComputedStyle(b).outlineStyle};})())`))!) as
          {label:string;description:string;title:string;svg:boolean;color:string;background:string;outline:string};
        assert.equal(icon.label, "Queue current composer in this host");
        assert.ok(icon.title.includes('attachment 13') && icon.description.includes('never targets a run') && icon.svg && icon.outline === "solid");
        assert.ok(contrast(icon.color, icon.background) >= 4.5, `${width}/${theme} queue contrast: ${JSON.stringify(icon)}`);
        assert.ok(layout.pageWidth <= layout.viewWidth + 2 && layout.panelScrollWidth <= layout.panelWidth + 2
          && layout.toolbar <= (width === 390 ? 135 : 80), `${width}/${theme} queue and labelled cancellation toolbar overflow: ${JSON.stringify(layout)}`);
      }
    }
    const queueReads = await evaluate(`window.fixture.refreshes`);
    await evaluate(`(() => {const b=document.querySelector(${JSON.stringify(queueButton)});b.click();b.click();})()`);
    assert.equal(await evaluate(`window.fixture.queueCalls.length`), 1, "the real queue owner excludes synchronous duplicate clicks");
    assert.equal(await evaluate(`window.fixture.refreshes`), queueReads, "toolbar does not refresh runtime state or provider choices");
    assert.equal(await evaluate(`JSON.stringify((({text,expectedEpoch,sessionId,expectedRuntimeInstanceId,expectedAttachmentGeneration,expectedRunId})=>
      ({text,expectedEpoch,sessionId,expectedRuntimeInstanceId,expectedAttachmentGeneration,expectedRunId}))(window.fixture.queueCalls[0]))`),
      JSON.stringify({text:"Queue exact first\n",expectedEpoch:"fixture-epoch",sessionId:"fixture-session",
        expectedRuntimeInstanceId:"11111111-1111-4111-8111-111111111111",expectedAttachmentGeneration:"13"}),
      "queue targets the exact attachment; it must not invent a run target");
    assert.equal(await evaluate(`Object.isFrozen(window.fixture.queuePending().request)`), true);
    await writePrompt("Edited after reservation");
    await evaluate(`window.fixture.settleQueue()`);
    assert.equal(await waitFor(`!document.querySelector(${JSON.stringify(queueButton)})?.disabled`), "ready");
    assert.equal(await evaluate(`document.querySelector('#session-prompt').value`), "Edited after reservation",
      "accepted owner reservation cannot clear an edited composer draft");
    assert.equal(await evaluate(`document.querySelector('.composer-notice[role="status"]')?.textContent.includes('insertion, durability and execution are not confirmed')`), true);
    assert.equal(await evaluate(`[...document.querySelectorAll('.context-actions label')].find(x=>x.textContent.includes('Host-only queued text'))?.querySelector('textarea').value`),
      "Separate secondary queue text", "the secondary queue editor is independent of composer queueing");
    await evaluate(`document.querySelector(${JSON.stringify(queueButton)}).focus(); window.fixture.queueMode('uncertain')`);
    await command("Page.bringToFront");
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", text: "\r", windowsVirtualKeyCode: 13, nativeVirtualKeyCode: 13 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    assert.equal(await waitFor(`!!window.fixture.queuePending() && !window.fixture.queuePending().inFlight`), "ready");
    assert.equal(await evaluate(`window.fixture.queueCalls.length`), 2);
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(queueButton)})?.disabled`), true);
    await writePrompt("Newer independent queue draft");
    await evaluate(`window.fixture.observe(null,14);document.querySelector('#refresh-session-context').click()`);
    assert.equal(await waitFor(`document.querySelector('.advanced-session-controls dd')?.textContent === '14'`), "ready");
    await evaluate(`document.querySelector(${JSON.stringify(queueButton)})?.click()`);
    assert.equal(await evaluate(`window.fixture.queueCalls.length`), 2, "toolbar cannot retry or retarget retained uncertainty");
    await evaluate(`window.fixture.switchSession('fixture-other')`);
    assert.equal(await waitFor(`!!document.querySelector('#session-prompt')`), "ready");
    assert.equal(await evaluate(`!!window.fixture.queuePending()`), false);
    await evaluate(`window.fixture.switchSession('fixture-session')`);
    assert.equal(await waitFor(`!!window.fixture.queuePending()`), "ready");
    assert.equal(await evaluate(`document.querySelector('#session-prompt').value`), "Newer independent queue draft");
    await evaluate(`window.fixture.queueMode('hold')`);
    assert.equal(await retryQueue(), true);
    await retryQueue();
    assert.equal(await evaluate(`window.fixture.queueCalls.length`), 3);
    assert.equal(await evaluate(`window.fixture.queueCalls[2].clientRequestId === window.fixture.queueCalls[1].clientRequestId &&
      window.fixture.queueCalls[2].expectedAttachmentGeneration === '13' && window.fixture.queueCalls[2].text === 'Edited after reservation'`), true);
    await evaluate(`window.fixture.settleQueue('mismatched')`);
    assert.equal(await waitFor(`!!window.fixture.queuePending() && !window.fixture.queuePending().inFlight`), "ready",
      "mismatched accepted receipt cannot clear retained intent");
    assert.equal(await retryQueue(), true);
    await evaluate(`window.fixture.settleQueue()`);
    assert.equal(await waitFor(`!window.fixture.queuePending()`), "ready");
    assert.equal(await evaluate(`window.fixture.queueCalls[3].clientRequestId === window.fixture.queueCalls[1].clientRequestId &&
      window.fixture.queueCalls[3].text === 'Edited after reservation'`), true);
    assert.equal(await evaluate(`document.querySelector('#session-prompt').value`), "Newer independent queue draft");
    assert.equal(await evaluate(`[...document.querySelectorAll('.context-actions label')].find(x=>x.textContent.includes('Host-only queued text'))?.querySelector('textarea').value`),
      "Separate secondary queue text", "toolbar uncertainty and exact retry across selection preserve the independent secondary draft");
    await evaluate(`(() => {const el=[...document.querySelectorAll('.context-actions label')]
      .find(x=>x.textContent.includes('Host-only queued text')).querySelector('textarea');
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(el,'Independent secondary draft across retry');
      el.dispatchEvent(new Event('input',{bubbles:true}));})()`);
    await evaluate(`window.fixture.switchSession('fixture-other')`);
    assert.equal(await waitFor(`!!document.querySelector('#session-prompt')`), "ready");
    await evaluate(`window.fixture.switchSession('fixture-session')`);
    assert.equal(await waitFor(`[...document.querySelectorAll('.context-actions label')].some(x=>x.textContent.includes('Host-only queued text') &&
      x.querySelector('textarea')?.value === 'Independent secondary draft across retry')`), "ready",
      "switching sessions in the same host must not replace an editor draft with earlier retained toolbar text");
    assert.equal(await waitFor(`!document.querySelector(${JSON.stringify(queueButton)})?.disabled`), "ready");
    await evaluate(`document.querySelector(${JSON.stringify(queueButton)})?.click()`);
    assert.equal(await evaluate(`window.fixture.queueCalls.length`), 5);
    assert.equal(await evaluate(`JSON.stringify([window.fixture.queueCalls[4].text,window.fixture.queueCalls[4].expectedAttachmentGeneration,
      window.fixture.queueCalls[4].clientRequestId !== window.fixture.queueCalls[1].clientRequestId])`),
      '["Newer independent queue draft","14",true]');
    await evaluate(`window.fixture.settleQueue('malformed')`);
    assert.equal(await waitFor(`!!window.fixture.queuePending() && !window.fixture.queuePending().inFlight`), "ready",
      "a malformed reservation receipt is not confirmation and retains the original request");
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(queueButton)})?.disabled`), true);
    await retryQueue();
    assert.equal(await evaluate(`window.fixture.queueCalls.length`), 6);
    assert.equal(await evaluate(`window.fixture.queueCalls[5].clientRequestId === window.fixture.queueCalls[4].clientRequestId &&
      window.fixture.queueCalls[5].expectedAttachmentGeneration === '14'`), true);
    await evaluate(`window.fixture.settleQueue()`);
    assert.equal(await waitFor(`!window.fixture.queuePending() && [...document.querySelectorAll('.context-actions label')]
      .find(x=>x.textContent.includes('Host-only queued text'))?.querySelector('textarea').value === 'Independent secondary draft across retry'`),
      "ready", "manual recovery of a toolbar queue request must not erase the independent secondary draft");
    assert.equal(await evaluate(`document.querySelector('#session-prompt').value`), "Newer independent queue draft");
    await evaluate(`window.fixture.queueMode('uncertain');document.querySelector(${JSON.stringify(queueButton)})?.click()`);
    assert.equal(await waitFor(`!!window.fixture.queuePending() && !window.fixture.queuePending().inFlight`), "ready");
    assert.equal(await evaluate(`window.fixture.queueCalls.length`), 7);
    assert.equal(await evaluate(`[...document.querySelectorAll('.context-actions label')].find(x=>x.textContent.includes('Host-only queued text'))?.querySelector('textarea').value`),
      "Newer independent queue draft", "the disabled queue editor shows the exact retained request for inspection");
    await evaluate(`(() => {const request=window.fixture.queueCalls[6];window.fixtureReceiptRows=[{
      kind:'Queue',clientRequestId:'wrong-key',sessionId:request.sessionId,operationId:'33333333-3333-4333-8333-333333333333',
      targetOperationId:null,state:'pending',outcome:null,code:null,runId:null,
      queueInsertion:{state:'pending',accepted:null,code:null}}];})()`);
    const receiptReads = Number(await evaluate(`window.fixtureReceiptReads ?? 0`));
    const refreshReceipts = () => evaluate(`[...document.querySelectorAll('.advanced-session-controls button')]
      .find(b=>b.textContent==='Refresh submissions').click()`);
    await refreshReceipts();
    assert.equal(await waitFor(`window.fixtureReceiptReads === ${receiptReads + 1}`), "ready");
    assert.equal(await evaluate(`!!window.fixture.queuePending()`), true, "unrelated receipt cannot reconcile a queue request");
    await evaluate(`window.fixtureReceiptRows[0].clientRequestId=window.fixture.queueCalls[6].clientRequestId`);
    await refreshReceipts();
    assert.equal(await waitFor(`!window.fixture.queuePending() && [...document.querySelectorAll('.context-actions label')]
      .find(x=>x.textContent.includes('Host-only queued text'))?.querySelector('textarea').value === 'Independent secondary draft across retry'`), "ready");
    assert.equal(await evaluate(`window.fixtureReceiptReads`), receiptReads + 2, "only explicit receipt refreshes read the fixture");
    assert.equal(await evaluate(`[...document.querySelectorAll('.context-actions label')].find(x=>x.textContent.includes('Host-only queued text'))?.querySelector('textarea').value`),
      "Independent secondary draft across retry", "receipt recovery must not clear or replace the toolbar-independent queue draft");
    assert.equal(await evaluate(`document.querySelector('#session-prompt').value`), "Newer independent queue draft");
    await evaluate(`window.fixture.queueMode('hold');[...document.querySelectorAll('.context-actions button')]
      .find(b=>b.textContent==='Queue text — this host only').click()`);
    assert.equal(await evaluate(`window.fixture.queueCalls.length`), 8);
    assert.equal(await evaluate(`window.fixture.queueCalls[7].text`), "Independent secondary draft across retry");
    await evaluate(`window.fixture.settleQueue()`);
    assert.equal(await waitFor(`!window.fixture.queuePending() && [...document.querySelectorAll('.context-actions label')]
      .find(x=>x.textContent.includes('Host-only queued text'))?.querySelector('textarea').value === ''`),
      "ready", "a successful direct secondary submission still clears the exact editor draft");
    assert.equal(await evaluate(`document.querySelector('#session-prompt').value`), "Newer independent queue draft");
    await evaluate(`window.fixture.observe('run-two',14);document.querySelector('#refresh-session-context').click()`);
    assert.equal(await waitFor(`!!document.querySelector('.composer-toolbar [aria-label="Cancel observed run"]')`), "ready");
    await evaluate(`window.fixture.mode('uncertain'); document.querySelector('.cancel-run-button').click()`);
    assert.equal(await waitFor(`document.querySelector('.cancel-run-button')?.textContent === 'Retry exact cancellation'`), "ready");
    await writePrompt('Pending exact Send text');
    await evaluate(`window.fixtureChoicesFail=false; [...document.querySelectorAll('.advanced-session-controls button')]
      .find(button=>button.textContent.includes('Refresh choices')).click()`);
    assert.equal(await waitFor(`!document.querySelector('select[aria-label="Reasoning"]').disabled`), "ready");
    await evaluate(`(() => {const select=document.querySelector('select[aria-label="Reasoning"]');select.value='high';
      select.dispatchEvent(new Event('change',{bubbles:true}));})()`);
    assert.equal(await waitFor(`document.querySelector('select[aria-label="Reasoning"]').value === 'high'`), "ready");
    // Enter still owns Send even while cancellation is visually primary. Excluded keys stay editor input.
    for (const extra of ["shiftKey:true", "altKey:true", "metaKey:true", "repeat:true", "isComposing:true", "keyCode:229"]) {
      assert.equal(await evaluate(`(() => {const event=new KeyboardEvent('keydown',{key:'Enter',bubbles:true,cancelable:true,${extra}});
        document.querySelector('#session-prompt').dispatchEvent(event);return event.defaultPrevented;})()`), false);
    }
    assert.equal(await evaluate(`window.fixture.sendCalls.length`), 0);
    // The existing expanded modal owns Enter; it must close without dispatching either primary action.
    await evaluate(`document.querySelector('#expand-session-prompt').click()`);
    assert.equal(await waitFor(`!!document.querySelector('dialog[open]')`), "ready");
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", text: "\r", windowsVirtualKeyCode: 13 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    assert.equal(await waitFor(`!document.querySelector('dialog[open]')`), "ready");
    assert.equal(await evaluate(`window.fixture.sendCalls.length`), 0);
    assert.equal(await evaluate(`window.fixture.abortCalls.length`), 4);
    await evaluate(`window.recoverySend=document.querySelector('.send-button');window.recoveryEditor=document.querySelector('#session-prompt');
      window.recoveryCancel=document.querySelector('.cancel-run-button');document.querySelector('#session-prompt').focus();window.fixture.holdSend()`);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", text: "\r", windowsVirtualKeyCode: 13 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    assert.equal(await waitFor(`document.querySelector('.composer-toolbar .send-button')?.textContent === 'Retry exact request'`), "ready");
    assert.equal(await evaluate(`document.querySelector('.composer-toolbar .primary-button')?.textContent`), "Retry exact request",
      "original Send recovery takes primary precedence over cancellation");
    assert.equal(await evaluate(`document.querySelector('.cancel-run-button')?.classList.contains('primary-button')`), false);
    assert.equal(await evaluate(`window.recoverySend===document.querySelector('.send-button') && window.recoveryEditor===document.querySelector('#session-prompt') &&
      window.recoveryCancel===document.querySelector('.cancel-run-button')`), true);
    assert.equal(await evaluate(`window.fixture.sendCalls.length`), 1);
    assert.equal(await evaluate(`document.querySelector('.send-button').disabled`), true, "primary recovery stays disabled during the original waiter");
    await evaluate(`document.querySelector('.send-button').click()`);
    assert.equal(await evaluate(`window.fixture.sendCalls.length`), 1);
    await evaluate(`window.fixture.settleSend()`);
    assert.equal(await waitFor(`!document.querySelector('.send-button').disabled`), "ready");
    assert.equal(await evaluate(`JSON.stringify(window.fixture.sendCalls[0].selection)`),
      JSON.stringify({providerKey:'fixture-provider',agentPromptId:'default',modelId:'fixture-model',reasoningEffort:'high'}));
    await evaluate(`document.querySelector('.send-button').focus()`);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: " ", code: "Space", text: " ", windowsVirtualKeyCode: 32 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: " ", code: "Space", windowsVirtualKeyCode: 32 });
    assert.equal(await waitFor(`window.fixture.sendCalls.length === 2`), "ready");
    assert.equal(await evaluate(`window.fixture.sendCalls[0]===window.fixture.sendCalls[1] &&
      window.fixture.sendCalls[1].text==='Pending exact Send text'`), true, "retry preserves immutable text, key and selection");
    for (const width of [390,1120]) {
      await command("Emulation.setDeviceMetricsOverride", {width,height:800,deviceScaleFactor:1,mobile:false});
      for (const theme of ['dark','light']) {
        await evaluate(`document.documentElement.dataset.theme='${theme}'`);
        const layout=await sample();
        assert.ok(layout.pageWidth<=layout.viewWidth+2 && layout.panelScrollWidth<=layout.panelWidth+2 && layout.toolbar<=(width===390 ? 135 : 80),
          `${width}/${theme} simultaneous exact recovery must wrap without clipping: ${JSON.stringify(layout)}`);
        assert.equal(await evaluate(`document.querySelector('.cancel-run-button')?.textContent`), 'Retry exact cancellation');
      }
    }
    assert.equal(await evaluate(`document.querySelector('.prompt-input').value`), "Pending exact Send text");
    await evaluate(`window.fixtureReceiptRows=[{kind:'Send',clientRequestId:'older-send',sessionId:'fixture-session',
      operationId:'55555555-5555-4555-8555-555555555555',targetOperationId:null,state:'pending',outcome:null,code:null,runId:null,queueInsertion:null}];
      [...document.querySelectorAll('.owned-session button')].find(b=>b.textContent==='Refresh receipts').click()`);
    assert.equal(await waitFor(`[...document.querySelectorAll('button')].some(b=>b.textContent==='Abort original Send operation')`), "ready");
    await evaluate(`[...document.querySelectorAll('button')].find(b=>b.textContent==='Abort original Send operation').click()`);
    assert.equal(await evaluate(`window.fixture.submissionAbortCalls[0]?.targetOperationId`), '55555555-5555-4555-8555-555555555555');
    assert.equal(await evaluate(`window.fixture.abortCalls.length`), 4, "operation-targeted Abort must not dispatch run cancellation");
    assert.equal(await evaluate(`document.querySelector('.composer-toolbar .primary-button')?.textContent`), 'Retry exact request');
    assert.equal(await evaluate(`document.querySelector('.prompt-input').value`), 'Pending exact Send text');
    assert.equal(await evaluate(`!!document.querySelector('.composer-toolbar [aria-label^="Retry exact cancellation"]')`), true,
      "pending exact Send recovery does not disappear when observed-run cancellation is available");
    assert.equal(await evaluate(`document.querySelector('.composer-toolbar [aria-label="Steer current composer to observed run"]')?.disabled`), true,
      "the retained Send text shown in a disabled editor is not an editable steering draft");
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(queueButton)})?.disabled`), true,
      "pending Send recovery is never editable queue text");
    const queueCount = await evaluate(`window.fixture.queueCalls.length`);
    await evaluate(`document.querySelector(${JSON.stringify(queueButton)})?.click()`);
    assert.equal(await evaluate(`window.fixture.queueCalls.length`), queueCount);
    await evaluate(`window.fixture.observe(null, 13); document.querySelector('#refresh-session-context').click()`);
    assert.equal(await waitFor(`!!document.querySelector('.composer-toolbar [aria-label="Compact observed idle attachment"]')`), "ready");
    assert.equal(await evaluate(`document.querySelector('.composer-toolbar .send-button')?.textContent`), "Retry exact request",
      "eligible compaction cannot hide exact Send recovery");
    assert.equal(await evaluate(`document.querySelector('.prompt-input').value`), "Pending exact Send text");
    await evaluate(`window.fixture.observe('run-three', 14, '22222222-2222-4222-8222-222222222222'); document.querySelector('#refresh-session-context').click()`);
    assert.equal(await waitFor(`[...document.querySelectorAll('.owned-session [role="alert"]')].some(el => el.textContent.includes('Reload required'))`), "ready");
    assert.equal(await evaluate(`document.querySelector('.composer-toolbar [aria-label="Cancel observed run"]') === null`), true,
      "stale runtime identity cannot authorize another observed-run action");
    assert.equal(await evaluate(`document.querySelector('.composer-toolbar [aria-label="Compact observed idle attachment"]') === null`), true,
      "stale runtime identity cannot authorize an idle compaction attempt");
    await evaluate(`document.querySelector(${JSON.stringify(queueButton)})?.click()`);
    assert.equal(await evaluate(`window.fixture.queueCalls.length`), queueCount, "stale runtime/capability cannot dispatch queueing");
    const steerCount = await evaluate(`window.fixture.steerCalls.length`);
    await evaluate(`document.querySelector('.composer-toolbar [aria-label="Steer current composer to observed run"]')?.click()`);
    assert.equal(await evaluate(`window.fixture.steerCalls.length`), steerCount,
      "stale runtime/host authority cannot dispatch toolbar steering");
    assert.equal(await compactKey('.project-rename input'), false);
    assert.equal(await compactKey('.composer-toolbar .send-button'), false);
    assert.equal(await evaluate(`document.querySelector('.composer-toolbar .send-button')?.disabled`), true);
    assert.equal(await evaluate(`document.querySelector('.cancel-run-button')?.disabled`), true);
    await evaluate(`document.querySelector('.cancel-run-button').click();document.querySelector('.send-button').click()`);
    assert.equal(await evaluate(`window.fixture.sendCalls.length`), 2);
    assert.equal(await evaluate(`window.fixture.abortCalls.length`), 4);
    await evaluate(`document.querySelector('.owned-session').remove()`);
    assert.equal(await evaluate(`!!document.querySelector('#open-provider-configuration')`), false,
      "catalog-only composer has no redundant visible configuration launcher");
    assert.equal(await evaluate(`!!document.querySelector('.composer-toolbar [aria-label="Steer current composer to observed run"]')`), false,
      "catalog-only view never exposes owned steering");
    assert.equal(await evaluate(`!!document.querySelector(${JSON.stringify(queueButton)})`), false);
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
