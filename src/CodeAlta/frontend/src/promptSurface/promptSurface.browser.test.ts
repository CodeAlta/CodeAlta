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
      + readFileSync(new URL("../style.css", import.meta.url), "utf8") + readFileSync(new URL("../worktrees/worktrees.css", import.meta.url), "utf8")
      + readFileSync(new URL("../speech/speech.css", import.meta.url), "utf8"));
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
    // The microphone of a composer: inside its surface, after the `⋯` menu, just before Send.
    const microphone = (index = 0) => evaluate(`(()=>{const surface=document.querySelectorAll('.prompt-surface')[${index}];const mic=surface.querySelector('.speech-button');
      if(!mic)return null;const box=surface.getBoundingClientRect(),rect=mic.getBoundingClientRect(),send=surface.querySelector('.composer-send-group').getBoundingClientRect(),
        more=surface.querySelector('.prompt-surface-more-button').getBoundingClientRect();
      return {view:mic.dataset.speech,id:mic.id,inside:rect.width>0&&rect.left>=box.left-1&&rect.right<=box.right+1,beside:send.left-rect.right>=-1&&send.left-rect.right<=12,
        afterMore:rect.left>=more.right-1,overflow:surface.scrollWidth>surface.clientWidth+1,
        outside:[...surface.querySelectorAll('*')].filter(element=>element.getBoundingClientRect().right>box.right+1).slice(0,4).map(element=>String(element.className?.baseVal??element.className).split(' ')[0]+':'+Math.round(element.getBoundingClientRect().right)),
        right:Math.round(box.right),editor:Math.round(surface.querySelector('.prompt-editor')?.getBoundingClientRect().right??0)}})()`);
    const assertMicrophone = async (where: string, view: string, index = 0) => {
      const seen = await microphone(index);
      assert.ok(seen && seen.view === view && seen.inside && seen.beside && seen.afterMore && !seen.overflow, `the microphone (${view}) beside Send at ${where}: ${JSON.stringify(seen)}`);
    };
    // A dictation started by the command of F9 (it presses #dictate-prompt of the window), then a download of the model:
    // the microphone stays beside Send in each state, in every pane.
    const speechStates = async (where: string, panes = 1) => {
      // The editor follows a resize of the window on its next layout.
      assert.equal(await wait("[...document.querySelectorAll('.prompt-surface')].every(surface=>surface.scrollWidth<=surface.clientWidth+1)"), true, `the surface settles at ${where}`);
      await evaluate("document.querySelector('#workspace-shell #dictate-prompt').click()");
      assert.equal(await wait("document.querySelector('#dictate-prompt')?.dataset.speech==='recording'"), true, `a dictation records at ${where}`);
      for (let index = 0; index < panes; index++) await assertMicrophone(where, index === 0 ? "recording" : "idle", index);
      await picture(`microphone-${where}-recording`);
      await evaluate("document.querySelector('#workspace-shell #dictate-prompt').click()");
      assert.equal(await wait("document.querySelector('#dictate-prompt')?.dataset.speech==='idle'"), true, `the dictation stops at ${where}`);
      await evaluate("fixture.speechHost.tell({state:'downloading',modelId:'parakeet-tdt-0.6b-v3',modelName:'Parakeet TDT 0.6b v3',receivedBytes:'0',totalBytes:'702545920',error:null})");
      assert.equal(await wait(`[...document.querySelectorAll('.speech-button')].length===${panes} && [...document.querySelectorAll('.speech-button')].every(mic=>mic.dataset.speech==='installing')`), true, `the download at ${where}`);
      for (let index = 0; index < panes; index++) await assertMicrophone(where, "installing", index);
      await evaluate("fixture.speechHost.tell({state:'done'})");
      assert.equal(await wait("[...document.querySelectorAll('.speech-button')].every(mic=>mic.dataset.speech==='idle')"), true, `the download ends at ${where}`);
    };

    await command("Page.enable");
    await command("Runtime.enable");
    // A user who already chose the microphone: a click dictates at once.
    await command("Page.addScriptToEvaluateOnNewDocument", { source: "window.fixturePermission={effort:'high',mode:null,status:'ok',calls:[]};"
      + "localStorage.setItem('codealta.desktop.speech.microphone.v1',JSON.stringify({device:null,confirmed:true}));" });
    await size(1600);
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait("!!document.querySelector('.prompt-surface .context-usage-tokens') && !!document.querySelector('.workspace-chip-counts') && !!document.querySelector('.composer-plugin-status-item')"), true, exceptions.join("\n"));
    // Archive in the `⋯` menu archives the session as the menu of the session in the Explorer does.
    const archive = "[...document.querySelectorAll('.prompt-surface-more-menu .bp6-button')].find(button=>button.textContent.trim()==='Archive')";
    assert.equal(await wait(`!!${archive} && !${archive}.disabled`), true);
    await evaluate(`${archive}.click()`);
    assert.equal(await evaluate("fixture.archives()"), 1);
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
          await assertMicrophone(where, "idle");
          assert.equal((await microphone())!.id, "dictate-prompt");
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
    // Panes of about 395 px, as two of an 860-px window: the microphone of each stays beside its Send.
    await size(860);
    assert.equal(await wait("[...document.querySelectorAll('.prompt-surface')].every(surface=>surface.dataset.density==='tiny')"), true);
    for (const index of [0, 1]) {
      const seen = await measure(index);
      assert.ok(seen.width >= 380 && seen.width <= 400 && seen.top <= 34 && seen.bottom <= 38 && !seen.overflow, `split pane ${index} at 860: ${JSON.stringify(seen)}`);
      await assertMicrophone(`split pane ${index}`, "idle", index);
    }
    // Only the composer in front answers F9.
    assert.deepEqual(await evaluate("[...document.querySelectorAll('.speech-button')].map(mic=>mic.id)"), ["dictate-prompt", "dictate-prompt-fixture-session-2"]);
    await speechStates("split-860", 2);
    await evaluate("fixture.split(false)");
    // The states of the microphone at the widths of a window, in both themes.
    for (const width of [1600, 900, 600, 420]) {
      await size(width);
      for (const name of ["dark", "light"] as const) { await theme(name); await speechStates(`${width}-${name}`); }
    }
    await theme("dark");

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
    // While the session works, Archive waits for it to be idle.
    assert.equal(await evaluate(`${archive}?.disabled`), true);

    // A session in a worktree says so: the word where there is room, the tone of worktrees, and a tooltip with the project and the branch.
    await size(1600);
    const marker = `(()=>{const part=document.querySelector('.workspace-chip-place');const kind=part.querySelector('.workspace-chip-kind');
      return {worktree:part.dataset.worktree,kind:kind&&kind.getBoundingClientRect().width>0?kind.textContent:null,label:part.getAttribute('aria-label'),title:part.title,
        tinted:getComputedStyle(part).backgroundColor!=='rgba(0, 0, 0, 0)'}})()`;
    assert.equal(await wait("!!document.querySelector('.workspace-chip-place .workspace-chip-counts') && document.querySelector('.prompt-surface')?.dataset.density==='wide'"), true);
    assert.deepEqual(await evaluate(marker), { worktree: "yes", kind: "Worktree", label: "Works in the worktree mellow-chisel-pz93 of CodeAlta +94 −32",
      title: "Works in the worktree mellow-chisel-pz93 of CodeAlta\nBranch alta/mellow-chisel-pz93\nC:\\Users\\alexa\\.alta\\worktrees\\codealta\\mellow-chisel-pz93", tinted: true });
    // A narrower surface keeps the tone and the icon, without the word.
    await size(900);
    assert.equal(await wait("document.querySelector('.prompt-surface')?.dataset.density==='medium'"), true);
    assert.equal((await evaluate(marker)).kind, null);
    await size(1600);

    // The Workspace chip opens what it is: project, worktree, branch, changes, path, and the ways to the changes and a terminal.
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

    // A new session shows where it will work in the chip itself, the folder of the project until a new worktree is chosen there.
    await evaluate("fixture.draft(true)");
    const place = `(()=>{const part=document.querySelector('.workspace-chip-work-place');const name=part.querySelector('.workspace-chip-name');
      return {text:name.getBoundingClientRect().width>0?name.scrollWidth>name.clientWidth+1?'cut':name.textContent:null,label:part.getAttribute('aria-label'),title:part.title,worktree:part.dataset.worktree??null,
        tinted:getComputedStyle(part).backgroundColor!=='rgba(0, 0, 0, 0)',order:[...document.querySelectorAll('.workspace-chip .workspace-chip-part')].map(part=>part.classList[1])}})()`;
    assert.equal(await wait("!!document.querySelector('.workspace-chip-work-place') && !!document.querySelector('.workspace-chip-place .workspace-chip-counts')"), true);
    assert.deepEqual(await evaluate(place), { text: "Project folder", label: "Where to work: Project folder", title: "The next session works in the folder of the project",
      worktree: null, tinted: false, order: ["workspace-chip-project", "workspace-chip-work-place", "workspace-chip-place"] });
    // It opens the choice of 1.8: Work in, then for a worktree what it starts from and where it is created.
    await evaluate("document.querySelector('.workspace-chip-work-place').click()");
    assert.equal(await wait("!!document.querySelector('.work-place-form')"), true);
    await evaluate("[...document.querySelectorAll('.work-place-form button')].find(button=>button.textContent.trim()==='New worktree').click()");
    assert.equal(await wait("document.querySelector('.workspace-chip-work-place')?.dataset.worktree==='true' && document.querySelector('.work-place-folder code')?.textContent==='C:\\\\Users\\\\alexa\\\\.alta\\\\worktrees\\\\codealta'"), true);
    const form = await evaluate("document.querySelector('.work-place-form').textContent");
    for (const fact of ["Work in", "Start from", "Current commit (alta/mellow-chisel-pz93)", "Created in"]) assert.ok(form.includes(fact), `the choice says ${fact}: ${form}`);
    await picture("work-place-1600-dark");
    await evaluate("document.querySelector('.workspace-chip-work-place').click()");
    assert.equal(await wait("!document.querySelector('.work-place-form')"), true);
    assert.deepEqual(await evaluate(place), { text: "New worktree", label: "Where to work: New worktree", title: "The next session works in a new git worktree",
      worktree: "true", tinted: true, order: ["workspace-chip-project", "workspace-chip-work-place", "workspace-chip-place"] });
    // The choice stays in the bar at every width: in words down to a narrow surface, as its icon on the tiniest.
    for (const [width, density] of widths) {
      await size(width);
      assert.equal(await wait(`document.querySelector('.prompt-surface')?.dataset.density==='${density}'`), true);
      const seen = await measure();
      assert.ok(seen.top <= 34 && !seen.overflow && seen.workspace, `the chip of a new session fits at ${width}px: ${JSON.stringify(seen)}`);
      const shown = await evaluate(place);
      assert.equal(shown.text, density === "tiny" ? null : "New worktree", `the place is said at ${width}px: ${JSON.stringify(shown)}`);
      assert.equal(await evaluate("(()=>{const surface=document.querySelector('.prompt-surface').getBoundingClientRect();const rect=document.querySelector('.workspace-chip-work-place').getBoundingClientRect();return rect.width>0&&rect.left>=surface.left&&rect.right<=surface.right})()"), true);
      // The composer of a new session has its microphone beside Start session, which F9 presses.
      await assertMicrophone(`new session ${width}px`, "idle");
      assert.equal((await microphone())!.id, "dictate-prompt");
      if (width !== 1200) await speechStates(`new-session-${width}`);
      await picture(`work-place-${width}-dark`);
    }
    await size(860); await evaluate("fixture.split(true)");
    assert.equal(await wait("document.querySelectorAll('.prompt-surface').length===2 && [...document.querySelectorAll('.prompt-surface')].every(surface=>surface.dataset.density==='tiny')"), true);
    for (const index of [0, 1]) await assertMicrophone(`new session split pane ${index}`, "idle", index);
    await speechStates("new-session-split-860", 2);
    await evaluate("fixture.split(false)");
    await evaluate("fixture.draft(false)");
    await size(1600);

    // The Agent chip opens its form, with the permission mode in it.
    await evaluate("document.querySelector('.composer-selection').click()");
    // Its permission mode is a select as wide as the selects of the other rows.
    assert.equal(await wait("!!document.querySelector('.composer-selection-form select#composer-permission-fixture-session')"), true);
    assert.equal(await evaluate(`(()=>{const width=selector=>Math.round(document.querySelector(selector).getBoundingClientRect().width);
      return width('#composer-permission-fixture-session')===width('#composer-agent-fixture-session')})()`), true);
    await picture("agent-1600-dark");

    // A model whose provider lists all its efforts and lists none (Haiku of Claude Code): the saved effort is not sent,
    // so the chip shows no reasoning and says why in its tooltip, and the form says the same in the place of a slider.
    await command("Page.addScriptToEvaluateOnNewDocument", { source: "window.fixtureNoReasoning=true;" });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait("!!document.querySelector('.prompt-surface .composer-selection-model-name')?.textContent.includes('Fixture model')"), true);
    const chip = await evaluate(`(()=>{const chip=document.querySelector('.composer-selection');return {text:chip.textContent,title:chip.title,
      reasoning:!!chip.querySelector('.composer-selection-reasoning')}})()`);
    assert.equal(chip.reasoning, false, JSON.stringify(chip));
    // Without the reasoning, no separator stands before the shield.
    assert.equal(await evaluate("getComputedStyle(document.querySelector('.composer-selection-permission'),'::before').content"), "none");
    assert.ok(chip.title.split("\n").includes("Fixture model has no reasoning effort"), JSON.stringify(chip));
    await evaluate("document.querySelector('.composer-selection').click()");
    assert.equal(await wait("document.querySelector('.composer-selection-form .composer-reasoning-static[data-none]')?.textContent==='Fixture model has no reasoning effort'"), true);
    assert.equal(await evaluate("!!document.querySelector('.composer-selection-form .composer-reasoning-slider')"), false);
    await picture("agent-no-reasoning-1600-dark");
    assert.deepEqual(exceptions, []);
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close(); await endBrowser(browser); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
