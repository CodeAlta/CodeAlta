import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { readFileSync } from "node:fs";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, endBrowser, waitForBrowserPort } from "../browserTarget";

const edge = browserExecutable;
/** A folder to keep a picture of each width in, for a person to look at; none by default. */
const shots = process.env.CODEALTA_SURFACE_SHOTS;

// Mounts the production prompt surface (the owned composer with the Workspace chip, the MCP status and the sub-agents)
// with the production styles, and checks it at the widths of a desktop, a laptop, a narrow window, a 600-px pane and a
// phone-like pane, in both themes, idle and running, and in a split view: one row above the editor, one below, nothing
// out of the surface, the tokens of the context always shown, and the meters folded into the `⋯` menu last.
test("the prompt surface keeps one row above and one below the editor at every width", { skip: !edge, timeout: 180_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-prompt-surface-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./promptSurface.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"),
      bundle: true, platform: "browser", format: "iife", loader: { ".css": "empty", ".flf": "text", ".svg": "dataurl" },
      define: { "import.meta.env.VITE_DEMO_MODE": '"false"' }, plugins: [{ name: "isolated-surface", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./promptSurface.neoastra.mount.ts", import.meta.url)) }));
        bundle.onResolve({ filter: /^monaco-editor\/.*\?worker$/ }, args => ({ path: args.path, namespace: "fixture-worker" }));
        bundle.onLoad({ filter: /.*/, namespace: "fixture-worker" }, async () => {
          const worker = await build({ entryPoints: [fileURLToPath(new URL("../../node_modules/monaco-editor/esm/vs/editor/editor.worker.js", import.meta.url))],
            bundle: true, platform: "browser", format: "iife", write: false });
          return { loader: "js", contents: `export default class extends Worker { constructor() {
            const url=URL.createObjectURL(new Blob([${JSON.stringify(worker.outputFiles[0].text)}],{type:"text/javascript"}));
            super(url); URL.revokeObjectURL(url);
          } }` };
        });
      } }] });
    await writeFile(join(root, "style.css"), readFileSync(new URL("../../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8")
      + readFileSync(new URL("../style.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    // The fixture loads no Monaco styles: its input area is hidden as Monaco's own styles hide it.
    await writeFile(page, '<!doctype html><html data-theme="dark" class="bp6-dark"><head><meta charset="utf-8"><link rel="stylesheet" href="style.css">'
      + '<style>.prompt-editor .ime-text-area { opacity: 0; }</style></head><body><div id="app"></div><script src="fixture.js"></script></body></html>');
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
      if (value.method === "Runtime.consoleAPICalled" && value.params.type === "error")
        exceptions.push(value.params.args.map((arg: { description?: string; value?: unknown }) => arg.description ?? arg.value).join(" "));
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
      const timer = setTimeout(() => { socket!.removeEventListener("message", reply); reject(new Error(`${method} timed out`)); }, 12_000);
      socket!.addEventListener("message", reply); socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async (expression: string) => {
      const value = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(value.exceptionDetails, undefined, JSON.stringify(value.exceptionDetails)); return value.result?.value;
    };
    const wait = (condition: string) => evaluate(`new Promise(resolve=>{const end=Date.now()+8000;const tick=()=>{if(${condition})resolve(true);
      else if(Date.now()>end)resolve(false);else setTimeout(tick,20)};tick()})`);
    const size = (width: number) => command("Emulation.setDeviceMetricsOverride", { width, height: 700, deviceScaleFactor: 1, mobile: false });
    const theme = (name: "dark" | "light") => evaluate(`document.documentElement.dataset.theme='${name}';document.documentElement.classList.toggle('bp6-dark',${name === "dark"})`);
    const picture = async (name: string) => {
      if (!shots) return;
      await mkdir(shots, { recursive: true });
      const shot = await command("Page.captureScreenshot", { format: "png" });
      await writeFile(join(shots, `${name}.png`), Buffer.from(shot.data, "base64"));
    };
    // What a surface shows, measured: the rows, what overflows, and what is visible.
    const measure = (index = 0) => evaluate(`(()=>{const surface=document.querySelectorAll('.prompt-surface')[${index}];const box=element=>element?element.getBoundingClientRect():null;
      const visible=selector=>{const element=surface.querySelector(selector);if(!element)return false;const rect=element.getBoundingClientRect();
        const style=getComputedStyle(element);return rect.width>0&&rect.height>0&&style.visibility!=='hidden'&&!element.closest('details:not([open]) > div')
          &&rect.left>=box(surface).left-1&&rect.right<=box(surface).right+1};
      // The Agent chip ends before the meters begin.
      const chip=box(surface.querySelector('.composer-selection')),end=box(surface.querySelector('.prompt-surface-end'));
      const name=surface.querySelector('.composer-selection-model-name');
      return {density:surface.dataset.density,width:Math.round(box(surface).width),apart:chip.right<=end.left+1,
        logos:surface.querySelectorAll('.composer-selection-model svg, .composer-selection-model img').length,
        model:name.getBoundingClientRect().width>0?(name.scrollWidth<=name.clientWidth+1?'whole':'cut'):'hidden',
        reasoning:Math.round(surface.querySelector('.composer-selection-reasoning')?.getBoundingClientRect().width??0),
        glyph:visible('.background-tasks-glyph'),
        top:Math.round(box(surface.querySelector('.prompt-surface-top')).height),bottom:Math.round(box(surface.querySelector('.composer-toolbar')).height),
        overflow:surface.scrollWidth>surface.clientWidth+1||document.documentElement.scrollWidth>document.documentElement.clientWidth+1,
        tokens:visible('.context-usage-tokens')?surface.querySelector('.context-usage-tokens').textContent:null,
        cache:visible('.prompt-cache-countdown')?surface.querySelector('.prompt-cache-countdown').textContent:null,
        send:visible('[aria-label="Send"]'),stop:visible('[aria-label="Cancel observed run"]'),workspace:visible('.workspace-chip'),mcp:visible('.composer-plugin-status-item'),
        agent:visible('.composer-selection'),more:visible('.prompt-surface-more-button'),subAgents:visible('.prompt-surface-meters .sub-agents-chip'),
        folded:!!surface.querySelector('.prompt-surface-folded .sub-agents-chip'),mark:surface.querySelector('.prompt-surface-more-mark')?.textContent??null,
        jobs:visible('.background-tasks-status')}})()`);

    await command("Page.enable");
    await command("Runtime.enable");
    await command("Page.addScriptToEvaluateOnNewDocument", { source: "window.fixturePermission={effort:'high',mode:null,status:'ok',calls:[]};" });
    await size(1600);
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait("!!document.querySelector('.prompt-surface .context-usage-tokens') && !!document.querySelector('.workspace-chip-counts') && !!document.querySelector('.composer-plugin-status-item')"), true, exceptions.join("\n"));
    await evaluate("fixture.subAgents(3, 1)");

    const widths = [[1600, "wide"], [1200, "wide"], [900, "medium"], [600, "narrow"], [420, "tiny"]] as const;
    for (const running of [false, true]) {
      if (running) { await evaluate("fixture.startRun(); fixture.background(['running','running'])"); }
      for (const [width, density] of widths) {
        await size(width);
        for (const name of ["dark", "light"] as const) {
          await theme(name);
          assert.equal(await wait(`document.querySelector('.prompt-surface')?.dataset.density==='${density}'`
            + (running ? " && !!document.querySelector('[aria-label=\"Cancel observed run\"]') && !!document.querySelector('.background-tasks-status')" : "")), true, `${width}px is ${density}`);
          const seen = await measure();
          const where = `${width}px ${name} ${running ? "running" : "idle"}: ${JSON.stringify(seen)}`;
          assert.ok(seen.top <= 34 && seen.bottom <= 38, `one row above and one below the editor at ${where}`);
          assert.equal(seen.overflow, false, `nothing overflows at ${where}`);
          assert.equal(seen.apart, true, `the Agent chip does not cover the meters at ${where}`);
          assert.equal(seen.tokens, "563k/1M", `the tokens of the context are always shown at ${where}`);
          assert.equal(seen.cache, "37m", `the prompt cache countdown is shown at ${where}`);
          assert.ok(seen.workspace && seen.mcp && seen.agent && seen.more, `the chips and the menu are shown at ${where}`);
          assert.equal(running ? seen.stop : seen.send, true, `the main action is shown at ${where}`);
          if (running) assert.equal(seen.jobs, true, `the background jobs are shown at ${where}`);
          // Where the label of the tasks gives way, their glyph stands beside the count.
          if (running) assert.equal(seen.glyph, density === "narrow" || density === "tiny", `the glyph of the tasks at ${where}`);
          // One logo on the Agent chip; the model name is the part kept longest, the reasoning gives way first.
          assert.equal(seen.logos, 1, `one logo on the Agent chip at ${where}`);
          assert.notEqual(seen.model, "hidden", `the model name is shown at ${where}`);
          if (seen.model === "cut") assert.equal(seen.reasoning, 0, `the reasoning gives way before the model name at ${where}`);
          // The sub-agents stay in the bar until the surface is tiny; then the `⋯` menu holds them and keeps a mark.
          assert.equal(seen.subAgents, density !== "tiny", `the sub-agents are in the bar at ${where}`);
          assert.equal(seen.folded, density === "tiny", `the sub-agents fold into the menu at ${where}`);
          assert.equal(seen.mark, "2", `the menu shows the active reminders at ${where}`);
          await picture(`surface-${width}-${name}-${running ? "running" : "idle"}`);
        }
      }
    }

    // A split view: two panes of a 1200-px window are narrow, and each keeps its rows.
    await size(1200); await theme("dark");
    await evaluate("fixture.split(true)");
    assert.equal(await wait("document.querySelectorAll('.prompt-surface').length===2 && [...document.querySelectorAll('.prompt-surface')].every(surface=>surface.dataset.density==='narrow')"), true);
    for (const index of [0, 1]) {
      const seen = await measure(index);
      assert.ok(seen.top <= 34 && seen.bottom <= 38 && !seen.overflow && seen.tokens === "563k/1M", `split pane ${index}: ${JSON.stringify(seen)}`);
    }
    await picture("surface-1200-split");
    await evaluate("fixture.split(false)");

    // The `⋯` menu opens against the window, inside it, with the controls used less often and their shortcuts.
    await size(900);
    await evaluate("document.querySelector('.prompt-surface-more-button').click()");
    assert.equal(await wait("!!document.querySelector('.prompt-surface-more[open]')"), true);
    await picture("menu-900-dark");
    const menu = await evaluate(`(()=>{const rect=document.querySelector('.prompt-surface-more-menu').getBoundingClientRect();
      return {inside:rect.left>=0&&rect.top>=0&&rect.right<=innerWidth&&rect.bottom<=innerHeight&&rect.height>0,rect:[rect.left,rect.top,rect.right,rect.bottom,innerWidth,innerHeight],
        items:[...document.querySelectorAll('.prompt-surface-more-menu .bp6-button')].map(button=>button.querySelector('.bp6-button-text')?.textContent?.trim()).filter(Boolean)}})()`);
    assert.equal(menu.inside, true, JSON.stringify(menu));
    for (const item of ["Reminders", "Steer the running turn", "Compact the conversation", "Expand prompt editor"])
      assert.ok(menu.items.some((text: string) => text.startsWith(item)), `the menu has ${item}: ${JSON.stringify(menu.items)}`);
    // A press outside closes it; Escape too.
    await evaluate("document.body.dispatchEvent(new PointerEvent('pointerdown',{bubbles:true}))");
    assert.equal(await wait("!document.querySelector('.prompt-surface-more[open]')"), true);

    // The Workspace chip opens what it is: project, worktree, branch, changes, path, and the ways to the changes and a terminal.
    await size(1600);
    await evaluate("document.querySelector('.workspace-chip-project').click()");
    assert.equal(await wait("!!document.querySelector('.workspace-popover')"), true);
    const workspace = await evaluate(`(()=>{const popover=document.querySelector('.workspace-popover');return {text:popover.textContent,
      buttons:[...popover.querySelectorAll('footer .bp6-button')].map(button=>button.textContent.trim())}})()`);
    for (const fact of ["CodeAlta", "mellow-chisel-pz93", "alta/mellow-chisel-pz93", "+94", "−32", "C:\\Users\\alexa\\.alta\\worktrees\\codealta\\mellow-chisel-pz93"])
      assert.ok(workspace.text.includes(fact), `the Workspace popover says ${fact}: ${workspace.text}`);
    assert.deepEqual(workspace.buttons, ["View changes", "Open terminal"]);
    await picture("workspace-1600-dark");
    await evaluate("document.querySelector('.workspace-chip-project').click()");
    assert.equal(await wait("!document.querySelector('.workspace-popover')"), true);

    // The Agent chip opens its form, with the permission mode in it.
    await evaluate("document.querySelector('.composer-selection').click()");
    // Its permission mode is a select as wide as the selects of the other rows.
    assert.equal(await wait("!!document.querySelector('.composer-selection-form select#composer-permission-fixture-session')"), true);
    assert.equal(await evaluate(`(()=>{const width=selector=>Math.round(document.querySelector(selector).getBoundingClientRect().width);
      return width('#composer-permission-fixture-session')===width('#composer-agent-fixture-session')})()`), true);
    await picture("agent-1600-dark");
    assert.deepEqual(exceptions, []);
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close(); await endBrowser(browser); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
