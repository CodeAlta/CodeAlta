import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, waitForBrowserPort } from "./browserTarget";
import { projectRailVisibilityKey } from "./explorer/projectRailVisibility";
import { landingStartupKey } from "./landing/landingPreferences";
import { languageStorageKey } from "./localization";

test("a session title the Explorer cuts and a settings page name the navigation cuts are shown whole, on hover and on keyboard focus",
  { skip: !browserExecutable, timeout: 90_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-truncated-labels-"));
  let browser: ReturnType<typeof spawn> | undefined, socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./main.tsx", import.meta.url))], outfile: join(root, "fixture.js"),
      bundle: true, platform: "browser", format: "iife", loader: { ".css": "empty", ".flf": "text", ".svg": "dataurl" },
      define: { "import.meta.env.VITE_DEMO_MODE": '"false"' }, plugins: [{ name: "isolated-shell", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./settingsOrganization.neoastra.mount.ts", import.meta.url)) }));
        bundle.onResolve({ filter: /^monaco-editor\/.*\?worker$/ }, args => ({ path: args.path, namespace: "fixture-worker" }));
        bundle.onLoad({ filter: /.*/, namespace: "fixture-worker" }, async () => {
          const worker = await build({ entryPoints: [fileURLToPath(new URL("../node_modules/monaco-editor/esm/vs/editor/editor.worker.js", import.meta.url))],
            bundle: true, platform: "browser", format: "iife", write: false });
          return { loader: "js", contents: `export default class extends Worker { constructor() {
            const url=URL.createObjectURL(new Blob([${JSON.stringify(worker.outputFiles[0].text)}],{type:"text/javascript"}));
            super(url); URL.revokeObjectURL(url);
          } }` };
        });
      } }] });
    await writeFile(join(root, "style.css"), (await Promise.all([
      "../node_modules/@blueprintjs/core/lib/css/blueprint.css", "../node_modules/flexlayout-react/style/dark.css", "./style.css",
    ].map(path => readFile(new URL(path, import.meta.url), "utf8")))).join("\n"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(browserExecutable!, [...browserBaseArgs, "--allow-file-access-from-files", `--user-data-dir=${join(root, "profile")}`,
      "--remote-debugging-port=0", "about:blank"], { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(root, "profile"), browser);
    const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json() as { type: string; webSocketDebuggerUrl: string }[];
    socket = new WebSocket(pages.find(tab => tab.type === "page")!.webSocketDebuggerUrl);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", reject, { once: true }); });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<Record<string, any>>((resolve, reject) => {
      const id = ++sequence, timer = setTimeout(() => reject(Error(`${method} timed out`)), 12_000);
      const reply = (event: MessageEvent) => {
        const message = JSON.parse(String(event.data)); if (message.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer);
        if (message.error) reject(Error(JSON.stringify(message.error))); else resolve(message.result ?? {});
      };
      socket!.addEventListener("message", reply); socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async (expression: string) => {
      const value = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(value.exceptionDetails, undefined, JSON.stringify(value.exceptionDetails)); return value.result?.value;
    };
    const until = async (condition: string, message?: string) => assert.equal(await evaluate(`new Promise(resolve=>{const end=Date.now()+7000;const tick=()=>{
      let ok=false;try{ok=!!(${condition})}catch{}if(ok)resolve(true);else if(Date.now()>end)resolve(false);else setTimeout(tick,20)};tick()})`), true, message ?? condition);
    const pause = (milliseconds: number) => new Promise(resolve => setTimeout(resolve, milliseconds));
    // The pointer rests on an element, as a user's does.
    const hover = async (selector: string) => {
      const box = await evaluate(`(()=>{const node=${selector};node.scrollIntoView({block:'nearest'});const box=node.getBoundingClientRect();return {x:box.x+box.width/2,y:box.y+box.height/2}})()`);
      // The scroll is over before the pointer comes.
      await pause(80);
      await command("Input.dispatchMouseEvent", { type: "mouseMoved", x: box.x, y: box.y });
    };
    const capture = async (name: string) => {
      if (!process.env.TRUNCATED_LABELS_SHOTS) return;
      const shot = await command("Page.captureScreenshot", { format: "png" });
      await writeFile(join(process.env.TRUNCATED_LABELS_SHOTS, `${name}.png`), Buffer.from(shot.data, "base64"));
    };
    // Tab, or Shift+Tab, as a user presses it.
    const tab = async (back: boolean) => {
      for (const type of ["rawKeyDown", "keyUp"]) await command("Input.dispatchKeyEvent", { type, key: "Tab", code: "Tab", windowsVirtualKeyCode: 9, modifiers: back ? 8 : 0 });
    };
    const away = () => command("Input.dispatchMouseEvent", { type: "mouseMoved", x: 900, y: 600 });
    const tip = "document.querySelector('.overflow-tip')";
    // The tip is whole: in the window, wider than the label it completes, and its words are not cut.
    const whole = `(()=>{const tip=${tip};if(!tip)return false;const box=tip.getBoundingClientRect();
      return box.left>=0&&box.top>=0&&box.right<=innerWidth&&box.bottom<=innerHeight&&tip.scrollWidth<=tip.clientWidth&&tip.scrollHeight<=tip.clientHeight})()`;

    await command("Page.enable"); await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    await command("Emulation.setDeviceMetricsOverride", { width: 1280, height: 900, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    await until("!!window.settingsOrganization");
    await evaluate(`localStorage.clear();localStorage.setItem(${JSON.stringify(languageStorageKey)},'fr');localStorage.setItem('ideFixtureLongLabels','true');
      localStorage.setItem(${JSON.stringify(projectRailVisibilityKey)},'expanded');localStorage.setItem(${JSON.stringify(landingStartupKey)},'off');
      localStorage.setItem('settingsFixtureOwned','true')`);
    await command("Page.reload");
    await until("!!document.querySelector('.session-row[data-session-id=two]')");
    // The project is opened, to show its sessions: whether it is already depends on when its selection comes.
    await evaluate("(()=>{const project=document.querySelector('button[data-scope=project]');if(project.getAttribute('aria-expanded')!=='true')project.querySelector('.tree-twist').click()})()");
    await until("!document.querySelector('.session-row[data-session-id=one]').closest('[hidden]')", "The sessions of the project are shown.");

    // A long title is cut by the width of the Explorer; the row keeps its whole title for a screen reader.
    const row = "document.querySelector('.session-row[data-session-id=one] > button')";
    const full = "Literal Sitzung 日本語 <session> ".repeat(6).trim();
    await until(`(()=>{const title=${row}.querySelector('.session-title');return title.scrollWidth>title.clientWidth})()`, "The title is cut.");
    assert.equal(await evaluate(`document.getElementById(${row}.getAttribute('aria-describedby')).textContent.startsWith(${JSON.stringify(full)})`), true);
    // The pointer resting on it shows the whole title, over the panel that cuts it; the native tooltip is put aside meanwhile.
    await hover(row);
    await until(`${tip}?.textContent===${JSON.stringify(full)}`, "The tip shows the whole title.");
    await until(whole, "The tip is not cut.");
    assert.equal(await evaluate(`${tip}.getBoundingClientRect().width>${row}.querySelector('.session-title').clientWidth`), true);
    assert.equal(await evaluate(`${tip}.getAttribute('aria-hidden')`), "true");
    assert.equal(await evaluate(`${row}.hasAttribute('title')`), false);
    await capture("session-title");
    await away();
    await until(`!${tip}`, "The tip leaves with the pointer.");
    assert.equal(await evaluate(`${row}.getAttribute('title').startsWith(${JSON.stringify(full)})`), true, "The native tooltip is back.");
    // A title that the row shows whole has no tip.
    await hover("document.querySelector('.session-row[data-session-id=two] > button')");
    await pause(700);
    assert.equal(await evaluate(`!${tip}`), true);
    await away();
    // The keyboard on the row shows its tip at once; leaving the row takes it away. A focus that is not the keyboard's
    // (a click, a script) shows none.
    await evaluate(`${row}.focus()`);
    await pause(500);
    assert.equal(await evaluate(`!${tip}`), true, "A focus without the keyboard shows no tip.");
    await evaluate("document.querySelector('.session-row[data-session-id=one] .session-actions-trigger').focus()");
    await tab(true);
    await until(`document.activeElement===${row} && ${tip}?.textContent===${JSON.stringify(full)}`, "The keyboard shows the tip.");
    await tab(false);
    await tab(false);
    await until(`document.activeElement===document.querySelector('.session-row[data-session-id=two] > button') && !${tip}`, "The tip of another row that shows its title whole is none.");

    // Settings in French: the long names of the navigation take a second line instead of being cut.
    await evaluate("document.querySelector('button.activity-settings').click()");
    await until("!!document.querySelector('.settings-dialog-navigation')");
    const label = (text: string) => `[...document.querySelectorAll('.settings-dialog-navigation button > span')].find(span=>span.textContent===${JSON.stringify(text)})`;
    for (const text of ["Instructions de l’agent", "Fichier de configuration", "Journaux de l’application"]) {
      assert.equal(await evaluate(`(()=>{const span=${label(text)};return span.scrollWidth<=span.clientWidth&&span.scrollHeight<=span.clientHeight})()`), true, `${text} is whole`);
    }
    assert.equal(await evaluate(`(()=>{const span=${label("Journaux de l’application")};return span.getClientRects().length===1&&span.clientHeight>parseFloat(getComputedStyle(span).lineHeight)*1.5})()`), true,
      "A long name takes two lines.");
    await hover(`${label("Fichier de configuration")}.parentElement`);
    await pause(700);
    await capture("settings-french");
    assert.equal(await evaluate(`!${tip}`), true, "A name shown whole has no tip.");
    // A navigation narrower still cuts the names: the tip shows them whole, in the window of the settings.
    await evaluate("document.querySelector('.settings-dialog-navigation').style.flexBasis='140px'");
    await until(`(()=>{const span=${label("Journaux de l’application")};return span.scrollHeight>span.clientHeight||span.scrollWidth>span.clientWidth})()`, "The name is cut.");
    await hover(`${label("Journaux de l’application")}.parentElement`);
    await until(`${tip}?.textContent==='Journaux de l’application'`, "The tip shows the whole name.");
    await until(whole);
    await capture("settings-narrow");
    assert.equal(await evaluate(`!!${tip}.closest('dialog.settings-dialog')`), true, "The tip is drawn in the window of the settings, above the page.");
    await away();
    await until(`!${tip}`);
    await evaluate(`${label("Instructions de l’agent")}.parentElement.previousElementSibling.focus()`);
    await tab(false);
    await until(`${tip}?.textContent==='Instructions de l’agent'`, "The keyboard shows the tip of a cut name.");
    await tab(false);
    await until(`document.activeElement!==${label("Instructions de l’agent")}.parentElement && ${tip}?.textContent!=='Instructions de l’agent'`, "The tip follows the keyboard.");
  } finally {
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
