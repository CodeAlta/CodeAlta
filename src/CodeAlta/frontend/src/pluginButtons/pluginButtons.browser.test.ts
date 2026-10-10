import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { blueprintPaletteVariables } from "../blueprintPalette";
import { browserBaseArgs, browserExecutable } from "../browserTarget";

const edge = browserExecutable;
// Where the pictures of this test go, when a person wants to look at them.
const shots = process.env.CODEALTA_SHOTS ?? "";

test("buttons of plugins sit before the space switch, show their state, hide on request, and fold when the window is narrow", { skip: !edge, timeout: 240_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-plugin-buttons-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./pluginButtons.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    await writeFile(join(root, "style.css"), blueprintPaletteVariables(await readFile(new URL("../../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8"))
      + await readFile(new URL("../style.css", import.meta.url), "utf8") + await readFile(new URL("../spaces/spaces.css", import.meta.url), "utf8")
      + await readFile(new URL("./pluginButtons.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, [...browserBaseArgs, `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
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
      const timer = setTimeout(() => reject(Error(`${method} timed out`)), 30_000);
      const reply = (event: MessageEvent) => {
        const message = JSON.parse(String(event.data));
        if (message.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer);
        if (message.error) reject(Error(JSON.stringify(message.error))); else resolve(message.result);
      };
      socket!.addEventListener("message", reply); socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async <T,>(expression: string): Promise<T> => {
      const result = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails));
      return result.result?.value as T;
    };
    const wait = (expression: string, milliseconds = 8_000) => evaluate<boolean>(
      `new Promise(resolve=>{const end=Date.now()+${milliseconds};const tick=()=>{let ok=false;try{ok=!!(${expression})}catch{}ok?resolve(true):Date.now()>end?resolve(false):setTimeout(tick,25)};tick()})`);
    const viewport = (width: number) => command("Emulation.setDeviceMetricsOverride", { width, height: 260, deviceScaleFactor: 1, mobile: false });
    const shot = async (name: string) => {
      if (!shots) return;
      await mkdir(shots, { recursive: true });
      await writeFile(join(shots, name), Buffer.from((await command("Page.captureScreenshot", { format: "png" })).data, "base64"));
    };
    const rect = (selector: string) => evaluate<{ left: number; right: number; width: number; top: number; height: number } | null>(
      `(()=>{const e=document.querySelector(${JSON.stringify(selector)});if(!e)return null;const r=e.getBoundingClientRect();return {left:r.left,right:r.right,width:r.width,top:r.top,height:r.height}})()`);
    const text = (selector: string) => evaluate<string[]>(`Array.from(document.querySelectorAll(${JSON.stringify(selector)})).map(e=>e.textContent)`);

    await viewport(1400);
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait("window.buttonsFixture"), true);

    // Two in the title bar, one in the rail: Statistics opens a canvas (marked, as its canvas is the tab in front), Alerts runs a command.
    await evaluate(`buttonsFixture.setButtons([
      buttonsFixture.wire({ buttonId: "statistics", label: "Statistics", icon: "chart-column", canvas: "statistics", canvasScope: "Application", commandId: null, badge: "busy" }),
      buttonsFixture.wire({ buttonId: "alerts", label: "Alerts", icon: "bell", badge: "count", count: 3, tone: "Warning" }),
      buttonsFixture.wire({ buttonId: "release", label: "Release", place: "Rail", icon: "icons/release.svg", iconData: "data:image/svg+xml;base64," + btoa('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><path d="M2 12 L12 2 L22 12 L12 22 Z"/></svg>'), badge: "dot" }),
    ]); buttonsFixture.state.active.add("statistics"); buttonsFixture.render();`);
    assert.equal(await wait("document.querySelectorAll('.window-actions .plugin-button').length === 2"), true);
    assert.equal(await wait("document.querySelectorAll('.activity-rail .plugin-button').length === 1"), true);

    // StrictMode ran each effect twice: each button is drawn once, and what was read says the context of the place.
    assert.equal(await evaluate("document.querySelectorAll('.plugin-button').length"), 3);
    assert.ok(await evaluate("buttonsFixture.state.reads.every(read => read.spaceId === 'work' && read.projectId === 'project-1' && read.sessionId === 'session-1')"));
    assert.ok(await evaluate("buttonsFixture.state.reads.some(read => read.place === 'TitleBar') && buttonsFixture.state.reads.some(read => read.place === 'Rail')"));

    // Left to right: the buttons of plugins, the space switch, the zoom, the theme; in the rail between Issues and Settings.
    const order = await evaluate<string[]>(`Array.from(document.querySelectorAll('.window-actions > .plugin-buttons .plugin-button, .window-actions > .bp6-button')).map(e=>e.dataset.button||e.className.split(' ').find(c=>['space-switch','window-zoom','theme-switch'].includes(c)))`);
    assert.deepEqual(order, ["statistics", "alerts", "space-switch", "window-zoom", "theme-switch"]);
    const lefts = await evaluate<number[]>("Array.from(document.querySelectorAll('.window-actions .plugin-button, .window-actions > .bp6-button')).map(e=>e.getBoundingClientRect().left)");
    assert.deepEqual([...lefts].sort((a, b) => a - b), lefts, "drawn left to right in that order");
    const rail = await evaluate<string[]>("Array.from(document.querySelectorAll('.activity-rail > .bp6-button, .activity-rail .plugin-button')).map(e=>e.getAttribute('aria-label'))");
    assert.deepEqual(rail, ["Issues", "Release", "Settings"]);

    // Accessible names, tooltips, the number of the badge, the tone, the busy ring, and the mark of the canvas that is in front.
    assert.equal(await evaluate("document.querySelector('[data-button=alerts]').getAttribute('aria-label')"), "Alerts: 3");
    assert.equal(await evaluate("document.querySelector('[data-button=alerts]').title"), "Alerts: 3");
    assert.deepEqual(await text("[data-button=alerts] .plugin-button-badge"), ["3"]);
    assert.equal(await evaluate("document.querySelector('[data-button=alerts]').dataset.tone"), "Warning");
    assert.equal(await evaluate("document.querySelector('[data-button=statistics]').getAttribute('aria-busy')"), "true");
    assert.equal(await evaluate("document.querySelectorAll('[data-button=statistics] .plugin-button-badge[data-kind=busy]').length"), 1);
    assert.equal(await evaluate("document.querySelector('[data-button=statistics]').classList.contains('bp6-active')"), true);
    assert.equal(await evaluate("document.querySelector('[data-button=alerts]').classList.contains('bp6-active')"), false);
    assert.equal(await evaluate("document.querySelectorAll('[data-button=release] .plugin-button-badge[data-kind=dot]').length"), 1);
    // A file of the plugin is a mask in the color of the text; a library icon is an inline drawing.
    assert.equal(await evaluate("getComputedStyle(document.querySelector('[data-button=release] .plugin-icon-file')).maskImage.startsWith('url(\"data:image/svg+xml')"), true);
    assert.equal(await wait("document.querySelector('[data-button=statistics] svg.lucide-chart-column, [data-button=statistics] svg.lucide-chart-no-axes-column, [data-button=statistics] svg[class*=chart-column]')"), true, "the library chunk brought the icon");
    assert.equal(await wait("document.querySelector('[data-button=alerts] svg')"), true);
    await shot("plugin-buttons-wide.png");

    // The bar of the title does not shift when a badge appears or the number changes: the button keeps its box.
    const before = await rect("[data-button=alerts]");
    await evaluate("buttonsFixture.setButtons(buttonsFixture.state.buttons.map(b => b.buttonId === 'alerts' ? { ...b, count: 128, badge: 'count' } : b)); buttonsFixture.invalidate()");
    assert.equal(await wait("document.querySelector('[data-button=alerts] .plugin-button-badge')?.textContent === '99+'"), true);
    assert.deepEqual(await rect("[data-button=alerts]"), before);

    // A button the plugin hides for now, and one the plugin disables.
    await evaluate("buttonsFixture.setButtons(buttonsFixture.state.buttons.map(b => b.buttonId === 'alerts' ? { ...b, hidden: true } : b.buttonId === 'statistics' ? { ...b, disabled: true } : b)); buttonsFixture.invalidate()");
    assert.equal(await wait("!document.querySelector('[data-button=alerts]')"), true);
    assert.equal(await evaluate("document.querySelector('[data-button=statistics]').disabled"), true);
    await evaluate("buttonsFixture.setButtons(buttonsFixture.state.buttons.map(b => b.buttonId === 'alerts' ? { ...b, hidden: false } : b.buttonId === 'statistics' ? { ...b, disabled: false } : b)); buttonsFixture.invalidate()");
    assert.equal(await wait("document.querySelector('[data-button=alerts]') && !document.querySelector('[data-button=statistics]').disabled"), true);

    // A click does what the button does, for the context of the place.
    await evaluate("document.querySelector('[data-button=alerts]').click()");
    assert.deepEqual(await evaluate("buttonsFixture.state.activated"), [{ button: "alerts", projectId: "project-1", sessionId: "session-1" }]);

    // The user keeps the window: a right click offers to hide the button, and the choice is kept under the plugin and the button.
    const target = await rect("[data-button=alerts]");
    await evaluate("document.querySelector('[data-button=alerts]').dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 700, clientY: 30 }))");
    assert.equal(await wait("document.querySelector('.session-tab-popup [role=menuitem]')?.textContent === 'Hide this button'"), true);
    await shot("plugin-buttons-context-menu.png");
    await evaluate("document.querySelector('.session-tab-popup [role=menuitem]').click()");
    assert.equal(await wait("!document.querySelector('[data-button=alerts]')"), true);
    assert.deepEqual(JSON.parse((await evaluate<string>("buttonsFixture.storage()"))!), ["builtin:fixture\nalerts"]);
    assert.ok(target);
    // The space switch is where it was: the rest of the bar closed up around the missing button.
    assert.equal(await evaluate("document.querySelectorAll('.window-actions .plugin-button').length"), 1);

    // Settings > Plugins lists each button with a switch that shows it again.
    await evaluate(`buttonsFixture.renderSwitches(buttonsFixture.state.buttons)`);
    assert.equal(await wait("document.querySelectorAll('.plugin-button-list li').length === 3"), true);
    assert.equal(await evaluate("document.querySelector('.plugin-button-list input[aria-label=\"Show the Alerts button\"]').checked"), false);
    assert.equal(await evaluate("document.querySelector('.plugin-button-list input[aria-label=\"Show the Statistics button\"]').checked"), true);
    await evaluate("document.querySelector('.plugin-button-list input[aria-label=\"Show the Alerts button\"]').click()");
    assert.equal(await wait("document.querySelector('.plugin-button-list input[aria-label=\"Show the Alerts button\"]').checked"), true);
    assert.deepEqual(JSON.parse((await evaluate<string>("buttonsFixture.storage()"))!), []);
    await evaluate("buttonsFixture.render()");
    assert.equal(await wait("document.querySelector('[data-button=alerts]')"), true);

    // A narrow window folds the buttons of plugins into one menu, before the space switch moves: it is still wider than its folding point.
    const switchBefore = await rect(".space-switch");
    await viewport(1000);
    assert.equal(await wait("document.querySelectorAll('.window-actions .plugin-button:not(.plugin-buttons-more)').length === 0 && document.querySelector('.window-actions .plugin-buttons-more')"), true);
    assert.equal(await evaluate("document.querySelector('.space-switch .bp6-button-text') !== null"), true, "the space switch kept its name: no application control moved yet");
    assert.ok(Math.abs((await rect(".space-switch"))!.width - switchBefore!.width) < 1);
    await evaluate("document.querySelector('.window-actions .plugin-buttons-more').click()");
    assert.equal(await wait("document.querySelectorAll('.bp6-menu .bp6-menu-item').length === 2"), true);
    assert.deepEqual(await text(".bp6-menu .bp6-menu-item .bp6-text-overflow-ellipsis"), ["Statistics", "Alerts"]);
    await shot("plugin-buttons-narrow-menu.png");
    await evaluate("Array.from(document.querySelectorAll('.bp6-menu .bp6-menu-item')).find(e => e.textContent.includes('Alerts')).click()");
    assert.equal(await evaluate("buttonsFixture.state.activated.at(-1).button"), "alerts");
    // The rail folds into the same kind of menu; the application's own buttons stay.
    assert.equal(await evaluate("document.querySelectorAll('.activity-rail .plugin-buttons-more').length"), 1);
    assert.deepEqual(await evaluate("Array.from(document.querySelectorAll('.activity-rail > .bp6-button')).map(e=>e.getAttribute('aria-label'))"), ["Issues", "Settings"]);
    await viewport(1400);
    assert.equal(await wait("document.querySelectorAll('.window-actions .plugin-button:not(.plugin-buttons-more)').length === 2"), true);

    // Menus of rows: the lines that plugins add, read for the row of the menu and not for the selection.
    await evaluate(`buttonsFixture.setButtons([...buttonsFixture.state.buttons, buttonsFixture.wire({ buttonId: "checklist", label: "Release checklist", place: "ProjectMenu", icon: "list-checks", badge: "count", count: 2 })]); buttonsFixture.render({ menu: true, project: "selected", session: null })`);
    assert.equal(await wait("Array.from(document.querySelectorAll('.session-tab-popup [role=menuitem]')).some(e => e.textContent.startsWith('Release checklist'))"), true);
    assert.deepEqual(await evaluate("buttonsFixture.state.reads.filter(read => read.place === 'ProjectMenu')"), [{ place: "ProjectMenu", spaceId: "work", projectId: "project-row", sessionId: null }, { place: "ProjectMenu", spaceId: "work", projectId: "project-row", sessionId: null }]);
    assert.deepEqual(await text(".session-tab-popup .plugin-menu-badge"), ["2"]);
    await shot("plugin-buttons-project-menu.png");
    await evaluate("Array.from(document.querySelectorAll('.session-tab-popup [role=menuitem]')).find(e => e.textContent.startsWith('Release checklist')).click()");
    assert.deepEqual(await evaluate("buttonsFixture.state.activated.at(-1)"), { button: "checklist", projectId: "project-row", sessionId: null });

    // The icon of a canvas goes through the same lookup: a file the host sent, an icon of the window, one of the library, a brand, and a neutral icon for the rest.
    await evaluate("buttonsFixture.renderIcons()");
    const kind = (name: string) => evaluate<string>(`(()=>{const e=document.querySelector('[data-icon="${name}"]');const c=e&&e.firstElementChild;return !c?'none':c.classList.contains('plugin-icon-file')?'file':c.classList.contains('plugin-icon-pending')?'pending':c.dataset.brand?'brand:'+c.dataset.brand:'svg:'+(Array.from(c.classList).find(x=>x.startsWith('lucide-'))||'')})()`);
    assert.equal(await kind("icons/a.svg"), "file");
    assert.equal(await kind("star"), "svg:lucide-star", "an icon the window draws needs no loading");
    assert.equal(await wait("document.querySelector('[data-icon=chart-column] svg')"), true);
    assert.match(await kind("chart-column"), /^svg:lucide-chart/);
    assert.equal(await wait("document.querySelector('[data-icon=openai] [data-brand]')"), true, "the library has no such icon, so the logo of the brand is drawn");
    for (const name of ["no-such-icon", "icons/missing.svg", "../..", "Not Valid"]) {
      assert.equal(await wait(`document.querySelector('[data-icon="${name}"] svg.lucide-puzzle')`), true, `${name} draws the neutral icon`);
    }
    await shot("plugin-icons.png");

    // A host that stops answering leaves no button behind.
    await evaluate("buttonsFixture.state.fail = true; buttonsFixture.render(); buttonsFixture.invalidate()");
    assert.equal(await wait("document.querySelectorAll('.plugin-button').length === 0"), true);
  } finally {
    socket?.close();
    browser?.kill();
    await new Promise(resolve => setTimeout(resolve, 200));
    await rm(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }).catch(() => { });
  }
});

