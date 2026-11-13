import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, waitForBrowserPort } from "./browserTarget";
import { testLimit, testLimitRanOut } from "./testTime";

type Box = { left: number; top: number; right: number; bottom: number; width: number; height: number };
type Layout = { pane: Box; area: Box; notes: Box; window: Box; timestamp: Box; tasks: Box | null; collapsed: string; stored: string | null };
const inspect = `(() => {
  const box = element => { const {left,top,right,bottom,width,height}=element.getBoundingClientRect();return {left,top,right,bottom,width,height}; };
  const notes=document.querySelector('.session-notes-overlay');
  return { pane:box(document.querySelector('.session-timeline-area')), area:box(notes), window:box(document.querySelector('.session-notes-window')),
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

test("mounted production Notes and proposal overlays cooperate without replacing their owners", { skip: !browserExecutable, timeout: testLimit(90_000) }, async t => {
  const root = await mkdtemp(join(tmpdir(), "codealta-session-overlays-"));
  let browser: ReturnType<typeof spawn> | undefined, socket: WebSocket | undefined;
  try {
    // The cards read the tiers of models through the host: the fixture has the inert services of the demo.
    await build({ entryPoints: [fileURLToPath(new URL("./sessionOverlays.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife",
      plugins: [{ name: "inert-host", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./demo-api.ts", import.meta.url)) }));
      } }] });
    const styles = await Promise.all(["../node_modules/@blueprintjs/core/lib/css/blueprint.css", "./style.css", "./workItems/workItems.css"].map(path => readFile(new URL(path, import.meta.url), "utf8")));
    await writeFile(join(root, "style.css"), styles.join("\n"));
    await writeFile(join(root, "fixture.html"), '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"></head><body><div id="app"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(browserExecutable!, [...browserBaseArgs, `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(root, "profile"), browser);
    const pages: { type?: string; webSocketDebuggerUrl?: string }[] = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(testLimit(10_000)) })).json();
    socket = new WebSocket(pages.find(page => page.type === "page")!.webSocketDebuggerUrl!);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", () => reject(Error("browser unavailable")), { once: true }); });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<{ result?: { value?: unknown }; exceptionDetails?: unknown }>((resolve, reject) => {
      const id = ++sequence, timer = setTimeout(() => reject(Error(testLimitRanOut(testLimit(12_000), `the answer to ${method}`))), testLimit(12_000));
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
    const wait = async (condition: string) => assert.equal(await evaluate(`new Promise(resolve=>{const end=Date.now()+${testLimit(7000)};function check(){if(${condition})resolve(true);else if(Date.now()>end)resolve(document.body.innerText.slice(-1200));else setTimeout(check,20)}check()})`), true, condition);
    // ResizeObserver and react-rnd both commit geometry on animation frames. Wait for stable measured
    // rectangles, rather than sleeping or providing the desired geometry in the fixture.
    const layout = async () => {
      await evaluate(`new Promise((resolve,reject)=>{let previous='',stable=0;const end=Date.now()+${testLimit(7000)};function check(){const value=JSON.stringify(${inspect});if(value===previous)stable++;else stable=0;previous=value;if(stable===4)resolve();else if(Date.now()>end)reject(Error('layout did not settle'));else requestAnimationFrame(check)}requestAnimationFrame(check)})`);
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
    // The first message of a session is a prompt: its heading has Fork and Rewind before the time.
    assert.equal(await evaluate("document.querySelectorAll('.message-actions .branch-from-message').length"), 2);

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
        // Narrow, the card goes above or below Notes when it has room there, else in a strip at the bottom; cut short, it scrolls.
        else assert.equal(await evaluate(`(() => { const card=document.querySelector('.work-card');
          if (card.scrollHeight <= card.clientHeight) return true;
          card.scrollTop=1;const scrolls=card.scrollTop===1;card.scrollTop=0;return scrolls; })()`), true,
          "A narrow proposal card shows its actions whole or scrolls to them");
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
    // A real pointer drag, as react-rnd listens to the mouse events of the document.
    const drag = async (from: { x: number; y: number }, to: { x: number; y: number }) => {
      await command("Input.dispatchMouseEvent", { type: "mouseMoved", x: from.x, y: from.y });
      await command("Input.dispatchMouseEvent", { type: "mousePressed", x: from.x, y: from.y, button: "left", buttons: 1, clickCount: 1 });
      for (let step = 1; step <= 8; step++)
        await command("Input.dispatchMouseEvent", { type: "mouseMoved", button: "left", buttons: 1,
          x: from.x + (to.x - from.x) * step / 8, y: from.y + (to.y - from.y) * step / 8 });
      // Chromium delivers the moves of a frame together: a release sent before the next frame can come before the last
      // move on a busy machine, and the tab stops one step short.
      await evaluate("new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))");
      await command("Input.dispatchMouseEvent", { type: "mouseReleased", x: to.x, y: to.y, button: "left", buttons: 0, clickCount: 1 });
      return layout();
    };
    const center = (box: Box) => ({ x: (box.left + box.right) / 2, y: (box.top + box.bottom) / 2 });
    const inside = (box: Box, area: Box) => box.left >= area.left - 1 && box.right <= area.right + 1 && box.top >= area.top - 1 && box.bottom <= area.bottom + 1;
    await t.test("the collapsed Notes tab drags to every edge of its pane and expands back inside it", async () => {
      let value = await reset({ width: 1080 });
      assert.equal(value.collapsed, "true");
      assert.ok(Math.abs(value.window.width - value.notes.width) <= 1 && Math.abs(value.window.height - value.notes.height) <= 1,
        `Collapsed, the window is the size of its tab: ${JSON.stringify(value)}`);
      assert.ok(Math.abs(value.notes.right - value.area.right) <= 1, "The default tab sits at the right edge");
      value = await drag(center(value.notes), { x: 2, y: center(value.notes).y });
      assert.ok(Math.abs(value.notes.left - value.area.left) <= 1, `The tab reaches the left edge: ${JSON.stringify(value)}`);
      assert.equal(value.collapsed, "true", "Dragging the tab does not expand it");
      value = await drag(center(value.notes), { x: center(value.notes).x, y: 790 });
      assert.ok(Math.abs(value.notes.bottom - value.area.bottom) <= 1 && Math.abs(value.notes.left - value.area.left) <= 1,
        `The tab reaches the bottom-left corner: ${JSON.stringify(value)}`);
      const corner = value.notes;
      value = await click(".session-notes-toggle");
      assert.equal(value.collapsed, "false");
      assert.ok(inside(value.window, value.area), `Expanded Notes open inside the pane: ${JSON.stringify(value)}`);
      assert.ok(Math.abs(value.window.left - value.area.left) <= 1 && Math.abs(value.window.bottom - value.area.bottom) <= 1, "Expanded Notes stay in the corner");
      value = await click('[aria-label="Collapse notes"]');
      assert.deepEqual(value.notes, corner, "Collapsing returns the tab to where it was dragged");
      value = await drag(center(value.notes), { x: 1270, y: 2 });
      assert.ok(Math.abs(value.notes.right - value.area.right) <= 1 && Math.abs(value.notes.top - value.area.top) <= 1,
        `The tab goes back to the top-right corner: ${JSON.stringify(value)}`);
      value = await click(".session-notes-toggle");
      assert.ok(inside(value.window, value.area) && Math.abs(value.window.right - value.area.right) <= 1, "Expanded at the right edge, inside the pane");
    });
    await t.test("proposals follow Notes to its height, stay inside the pane, and keep their own place while Notes are hidden", async () => {
      for (const y of [0, 36, 300]) {
        const value = await reset({ width: 1080, stored: { x: 18, y, width: 260, height: 190 }, notes: "Moved notes", proposals: true });
        readable(value);
        assert.ok(value.tasks!.right <= value.notes.left, `Proposals stay on the left of Notes: ${JSON.stringify(value)}`);
        assert.ok(Math.abs(value.tasks!.top - value.notes.top) <= 1, `Proposals take the height of Notes at y=${y}: ${JSON.stringify(value)}`);
      }
      // A collapsed Notes tab near the bottom: the card rises just enough to stay whole inside the pane.
      let value = await reset({ width: 1080, stored: { x: 18, y: 560, width: 260, height: 190 }, proposals: true });
      assert.equal(value.collapsed, "true");
      readable(value);
      assert.ok(value.tasks!.top < value.notes.top && value.pane.bottom - value.tasks!.bottom <= 9, `The card rises to fit: ${JSON.stringify(value)}`);
      await evaluate("overlays.hide(true)");
      value = await layout();
      assert.ok(Math.abs(value.pane.right - 14 - value.tasks!.right) <= 1 && Math.abs(value.tasks!.top - value.pane.top - 44) <= 1,
        `Without Notes, proposals return to the top right corner: ${JSON.stringify(value)}`);
      await evaluate("overlays.hide(false)");
      value = await layout();
      assert.ok(value.tasks!.top < value.notes.top && value.tasks!.right <= value.notes.left, "Shown again, Notes take the proposals back beside them");
    });
    await t.test("collapsed Notes and proposals stay collapsed when the pane is mounted again, until a new proposal arrives", async () => {
      let value = await reset({ width: 1080, notes: "Notes the user put away", proposals: true });
      assert.equal(value.collapsed, "false");
      await click('[aria-label="Put away"]');
      value = await click('[aria-label="Collapse notes"]');
      assert.equal(value.collapsed, "true");
      await evaluate("overlays.remount()");
      await wait("!!document.querySelector('.session-notes-window') && !document.querySelector('.notes-pane [role=status]')");
      value = await layout();
      assert.equal(value.collapsed, "true", "Notes read again after the remount stay collapsed");
      assert.equal(await evaluate("!!document.querySelector('.work-cards-chip') && !document.querySelector('.work-card')"), true, "Proposals stay put away");
      readable(value);
      // After a switch of space, the list of the session is empty for a moment before its proposals come back.
      await evaluate("overlays.proposals(0); overlays.remount(); overlays.proposals(1)");
      await wait("!!document.querySelector('.session-notes-window') && !document.querySelector('.notes-pane [role=status]')");
      value = await layout();
      assert.equal(await evaluate("!!document.querySelector('.work-cards-chip') && !document.querySelector('.work-card')"), true,
        "Proposals that come back after a moment without them are not new");
      await evaluate("overlays.proposals(2)");
      value = await layout();
      assert.equal(await evaluate("document.querySelector('.work-card h3')?.textContent"), "A proposal that arrives later", "A new proposal opens the cards on itself");
      assert.equal(value.collapsed, "true", "A new proposal does not open Notes");
      readable(value);
      // Expanded overlays stay expanded too.
      await evaluate("overlays.remount()");
      await wait("!!document.querySelector('.session-notes-window') && !document.querySelector('.notes-pane [role=status]')");
      assert.equal(await evaluate("!!document.querySelector('.work-card')"), true, "Open proposals stay open");
    });
    await t.test("dragging the proposals onto a target around Notes docks them there, and the menu does the same", async () => {
      let value = await reset({ width: 1080, notes: "## Docking\n\n- [ ] Put the proposals below.", proposals: true });
      readable(value);
      assert.ok(value.tasks!.right <= value.notes.left, "Left of Notes by default");
      // The cross of targets is centred on Notes: the one below is 40 pixels under its middle.
      const middle = center(value.notes);
      const header = center(await evaluate("(() => { const {left,top,right,bottom,width,height}=document.querySelector('.work-card .work-card-kind').getBoundingClientRect(); return {left,top,right,bottom,width,height}; })()") as Box);
      value = await drag(header, { x: middle.x, y: middle.y + 40 });
      readable(value);
      assert.equal(await evaluate("!!document.querySelector('.work-dock-guide')"), false, "The targets go when the drag ends");
      assert.equal(await evaluate("getSelection().toString()"), "", "Dragging the header selects no text");
      assert.ok(value.tasks!.top >= value.notes.bottom && Math.abs(value.tasks!.right - value.notes.right) <= 1,
        `Dropped below, the proposals sit under Notes, at its right edge: ${JSON.stringify(value)}`);
      assert.equal(await evaluate("overlays.dock()"), "below");
      assert.equal(await evaluate("!!document.querySelector('.work-card')"), true, "Dragging the card does not put it away");
      // The menu of the header: every side can be chosen, and Automatic goes back to the room Notes leave.
      const choose = async (label: string) => {
        await click('[aria-label="Place the proposals"]');
        await wait("!!document.querySelector('.bp6-menu')");
        assert.deepEqual(await evaluate("[...document.querySelectorAll('.bp6-menu-item')].map(item => item.textContent)"),
          ["Automatic", "Left of Notes", "Right of Notes", "Above Notes", "Below Notes"]);
        // A real press and release: the menu is a portal under the header, whose drag handle sees the press too.
        const item = center(await evaluate(`(() => { const {left,top,right,bottom,width,height}=[...document.querySelectorAll('.bp6-menu-item')]
          .find(item => item.textContent === ${JSON.stringify(label)}).getBoundingClientRect(); return {left,top,right,bottom,width,height}; })()`) as Box);
        await command("Input.dispatchMouseEvent", { type: "mouseMoved", x: item.x, y: item.y });
        await command("Input.dispatchMouseEvent", { type: "mousePressed", x: item.x, y: item.y, button: "left", buttons: 1, clickCount: 1 });
        await command("Input.dispatchMouseEvent", { type: "mouseReleased", x: item.x, y: item.y, button: "left", buttons: 0, clickCount: 1 });
        await wait("!document.querySelector('.bp6-menu')");
        return layout();
      };
      value = await choose("Automatic");
      assert.ok(value.tasks!.right <= value.notes.left && Math.abs(value.tasks!.top - value.notes.top) <= 1, "Automatic puts the proposals on the left");
      assert.equal(await evaluate("overlays.dock()"), null, "Automatic is not stored");
      const free = value.window;
      // The chip of the proposals drags too, and a drag does not open the cards.
      value = await click('[aria-label="Put away"]');
      const chip = center(await evaluate("(() => { const {left,top,right,bottom,width,height}=document.querySelector('.work-cards-chip').getBoundingClientRect(); return {left,top,right,bottom,width,height}; })()") as Box);
      value = await drag(chip, { x: middle.x, y: middle.y + 40 });
      assert.equal(await evaluate("!!document.querySelector('.work-cards-chip') && !document.querySelector('.work-card')"), true, "The dragged chip stays a chip");
      assert.ok(value.tasks!.top >= value.notes.bottom, `The chip sits below Notes: ${JSON.stringify(value)}`);
      // The side is the window's: a pane mounted again keeps it.
      await evaluate("overlays.remount()");
      await wait("!!document.querySelector('.session-notes-window') && !document.querySelector('.notes-pane [role=status]')");
      value = await layout();
      assert.ok(value.tasks!.top >= value.notes.bottom, "A pane mounted again keeps the side");
      // A side without room gets it: Notes at the top move down for proposals above them, without saving it.
      await click(".work-cards-chip");
      const stored = await evaluate("overlays.stored()");
      value = await choose("Above Notes");
      readable(value);
      assert.ok(value.tasks!.bottom <= value.notes.top - 7 && value.notes.top > free.top + 100, `Notes make room above them: ${JSON.stringify(value)}`);
      assert.equal(await evaluate("overlays.stored()"), stored, "Making room does not save over the place of Notes");
      // Notes at the right edge move left for proposals on their right.
      value = await choose("Right of Notes");
      readable(value);
      assert.ok(value.tasks!.left >= value.notes.right + 7 && value.notes.right < free.right - 200, `Notes make room on their right: ${JSON.stringify(value)}`);
      value = await choose("Automatic");
      assert.deepEqual(value.window, free, "Automatic gives Notes back their own place");
    });
    await t.test("the side chosen in one pane is the one of every mounted pane of the window", async () => {
      await reset({ width: 1080, notes: "## Docking\n\n- [ ] Put the proposals below.", proposals: true, second: true });
      const sides = "[...document.querySelectorAll('.work-cards')].map(cards => cards.dataset.side ?? null)";
      await wait(`JSON.stringify(${sides}) === '["left","left"]'`);
      const choose = async (label: string) => {
        await click('[aria-label="Place the proposals"]');
        await wait("!!document.querySelector('.bp6-menu')");
        await evaluate(`[...document.querySelectorAll('.bp6-menu-item')].find(item => item.textContent === ${JSON.stringify(label)}).click()`);
        await wait("!document.querySelector('.bp6-menu')");
      };
      // Chosen in the first pane, the side is the one of the second, which stays mounted.
      await choose("Below Notes");
      await wait(`JSON.stringify(${sides}) === '["below","below"]'`);
      assert.equal(await evaluate("overlays.dock()"), "below");
      await choose("Automatic");
      await wait(`JSON.stringify(${sides}) === '["left","left"]'`);
      assert.equal(await evaluate("overlays.dock()"), null);
    });
    await t.test("hiding Notes removes the window and its tab and keeps the loaded notes", async () => {
      for (const notes of ["", "Keep these notes while hidden"]) {
        const before = await reset({ width: 1080, notes });
        assert.equal(before.collapsed, notes ? "false" : "true");
        await evaluate("window.notesOwner=document.querySelector('.notes-pane');window.notesReads=overlays.reads()");
        await evaluate("overlays.hide(true)");
        const target = center(before.notes);
        const hidden = await evaluate(`(() => { const hit=document.elementFromPoint(${target.x},${target.y});
          return { hit: !!hit?.closest('.session-notes-overlay'), visibility: getComputedStyle(document.querySelector('.session-notes-toggle')).visibility,
            inert: document.querySelector('.session-notes-overlay').inert }; })()`);
        assert.deepEqual(hidden, { hit: false, visibility: "hidden", inert: true }, "Hidden Notes can be neither seen nor reached");
        await evaluate("overlays.hide(false)");
        const after = await layout();
        assert.deepEqual(after.notes, before.notes, "Showing Notes again restores the same window");
        assert.equal(after.collapsed, before.collapsed, "Hiding is not collapsing");
        assert.equal(await evaluate("notesOwner===document.querySelector('.notes-pane') && notesReads===overlays.reads()"), true, "Hiding does not remount or re-read Notes");
        assert.equal(await evaluate(`!!document.elementFromPoint(${target.x},${target.y})?.closest('.session-notes-overlay')`), true);
      }
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
