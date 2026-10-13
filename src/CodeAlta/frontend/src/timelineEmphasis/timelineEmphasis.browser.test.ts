import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, waitForBrowserPort } from "../browserTarget";
import { blueprintPaletteVariables } from "../blueprintPalette";
import { defaultTimelineEmphasis, timelineEmphasisKey, timelineEntryTypeLabel, timelineEntryTypes } from "./timelineEmphasis";
import { translate } from "../localization";

const shots = process.env.CODEALTA_SHOTS ?? "";

test("the window customizes actual timelines, follows hover and focus, and keeps failed writes for later consumers", { skip: !browserExecutable, timeout: 120_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-timeline-emphasis-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./timelineEmphasis.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"),
      bundle: true, platform: "browser", format: "iife", define: { "process.env.NODE_ENV": '"development"' },
      // History reads the fixture's page, never the host or the user's sessions.
      plugins: [{ name: "no-host", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: "neoastra", namespace: "no-host" }));
        bundle.onLoad({ filter: /.*/, namespace: "no-host" }, () => ({ contents: "export const workspace = {};", loader: "js" }));
      } }] });
    await writeFile(join(root, "style.css"), blueprintPaletteVariables(await readFile(new URL("../../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8"))
      + await readFile(new URL("../style.css", import.meta.url), "utf8") + await readFile(new URL("./timelineEmphasis.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(browserExecutable!, [...browserBaseArgs, `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"],
      { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(root, "profile"), browser);
    const pages: { type?: string; webSocketDebuggerUrl?: string }[] = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10_000) })).json();
    socket = new WebSocket(pages.find(value => value.type === "page")!.webSocketDebuggerUrl!);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", () => reject(Error("browser unavailable")), { once: true }); });
    let sequence = 0;
    const problems: string[] = [];
    socket.addEventListener("message", event => {
      const message = JSON.parse(String(event.data));
      if (message.method === "Runtime.exceptionThrown") problems.push(JSON.stringify(message.params.exceptionDetails));
      if (message.method === "Runtime.consoleAPICalled" && message.params.type === "error") problems.push(JSON.stringify(message.params.args));
    });
    const command = (method: string, params: object = {}) => new Promise<{ result?: { value?: unknown }; exceptionDetails?: unknown; data?: string }>((resolve, reject) => {
      const id = ++sequence;
      const reply = (event: MessageEvent) => {
        const message = JSON.parse(String(event.data)); if (message.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer);
        if (message.error) reject(Error(`${method}: ${JSON.stringify(message.error)}`)); else resolve(message.result ?? {});
      };
      const timer = setTimeout(() => { socket!.removeEventListener("message", reply); reject(Error(`${method} timed out`)); }, 12_000);
      socket!.addEventListener("message", reply); socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async <T,>(expression: string): Promise<T> => {
      const result = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails));
      return result.result?.value as T;
    };
    const wait = (condition: string) => evaluate<boolean>(`new Promise(resolve => { const end = Date.now() + 7000; (function check() {
      if (${condition}) resolve(true); else if (Date.now() > end) resolve(false); else requestAnimationFrame(check); })(); })`);
    const frames = () => evaluate("new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))");
    const screenshot = async (name: string) => {
      if (!shots) return;
      await mkdir(shots, { recursive: true });
      await writeFile(join(shots, `${name}.png`), Buffer.from((await command("Page.captureScreenshot", { format: "png" })).data!, "base64"));
    };
    const key = async (key: string, code: string, windowsVirtualKeyCode: number, modifiers = 0) => {
      await command("Input.dispatchKeyEvent", { type: "keyDown", key, code, windowsVirtualKeyCode, modifiers });
      await command("Input.dispatchKeyEvent", { type: "keyUp", key, code, windowsVirtualKeyCode, modifiers });
      await frames();
    };
    const customize = '[aria-haspopup="dialog"]';
    const dialog = 'dialog.timeline-emphasis-dialog[open]';
    const message = (type: string, history = 0) => `[data-history="${history}"] [data-entry-type="${type}"]${type === "tool" ? "" : " > .message"}`;
    const opacity = (type: string, history = 0) => evaluate<number>(`Number(getComputedStyle(document.querySelector(${JSON.stringify(message(type, history) + " .message-body")})).opacity)`);
    const protectedOpacities = (history = 0) => evaluate<number[]>(`Array.from(document.querySelectorAll('[data-history="${history}"] [data-entry-type="attention"]'))
      .map(group => Number(getComputedStyle(group.classList.contains('timeline-tool-group') ? group : group.querySelector('.message')).opacity))`);
    const tabUntil = async (condition: string) => {
      for (let count = 0; count < 80; count++) { await key("Tab", "Tab", 9); if (await evaluate(condition)) return; }
      assert.fail(`Tab did not reach the expected control: ${condition}`);
    };
    const tabInto = async (selector: string) => {
      await evaluate(`document.querySelector('${customize}').focus()`);
      await tabUntil(`document.querySelector(${JSON.stringify(selector)}).contains(document.activeElement)`);
      assert.equal(await evaluate("document.activeElement.matches(':focus-visible')"), true, "real Tab focus is visibly keyboard-driven");
    };
    const pointerFocus = async (selector: string) => {
      // Release outside the button: retain real mouse focus without activating Copy or opening Details.
      const box = await evaluate<{ x: number; y: number }>(`(() => { document.activeElement.blur(); const button = document.querySelector(${JSON.stringify(selector)}).querySelector('button');
        button.scrollIntoView({ block: 'nearest' }); const r = button.getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2 }; })()`);
      await command("Input.dispatchMouseEvent", { type: "mouseMoved", ...box });
      await command("Input.dispatchMouseEvent", { type: "mousePressed", ...box, button: "left", buttons: 1, clickCount: 1 });
      await command("Input.dispatchMouseEvent", { type: "mouseMoved", x: 1, y: 1, button: "left", buttons: 1 });
      await command("Input.dispatchMouseEvent", { type: "mouseReleased", x: 1, y: 1, button: "left", buttons: 0, clickCount: 1 });
      await frames();
      assert.equal(await evaluate(`document.querySelector(${JSON.stringify(selector)}).contains(document.activeElement) && !document.activeElement.matches(':focus-visible')`), true);
    };
    const open = async () => {
      await evaluate(`document.querySelector('${customize}').focus()`);
      await key("Enter", "Enter", 13);
      assert.equal(await wait(`!!document.querySelector('${dialog} table tbody tr')`), true);
      assert.equal(await evaluate(`document.querySelector('${dialog}').contains(document.activeElement)`), true, "focus enters the modal");
    };
    const close = async () => {
      await key("Escape", "Escape", 27);
      assert.equal(await evaluate(`!!document.querySelector('${dialog}')`), false);
      assert.equal(await evaluate(`document.activeElement === document.querySelector('${customize}')`), true, "Escape returns focus to Customize");
    };
    const chooseType = async (type: typeof timelineEntryTypes[number]) => {
      await evaluate(`(() => { const select = document.querySelector('${dialog} select[aria-label="Entry type"]'); select.value = '${type}'; select.dispatchEvent(new Event('change', { bubbles: true })); })()`);
      await frames();
    };
    const select = async (label: string, value: number | string) => {
      const type = timelineEntryTypes.find(type => label.includes(`of ${timelineEntryTypeLabel[type]}`));
      if (type) await chooseType(type);
      await evaluate(`(() => { const select = document.querySelector('select[aria-label=${JSON.stringify(label)}]');
        select.value = '${value}'; select.dispatchEvent(new Event('change', { bubbles: true })); })()`);
      await frames();
    };
    await command("Runtime.enable");
    await command("Page.enable");
    await command("Emulation.setDeviceMetricsOverride", { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });
    await command("Emulation.setEmulatedMedia", { features: [{ name: "prefers-reduced-motion", value: "reduce" }] });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait(`window.emphasisFixture && !!document.querySelector('${message("plan")}')`), true, problems.join("\n"));
    await command("Input.dispatchMouseEvent", { type: "mouseMoved", x: 1590, y: 990 });
    assert.equal(await evaluate("document.querySelector('#settings-dim-intermediate').checked"), false);
    assert.equal(await evaluate(`localStorage.getItem('${timelineEmphasisKey}')`), null, "the preset is not written until first activation");
    assert.equal(await evaluate(`document.querySelector('${customize}').disabled`), true);
    for (const type of ["plan", "step", "reasoning", "tool", "prompt", "answer"]) assert.equal(await opacity(type), 1, `${type} before opting in`);
    const originalToolBackground = await evaluate(`getComputedStyle(document.querySelector('${message("tool")}')).backgroundColor`);
    await evaluate("document.querySelector('#settings-dim-intermediate').focus()");
    await key(" ", "Space", 32);
    assert.equal(await evaluate("document.querySelector('#settings-dim-intermediate').checked"), true);
    assert.deepEqual(await evaluate(`JSON.parse(localStorage.getItem('${timelineEmphasisKey}')).types`), defaultTimelineEmphasis.types, "first activation saves all preset properties");
    assert.equal(await opacity("plan"), 1);
    assert.equal(await evaluate(`getComputedStyle(document.querySelector('${message("tool")}')).backgroundColor`), originalToolBackground, "baseline tint preserves the actual group surface");
    assert.equal(await opacity("step"), 0.7);
    assert.equal(await opacity("reasoning"), 0.5);
    for (const type of ["prompt", "answer", "attention"]) assert.equal(await opacity(type), 1, type);
    assert.deepEqual(await protectedOpacities(), [1, 1, 1, 1, 1, 1, 1], "warnings, permission requests, unsuccessful operations and system-prompt warnings stay prominent");
    await evaluate(`document.querySelector('${message("prompt")} button[aria-label="Fork from here"]').click()`);
    await evaluate(`document.querySelector('${message("prompt")} button[aria-label="Rewind to here"]').click()`);
    assert.deepEqual(await evaluate("emphasisFixture.branches"), [{ offset: "1", mode: "fork" }, { offset: "1", mode: "rewind" }],
      "the extracted renderer preserves upstream fork/rewind actions on journal prompts");
    assert.equal(await evaluate(`getComputedStyle(document.querySelector('${message("plan")} .message-body')).transitionDuration`), "0s", "reduced motion removes the transition");

    // Already-open histories follow a change, without a remount.
    await evaluate("emphasisFixture.histories(2)");
    assert.equal(await wait(`!!document.querySelector('${message("plan", 1)}')`), true);
    await open();
    assert.equal(await evaluate(`document.querySelectorAll('${dialog} table tbody tr').length`), 4);
    assert.deepEqual(await evaluate(`Array.from(document.querySelectorAll('${dialog} table thead th[scope="col"]')).map(header => header.textContent)`), ["Appearance", "Normal", "Hover / keyboard focus"]);
    assert.equal(await evaluate(`document.querySelectorAll('${dialog} nav button').length`), 11);
    assert.equal(await evaluate(`Array.from(document.querySelectorAll('${dialog} select[aria-label^="Opacity"] option')).every(option => /^\\d+%$/.test(option.textContent))`), true, "both opacity columns show only percentages");
    await screenshot("wide-dark");
    await select("Opacity of Plans", 60);
    await chooseType("plan");
    await evaluate(`document.querySelector('select[aria-label="Opacity of Plans"]').focus()`);
    await key("ArrowDown", "ArrowDown", 40);
    assert.equal(await evaluate("document.activeElement.value"), "65", "the opacity select works with arrow keys");
    await key("Tab", "Tab", 9);
    assert.equal(await evaluate("document.activeElement.getAttribute('aria-label')"), "Opacity of Plans on hover or focus", "Tab enters the hover/focus column");
    await key("ArrowUp", "ArrowUp", 38);
    assert.equal(await evaluate("document.activeElement.value"), "95", "the hover/focus select works with arrow keys");
    await select("Opacity of Plans", 35);
    await select("Opacity of Plans on hover or focus", 80);
    await select("Opacity of Reasoning", 100);
    await close();
    for (const history of [0, 1]) {
      assert.equal(await opacity("plan", history), 0.35);
      assert.equal(await opacity("reasoning", history), 1);
      assert.equal(await opacity("attention", history), 1);
      assert.deepEqual(await protectedOpacities(history), [1, 1, 1, 1, 1, 1, 1]);
    }
    assert.equal(await evaluate(`JSON.parse(localStorage.getItem('${timelineEmphasisKey}')).types.plan.opacity`), 35);

    // The opacity belongs to the real message, including its controls, on hover and keyboard focus.
    const box = await evaluate<{ x: number; y: number }>(`(() => { const rect = document.querySelector('${message("plan")}').getBoundingClientRect(); return { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 }; })()`);
    await command("Input.dispatchMouseEvent", { type: "mouseMoved", ...box });
    assert.equal(await opacity("plan"), 0.8);
    assert.equal(await opacity("plan", 1), 0.35);
    await command("Input.dispatchMouseEvent", { type: "mouseMoved", x: 1590, y: 990 });
    await pointerFocus(message("plan"));
    assert.equal(await opacity("plan"), 0.35, "incidental mouse focus does not keep an entry bright");
    await key("Tab", "Tab", 9);
    assert.equal(await opacity("plan"), 0.8, "switching from mouse to keyboard focus emphasizes the entry");
    await tabInto(message("plan"));
    assert.equal(await opacity("plan"), 0.8);
    assert.equal(await opacity("plan", 1), 0.35, "another history is not focused");
    await key("Tab", "Tab", 9);
    assert.equal(await opacity("plan"), 0.8, "Tab between the row's controls keeps it emphasized");
    await tabUntil(`!document.querySelector('${message("plan")}').contains(document.activeElement)`);
    assert.equal(await opacity("plan"), 0.35);
    await key("Tab", "Tab", 9, 8);
    assert.equal(await opacity("plan"), 0.8, "Shift+Tab back into the row emphasizes it");
    await evaluate("document.activeElement.blur()");
    const toolBox = await evaluate<{ x: number; y: number }>(`(() => { const rect = document.querySelector('${message("tool")}').getBoundingClientRect(); return { x: rect.left + 10, y: rect.top + 10 }; })()`);
    await command("Input.dispatchMouseEvent", { type: "mouseMoved", ...toolBox });
    assert.equal(await opacity("tool"), 1);
    await command("Input.dispatchMouseEvent", { type: "mouseMoved", x: 1590, y: 990 });
    assert.equal(await opacity("tool"), 0.7);
    await pointerFocus(message("tool"));
    assert.equal(await opacity("tool"), 0.7, "mouse focus does not pin a tool group at hover opacity");
    await tabInto(message("tool"));
    assert.equal(await opacity("tool"), 1);
    assert.equal(await opacity("tool", 1), 0.7);
    assert.equal(await evaluate(`document.querySelectorAll('${message("tool")} [data-tool-phase="completed"]').length`), 2);
    await tabUntil(`document.querySelectorAll('${message("tool")} [data-tool-phase="completed"]')[1].contains(document.activeElement)`);
    assert.equal(await opacity("tool"), 1, "keyboard focus in either tile emphasizes the whole group");
    assert.deepEqual(await evaluate(`Array.from(document.querySelectorAll('${message("tool")} > .message')).map(tile => Number(getComputedStyle(tile).opacity))`), [1, 1], "tile shells never compound content opacity");
    await tabUntil(`!document.querySelector('${message("tool")}').contains(document.activeElement)`);
    assert.equal(await opacity("tool"), 0.7);
    await evaluate("document.activeElement.blur()");

    // Raising the resting opacity floors hover/focus without overwriting the user's previous hover choice.
    await open();
    await select("Opacity of Plans", 90);
    assert.equal(await evaluate(`document.querySelector('select[aria-label="Opacity of Plans on hover or focus"]').value`), "90");
    assert.equal(await evaluate(`document.querySelector('select[aria-label="Opacity of Plans on hover or focus"] option[value="80"]').disabled`), true);
    assert.equal(await evaluate(`JSON.parse(localStorage.getItem('${timelineEmphasisKey}')).types.plan.hoverOpacity`), 80);
    await close();
    await tabInto(message("plan"));
    assert.equal(await opacity("plan"), 0.9, "keyboard focus never lowers the resting opacity");
    await open();
    await select("Opacity of Plans", 35);
    assert.equal(await evaluate(`document.querySelector('select[aria-label="Opacity of Plans on hover or focus"]').value`), "80", "the previous hover choice is restored when resting opacity drops");
    await close();

    // Independent paint settings use the same production components, with explicit states and no host reads.
    await open();
    await select("Background tint of Plans", 8);
    await select("Background tint of Plans on hover or focus", 14);
    await select("Outer border of Plans", "false");
    await select("Outer border of Plans on hover or focus", "true");
    await select("Elevation of Plans on hover or focus", "subtle");
    await evaluate(`document.querySelector('${dialog} input[type="checkbox"]').click()`);
    await frames();
    const preview = (state: string) => `${dialog} [data-preview-state="${state}"]`;
    assert.equal(await evaluate(`Number(getComputedStyle(document.querySelector('${preview("normal")} .message-body')).opacity)`), 0.35);
    assert.equal(await evaluate(`Number(getComputedStyle(document.querySelector('${preview("hover")} .message-body')).opacity)`), 0.8);
    const normalBox = await evaluate<{ x: number; y: number }>(`(() => { const box = document.querySelector('${preview("normal")} .message').getBoundingClientRect(); return {x:box.left+10,y:box.top+10}; })()`);
    await command("Input.dispatchMouseEvent", { type: "mouseMoved", ...normalBox });
    assert.equal(await evaluate(`Number(getComputedStyle(document.querySelector('${preview("normal")} .message-body')).opacity)`), 0.35, "Normal preview does not become Hover under the pointer");
    assert.equal(await evaluate(`getComputedStyle(document.querySelector('${preview("normal")} .message')).borderTopColor`), "rgba(0, 0, 0, 0)");
    assert.notEqual(await evaluate(`getComputedStyle(document.querySelector('${preview("hover")} .message')).boxShadow`), "none");
    assert.doesNotMatch(await evaluate<string>(`getComputedStyle(document.querySelector('${preview("hover")} .message')).boxShadow`), /inset/, "elevation is an outer shadow, not an additional border");
    assert.equal(await evaluate(`getComputedStyle(document.querySelector('${preview("normal")} .message')).borderLeftColor === getComputedStyle(document.querySelector('${preview("hover")} .message')).borderLeftColor`), true, "left accent remains un-dimmed in both states");
    assert.equal(await evaluate(`document.querySelector('${preview("normal")} .message').getBoundingClientRect().height === document.querySelector('${preview("hover")} .message').getBoundingClientRect().height`), true, "state changes do not shift geometry");
    assert.equal(await evaluate(`getComputedStyle(document.querySelector('${preview("normal")} .message-actions button')).opacity`), "0");
    assert.equal(await evaluate(`getComputedStyle(document.querySelector('${preview("hover")} .message-actions button')).opacity`), "1", "forced Hover reveals actual action buttons");
    assert.equal(await evaluate(`getComputedStyle(document.querySelector('${preview("normal")} .message-actions')).opacity`), "1", "timestamps are not hidden with action buttons");
    const originalAccent = await evaluate(`getComputedStyle(document.querySelector('${preview("normal")} .message')).borderLeftColor`);
    const originalTint = await evaluate(`getComputedStyle(document.querySelector('${preview("normal")} .message')).backgroundColor`);
    await evaluate("document.documentElement.style.setProperty('--green', '#237753')");
    assert.notEqual(await evaluate(`getComputedStyle(document.querySelector('${preview("normal")} .message')).borderLeftColor`), originalAccent, "existing scheme role controls the left accent");
    assert.notEqual(await evaluate(`getComputedStyle(document.querySelector('${preview("normal")} .message')).backgroundColor`), originalTint, "the tint follows that same role");
    await evaluate("document.documentElement.style.removeProperty('--green')");
    const reads = await evaluate<number>("emphasisFixture.readCalls()");
    for (const type of timelineEntryTypes) {
      await chooseType(type);
      assert.equal(await evaluate(`document.querySelectorAll('${dialog} [data-preview-state] [data-entry-type="${type}"]').length`), 2, `${type} has both real-component previews`);
      assert.equal(await evaluate(`document.querySelectorAll('${dialog} input[type="checkbox"]').length`), type === "tool" ? 0 : 1, `${type} only offers accent dimming when it has a left accent`);
    }
    assert.equal(await evaluate("emphasisFixture.readCalls()"), reads, "previews never load host history");
    await select("Background tint of System prompt on hover or focus", 20);
    const promptHoverTint = await evaluate<string>(`getComputedStyle(document.querySelector('${preview("hover")} .message-prompt')).backgroundColor`);
    await close();
    await evaluate(`document.querySelector('${message("systemPrompt")}').scrollIntoView({ block: 'center' })`);
    const promptBox = await evaluate<{ x: number; y: number }>(`(() => { const card = document.querySelector('${message("systemPrompt")}').getBoundingClientRect(); return { x: card.left + 10, y: card.top + 10 }; })()`);
    await command("Input.dispatchMouseEvent", { type: "mouseMoved", ...promptBox });
    await frames();
    assert.equal(await evaluate(`getComputedStyle(document.querySelector('${message("systemPrompt")}')).backgroundColor`), promptHoverTint,
      "real system-prompt hover uses the configured tint, just like its forced-hover preview");
    await open();
    await chooseType("tool");
    assert.equal(await evaluate(`document.querySelectorAll('${dialog} .tool-tile-title').length`), 4, "actual tool tiles, not imitation cards");
    await select("Opacity of Your prompts", 100);
    await select("Background tint of Your prompts on hover or focus", 14);
    await select("Elevation of Your prompts on hover or focus", "subtle");
    assert.equal(await evaluate(`Number(getComputedStyle(document.querySelector('${preview("normal")} .message-body')).opacity)`), 1);
    assert.equal(await evaluate(`getComputedStyle(document.querySelector('${preview("normal")} .message')).backgroundColor !== getComputedStyle(document.querySelector('${preview("hover")} .message')).backgroundColor`), true, "highlighting works without dimming");
    await close();
    // A real conversation has adjacent cards: the following opaque card must not paint over the hover shadow.
    await open();
    await select("Elevation of Tool calls on hover or focus", "subtle");
    await close();
    await evaluate(`document.querySelector('${message("tool")}').scrollIntoView({ block: 'center' })`);
    await command("Input.dispatchMouseEvent", { type: "mouseMoved", x: 1590, y: 990 });
    await frames();
    const shadowPoint = await evaluate<{ x: number; y: number }>(`(() => { const card = document.querySelector('${message("plan")}').getBoundingClientRect(); return { x: Math.floor(card.left + card.width * .75), y: Math.floor(card.top + 4) }; })()`);
    const shadowPixel = async () => {
      const image = await command("Page.captureScreenshot", { format: "png", clip: { ...shadowPoint, width: 1, height: 1, scale: 1 } });
      return evaluate<number[]>(`(async () => { const bitmap = await createImageBitmap(await (await fetch('data:image/png;base64,${image.data}')).blob()); const canvas = document.createElement('canvas'); canvas.width = canvas.height = 1; const context = canvas.getContext('2d'); context.drawImage(bitmap, 0, 0); bitmap.close(); return Array.from(context.getImageData(0, 0, 1, 1).data).slice(0, 3); })()`);
    };
    const restingPixel = await shadowPixel();
    const elevatedBox = await evaluate<{ x: number; y: number }>(`(() => { const card = document.querySelector('${message("tool")}').getBoundingClientRect(); return { x: card.left + 10, y: card.top + 10 }; })()`);
    await command("Input.dispatchMouseEvent", { type: "mouseMoved", ...elevatedBox });
    await frames();
    const elevatedPixel = await shadowPixel();
    assert.ok(elevatedPixel.reduce((sum, value) => sum + value, 0) < restingPixel.reduce((sum, value) => sum + value, 0) - 3,
      "the dark hover shadow remains painted over the following card, not hidden behind it");
    await command("Input.dispatchMouseEvent", { type: "mouseMoved", x: 1590, y: 990 });
    // Protection applies to the entire unsuccessful group, including its successful tile, under real keyboard focus.
    const protectedGroup = '[data-history="0"] .timeline-tool-group[data-entry-type="attention"]';
    await tabInto(protectedGroup);
    assert.equal(await evaluate(`getComputedStyle(document.querySelector('${protectedGroup}')).zIndex`), "auto", "attention groups keep their original paint layer");
    assert.deepEqual(await protectedOpacities(), [1, 1, 1, 1, 1, 1, 1]);
    assert.deepEqual(await evaluate(`Array.from(document.querySelectorAll('[data-history="0"] [data-entry-type="attention"] .message')).map(element => {
      let opacity = 1; for (let node = element; node; node = node.parentElement) opacity *= Number(getComputedStyle(node).opacity); return opacity; })`), [1, 1, 1, 1, 1, 1, 1, 1, 1]);
    await evaluate("document.activeElement.blur()");

    // The same controls fit the narrow desktop layout in either theme, and Reset restores all types.
    await command("Emulation.setDeviceMetricsOverride", { width: 900, height: 650, deviceScaleFactor: 1, mobile: false });
    await open();
    await chooseType("prompt");
    for (const dark of [true, false]) {
      await evaluate(`emphasisFixture.theme(${dark})`);
      await frames();
      const shadow = await evaluate<string>(`getComputedStyle(document.querySelector('${preview("hover")} .message')).boxShadow`);
      assert.notEqual(shadow, "none", `elevation stays visible in ${dark ? "dark" : "light"}`);
      assert.doesNotMatch(shadow, /inset/, "elevation does not thicken the border in either theme");
      assert.match(shadow, dark ? /4px 12px/ : /3px 10px/, "dark backgrounds need stronger, broader elevation than light backgrounds");
      assert.deepEqual(await evaluate(`(() => { const problems = [], window = document.querySelector('${dialog} .app-window'), box = window.getBoundingClientRect();
        for (const control of window.querySelectorAll('.timeline-appearance-properties :is(th, td, select)')) { const rect = control.getBoundingClientRect();
          if (rect.left < box.left || rect.right > box.right) problems.push('control outside window'); }
        const body = window.querySelector('.app-window-body');
        if (body.scrollWidth > body.clientWidth) problems.push('horizontal overflow'); return problems; })()`), [], `narrow ${dark ? "dark" : "light"}`);
      await screenshot(`narrow-${dark ? "dark" : "light"}`);
    }
    // At the dialog's minimum width, headings and long translated entry names wrap without moving controls outside their cells.
    await command("Emulation.setDeviceMetricsOverride", { width: 400, height: 650, deviceScaleFactor: 1, mobile: false });
    for (const locale of ["en", "es", "fr", "de", "ja", "zh-CN"] as const) {
      await evaluate(`emphasisFixture.language('${locale}')`);
      for (const dark of [true, false]) {
        await evaluate(`emphasisFixture.theme(${dark})`);
        await frames();
        assert.deepEqual(await evaluate(`Array.from(document.querySelectorAll('${dialog} thead th')).map(header => header.textContent)`),
          (["Appearance", "Normal", "Hover / keyboard focus"] as const).map(header => translate(locale, header)));
        assert.deepEqual(await evaluate(`(() => { const problems = [], body = document.querySelector('${dialog} .app-window-body'), table = body.querySelector('table');
          if (table.getBoundingClientRect().width > 330) problems.push('not at minimum width');
          if (body.scrollWidth > body.clientWidth) problems.push('horizontal overflow');
          for (const control of table.querySelectorAll('select, .bp6-switch')) { const rect = control.getBoundingClientRect(), cell = control.closest('td').getBoundingClientRect();
            if (rect.left < cell.left || rect.right > cell.right) problems.push('control outside cell'); }
          return problems; })()`), [], `minimum-width ${locale} ${dark ? "dark" : "light"}`);
        if (locale === "en" || locale === "de") await screenshot(`minimum-${locale}-${dark ? "dark" : "light"}`);
      }
    }
    await evaluate("emphasisFixture.language('en')");
    await chooseType("plan");
    const customPrompt = await evaluate(`JSON.parse(localStorage.getItem('${timelineEmphasisKey}')).types.prompt`);
    await evaluate(`Array.from(document.querySelectorAll('${dialog} footer button')).find(button => button.textContent === 'Reset type').click()`);
    await frames();
    assert.deepEqual(await evaluate(`JSON.parse(localStorage.getItem('${timelineEmphasisKey}')).types.plan`), defaultTimelineEmphasis.types.plan);
    assert.deepEqual(await evaluate(`JSON.parse(localStorage.getItem('${timelineEmphasisKey}')).types.prompt`), customPrompt, "resetting one type preserves the other types");
    await evaluate(`Array.from(document.querySelectorAll('${dialog} footer button')).find(button => button.textContent === 'Reset all').click()`);
    await frames();
    await chooseType("plan");
    assert.equal(await evaluate(`document.querySelector('select[aria-label="Opacity of Plans"]').value`), "100");
    assert.deepEqual(await evaluate(`JSON.parse(localStorage.getItem('${timelineEmphasisKey}')).types`), defaultTimelineEmphasis.types, "Reset all restores the same complete preset as first activation");
    assert.equal(await evaluate(`JSON.parse(localStorage.getItem('${timelineEmphasisKey}')).types.reasoning.dim`), true);
    await close();
    await command("Emulation.setDeviceMetricsOverride", { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });
    await command("Input.dispatchMouseEvent", { type: "mouseMoved", x: 1590, y: 990 });
    await frames();
    assert.equal(await opacity("plan"), 1);
    await command("Emulation.setEmulatedMedia", { features: [{ name: "prefers-reduced-motion", value: "no-preference" }] });
    assert.equal(await evaluate(`getComputedStyle(document.querySelector('${message("plan")} .message-body')).transitionDuration`), "0.12s");
    await command("Emulation.setEmulatedMedia", { features: [{ name: "prefers-reduced-motion", value: "reduce" }] });

    // A failed write updates memory; after every consumer unmounts, new consumers still read that memory.
    await evaluate(`(() => { window.fixtureStorageFails = true; const write = Storage.prototype.setItem; Storage.prototype.setItem = function(key, value) {
      if (key === '${timelineEmphasisKey}' && window.fixtureStorageFails) throw new Error('fixture storage unavailable'); return write.call(this, key, value); }; })()`);
    await open();
    await select("Opacity of Plans", 35);
    assert.equal(await evaluate(`document.querySelector('${dialog} [role="status"][data-diagnostic="unsaved"]').textContent.includes('applied in this window')`), true, "failed saves are visible inside Customize");
    await evaluate("window.fixtureStorageFails = false");
    await select("Opacity of Plans", 60);
    assert.equal(await evaluate("document.querySelectorAll('[data-diagnostic=unsaved]').length"), 0, "successful saves clear both notices");
    await evaluate("window.fixtureStorageFails = true");
    await close();
    await evaluate("document.querySelector('#settings-dim-intermediate').focus()");
    await key(" ", "Space", 32);
    assert.equal(await evaluate("document.querySelector('#settings-dim-intermediate').checked"), false);
    assert.equal(await opacity("plan"), 1);
    await evaluate("emphasisFixture.histories(0); emphasisFixture.settings(false)");
    await evaluate("emphasisFixture.histories(2); emphasisFixture.settings(true)");
    assert.equal(await wait(`!!document.querySelector('${message("plan", 1)}')`), true);
    assert.equal(await evaluate("document.querySelector('#settings-dim-intermediate').checked"), false);
    assert.equal(await evaluate(`document.querySelector('${customize}').disabled`), true);
    assert.equal(await evaluate("document.querySelector('[role=status][data-diagnostic=unsaved]').textContent.includes('could not save')"), true, "later-mounted settings retain the failed-save notice");
    for (const history of [0, 1]) assert.equal(await opacity("plan", history), 1);
    assert.equal(await evaluate(`JSON.parse(localStorage.getItem('${timelineEmphasisKey}')).enabled`), true, "the failed write did not reach storage");

    // A fresh window restores the last successfully saved preference.
    await command("Page.reload");
    assert.equal(await wait(`window.emphasisFixture && !!document.querySelector('${message("plan")}')`), true);
    assert.equal(await evaluate("document.querySelector('#settings-dim-intermediate').checked"), true);
    assert.equal(await opacity("plan"), 0.6);
    assert.equal(await evaluate("document.querySelectorAll('[data-diagnostic=unsaved]').length"), 0, "a new window has no stale save notice");
    assert.deepEqual(problems, [], "no browser errors");
  } finally {
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
