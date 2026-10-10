import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable } from "./browserTarget";

type Box = { left: number; top: number; right: number; bottom: number; width: number; height: number };
type Layout = { pane: Box; notes: Box; window: Box; timestamp: Box; tasks: Box | null; collapsed: string; stored: string | null };
const inspect = `(() => {
  const box = element => { const {left,top,right,bottom,width,height}=element.getBoundingClientRect();return {left,top,right,bottom,width,height}; };
  const notes=document.querySelector('.session-notes-overlay');
  return { pane:box(document.querySelector('.session-timeline-area')), window:box(document.querySelector('.session-notes-window')),
    notes:box(notes.querySelector(notes.dataset.collapsed==='true'?'.session-notes-toggle':'.notes-pane')),
    timestamp:box(document.querySelector('.message-actions time')), collapsed:notes.dataset.collapsed,
    tasks:document.querySelector('.work-cards')?box(document.querySelector('.work-cards')):null, stored:overlays.stored() };
})()`;
const overlaps = (a: Box, b: Box) => a.left < b.right - 1 && a.right > b.left + 1 && a.top < b.bottom - 1 && a.bottom > b.top + 1;
function readable(layout: Layout) {
  assert.ok(layout.tasks, "The proposal is still shown");
  assert.equal(overlaps(layout.notes, layout.tasks), false, `Notes and tasks must not overlap: ${JSON.stringify(layout)}`);
  for (const [name, box] of [["Notes", layout.notes], ["tasks", layout.tasks]] as const) {
    assert.ok(box.width > 0 && box.height > 0, `${name} is visible`);
    assert.ok(box.left >= layout.pane.left - 1 && box.right <= layout.pane.right + 1 && box.top >= layout.pane.top - 1 && box.bottom <= layout.pane.bottom + 1,
      `${name} stays inside its pane: ${JSON.stringify(layout)}`);
  }
}

