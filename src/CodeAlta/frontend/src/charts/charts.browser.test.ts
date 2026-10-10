import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable } from "../browserTarget";

const edge = browserExecutable;
const origin = "https://charts.invalid", pageUrl = `${origin}/index.html`;
// The screenshots of the charts, to look at: the git-ignored tmp folder of the repository.
const pictures = fileURLToPath(new URL("../../../../../tmp/charts/", import.meta.url));
const galleryCss = `body { margin: 0; padding: 12px; background: var(--bg); color: var(--text); font-size: 14px; }
.gallery { display: flex; flex-wrap: wrap; gap: 12px; align-items: flex-start; }
.gallery-box { box-sizing: border-box; padding: 8px 10px; border: 1px solid var(--line); border-radius: 6px; background: var(--panel); }
.gallery-box > h4 { margin: 0 0 4px; font-size: 12px; color: var(--muted); }
.gallery-tiles { display: flex; flex-wrap: wrap; gap: 10px; align-items: flex-start; }
.gallery-key { display: flex; gap: 4px; width: 100%; } .gallery-key > i { width: 28px; height: 14px; border-radius: 2px; }`;

type Page = Readonly<{
  evaluate: <T = unknown>(expression: string) => Promise<T>;
  command: (method: string, params?: object) => Promise<Record<string, any>>;
  until: (expression: string, what: string) => Promise<void>;
  shot: (name: string) => Promise<void>;
  problems: string[];
}>;

