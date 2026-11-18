import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, endBrowser, waitForBrowserPort } from "./browserTarget";
import { canvasTab, editorTab, fileTabsKey, issuesTab, type FileTabs } from "./fileTabs";
import { landingStartupKey } from "./landing/landingPreferences";
import { sessionTabsKey, type SessionTabs } from "./sessionTabs";
import { defaultSpaceId, shownSpaceKey } from "./spaces/spaces";
import { devToolsAnswerLimit, pageWaitLimit, testLimit, testLimitRanOut } from "./testTime";

const edge = browserExecutable;

type Box = [left: number, top: number, width: number, height: number];
type Frame = { mark: Box; explorer: Box; content: Box; tabs: number };
// Where the parts of the frame are: the mark of the title bar, the Explorer, the content, and the room the first
// tab strip leaves at its start.
const frame = `(() => {
  const box = selector => { const rect = document.querySelector(selector).getBoundingClientRect(); return [rect.left, rect.top, rect.width, rect.height].map(Math.round); };
  return { mark: box(".window-brand"), explorer: box(".session-content-rail-slot"), content: box(".session-content-main-panel"),
    tabs: Math.round(parseFloat(getComputedStyle(document.querySelector(".flexlayout__tabset_tabbar_outer[data-titlebar-start]")).paddingLeft)) };
})()`;
// The height of the title bar, the width the mark takes when it is alone in a narrow window and in a wide one, and the
// width the Explorer starts with.
const bar = 38, mark = 270, wideMark = 298, explorer = 272;