test("mounted production Notes and proposal overlays cooperate without replacing their owners", { skip: !browserExecutable, timeout: 90_000 }, async t => {
  const root = await mkdtemp(join(tmpdir(), "codealta-session-overlays-"));
  let browser: ReturnType<typeof spawn> | undefined, socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./sessionOverlays.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    const styles = await Promise.all(["../node_modules/@blueprintjs/core/lib/css/blueprint.css", "./style.css", "./workItems/workItems.css"].map(path => readFile(new URL(path, import.meta.url), "utf8")));
    await writeFile(join(root, "style.css"), styles.join("\n"));
    await writeFile(join(root, "fixture.html"), '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"></head><body><div id="app"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(browserExecutable!, [...browserBaseArgs, `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
    let port = "";
    for (let attempt = 0; attempt < 100 && !port; attempt++) {
      try { port = (await readFile(join(root, "profile", "DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; }
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
      const result = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails)); return result.result?.value;
    };
    const wait = async (condition: string) => assert.equal(await evaluate(`new Promise(resolve=>{const end=Date.now()+7000;function check(){if(${condition})resolve(true);else if(Date.now()>end)resolve(document.body.innerText.slice(-1200));else setTimeout(check,20)}check()})`), true, condition);
    // ResizeObserver and react-rnd both commit geometry on animation frames. Wait for stable measured
    // rectangles, rather than sleeping or providing the desired geometry in the fixture.
    const layout = async () => {
      await evaluate(`new Promise((resolve,reject)=>{let previous='',stable=0;const end=Date.now()+7000;function check(){const value=JSON.stringify(${inspect});if(value===previous)stable++;else stable=0;previous=value;if(stable===4)resolve();else if(Date.now()>end)reject(Error('layout did not settle'));else requestAnimationFrame(check)}requestAnimationFrame(check)})`);
      return await evaluate(inspect) as Layout;
    };
    const reset = async (options: object) => {
      await evaluate(`overlays.reset(${JSON.stringify(options)})`);
      await wait("!!document.querySelector('.session-notes-window') && !document.querySelector('.notes-pane [role=status]')");
      return layout();
    };
    const click = async (selector: string) => { await evaluate(`document.querySelector(${JSON.stringify(selector)}).click()`); return layout(); };
    await command("Page.enable");
    await command("Emulation.setDeviceMetricsOverride", { width: 1280, height: 800, deviceScaleFactor: 1, mobile: false });
    await command("Emulation.setEmulatedMedia", { features: [{ name: "prefers-reduced-motion", value: "reduce" }] });
    await command("Page.navigate", { url: pathToFileURL(join(root, "fixture.html")).href });
    await wait("!!window.overlays && !!document.querySelector('.session-notes-window')");

    for (const width of [1080, 480, 320]) {
      await t.test(`default and restored Notes chip clears the first timestamp at ${width}px`, async () => {
        let value = await reset({ width });
        assert.equal(value.collapsed, "true");
        assert.equal(overlaps(value.notes, value.timestamp), false, JSON.stringify(value));
        const stored = { x: 18, y: 72, width: 260, height: 190 };
        value = await reset({ width, stored });
        assert.equal(overlaps(value.notes, value.timestamp), false);
        assert.equal(value.stored, JSON.stringify(stored));
        assert.equal(Math.round(value.window.top - value.pane.top), stored.y + 8, "Restoring keeps the dragged top offset");
        await click(".session-notes-toggle");
        value = await click('[aria-label="Restore default size and position"]');
        assert.equal(value.collapsed, "false", "Resetting geometry must not collapse Notes");
        value = await click('[aria-label="Collapse notes"]');
        assert.equal(value.stored, null);
        assert.equal(overlaps(value.notes, value.timestamp), false, "Restoring defaults also clears the timestamp");
      });
      for (const restored of [false, true]) await t.test(`Notes and proposals stay readable at ${width}px (${restored ? "restored" : "default"})`, async () => {
        const stored = restored ? { x: 18, y: 72, width: 260, height: 190 } : undefined;
        const before = await reset({ width, stored, notes: "## Current work\n\n- [ ] Keep this checklist readable.\n- [ ] Keep the proposal readable too." });
        assert.equal(before.collapsed, "false");
        await evaluate("window.notesOwner=document.querySelector('.notes-pane');window.notesReads=overlays.reads();overlays.proposals(true)");
        let value = await layout();
        readable(value);
        if (width === 1080) assert.ok(value.tasks!.right <= value.notes.left, "Wide panes place proposals to the left of Notes");
        else assert.equal(await evaluate("(() => { const card=document.querySelector('.work-card');card.scrollTop=1;const scrolls=card.scrollTop===1;card.scrollTop=0;return scrolls; })()"), true,
          "The narrow proposal strip scrolls instead of clipping its actions");
        assert.equal(value.collapsed, "false");
        assert.equal(value.stored, before.stored, "Automatic arrangement must not save over dragged Notes geometry");
        assert.equal(await evaluate("notesOwner===document.querySelector('.notes-pane') && notesReads===overlays.reads()"), true, "Geometry does not remount/re-read Notes");
        value = await click('[aria-label="Put away"]'); readable(value);
        value = await click('[aria-label="Collapse notes"]'); readable(value);
        assert.equal(await evaluate("!!document.querySelector('.work-cards-chip')"), true, "Collapsing Notes keeps proposals collapsed");
        value = await click(".work-cards-chip"); readable(value);
        assert.equal(value.collapsed, "true", "Expanding proposals keeps Notes collapsed");
        value = await click(".session-notes-toggle"); readable(value);
        await evaluate("overlays.proposals(false)"); value = await layout();
        assert.deepEqual(value.window, before.window, "Removing proposals restores the original Notes geometry");
        assert.equal(value.stored, before.stored);
      });
    }
    await t.test("resizing preserves a tall saved Notes placement and moving Notes left gives proposals the right side", async () => {
      const before = await reset({ width: 1080, stored: { x: 18, y: 72, width: 260, height: 440 }, notes: "Keep these notes", proposals: true });
      readable(before);
      await evaluate("overlays.resize(320)");
      const narrow = await layout(); readable(narrow);
      assert.equal(narrow.stored, before.stored, "Temporary clamping does not overwrite a tall saved window");
      await evaluate("overlays.resize(1080)");
      assert.deepEqual((await layout()).window, before.window);
      const left = await reset({ width: 1080, stored: { x: 680, y: 72, width: 260, height: 190 }, notes: "Moved left", proposals: true });
      readable(left);
      assert.ok(left.tasks!.left >= left.notes.right, "A restored left-side Notes window is not moved for proposals");
      assert.equal(Math.round(left.window.left - left.pane.left), 126);
    });
    await evaluate("overlays.unmount()");
  } finally {
    socket?.close();
    if (browser && browser.exitCode === null) {
      const exited = new Promise<void>(resolve => browser!.once("exit", () => resolve()));
      browser.kill(); await exited;
    }
    await rm(root, { recursive: true, force: true }).catch(() => { /* Edge may still be releasing its disposable profile. */ });
  }
});