// Runs the gallery in headless Edge under the page's production content security policy, and hands `body` a page to drive.
async function withGallery(body: (page: Page) => Promise<void>, options: { reducedMotion?: boolean } = {}): Promise<void> {
  const directory = await mkdtemp(join(tmpdir(), "codealta-charts-"));
  let browser: ReturnType<typeof spawn> | undefined, socket: WebSocket | undefined;
  try {
    const config = JSON.parse(await readFile(new URL("../../../neoastra.json", import.meta.url), "utf8")) as { assets: { csp: string } };
    const bundle = await build({ entryPoints: [fileURLToPath(new URL("./charts.mount.tsx", import.meta.url))], bundle: true, platform: "browser", format: "iife", write: false,
      define: { "process.env.NODE_ENV": '"development"' } });
    const style = (await readFile(new URL("../../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8"))
      + await readFile(new URL("../style.css", import.meta.url), "utf8") + await readFile(new URL("./charts.css", import.meta.url), "utf8") + galleryCss;
    const assets = new Map<string, { type: string; body: string }>([
      [pageUrl, { type: "text/html", body: '<!doctype html><html><head><meta charset="utf-8"><link rel="icon" href="data:,"><link rel="stylesheet" href="/style.css"></head><body><div id="root"></div><script src="/fixture.js"></script></body></html>' }],
      [`${origin}/style.css`, { type: "text/css", body: style }], [`${origin}/fixture.js`, { type: "text/javascript", body: bundle.outputFiles[0].text }]]);
    browser = spawn(edge!, [...browserBaseArgs, "--host-resolver-rules=MAP * ~NOTFOUND", `--user-data-dir=${join(directory, "profile")}`, "--remote-debugging-port=0", "about:blank"],
      { stdio: "ignore", windowsHide: true });
    let port = "";
    for (let attempt = 0; attempt < 100 && !port; attempt++) {
      try { port = (await readFile(join(directory, "profile/DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; } catch { await new Promise(resolve => setTimeout(resolve, 50)); }
    }
    assert.match(port, /^\d+$/);
    const tabs = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json() as { type: string; url: string; webSocketDebuggerUrl: string }[];
    socket = new WebSocket(tabs.find(tab => tab.type === "page" && tab.url === "about:blank")!.webSocketDebuggerUrl);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", reject, { once: true }); });
    let sequence = 0;
    const waiting = new Map<number, (value: any) => void>();
    const problems: string[] = [];
    const command = (method: string, params: object = {}) => new Promise<Record<string, any>>((resolve, reject) => {
      const id = ++sequence;
      const timer = setTimeout(() => { waiting.delete(id); reject(new Error(`${method} timed out`)); }, 20_000);
      waiting.set(id, reply => { clearTimeout(timer); reply.error ? reject(new Error(JSON.stringify(reply.error))) : resolve(reply.result ?? {}); });
      socket!.send(JSON.stringify({ id, method, params }));
    });
    socket.addEventListener("message", event => {
      const message = JSON.parse(String(event.data));
      if (waiting.has(message.id)) { waiting.get(message.id)!(message); waiting.delete(message.id); return; }
      if (message.method === "Fetch.requestPaused") {
        const { requestId, request } = message.params, asset = assets.get(request.url);
        void (asset ? command("Fetch.fulfillRequest", { requestId, responseCode: 200, responseHeaders: [{ name: "Content-Type", value: asset.type }, { name: "Content-Security-Policy", value: config.assets.csp }],
          body: Buffer.from(asset.body).toString("base64") }) : command("Fetch.failRequest", { requestId, errorReason: "BlockedByClient" })).catch(() => { });
        if (!asset && !request.url.startsWith("data:")) problems.push(`request: ${request.url}`);
      }
      if (message.method === "Runtime.exceptionThrown") problems.push(`exception: ${JSON.stringify(message.params.exceptionDetails).slice(0, 400)}`);
      if (message.method === "Runtime.consoleAPICalled" && ["error", "warning"].includes(message.params.type)) problems.push(`console ${message.params.type}: ${message.params.args.map((a: any) => a.value ?? a.description).join(" ").slice(0, 300)}`);
      if (message.method === "Log.entryAdded" && message.params.entry.source === "security") problems.push(`security: ${message.params.entry.text}`);
    });
    const evaluate = async <T>(expression: string): Promise<T> => {
      const reply = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(reply.exceptionDetails, undefined, JSON.stringify(reply.exceptionDetails)); return reply.result?.value as T;
    };
    const until = async (expression: string, what: string) => {
      const found = await evaluate<boolean>(`new Promise(resolve => { const end = Date.now() + 12000; (function check() { let ok = false; try { ok = !!(${expression}); } catch {} if (ok) resolve(true); else if (Date.now() > end) resolve(false); else setTimeout(check, 30); })(); })`);
      if (!found) {
        const summary = await evaluate<string>(`[...document.querySelectorAll('[data-chart]')].map(box => box.dataset.chart + ':' + (box.querySelector('svg, canvas') ? 'drawn' : 'empty') + (box.querySelector('.chart-failure') ? '!' + box.querySelector('.chart-failure').textContent : '')).join(' ')`);
        assert.fail(`waited for ${what}: ${summary}; ${problems.join(" | ")}`);
      }
    };
    const shot = async (name: string) => {
      await mkdir(pictures, { recursive: true });
      const picture = await command("Page.captureScreenshot", { format: "png", captureBeyondViewport: true });
      await writeFile(join(pictures, `${name}.png`), Buffer.from(picture.data, "base64"));
    };
    await command("Page.enable"); await command("Runtime.enable"); await command("Log.enable");
    await command("Emulation.setDeviceMetricsOverride", { width: 1200, height: 2600, deviceScaleFactor: 1, mobile: false });
    if (options.reducedMotion) await command("Emulation.setEmulatedMedia", { features: [{ name: "prefers-reduced-motion", value: "reduce" }] });
    await command("Fetch.enable", { patterns: [{ urlPattern: "*", requestStage: "Request" }] });
    await command("Page.navigate", { url: pageUrl });
    await until("window.chartsFixture", "the gallery");
    await body({ evaluate, command, until, shot, problems });
    assert.deepEqual(problems, [], "no exception, console error or policy violation");
  } finally {
    socket?.close(); browser?.kill();
    await new Promise(resolve => setTimeout(resolve, 300));
    await rm(directory, { recursive: true, force: true, maxRetries: 5 }).catch(() => { });
  }
}

const allDrawn = `["lines","lines2","stacked","grouped","ranked","treemap","pie","scatter","histogram","boxes","calendar-echarts","canvas"].every(id => document.querySelector('[data-chart="' + id + '"] .chart-surface svg, [data-chart="' + id + '"] .chart-surface canvas'))`;

test("every kind of chart is drawn under the production policy, in both themes, with the colors of the page", { skip: !edge, timeout: 120_000 }, async () => {
  await withGallery(async page => {
    await page.until(allDrawn, "the charts of the gallery");
    // The accessible name, and the description ECharts generates for the picture.
    const names = await page.evaluate<string[]>(`[...document.querySelectorAll('.chart')].map(chart => chart.getAttribute('aria-labelledby') && document.getElementById(chart.getAttribute('aria-labelledby')).textContent)`);
    assert.ok(names.includes("Prompts, answers and errors per day"));
    assert.match(await page.evaluate<string>(`document.querySelector('[data-chart="lines"] .chart-surface').getAttribute('aria-label')`), /line chart|chart/i);
    // The series take the palette of the page, in order.
    const paletteOf = (dark: boolean) => dark ? ["#3fa6da", "#43bf4d", "#f0b726"] : ["#147eb3", "#29a634", "#866103"];
    const strokes = () => page.evaluate<string[]>(`[...document.querySelectorAll('[data-chart="lines"] .chart-surface svg path')].map(p => (p.getAttribute('stroke') || '').toLowerCase()).filter(c => /^#[0-9a-f]{6}$/.test(c))`);
    const dark = await strokes();
    for (const color of paletteOf(true)) assert.ok(dark.includes(color), `${color} in ${dark.join()}`);
    // The text takes the text color of the page.
    const textColor = await page.evaluate<string>(`getComputedStyle(document.body).color`);
    const axisFill = await page.evaluate<string[]>(`[...document.querySelectorAll('[data-chart="grouped"] .chart-surface svg text')].map(t => t.getAttribute('fill'))`);
    assert.ok(axisFill.length > 0 && axisFill.every(Boolean));
    assert.notEqual(textColor, "");
    await page.shot("gallery-dark");

    await page.evaluate(`chartsFixture.setTheme(false)`);
    await page.until(`(${`[...document.querySelectorAll('[data-chart="lines"] .chart-surface svg path')].some(p => (p.getAttribute('stroke') || '').toLowerCase() === '#147eb3')`})`, "the chart redrawn with the light palette");
    const light = await strokes();
    for (const color of paletteOf(false)) assert.ok(light.includes(color), `${color} in ${light.join()}`);
    assert.ok(!light.includes("#3fa6da"));
    await page.until(allDrawn, "every chart redrawn");
    await page.shot("gallery-light");

    // A color scheme that redefines the variables restyles the charts.
    await page.evaluate(`chartsFixture.setVariable('--chart-1', '#ff0000')`);
    await page.until(`[...document.querySelectorAll('[data-chart="lines"] .chart-surface svg path')].some(p => (p.getAttribute('stroke') || '').toLowerCase() === '#ff0000')`, "the chart redrawn with the scheme's color");
    // The same series keeps the same color in the legend.
    assert.equal(await page.evaluate<string>(`getComputedStyle(document.querySelector('[data-chart="lines"] .chart-legend-item i')).backgroundColor`), "rgb(255, 0, 0)");
  });
});

test("the chart keeps the zoom and the hidden series through a change of theme, and tells the page about both", { skip: !edge, timeout: 120_000 }, async () => {
  await withGallery(async page => {
    await page.until(allDrawn, "the charts");
    await page.evaluate(`chartsFixture.instance('lines').dispatchAction({ type: 'dataZoom', start: 20, end: 60 })`);
    const periods = await page.evaluate<any[]>(`chartsFixture.log.filter(entry => entry.period)`);
    assert.ok(periods.length > 0);
    assert.deepEqual([periods.at(-1).start, periods.at(-1).end], [20, 60]);
    assert.match(String(periods.at(-1).from), /^\d\d-\d\d$/);
    // A legend entry the user hides by keyboard stays hidden.
    await page.evaluate(`document.querySelectorAll('[data-chart="lines"] .chart-legend-item')[1].focus()`);
    await page.command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13, text: "\r" });
    await page.command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    await page.until(`document.querySelectorAll('[data-chart="lines"] .chart-legend-item')[1].getAttribute('aria-pressed') === 'false'`, "the series hidden");
    assert.equal(await page.evaluate(`chartsFixture.instance('lines').getOption().legend[0].selected.Answers`), false);
    await page.evaluate(`chartsFixture.setTheme(false)`);
    await page.until(`[...document.querySelectorAll('[data-chart="lines"] .chart-surface svg path')].some(p => (p.getAttribute('stroke') || '').toLowerCase() === '#147eb3')`, "the light theme");
    await page.until(`chartsFixture.instance('lines') && Math.round(chartsFixture.instance('lines').getOption().dataZoom[0].start) === 20`, "the zoom kept");
    assert.equal(await page.evaluate(`chartsFixture.instance('lines').getOption().legend[0].selected.Answers`), false);
    assert.equal(await page.evaluate(`document.querySelectorAll('[data-chart="lines"] .chart-legend-item')[1].getAttribute('aria-pressed')`), "false");
    // A click on a bar is told to the page as the item it is.
    const box = await page.evaluate<{ x: number; y: number }>(`(() => { const r = document.querySelector('[data-chart="grouped"] .chart-surface').getBoundingClientRect(); const chart = chartsFixture.instance('grouped'); const [x, y] = chart.convertToPixel({ seriesIndex: 0 }, [2, 20]); return { x: r.left + x, y: r.top + y }; })()`);
    await page.command("Input.dispatchMouseEvent", { type: "mouseMoved", x: box.x, y: box.y });
    await page.command("Input.dispatchMouseEvent", { type: "mousePressed", x: box.x, y: box.y, button: "left", clickCount: 1 });
    await page.command("Input.dispatchMouseEvent", { type: "mouseReleased", x: box.x, y: box.y, button: "left", clickCount: 1 });
    await page.until(`chartsFixture.log.some(entry => entry.select === 'grouped')`, "the click reported");
    // The charts of a group are connected.
    assert.deepEqual(await page.evaluate(`[chartsFixture.instance('lines').group, chartsFixture.instance('lines2').group]`), ["page", "page"]);
  });
});

test("a chart follows its box, waits while hidden, redraws at every page zoom, and moves less when the user asks for calm", { skip: !edge, timeout: 120_000 }, async () => {
  await withGallery(async page => {
    await page.until(allDrawn, "the charts");
    const width = () => page.evaluate<number>(`Math.round(document.querySelector('[data-chart="grouped"] .chart-surface svg').getBoundingClientRect().width)`);
    const before = await width();
    await page.evaluate(`chartsFixture.set({ width: 760 })`);
    await page.until(`Math.round(document.querySelector('[data-chart="grouped"] .chart-surface svg').getBoundingClientRect().width) > ${before + 100}`, "the chart widened");
    await page.evaluate(`chartsFixture.set({ width: 520 })`);
    await page.until(`Math.round(document.querySelector('[data-chart="grouped"] .chart-surface svg').getBoundingClientRect().width) === ${before}`, "the chart narrowed");

    // Hidden: nothing is drawn, an update waits, and shown it catches up.
    assert.equal(await page.evaluate(`document.querySelector('[data-chart="late"] .chart-surface svg')`), null);
    await page.evaluate(`chartsFixture.set({ lateVisible: true })`);
    await page.until(`chartsFixture.instance('late')`, "the late chart shown");
    assert.equal(await page.evaluate(`chartsFixture.instance('late').getOption().series[0].data.length`), 1);
    await page.evaluate(`chartsFixture.set({ lateVisible: false, lateOption: chartsLate })`);
    await new Promise(resolve => setTimeout(resolve, 400));
    assert.equal(await page.evaluate(`chartsFixture.instance('late').getOption().series[0].data.length`), 1, "a hidden chart does not draw");
    await page.evaluate(`chartsFixture.set({ lateVisible: true })`);
    await page.until(`chartsFixture.instance('late').getOption().series[0].data.length === 2`, "the hidden chart caught up");

    // Page zoom: the canvas chart is drawn again at the pixel ratio of the page; the SVG ones are vectors.
    for (const ratio of [1.25, 1.5]) {
      await page.command("Emulation.setDeviceMetricsOverride", { width: 1200, height: 2600, deviceScaleFactor: ratio, mobile: false });
      // The emulation changes the ratio without telling the page; a real zoom is followed by a resize of the window.
      await page.evaluate(`window.dispatchEvent(new Event('resize'))`);
      await page.until(`(() => { const c = document.querySelector('[data-chart="canvas"] canvas'); return c && Math.abs(c.width / c.getBoundingClientRect().width - ${ratio}) < 0.02; })()`, `the canvas chart at ${ratio}`);
      await page.shot(`gallery-dark-zoom-${String(ratio).replace(".", "")}`);
    }
    await page.command("Emulation.setDeviceMetricsOverride", { width: 1200, height: 2600, deviceScaleFactor: 1, mobile: false });

    // Narrow window: the boxes wrap and the charts stay readable.
    await page.command("Emulation.setDeviceMetricsOverride", { width: 420, height: 6000, deviceScaleFactor: 1, mobile: false });
    await page.evaluate(`chartsFixture.set({ width: 380 })`);
    await page.until(`document.querySelector('[data-chart="lines"] .chart-surface svg').getBoundingClientRect().width <= 380`, "the chart narrowed to the window");
    await page.shot("gallery-dark-narrow");
  });
  await withGallery(async page => {
    await page.until(allDrawn, "the charts");
    assert.equal(await page.evaluate(`chartsFixture.instance('lines').getOption().animation`), false);
  }, { reducedMotion: true });
});

test("the table shows what the chart draws and copies it, and the legend is a row of buttons", { skip: !edge, timeout: 120_000 }, async () => {
  await withGallery(async page => {
    await page.until(allDrawn, "the charts");
    await page.evaluate(`window.copied = []; Object.defineProperty(navigator, 'clipboard', { value: { writeText: async text => { window.copied.push(text); } }, configurable: true })`);
    await page.evaluate(`document.querySelector('[data-chart="grouped"] .chart-table-toggle').click()`);
    await page.until(`document.querySelector('[data-chart="grouped"] table.chart-table')`, "the table");
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('[data-chart="grouped"] thead th')].map(th => th.textContent)`), ["", "Claude", "Codex", "Copilot"]);
    assert.equal(await page.evaluate(`document.querySelectorAll('[data-chart="grouped"] tbody tr').length`), 7);
    assert.equal(await page.evaluate(`document.querySelector('[data-chart="grouped"] tbody th').textContent`), "Mon");
    assert.equal(await page.evaluate(`document.querySelector('[data-chart="grouped"] .chart-surface').hidden`), true);
    await page.shot("table-dark");
    await page.evaluate(`document.querySelector('[data-chart="grouped"] .chart-table-copy').click()`);
    const copied = await page.evaluate<string[]>(`window.copied`);
    assert.equal(copied.length, 1);
    assert.match(copied[0], /^\tClaude\tCodex\tCopilot\nMon\t\d+\t\d+\t\d+\n/);
    await page.evaluate(`document.querySelector('[data-chart="grouped"] .chart-table-toggle').click()`);
    await page.until(`!document.querySelector('[data-chart="grouped"] table.chart-table') && document.querySelector('[data-chart="grouped"] .chart-surface svg')`, "the chart back");
    // The legend is reachable by keyboard: each entry is a button with a pressed state.
    assert.equal(await page.evaluate(`document.querySelectorAll('[data-chart="grouped"] button.chart-legend-item').length`), 3);
  });
});

test("the heat maps take the focus by day, move with the arrow keys and report a selection", { skip: !edge, timeout: 120_000 }, async () => {
  await withGallery(async page => {
    await page.until(`document.querySelectorAll('[data-chart="calendar"] .heat-cell').length >= 365`, "the calendar");
    assert.equal(await page.evaluate(`document.querySelectorAll('[data-chart="calendar"] .heat-cell[tabindex="0"]').length`), 1, "one tab stop");
    assert.equal(await page.evaluate(`document.querySelectorAll('[data-chart="weekday"] .heat-cell').length`), 168);
    const key = (name: string, code: number) => async () => {
      await page.command("Input.dispatchKeyEvent", { type: "keyDown", key: name, code: name, windowsVirtualKeyCode: code });
      await page.command("Input.dispatchKeyEvent", { type: "keyUp", key: name, code: name, windowsVirtualKeyCode: code });
    };
    const focusedDay = () => page.evaluate<string>(`document.activeElement.getAttribute('data-key')`);
    await page.evaluate(`document.querySelector('[data-chart="calendar"] .heat-cell[tabindex="0"]').focus()`);
    const start = await focusedDay();
    await key("ArrowRight", 39)();
    const next = await focusedDay();
    assert.notEqual(next, start);
    assert.equal((Date.parse(next) - Date.parse(start)) / 86_400_000, 7, "one column is a week");
    await key("ArrowDown", 40)();
    assert.equal((Date.parse(await focusedDay()) - Date.parse(next)) / 86_400_000, 1);
    await page.command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13, text: "\r" });
    assert.equal((await page.evaluate<any[]>(`chartsFixture.log.filter(entry => entry.day)`)).length, 1);
    assert.match(await page.evaluate<string>(`document.activeElement.getAttribute('aria-label')`), /\d/);
    await page.evaluate(`document.querySelector('[data-chart="weekday"] .heat-cell[data-key="2-9"]').dispatchEvent(new MouseEvent('click', { bubbles: true }))`);
    assert.deepEqual(await page.evaluate(`chartsFixture.log.filter(entry => entry.hour !== undefined).map(entry => [entry.row, entry.hour])`), [[2, 9]]);
    await page.shot("heatmaps-focus-dark");
    await page.evaluate(`chartsFixture.setTheme(false)`);
    await page.until(`getComputedStyle(document.querySelector('[data-chart="calendar"] .heat-cell[data-level="5"]')).fill !== getComputedStyle(document.querySelector('[data-chart="calendar"] .heat-cell[data-level="0"]')).fill`, "the ramp");
    await page.shot("heatmaps-light");
  });
});