test("the title bar keeps Explorer in place and Home opens or focuses one Welcome tab", { skip: !edge, timeout: testLimit(90_000) }, async t => {
  const root = await mkdtemp(join(tmpdir(), "codealta-window-frame-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./main.tsx", import.meta.url))], outfile: join(root, "fixture.js"),
      bundle: true, platform: "browser", format: "iife", loader: { ".css": "empty", ".flf": "text", ".svg": "dataurl" },
      define: { "import.meta.env.VITE_DEMO_MODE": '"false"' }, plugins: [{ name: "isolated-window-frame", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./windowFrame.neoastra.mount.ts", import.meta.url)) }));
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
    await writeFile(join(root, "style.css"), readFileSync(new URL("../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8")
      + readFileSync(new URL("../node_modules/flexlayout-react/style/light.css", import.meta.url), "utf8") + readFileSync(new URL("./style.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, [...browserBaseArgs, "--allow-file-access-from-files",
      `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(root, "profile"), browser);
    const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json() as { type?: string; webSocketDebuggerUrl?: string }[];
    const tab = pages.find(tab => tab.type === "page");
    assert.ok(tab?.webSocketDebuggerUrl);
    socket = new WebSocket(tab.webSocketDebuggerUrl);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", reject, { once: true }); });
    const exceptions: string[] = [];
    socket.addEventListener("message", event => {
      const value = JSON.parse(String(event.data));
      if (value.method === "Runtime.exceptionThrown") exceptions.push(value.params.exceptionDetails.exception?.description ?? value.params.exceptionDetails.text);
    });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<Record<string, any>>((resolve, reject) => {
      const id = ++sequence;
      const reply = (event: MessageEvent) => {
        const value = JSON.parse(String(event.data));
        if (value.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer);
        value.error ? reject(new Error(JSON.stringify(value.error))) : resolve(value.result);
      };
      const timer = setTimeout(() => { socket!.removeEventListener("message", reply); reject(new Error(testLimitRanOut(devToolsAnswerLimit, `the answer to ${method}`))); }, devToolsAnswerLimit);
      socket!.addEventListener("message", reply); socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async (expression: string) => {
      const value = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(value.exceptionDetails, undefined, JSON.stringify(value.exceptionDetails)); return value.result?.value;
    };
    const wait = (condition: string) => evaluate(`new Promise(resolve=>{const end=Date.now()+${pageWaitLimit};const tick=()=>{if(${condition})resolve(true);else if(Date.now()>end)resolve(document.body.innerText.slice(0,600));else setTimeout(tick,25);};tick();})`);
    const frames = () => evaluate("new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(()=>requestAnimationFrame(()=>resolve(true)))))");
    const toggle = `document.querySelector('.activity-rail button[aria-controls="project-rail"]')`;
    const size = async (width: number, height: number) => {
      await command("Emulation.setDeviceMetricsOverride", { width, height, deviceScaleFactor: 1, mobile: false });
      assert.equal(await wait(`innerWidth===${width} && innerHeight===${height}`), true);
      // The page learns that the window turned narrow or wide on a later frame, through its media query: until then the
      // Explorer button can still say what the old width showed, and a click would undo what the new width shows.
      await frames();
    };
    // The frame once the Explorer is as asked: shown, by a click on its button when it is not, or hidden.
    const shown = async (open: boolean) => {
      if (await evaluate(`${toggle}.getAttribute('aria-expanded')`) !== String(open)) await evaluate(`${toggle}.click()`);
      assert.equal(await wait(`${toggle}.getAttribute('aria-expanded')===${JSON.stringify(String(open))}`), true);
      await frames();
      return await evaluate(frame) as Frame;
    };

    await command("Page.enable");
    await command("Runtime.enable");
    await command("Page.addScriptToEvaluateOnNewDocument", { source: `Object.defineProperty(navigator,'languages',{get:()=>['en-US']});
      localStorage.setItem('settingsFixtureOwned','true');` });
    const initial = await command("Page.addScriptToEvaluateOnNewDocument", { source: `localStorage.setItem(${JSON.stringify(landingStartupKey)},'off');` });
    await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    await size(800, 900);
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait(`!!document.querySelector('#catalog-prompt, #session-prompt') && !!document.querySelector('.flexlayout__tabset_tabbar_outer[data-titlebar-start]')`), true, exceptions.join("\n"));

    // A narrow window starts without the Explorer: the mark is at the start of the title bar, the first tab strip
    // starts after it, and the content has the whole window.
    const alone: Frame = { mark: [0, 0, mark, bar], explorer: [0, 0, 0, 0], content: [0, 0, 800, 900], tabs: mark };
    assert.equal(await evaluate(`${toggle}.getAttribute('aria-expanded')`), "false");
    assert.deepEqual(await shown(false), alone);

    // Its button shows the Explorer over the tabs, under the title bar: nothing else moves.
    assert.deepEqual(await shown(true), { ...alone, explorer: [0, bar, 320, 900 - bar] });
    await size(800, 560);
    assert.deepEqual(await shown(true), { ...alone, explorer: [0, bar, 320, 560 - bar], content: [0, 0, 800, 560] });
    assert.deepEqual(await shown(false), { ...alone, content: [0, 0, 800, 560] });

    // A wide window gives the Explorer a column, with the mark above it and its divider, which is before the content.
    await size(1280, 900);
    assert.deepEqual(await shown(true), { mark: [0, 0, explorer + 2, bar], explorer: [0, bar, explorer, 900 - bar], content: [explorer + 2, 0, 1280 - explorer - 2, 900], tabs: 0 });
    // Without the Explorer, a wide window gives the mark the room of all its buttons.
    assert.deepEqual(await shown(false), { ...alone, mark: [0, 0, wideMark, bar], tabs: wideMark, content: [0, 0, 1280, 900] });

    const home = `document.querySelector('.window-actions button[aria-label="Home"]')`;
    const welcomeTabs = `Array.from(document.querySelectorAll('.flexlayout__tab_button')).filter(tab=>tab.textContent.trim()==='Welcome')`;
    assert.equal(await wait(`${home} && !${home}.disabled`), true, "The top bar offers an accessible Home button");
    assert.equal(await evaluate(`${home}.title`), "Home");
    assert.equal(await evaluate(`${welcomeTabs}.length`), 0, "Startup preference stays off");
    await evaluate(`${home}.focus()`);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    assert.equal(await wait(`${welcomeTabs}.length === 1 && ${home}.classList.contains('bp6-active')`), true);
    await evaluate(`${home}.click(); ${home}.click()`);
    assert.equal(await wait("windowFrameFixture.commands.length === 3"), true);
    await frames();
    assert.equal(await evaluate(`${welcomeTabs}.length`), 1, "Repeated clicks reuse the tab");
    assert.deepEqual(await evaluate("windowFrameFixture.commands.map(command=>command.commandId)"), ["welcome-command", "welcome-command", "welcome-command"]);
    assert.equal(await evaluate("windowFrameFixture.opens.every(open=>open.projectId===null && open.sessionId===null && open.key===null)"), true);
    await evaluate("document.querySelector('.activity-issues').click()");
    assert.equal(await wait(`!${home}.classList.contains('bp6-active')`), true);
    await evaluate(`${home}.click()`);
    assert.equal(await wait(`${welcomeTabs}.length === 1 && ${home}.classList.contains('bp6-active')`), true, "Home focuses Welcome from another tab");
    // The control remains visible and clickable in both themes, even in a narrow window.
    for (const width of [1280, 390]) {
      await size(width, 650);
      for (const dark of [false, true]) {
        const theme = dark ? "dark" : "light";
        assert.equal(await evaluate(`(async()=>{for(let i=0;i<4 && document.documentElement.dataset.theme!==${JSON.stringify(theme)};i++) {
          document.querySelector('.theme-switch').click(); await new Promise(resolve=>requestAnimationFrame(resolve));
        } return document.documentElement.dataset.theme})()`), theme);
        assert.equal(await evaluate(`(()=>{const e=${home}, r=e.getBoundingClientRect(), icon=e.querySelector('svg');
          return r.width>=28 && r.height>=28 && r.left>=0 && r.right<=innerWidth && r.top>=0 && r.bottom<=38
            && e.contains(document.elementFromPoint(r.left+r.width/2,r.top+r.height/2))
            && !!icon && getComputedStyle(icon).stroke!=='none' && getComputedStyle(e).visibility==='visible'})()`), true, `Home at ${width}px, dark=${dark}`);
      }
    }
    assert.equal(await evaluate("localStorage.getItem('codealta.desktop.landing.startup.v1')"), "off", "Opening Welcome never changes startup");
    // Cmd+W comes from the native menu, not a DOM key: close the focused tab, never the window.
    await evaluate(`projectFocusFixture.notify({kind:'close-tab',runningSessions:0,busyTerminals:0})`);
    assert.equal(await wait(`${welcomeTabs}.length === 0`), true, "The native close action closes Welcome");
    await evaluate(`projectFocusFixture.notify({kind:'close-tab',runningSessions:0,busyTerminals:0})`);
    assert.equal(await wait(`document.querySelectorAll('.flexlayout__tab_button').length === 2 && !!document.querySelector('#catalog-prompt')`), true, "Closing the session returns to a prompt");
    await evaluate(`[...document.querySelectorAll('.flexlayout__tab_button')].find(tab => tab.textContent.trim() === 'Issues').click()`);
    await frames();
    await evaluate(`projectFocusFixture.notify({kind:'close-tab',runningSessions:0,busyTerminals:0})`);
    assert.equal(await wait(`document.querySelectorAll('.flexlayout__tab_button').length === 1 && !!document.querySelector('#catalog-prompt, #session-prompt')`), true);
    await evaluate(`projectFocusFixture.notify({kind:'close-tab',runningSessions:0,busyTerminals:0})`);
    await frames();
    assert.equal(await evaluate(`document.querySelectorAll('.flexlayout__tab_button').length`), 1, "The final prompt tab stays open");
    assert.equal(await evaluate(`!!document.querySelector('[role="alertdialog"]')`), false, "No exit or close-window question");
    await evaluate("windowFrameFixture.enabled=false; projectFocusFixture.notify({kind:'plugins-changed',runningSessions:0,busyTerminals:0})");
    assert.equal(await wait(`${home}.disabled`), true, "Home is unavailable when the built-in Landing page is disabled, not routed to another plugin");

    await command("Page.removeScriptToEvaluateOnNewDocument", { identifier: initial.identifier });
    await size(1280, 900);
    const reload = async (sessions: Omit<SessionTabs, "closed"> | null, files: Omit<FileTabs, "closed">,
      options: { deferWelcome?: boolean; spaceId?: string } = {}) => {
      const stored = {
        [landingStartupKey]: "on",
        [sessionTabsKey]: sessions ? JSON.stringify({ version: 1, ...sessions }) : null,
        [fileTabsKey]: JSON.stringify({ version: 1, ...files, beforeRestore: true }),
        [shownSpaceKey]: options.spaceId ?? null,
        windowFrameFixtureDeferWelcome: String(options.deferWelcome ?? false),
      };
      await evaluate(`window.fixtureReloading = true;
        for (const [key, value] of Object.entries(${JSON.stringify(stored)}))
          if (value === null) localStorage.removeItem(key); else localStorage.setItem(key, value);`);
      await command("Page.reload");
      assert.equal(await wait(`!window.fixtureReloading && window.windowFrameFixture?.catalogReads > 0
        && !JSON.parse(localStorage.getItem(${JSON.stringify(fileTabsKey)}))?.beforeRestore`), true, "the new window restored its tabs and read the canvases");
      await frames();
    };
    const homeActive = `${home}.classList.contains('bp6-active')`;
    const noSessions = { open: [], active: null };
    const noFiles = { open: [], active: null };
    const issuesActive = "document.querySelector('.activity-issues').classList.contains('bp6-active')";
    const session = { projectId: "project", path: "/fixture/project", sessionId: "one" };
    const savedSession = { open: [session], active: session };
    const selectedSessionLabel = "document.querySelector('.flexlayout__tab_button--selected [data-session-node]')?.textContent.trim()";
    await t.test("Welcome opens when the saved tabs no longer exist", async () => {
      const removed = { projectId: "removed", path: "/fixture/removed" };
      const gone = { ...removed, sessionId: "gone" };
      const editor = editorTab({ id: removed.projectId, path: removed.path });
      await reload({ open: [gone], active: gone, drafts: [removed] }, { open: [editor], active: editor });
      assert.equal(await evaluate(homeActive), true);
    });
    await t.test("the implicit first session does not count as a restored tab", async () => {
      await reload(null, noFiles);
      assert.deepEqual(await evaluate(`JSON.parse(localStorage.getItem(${JSON.stringify(sessionTabsKey)})).open.map(tab => tab.sessionId)`), ["one"]);
      assert.equal(await evaluate(homeActive), true);
    });
    await t.test("a Welcome tab left in the background is not activated by startup", async () => {
      const welcome = canvasTab({ pluginKey: "builtin:landing", canvasId: "landing" }, { title: "Welcome" });
      await reload(noSessions, { open: [welcome], active: null });
      assert.equal(await evaluate(`${welcomeTabs}.length`), 1);
      assert.equal(await evaluate(homeActive), false);
    });
    await t.test("a kept draft prevents Welcome from opening", async () => {
      await reload({ ...noSessions, drafts: [{ projectId: "project", path: "/fixture/project" }] }, noFiles);
      assert.equal(await evaluate(`${welcomeTabs}.length`), 0);
    });
    await t.test("a restored session stays in front", async () => {
      await reload(savedSession, noFiles);
      assert.equal(await evaluate(`${welcomeTabs}.length`), 0);
      assert.equal(await evaluate(selectedSessionLabel), "one - Project");
    });
    await t.test("tabs of the Default space stay in front if the saved space was removed", async () => {
      await reload(noSessions, { open: [issuesTab], active: issuesTab }, { spaceId: "deleted-space" });
      assert.equal(await evaluate(`localStorage.getItem(${JSON.stringify(shownSpaceKey)})`), defaultSpaceId);
      assert.equal(await evaluate(issuesActive), true);
      assert.equal(await evaluate(`${welcomeTabs}.length`), 0);

      // The Default space can restore the same session the deleted space used as its fallback.
      await reload(savedSession, noFiles, { spaceId: "deleted-space" });
      assert.equal(await evaluate(selectedSessionLabel), "one - Project");
      assert.equal(await evaluate(`${welcomeTabs}.length`), 0);
    });
    await t.test("a late Welcome declaration does not take over a tab opened during startup", async () => {
      await reload(noSessions, noFiles, { deferWelcome: true });
      await evaluate("document.querySelector('.activity-issues').click()");
      assert.equal(await wait(issuesActive), true);
      await evaluate("windowFrameFixture.declareWelcome()");
      await frames();
      assert.equal(await evaluate(`${welcomeTabs}.length`), 0);
      assert.equal(await evaluate(issuesActive), true);
    });
    assert.deepEqual(exceptions, []);
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close(); await endBrowser(browser); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
