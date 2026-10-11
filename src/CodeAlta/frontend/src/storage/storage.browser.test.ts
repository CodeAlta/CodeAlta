import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable } from "../browserTarget";

test("Storage edits valid durations only and measures independently without cleanup actions", { skip: !browserExecutable, timeout: 120_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-storage-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./storage.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife",
      plugins: [{ name: "isolated-storage", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("../demo-api.ts", import.meta.url)) }));
      } }] });
    await writeFile(join(root, "style.css"), await readFile(new URL("../../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8")
      + await readFile(new URL("../style.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"><link rel="stylesheet" href="fixture.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(browserExecutable!, [...browserBaseArgs, `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
    let port = "";
    for (let attempt = 0; attempt < 100 && !port; attempt++) {
      try { port = (await readFile(join(root, "profile", "DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; }
      catch { await new Promise(resolve => setTimeout(resolve, 50)); }
    }
    assert.match(port, /^\d+$/);
    const pages: { type?: string; webSocketDebuggerUrl?: string }[] = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10_000) })).json();
    socket = new WebSocket(pages.find(value => value.type === "page")!.webSocketDebuggerUrl!);
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
      const result = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails));
      return result.result?.value;
    };
    const act = (script: string) => evaluate(`(async () => { ${script}; for (let turn = 0; turn < 4; turn++) await new Promise(resolve => requestAnimationFrame(() => setTimeout(resolve, 0))); return true; })()`);
    const until = async (expression: string) => assert.equal(await evaluate(`new Promise(resolve => { const end = Date.now() + 7000; (function check() {
      if (${expression}) resolve(true); else if (Date.now() > end) resolve(false); else setTimeout(check, 20); })(); })`), true, expression);
    const f = "window.storageFixture", state = `${f}.state`, amount = "document.querySelector('#storage-retention-amount')";
    const mode = "document.querySelector('#storage-retention-mode')", unit = "document.querySelector('select[aria-label=\"Retention unit\"]')";
    const button = (text: string) => `[...document.querySelectorAll('button')].find(button => button.textContent === ${JSON.stringify(text)})`;
    const select = (target: string, value: string) => act(`${target}.value = ${JSON.stringify(value)}; ${target}.dispatchEvent(new Event('change', { bubbles: true }))`);
    const input = (value: string) => act(`${amount}.focus(); Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(${amount}, ${JSON.stringify(value)}); ${amount}.dispatchEvent(new Event('input', { bubbles: true }))`);
    const key = async (value: string, code: number) => { await command("Input.dispatchKeyEvent", { type: "keyDown", key: value, code: value, windowsVirtualKeyCode: code }); await command("Input.dispatchKeyEvent", { type: "keyUp", key: value, code: value, windowsVirtualKeyCode: code }); await act(""); };
    const snapshot = (bytes: number, partial = false) => ({ status: "ok", snapshot: { rootPath: "/fixture/.alta", totalBytes: String(bytes), completedAt: "2026-10-10T10:00:00Z",
      categories: [{ id: "sessions", bytes: String(Math.floor(bytes * .75)), fileCount: "3" }, { id: "configuration", bytes: String(bytes - Math.floor(bytes * .75)), fileCount: "1" }], skippedLinks: partial ? "2" : "0", skippedEntries: partial ? "1" : "0", isPartial: partial } });
    await command("Page.enable");
    await command("Emulation.setDeviceMetricsOverride", { width: 1000, height: 1100, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    await until(`${amount} && ${state}.scans.length > 0`);
    assert.deepEqual(await evaluate(`[document.querySelector('#storage-scratchpad').checked, ${amount}.value, ${unit}.value, ${mode}.value, ${state}.saves.length]`), [true, "3", "months", "duration", 0]);
    assert.equal(await evaluate(`${state}.scans.at(-1).timeoutMilliseconds`), 600000);
    assert.ok(await evaluate("document.body.textContent.includes('Measuring file sizes…')"));
    // In the content area of a 900 x 650 window, every cleanup control is visible without scrolling.
    assert.equal(await evaluate(`(() => { const box = document.querySelector('.settings-dialog-content').getBoundingClientRect();
      return ['#storage-scratchpad', '#storage-retention-mode', '#storage-retention-amount', 'select[aria-label="Retention unit"]']
        .every(selector => { const rect = document.querySelector(selector).getBoundingClientRect(); return rect.top >= box.top && rect.bottom <= box.bottom && rect.right <= box.right; }); })()`), true);
    assert.equal(await evaluate("document.querySelector('.storage-cleanup').compareDocumentPosition(document.querySelector('.storage-usage')) & Node.DOCUMENT_POSITION_FOLLOWING"), 4, "cleanup comes before the sizes");

    // Choosing Never within the group must preserve a valid unblurred draft, not save the stale shorter duration.
    await input("6"); await act(`${mode}.focus()`); await select(mode, "never");
    assert.deepEqual(await evaluate(`[${state}.settings.retentionEnabled, ${state}.settings.retentionAmount, ${amount}.value]`), [false, 6, "6"]);
    await select(mode, "duration");
    assert.deepEqual(await evaluate(`[${state}.settings.retentionEnabled, ${state}.settings.retentionAmount, ${amount}.value]`), [true, 6, "6"]);
    await input("1"); await key("Enter", 13);

    // Actual Blueprint increment/decrement buttons and native keyboard events.
    const step = (index: number) => act(`document.querySelectorAll('.storage-retention .bp6-numeric-input button')[${index}].dispatchEvent(new MouseEvent('mousedown', { bubbles: true })); document.dispatchEvent(new MouseEvent('mouseup', { bubbles: true }))`);
    await step(0);
    assert.equal(await evaluate(`${state}.settings.retentionAmount`), 2);
    await step(1);
    assert.equal(await evaluate(`${state}.settings.retentionAmount`), 1);
    await act(`${amount}.focus()`); await key("ArrowUp", 38);
    assert.equal(await evaluate(`${state}.settings.retentionAmount`), 2);
    await key("ArrowDown", 40);
    assert.equal(await evaluate(`${state}.settings.retentionAmount`), 1);
    await input("45");
    assert.equal(await evaluate(`${state}.settings.retentionAmount`), 1, "typing does not persist");
    await key("Enter", 13);
    assert.equal(await evaluate(`${state}.settings.retentionAmount`), 45);
    for (const value of ["days", "weeks", "years", "months"]) {
      await select(unit, value); assert.equal(await evaluate(`${state}.settings.retentionUnit`), value);
    }
    await input("46"); await act(`${unit}.focus(); ${unit}.blur()`);
    assert.equal(await evaluate(`${state}.settings.retentionAmount`), 46);

    const before = await evaluate(`${state}.saves.length`);
    for (const invalid of ["", "0", "-1", "1.5", "1.", "1e", "2147483648"]) {
      await input(invalid); await key("Enter", 13); await act(`${amount}.blur()`);
      assert.equal(await evaluate(`${state}.saves.length`), before, invalid);
      assert.equal(await evaluate(`${amount}.getAttribute('aria-invalid')`), "true");
      await step(0); await act(`${amount}.focus()`); await key("ArrowDown", 40);
      assert.equal(await evaluate(`${state}.saves.length`), before, "stepping never rounds an invalid draft into a shorter policy");
    }
    await select(unit, "days");
    assert.equal(await evaluate(`${state}.saves.length`), before, "unit changes never save invalid drafts");
    await select(mode, "never");
    assert.deepEqual(await evaluate(`[${state}.settings.retentionEnabled, ${state}.settings.retentionAmount, ${state}.settings.retentionUnit, ${amount}.disabled, ${unit}.disabled]`), [false, 46, "months", true, true]);
    assert.equal(await evaluate(`${amount}.value`), "46", "disabling with an invalid draft visibly restores the saved duration");
    await select(mode, "duration");
    assert.deepEqual(await evaluate(`[${state}.settings.retentionAmount, ${state}.settings.retentionUnit, ${amount}.value]`), [46, "months", "46"]);
    await input("47"); await key("Enter", 13);
    await act("document.querySelector('#storage-scratchpad').click()");
    assert.equal(await evaluate(`${state}.settings.clearScratchpadOnStartup`), false);

    // Write failure is explicit, leaves host preferences unchanged and does not retry an invalid draft.
    await act(`${state}.saveStatus = 'write_failed'`); await input("48"); await key("Enter", 13);
    assert.equal(await evaluate(`${state}.settings.retentionAmount`), 47);
    assert.ok(await evaluate("document.body.textContent.includes('Storage settings could not be saved.')"));
    await act(`${state}.saveStatus = 'ok'`);

    // Cancel/supersede cannot publish late results; closing Settings aborts only its own measurement.
    await act(`${button("Cancel scan")}.click()`);
    assert.equal(await evaluate(`${state}.scans.at(-1).signal.aborted`), true);
    await act(`${state}.scans.at(-1).resolve(${JSON.stringify(snapshot(999))})`);
    assert.equal(await evaluate("document.querySelector('.storage-table') === null"), true);
    await act(`${button("Refresh")}.click()`);
    await act(`${button("Refresh")}.click()`);
    assert.equal(await evaluate(`${state}.scans.at(-2).signal.aborted`), true);
    await act(`${state}.scans.at(-1).resolve(${JSON.stringify(snapshot(400, true))})`);
    await act(`${state}.scans.at(-2).resolve(${JSON.stringify(snapshot(999))})`);
    assert.equal(await evaluate("document.querySelector('.storage-total').textContent"), "400 B");
    assert.ok(await evaluate("document.body.textContent.includes('2 links and 1 unavailable')"));
    assert.equal(await evaluate("document.querySelectorAll('.storage-table tbody tr').length"), 10);
    assert.equal(await evaluate("document.querySelector('.storage-table tbody tr td:last-child').textContent"), "75%");
    // Rows are sorted by size; each keeps the color of its category, and categories without bytes stay listed.
    assert.deepEqual(await evaluate("[...document.querySelectorAll('.storage-table tbody th')].slice(0, 3).map(cell => cell.textContent)"), ["Sessions and attachments", "Configuration and credentials", "Scratchpads"]);
    assert.equal(await evaluate("document.querySelectorAll('.storage-table tbody tr')[1].querySelector('.storage-swatch').style.getPropertyValue('--storage-swatch')"), "var(--chart-5)");
    assert.equal(await evaluate("[...document.querySelectorAll('.storage-table button')].length"), 0);
    assert.ok(await evaluate("document.body.textContent.includes('Scratchpad cleanup was partial.')"));
    assert.ok(await evaluate("[...document.querySelectorAll('.storage-maintenance li')].some(item => item.textContent.includes('locked scratchpad item'))"));
    // Session retention has a line of its own, with known reasons only and never host text.
    const retentionLine = "document.querySelector('.storage-session-cleanup')?.textContent ?? null";
    assert.equal(await evaluate(retentionLine), "Session cleanup is not available yet.");
    const retention = (value: object) => act(`${f}.setRetention(${JSON.stringify({ examined: 0, deleted: 0, skipped: 0, reason: null, ...value })})`);
    await retention({ state: "skipped", reason: "archive_unavailable" });
    assert.equal(await evaluate(retentionLine), "Session cleanup was skipped.Saved Statistics are unavailable.");
    await retention({ state: "skipped", reason: "disabled" });
    assert.equal(await evaluate(retentionLine), "Session cleanup is off.");
    await retention({ state: "skipped", reason: "invalid_cutoff" });
    assert.equal(await evaluate(retentionLine), "Session cleanup was skipped.Check the retention duration.");
    await retention({ state: "skipped", reason: "C:\\\\secret\\\\path failed" });
    assert.equal(await evaluate(retentionLine), "Session cleanup was skipped.");
    await retention({ state: "completed", examined: 40, deleted: 1200, skipped: 3 });
    assert.equal(await evaluate(retentionLine), "Session cleanup completed: 1,200 removed.");
    await retention({ state: "partial", examined: 40, deleted: 2, skipped: 5, reason: "archive_unavailable" });
    assert.equal(await evaluate(retentionLine), "Session cleanup was partial: 2 removed, 5 kept.");
    await retention({ state: "mystery", reason: "anything" });
    assert.equal(await evaluate(retentionLine), "Session cleanup status is unavailable.");
    await retention({ state: "canceled" });
    assert.equal(await evaluate(retentionLine), "Session cleanup was canceled.");
    // The scratchpad line never claims a partial cleanup it cannot know of.
    const scratchpadLine = "document.querySelector('.storage-maintenance:not(.storage-session-cleanup) .storage-maintenance-title').textContent";
    for (const [value, text] of [["canceled", "Scratchpad cleanup was canceled."], ["unavailable", "Scratchpad cleanup status is unavailable."], ["mystery", "Scratchpad cleanup status is unavailable."]]) {
      await act(`${f}.setMaintenance({ state: '${value}', reasons: [] })`);
      assert.equal(await evaluate(scratchpadLine), text);
    }
    await act(`${f}.setMaintenance({ state: 'partial', reasons: ['Fixture: locked scratchpad item was left in place.'] })`);
    await retention({ state: "skipped", reason: "protection_unavailable" });
    await until("document.querySelector('.storage-distribution svg') !== null");
    // The table is the legend: the chart draws no second one.
    assert.equal(await evaluate("document.querySelector('.storage-distribution .chart-legend')"), null);
    // Explanations are behind the help buttons, which the keyboard reaches.
    assert.equal(await evaluate("document.querySelectorAll('.storage-help-button[aria-label]').length"), 2);
    await act("document.querySelector('.storage-help-button').click()");
    await until("document.querySelector('.storage-help') !== null");
    for (const dark of [true, false]) {
      await act(`document.documentElement.classList.toggle('bp6-dark', ${dark}); document.documentElement.dataset.theme = '${dark ? "dark" : "light"}'`);
      for (const width of [1080, 518, 360])
        for (const locale of ["en", "es", "fr", "de", "ja", "zh-CN"]) {
          await act(`${f}.setSize({ width: ${width}, height: 600 }); ${f}.setLocale('${locale}')`);
          assert.equal(await evaluate(`[document.querySelector('.settings-dialog-content'), document.querySelector('.storage-settings'), ...document.querySelectorAll('.storage-card')]
            .every(element => element.scrollWidth <= element.clientWidth)`), true, `${width}px ${locale} page has no horizontal overflow`);
          assert.ok(await evaluate("document.querySelector('.storage-usage').getBoundingClientRect().top >= document.querySelector('.storage-cleanup').getBoundingClientRect().bottom"), `${width}px: the sizes are below the cleanup`);
          // Sizes and shares end under their headings.
          assert.ok(await evaluate("[...document.querySelectorAll('.storage-table tr')].every(row => [...row.children].slice(1).every(cell => getComputedStyle(cell).textAlign === 'right'))"), `${width}px: numbers are right-aligned`);
          // The donut is beside its legend, the table, when there is room, and below it on a narrow page.
          const donut = "document.querySelector('.storage-donut').getBoundingClientRect()", table = "document.querySelector('.storage-table').getBoundingClientRect()";
          assert.ok(await evaluate(width >= 1000 ? `${donut}.left >= ${table}.right && ${donut}.width >= 200` : `${donut}.top >= ${table}.bottom`), `${width}px: the donut is ${width >= 1000 ? "beside" : "below"} the table`);
        }
    }
    await act(`${f}.setLocale('en'); ${f}.setSize({ width: 518, height: 484 })`);
    await act(`${button("Refresh")}.click(); ${state}.scans.at(-1).resolve({ status: 'read_failed', snapshot: null })`);
    assert.ok(await evaluate("document.body.textContent.includes('Storage could not be measured.')"));
    await act(`${button("Refresh")}.click(); ${state}.scans.at(-1).resolve(${JSON.stringify(snapshot(0))})`);
    assert.ok(await evaluate("document.body.textContent.includes('No file bytes were measured.')"));
    assert.equal(await evaluate("document.querySelectorAll('.storage-table tbody tr').length"), 10);
    await act(`${button("Refresh")}.click(); ${f}.setShown(false)`);
    assert.equal(await evaluate(`${state}.scans.at(-1).signal.aborted`), true);
    await act(`${state}.readStatus = 'invalid'; ${f}.setShown(true)`);
    assert.ok(await evaluate("document.body.textContent.includes('fixture invalid config')"));
    assert.equal(await evaluate(`${amount} === null`), true, "invalid persisted settings never become destructive defaults");
    await act(`${f}.setEpoch(null)`);
    assert.ok(await evaluate("document.body.textContent.includes('Storage settings unavailable')"));
  } finally {
    socket?.close(); browser?.kill();
    if (browser && browser.exitCode === null) await new Promise(resolve => browser!.once("exit", resolve));
    await rm(root, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
  }
});
