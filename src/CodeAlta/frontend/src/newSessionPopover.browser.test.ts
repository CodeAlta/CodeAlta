import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, endBrowser, waitForBrowserPort } from "./browserTarget";

type Box = { left: number; top: number; right: number; bottom: number; width: number; height: number };
const preferencesKey = "codealta.desktop.newSessionPreferences.v1";
const projectScope = JSON.stringify(["project", "/fixture/project"]);
const projectRow = '#project-list button[data-scope="project"]', projectPlus = "#project-list .project-session-trigger";
const chatsRow = '.project-root-list button[data-scope=""]', chatsPlus = ".project-root-list .project-session-trigger";
const form = ".new-session-form", create = `${form} button[type=submit]`, chip = `${form} .composer-selection`, placeChip = `${form} .project-context-place`;

// The whole window, as the application renders it (under StrictMode), with a host that only records what it is asked.
test("a new session is created from a form beside the row of its project or of the chats", { skip: !browserExecutable, timeout: 180_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-new-session-"));
  let browser: ReturnType<typeof spawn> | undefined, socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./main.tsx", import.meta.url))], outfile: join(root, "fixture.js"),
      bundle: true, platform: "browser", format: "iife", loader: { ".css": "empty", ".flf": "text", ".svg": "dataurl" },
      define: { "import.meta.env.VITE_DEMO_MODE": '"false"' }, plugins: [{ name: "isolated-creation", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./newSessionPopover.neoastra.mount.ts", import.meta.url)) }));
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
      "../node_modules/@blueprintjs/core/lib/css/blueprint.css", "../node_modules/flexlayout-react/style/light.css", "./style.css", "./explorer/explorer.css", "./worktrees/worktrees.css",
    ].map(path => readFile(new URL(path, import.meta.url), "utf8")))).join("\n"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(browserExecutable!, [...browserBaseArgs, "--allow-file-access-from-files",
      `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(root, "profile"), browser);
    const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json() as { type: string; webSocketDebuggerUrl: string }[];
    socket = new WebSocket(pages.find(tab => tab.type === "page")!.webSocketDebuggerUrl);
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
      const timer = setTimeout(() => { socket!.removeEventListener("message", reply); reject(new Error(`${method} timed out`)); }, 12_000);
      socket!.addEventListener("message", reply); socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async (expression: string) => {
      const value = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(value.exceptionDetails, undefined, JSON.stringify(value.exceptionDetails)); return value.result?.value;
    };
    const until = async (condition: string, what: string) => {
      assert.equal(await evaluate(`new Promise(resolve=>{const end=Date.now()+7000;const tick=()=>{if(${condition})resolve(true);else if(Date.now()>end)resolve(false);else setTimeout(tick,20)};tick()})`), true,
        `${what}; ${exceptions.join("\n")}; form: ${await evaluate(`document.querySelector(${JSON.stringify(form)})?.textContent`)}; draft: ${await evaluate(`JSON.stringify({ meter: document.querySelector('#active-session-content .context-usage')?.textContent, model: document.querySelector('#composer-model-new')?.textContent, context: document.querySelector('#composer-context-new')?.value, picker: document.querySelector('.composer-model-picker')?.textContent })`)}`);
    };
    const text = (selector: string) => evaluate(`document.querySelector(${JSON.stringify(selector)})?.textContent ?? null`) as Promise<string | null>;
    const shown = (selector: string) => evaluate(`!!document.querySelector(${JSON.stringify(selector)})`) as Promise<boolean>;
    const box = (selector: string) => evaluate(`(() => { const node = document.querySelector(${JSON.stringify(selector)}); if (!node) return null;
      const { left, top, right, bottom, width, height } = node.getBoundingClientRect(); return { left, top, right, bottom, width, height }; })()`) as Promise<Box | null>;
    // The pointer itself: a press is what a popover takes as "elsewhere", which a click made by script never is.
    const pointer = (type: string, x: number, y: number) => command("Input.dispatchMouseEvent", { type, x, y, button: type === "mouseMoved" ? "none" : "left", clickCount: type === "mouseMoved" ? 0 : 1 });
    const pressAt = async (x: number, y: number) => { for (const type of ["mouseMoved", "mousePressed", "mouseReleased"]) await pointer(type, x, y); };
    const over = async (selector: string) => { const at = await box(selector); assert.ok(at && at.width > 0, `${selector} is shown`); await pointer("mouseMoved", at.left + 40, at.top + at.height / 2); };
    // A popover grows into its place: what is pressed is pressed where it ends up.
    const settled = (selector: string) => evaluate(`new Promise(resolve => { let last = ""; const end = Date.now() + 3000; (function check() {
      const at = document.querySelector(${JSON.stringify(selector)})?.getBoundingClientRect(), now = at ? [at.left, at.top, at.width, at.height].join() : "";
      if (now && now === last || Date.now() > end) resolve(true); else { last = now; requestAnimationFrame(() => requestAnimationFrame(check)); } })(); })`);
    const press = async (selector: string, across = 0.5) => {
      await settled(selector);
      const at = await box(selector);
      assert.ok(at && at.width > 0 && at.height > 0, `${selector} is shown`);
      await pressAt(at.left + at.width * across, at.top + at.height / 2);
    };
    const select = (selector: string, value: string) => evaluate(`(() => { const node=document.querySelector(${JSON.stringify(selector)}); node.value=${JSON.stringify(value)}; node.dispatchEvent(new Event('change',{bubbles:true})); })()`);
    const key = async (key: string, virtual: number, text?: string) => {
      await command("Input.dispatchKeyEvent", { type: "keyDown", key, code: key, windowsVirtualKeyCode: virtual, ...(text ? { text } : {}) });
      await command("Input.dispatchKeyEvent", { type: "keyUp", key, code: key, windowsVirtualKeyCode: virtual });
    };
    const escape = () => key("Escape", 27), enter = () => key("Enter", 13, "\r");
    const gone = (selector: string, what: string) => until(`!document.querySelector(${JSON.stringify(selector)})`, what);
    const titleFocused = `document.activeElement === document.querySelector(${JSON.stringify(`${form} input`)})`;
    // The buttons of a row are shown with the pointer on it.
    const open = async (row = projectRow, plus = projectPlus) => {
      await over(row); await press(plus);
      await until(`${titleFocused} && !document.querySelector(${JSON.stringify(create)}).disabled`, "the form is open, its title has the focus and its choices are read");
      await settled(".new-session-popover");
    };
    // Whether something of the form is cut or out of it: the form itself, the text of the first chip, a chip of the place.
    const overflows = () => evaluate(`(() => { const form = document.querySelector(${JSON.stringify(form)}), edge = form.getBoundingClientRect().right;
      return form.scrollHeight > form.clientHeight || form.scrollWidth > form.clientWidth
        || [...form.querySelectorAll(".composer-selection, .composer-selection-part > span")].some(node => node.scrollWidth > node.clientWidth + 1)
        || [...form.querySelectorAll(".new-session-choices button")].some(node => node.getBoundingClientRect().right > edge); })()`) as Promise<boolean>;
    const remembered = async () => JSON.parse(await evaluate(`localStorage.getItem(${JSON.stringify(preferencesKey)})`) ?? "null");
    const creates = () => evaluate("newSessionFixture.creates.map(({scope,title,providerId,worktree,baseBranch})=>({scope,title,providerId,worktree,baseBranch}))") as Promise<object[]>;
    const start = async (what: string) => until(`!!document.querySelector(${JSON.stringify(projectPlus)})`, what);

    await command("Page.enable"); await command("Runtime.enable");
    await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    await command("Emulation.setDeviceMetricsOverride", { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });
    await command("Page.addScriptToEvaluateOnNewDocument", { source: "localStorage.setItem('settingsFixtureOwned','true')" });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    await start("the shell");
    await until("!!document.querySelector('.session-row > button[aria-pressed=true]')", "a session is selected: the form is not the prompt of a new session");

    // The menu of the row opens the same form as its button; Escape closes it and the row has the focus again.
    await over(projectRow); await press("#project-list .project-actions-trigger");
    await until("!!document.querySelector('.session-tab-popup')", "project actions");
    await evaluate("[...document.querySelectorAll('.session-tab-popup [role=menuitem]')].find(node=>node.textContent.trim()==='New session…').click()");
    await until(titleFocused, "the form opened from the menu");
    assert.equal(await text(`${form} > strong`), "New session in Project");
    await escape();
    await gone(form, "Escape closes the form");
    assert.equal(await evaluate("document.activeElement.dataset.scope"), "project", "the row has the focus again");

    // The button of the row: the form is a popover beside that row, not a form in the Explorer or a window.
    await open();
    assert.equal(await shown(".session-create, dialog[open]"), false);
    const row = (await box("#project-list li.project-action-row"))!, wide = (await box(".new-session-popover"))!;
    assert.ok(wide.left >= row.right && wide.left - row.right < 24 && Math.abs(wide.top - row.top) < 24, `beside its row: ${JSON.stringify({ row, wide })}`);
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(projectPlus)}).getAttribute("aria-expanded")`), "true");
    assert.equal(await overflows(), false, "nothing overflows the form");
    assert.equal(wide.width, 420, "the width the form is given");
    assert.equal(await remembered(), null, "the defaults that are shown are not stored as choices");

    // The pickers of the chips are popovers and lists of their own: a choice in one of them keeps the form open.
    await command("Input.insertText", { text: "Named work" });
    await press(chip);
    await until("!!document.querySelector('#composer-agent-create')", "the choices of the chip");
    await select("#composer-agent-create", "plan");
    // A provider without permission modes: the choices have no Permissions row, and the chip names no mode.
    await until("newSessionFixture.permissionReads.includes('fixture')", "the permission modes of the provider were read");
    assert.equal(await shown("#composer-permission-create"), false, "no Permissions row for a provider without modes");
    assert.equal(await shown(`${chip} .composer-selection-permission`), false);
    await select(".composer-selection-form select[aria-label='Provider for new session']", "alternate");
    await until("!document.querySelector('#composer-model-create').disabled", "the models of the other provider");
    assert.deepEqual(await remembered(), { [projectScope]: { providerKey: "alternate" } },
      "what was chosen, and nothing else: the agent prompt is for the session of this form only");
    // A provider with modes: the same Permissions row as an existing session, on the mode a new session gets.
    await until("!!document.querySelector('#composer-permission-create')", "the Permissions row of the other provider");
    assert.equal(await evaluate("document.querySelector('#composer-permission-create').selectedOptions[0].textContent"), "Accept edits · Default");
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(`${chip} .composer-selection-permission`)})?.dataset.tone`), "edits");
    await select("#composer-permission-create", "bypassPermissions");
    await until(`document.querySelector(${JSON.stringify(`${chip} .composer-selection-permission`)})?.dataset.tone === "open"`, "the chip names the mode chosen");
    assert.deepEqual(await remembered(), { [projectScope]: { providerKey: "alternate", permissionMode: "bypassPermissions" } });
    await press("#composer-model-create");
    await until("!!document.querySelector('.composer-model-picker [data-model-id=old]')", "the list of models");
    await press(".composer-model-picker [data-model-id=old]");
    await gone(".composer-model-picker", "a model was taken");
    assert.match((await text(chip))!, /Plan.*Old model.*Bypass permissions$/, "another model keeps the mode");
    assert.match(await evaluate(`document.querySelector(${JSON.stringify(chip)})?.title ?? ""`) as string, /Provider: Alternate/, "the chip names the provider in its tooltip");
    await press("#composer-model-create");
    await until("!!document.querySelector('.composer-model-picker [data-model-id=new]')", "the list of models again");
    // Escape in the list closes the list alone.
    await escape();
    await gone(".composer-model-picker", "Escape closes the list of models");
    assert.equal(await shown("#composer-agent-create") && await shown(form), true, "the choices and the form stay");
    await press("#composer-model-create");
    await until("!!document.querySelector('.composer-model-picker [data-model-id=new]')", "the list of models once more");
    await press(".composer-model-picker [data-model-id=new]");
    await gone(".composer-model-picker", "another model was taken");
    await until("!!document.querySelector('.composer-reasoning-slider .bp6-slider-track')", "the efforts of that model");
    await press(".composer-reasoning-slider .bp6-slider-track", 0.01);
    await until(`/Plan.*New model.*Low.*Bypass permissions$/.test(document.querySelector(${JSON.stringify(chip)}).textContent)
      && document.querySelector(${JSON.stringify(chip)}).title.includes("Provider: Alternate")`, "the chip names what was chosen, the provider in its tooltip");
    // A name too long for one line is not cut: the chip goes on on a second line, and names the provider of the model whole.
    await select("#composer-agent-create", "reviewer");
    await until(`/^Reviewer of pull requests.*New model.*Alternate.*Low/.test(document.querySelector(${JSON.stringify(chip)}).textContent)`, "the long agent prompt");
    assert.equal(await overflows(), false, "no name of the chip is cut");
    // A name cut with an ellipsis is as wide as its box, not wider: the width of its text tells.
    assert.deepEqual(await evaluate(`[...document.querySelectorAll(${JSON.stringify(`${chip} .composer-selection-part > span, ${chip} .composer-selection-model-name, ${chip} .composer-selection-provider-name`)})]
      .filter(node => { const range = document.createRange(); range.selectNodeContents(node); return range.getBoundingClientRect().width > node.getBoundingClientRect().width + 1; })
      .map(node => node.textContent)`), [], "every name of the chip is whole");
    assert.equal(await evaluate(`(() => { const chip = document.querySelector(${JSON.stringify(chip)}), name = chip.querySelector(".composer-selection-provider-name"),
      line = name.closest(".composer-selection-model-line").getBoundingClientRect(), box = name.getBoundingClientRect();
      return box.width > 0 && box.top >= line.top - 1 && box.bottom <= line.bottom + 1 && chip.getBoundingClientRect().height > 40; })()`), true,
      "the provider is shown beside its model, and the chip has two lines");
    await select("#composer-agent-create", "plan");
    await until(`/^Plan.*New model.*Low.*Bypass permissions$/.test(document.querySelector(${JSON.stringify(chip)}).textContent)`, "the agent prompt chosen before");
    assert.equal(await shown("#composer-context-create"), false, "context does not clutter the main picker");
    await press(".composer-selection-more-button");
    await until("!!document.querySelector('#composer-context-create')", "context is under More options");
    assert.equal(await evaluate(`(() => {
      const context = document.querySelector('#composer-context-create').closest('.bp6-form-group');
      const body = context.parentElement, subSessions = body.querySelector('.composer-selection-option');
      return body.firstElementChild === context && context.nextElementSibling === subSessions
        && parseFloat(getComputedStyle(body).paddingTop) === 14
        && parseFloat(getComputedStyle(context).marginBottom) === 12
        && parseFloat(getComputedStyle(subSessions).marginBottom) === 4
        && parseFloat(getComputedStyle(subSessions.querySelector('.bp6-control')).marginTop) === 0;
    })()`), true, "the section spacer stays above Context size, with space before Sub-sessions");
    await select("#composer-context-create", "922000");
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(chip)}).textContent.includes('922k')`), false, "no duplicate context in the chip");
    assert.equal(await shown(form), true, "the form stayed open through every choice");
    // Escape in the choices closes them alone.
    await escape();
    await gone(".composer-selection-form", "Escape closes the choices");
    assert.equal(await shown(form), true, "and not the form");

    // Where the session works.
    await until(`!!document.querySelector(${JSON.stringify(placeChip)})`, "the chip of the place");
    assert.equal(await evaluate(`getComputedStyle(document.querySelector(${JSON.stringify(`${form} .project-context-folder`)})).display`), "none", "the heading names the project");
    await press(placeChip);
    await until("!!document.querySelector('.work-place-form')", "the places");
    await settled(".work-place-form");
    const segment = await evaluate("(() => { const node=[...document.querySelectorAll('.work-place-form button')].find(node=>node.textContent==='New worktree'); const box=node.getBoundingClientRect(); return { x: box.left+box.width/2, y: box.top+box.height/2 }; })()") as { x: number; y: number };
    await pressAt(segment.x, segment.y);
    await until("[...document.querySelectorAll('.work-place-form select option')].some(node=>node.value==='feature')", "the branches a worktree starts from");
    // A long folder is cut, and the places stay inside the popover.
    await until("/a-project-with-a-long-name$/.test(document.querySelector('.work-place-folder code')?.textContent ?? '')", "the folder of new worktrees");
    assert.deepEqual(await evaluate(`(() => { const popover=document.querySelector('.work-place-popover').getBoundingClientRect(), code=document.querySelector('.work-place-folder code');
      return { cut: code.scrollWidth > code.clientWidth, outside: [...document.querySelectorAll('.work-place-row, .work-place-row > *, .work-place-folder > *')]
        .filter(node => node.getBoundingClientRect().right > popover.right + 0.5).map(node => node.className || node.tagName) }; })()`), { cut: true, outside: [] });
    await select(".work-place-form select", "feature");
    assert.equal(await shown(form), true, "the form stayed open");
    await escape();
    await gone(".work-place-form", "Escape closes the places");
    assert.match((await text(placeChip))!, /New worktree from feature/);

    // Enter in the title creates, with what was typed and chosen; the session is opened with those choices.
    await press(`${form} input`);
    await until(titleFocused, "the title has the focus");
    await enter();
    await until(`!document.querySelector(${JSON.stringify(form)}) && !!document.querySelector('.session-row[data-session-id=created-1] button[aria-pressed=true]')`, "the created session is opened");
    assert.deepEqual(await creates(), [{ scope: "project", title: "Named work", providerId: "alternate", worktree: true, baseBranch: "feature" }]);
    assert.equal(await evaluate("newSessionFixture.creates[0].permissionMode"), "bypassPermissions", "the session is created in the mode chosen");
    // The session starts in that mode: its own composer names it, as the mode of the session.
    await until("[...document.querySelectorAll('.owned-session .composer-selection')].some(node=>/Plan.*New model.*Low.*Bypass permissions/.test(node.textContent)&&node.title.includes('Provider: Alternate'))", "the choices are those of its first Send");
    assert.equal(await evaluate("JSON.parse(localStorage.getItem('codealta.desktop.selection.created-1')).selection.context.tokens"), 922000,
      "context is handed to the owned session, not a provider-wide mutation");
    assert.deepEqual(await evaluate("settingsShellFixture.sends"), [], "a creation sends nothing");
    assert.deepEqual(await remembered(), { [projectScope]: { providerKey: "alternate", modelId: "new", reasoningEffort: "Low", permissionMode: "bypassPermissions" } });

    // The next form of that project starts from those choices, with an empty title, and with the Default agent prompt:
    // the agent prompt chosen was for the session created.
    await open();
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(`${form} input`)}).value`), "");
    assert.match((await text(chip))!, /Default.*New model.*Low.*Bypass permissions$/);
    assert.match(await evaluate(`document.querySelector(${JSON.stringify(chip)})?.title ?? ""`) as string, /Provider: Alternate/);
    await until(`/New worktree from feature/.test(document.querySelector(${JSON.stringify(placeChip)})?.textContent)`, "the place of the last session");
    // Escape closes the picker that is open, and a second Escape the form: the row has the focus again.
    await press(chip);
    await until("!!document.querySelector('#composer-agent-create')", "the choices of the chip");
    await press(".composer-selection-more-button");
    await until("!!document.querySelector('#composer-context-create')", "the next draft's More options");
    assert.equal(await evaluate("document.querySelector('#composer-context-create').value"), "", "the next new session starts at Default");
    // An Escape that reaches the form while a picker is open, as in the moment before the picker has the focus, closes nothing.
    await evaluate(`document.querySelector(${JSON.stringify(`${form} input`)}).dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true }))`);
    assert.equal(await shown(form), true, "the form is not closed in the place of its picker");
    // Blueprint gives the focus to the overlay of the choices, a frame after it opens.
    await until("!!document.activeElement.closest('.bp6-overlay')?.querySelector('.composer-selection-form')", "the choices have the focus");
    await escape();
    await gone(".composer-selection-form", "the first Escape closes the choices");
    assert.equal(await shown(form), true);
    await escape();
    await gone(form, "the second Escape closes the form");
    assert.equal(await evaluate("document.activeElement.dataset.scope"), "project");
    // A press elsewhere closes it, and so does a press on the button that opened it.
    await open();
    await pressAt(1200, 700);
    await gone(form, "a press elsewhere closes the form");
    await open();
    await press(projectPlus);
    await evaluate("new Promise(resolve => setTimeout(resolve, 150))");
    assert.equal(await shown(form), false, "its button closes it");
    assert.equal((await creates()).length, 1, "closing creates nothing");

    // Create closes the form at once: the New session tab of the project says that the session starts, and the window
    // is not held while the host creates it. The session opens in that tab once it exists.
    const dockTabs = "[...document.querySelectorAll('.session-dock .flexlayout__tab_button')].map(button => button.textContent.trim())";
    await open();
    await evaluate("newSessionFixture.hold=true");
    await press(create);
    await until("newSessionFixture.creates.length===2", "the creation was asked");
    await gone(form, "the form closes on Create");
    await until(`${dockTabs}.some(name => name.startsWith("Starting… — Project"))`, "a tab says that the session starts");
    await until("document.querySelector('#active-session-content')?.textContent.includes('Creating session…')", "the tab shows the creation");
    // The tab shows the steps the host says, as they come: a spinner on the one that runs, a check mark on those
    // that are done, each with how long it took.
    const stepsShown = () => evaluate("[...document.querySelectorAll('.session-start-step')].map(node => [node.dataset.kind, node.dataset.state, node.querySelector('.session-start-label').textContent, node.querySelector('.session-start-time')?.textContent ?? '', !!node.querySelector('.activity-spinner')])") as Promise<[string, string, string, string, boolean][]>;
    const setSteps = (steps: object[]) => evaluate(`newSessionFixture.steps = ${JSON.stringify(steps)}`);
    const step = (kind: string, state: string, elapsedMilliseconds: number, detail: string | null = null, message: string | null = null) => ({ kind, state, detail, elapsedMilliseconds, message });
    await setSteps([step("worktree", "running", 1200, "alta/quiet-heron")]);
    await until("document.querySelector('.session-start-step[data-kind=worktree]')?.textContent.includes('alta/quiet-heron')", "the worktree that is created");
    assert.deepEqual(await stepsShown(), [["worktree", "running", "Creating the worktree alta/quiet-heron…git worktree add", "1.2 s", true]]);
    await setSteps([step("worktree", "done", 2300, "alta/quiet-heron"), step("checkout", "running", 14_500, "Updating files: 45% (450/1000)")]);
    await until("document.querySelector('.session-start-step[data-kind=checkout]')?.textContent.includes('45%')", "the progress git gives of its checkout");
    assert.deepEqual(await stepsShown(), [["worktree", "done", "Created the worktree alta/quiet-herongit worktree add", "2.3 s", false],
      ["checkout", "running", "Checking out the files…Updating files: 45% (450/1000)", "14 s", true]]);
    // A repository with submodules: git checks them out after the files.
    await setSteps([step("worktree", "done", 2300, "alta/quiet-heron"), step("checkout", "done", 75_000), step("submodules", "running", 3100, "Receiving objects: 60% (6/10)")]);
    await until("document.querySelector('.session-start-step[data-kind=submodules]')?.textContent.includes('60%')", "the progress git gives of the submodules");
    assert.deepEqual((await stepsShown()).at(-1), ["submodules", "running", "Updating the submodules…Receiving objects: 60% (6/10)", "3.1 s", true]);
    await setSteps([step("worktree", "done", 2300, "alta/quiet-heron"), step("checkout", "done", 75_000), step("submodules", "done", 4000), step("session", "running", 400)]);
    await until("document.querySelector('.session-start-step[data-kind=session]')?.dataset.state === 'running'", "the session is prepared");
    assert.deepEqual((await stepsShown()).map(([kind, state, label, time]) => [kind, state, label, time]), [["worktree", "done", "Created the worktree alta/quiet-herongit worktree add", "2.3 s"],
      ["checkout", "done", "Checked out the filesgit checkout", "1 min 15 s"], ["submodules", "done", "Updated the submodulesgit submodule update", "4.0 s"],
      ["session", "running", "Preparing the session…", "0.4 s"]]);
    await setSteps([step("worktree", "done", 2300, "alta/quiet-heron"), step("checkout", "done", 75_000), step("session", "done", 900), step("ready", "done", 0)]);
    // Nothing of the window waits: Escape and a press elsewhere have no form to close, and the prompt takes text.
    await escape(); await pressAt(1200, 700);
    assert.equal(await shown(form), false);
    assert.equal(await evaluate("document.querySelector('#catalog-prompt textarea')?.disabled ?? document.querySelector('#catalog-prompt')?.disabled"), false, "the prompt takes text");
    await evaluate("newSessionFixture.release(); newSessionFixture.hold=false");
    await until("!!document.querySelector('.session-row[data-session-id=created-2] button[aria-pressed=true]')", "the second session is opened");
    assert.equal((await evaluate(dockTabs) as string[]).some(name => name.startsWith("Starting")), false, "the session took the place of the tab that started it");
    assert.equal(await shown(".session-start"), false, "the steps are gone with the tab");
    await setSteps([]);
    assert.deepEqual((await creates())[1], { scope: "project", title: null, providerId: "alternate", worktree: true, baseBranch: "feature" }, "a title is optional");

    // A step that fails says so in the tab, with what git said, and offers Retry and Close; Close leaves nothing behind.
    await open();
    await evaluate("newSessionFixture.refused=true");
    await setSteps([step("worktree", "failed", 600, "alta/quiet-heron", "fatal: invalid reference: feature")]);
    await press(create);
    await until("!!document.querySelector('.session-start-error[role=alert]')", "the failure is said in the tab");
    assert.match((await text(".session-start-error p"))!, /branch the worktree starts from/);
    assert.equal(await text(".session-start-error pre"), "fatal: invalid reference: feature", "what git said");
    assert.deepEqual(await stepsShown(), [["worktree", "failed", "Creating the worktree alta/quiet-herongit worktree add", "0.6 s", false]]);
    assert.deepEqual(await evaluate("[...document.querySelectorAll('.session-start-actions button')].map(button => button.textContent.trim())"), ["Close", "Retry"]);
    assert.equal(await shown(form), false, "the form stays closed");
    assert.equal(await text(".session-rail .notice"), null, "and nothing is said in the list");
    await evaluate("newSessionFixture.refused=false");
    await setSteps([]);
    await evaluate("[...document.querySelectorAll('.session-start-actions button')].find(button => button.textContent.trim() === 'Close').click()");
    await gone(".session-start", "Close takes the steps away");
    assert.equal(await text(".session-rail .notice"), null);

    // A session that exists but cannot be given the choices is opened as it is, and a toast says so.
    await open();
    await evaluate("newSessionFixture.choicesFail=true");
    await press(create);
    await until(`!document.querySelector(${JSON.stringify(form)}) && !!document.querySelector('.session-row[data-session-id=created-4] button[aria-pressed=true]')`, "the session is opened");
    await until("[...document.querySelectorAll('.bp6-toast')].some(node=>node.textContent.includes('The session uses its default agent and model.'))", "the toast");
    await evaluate("newSessionFixture.choicesFail=false");
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(projectPlus)}).disabled`), false, "another session can be created");

    // The chats have the same button, menu entry and form, without a place.
    await over(chatsRow); await press(".project-root-list .project-actions-trigger");
    await until("[...document.querySelectorAll('.session-tab-popup [role=menuitem]')].some(node=>node.textContent.trim()==='New chat…')", "the menu of the chats");
    await escape();
    await gone(".session-tab-popup", "the menu is closed");
    await open(chatsRow, chatsPlus);
    assert.equal(await text(`${form} > strong`), "New chat");
    assert.equal(await shown(`${form} .project-context`), false);
    const chats = (await box(".project-root-list li.project-action-row"))!, beside = (await box(".new-session-popover"))!;
    assert.ok(beside.left >= chats.right && Math.abs(beside.top - chats.top) < 24, `beside the row of the chats: ${JSON.stringify({ chats, beside })}`);
    assert.match((await text(chip))!, /Default.*New model.*High$/, "the chats have choices of their own");
    assert.match(await evaluate(`document.querySelector(${JSON.stringify(chip)})?.title ?? ""`) as string, /Provider: Fixture/);
    await command("Input.insertText", { text: "A chat" });
    await enter();
    await until(`!document.querySelector(${JSON.stringify(form)}) && !!document.querySelector('.session-row[data-session-id=created-5] button[aria-pressed=true]')`, "the chat is opened");
    assert.deepEqual((await creates())[4], { scope: "global", title: "A chat", providerId: "fixture", worktree: false, baseBranch: null });
    assert.deepEqual(Object.keys(await remembered()), [projectScope], "nothing was chosen for the chats");

    // A narrow window: the form is whole, in the page.
    await command("Emulation.setDeviceMetricsOverride", { width: 900, height: 650, deviceScaleFactor: 1, mobile: false });
    await open();
    const narrow = (await box(".new-session-popover"))!;
    assert.ok(narrow.left >= 0 && narrow.top >= 0 && narrow.right <= 900 && narrow.bottom <= 650, `in the page: ${JSON.stringify(narrow)}`);
    assert.equal(await overflows(), false, "nothing overflows the narrow form");
    assert.equal(narrow.width, 420);
    // The choices of a chip open over the form, and are whole in the page too.
    await press(chip);
    await until("!!document.querySelector('#composer-agent-create')", "the choices of the chip");
    await settled(".composer-selection-form");
    const choices = (await box(".composer-selection-form"))!;
    assert.ok(choices.left >= 0 && choices.top >= 0 && choices.right <= 900 && choices.bottom <= 650, `the choices are in the page: ${JSON.stringify(choices)}`);
    await escape();
    await gone(".composer-selection-form", "the choices are closed");
    await escape();
    await gone(form, "closed");
    await command("Emulation.setDeviceMetricsOverride", { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });

    // A creation the host does not confirm: a toast says so and the list of sessions is read again. Nothing is sent
    // twice, and nothing is locked: another session can be created at once.
    const toasted = (words: string) => `[...document.querySelectorAll('.bp6-toast')].some(node => node.textContent.includes(${JSON.stringify(words)}))`;
    const dismissToasts = async () => { await evaluate("document.querySelectorAll('.bp6-toast button').forEach(button => button.click())"); await gone(".bp6-toast", "the toasts are closed"); };
    await dismissToasts();
    await open();
    await evaluate("newSessionFixture.uncertain=true");
    await press(create);
    await gone(form, "the form closes");
    await until(toasted("Creation may have completed"), "the toast says the creation is unconfirmed");
    await evaluate("newSessionFixture.uncertain=false");
    assert.equal((await creates()).length, 6, "an unconfirmed creation is not sent again");
    await until(`!document.querySelector(${JSON.stringify(projectPlus)}).disabled`, "creation is not locked");
    assert.equal(await evaluate("[...document.querySelectorAll('.notice')].some(node => node.textContent.includes('blocked'))"), false);

    // An answer lost on the way (the wait ended) is asked again by the identifier of its request: the session is
    // opened all the same, and was asked for once.
    await dismissToasts();
    await open();
    await evaluate("newSessionFixture.lost=true");
    await press(create);
    await until("newSessionFixture.outcomeReads.length===1", "the outcome is asked again");
    await until("!!document.querySelector('.session-row[data-session-id=created-7] button[aria-pressed=true]')", "the session of the lost answer is opened");
    assert.equal(await evaluate("newSessionFixture.outcomeReads[0] === newSessionFixture.creates[6].requestId"), true, "by the identifier of its request");
    assert.equal((await creates()).length, 7, "the creation was sent once");
    await evaluate("newSessionFixture.lost=false");

    // The user goes elsewhere while the session is created: it is created all the same, a toast offers it, and
    // creation is not locked.
    await open();
    await evaluate("newSessionFixture.hold=true");
    await press(create);
    await until("newSessionFixture.creates.length===8", "the creation was asked");
    await press(chatsRow, 0.15);
    await until(`document.querySelector(${JSON.stringify(chatsRow)}).getAttribute("aria-pressed") === "true"`, "the chats are shown");
    await evaluate("newSessionFixture.release(); newSessionFixture.hold=false");
    await until(toasted("was created"), "a toast offers the session");
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(chatsRow)}).getAttribute("aria-pressed")`), "true", "the user stays where they went");
    await until(`!document.querySelector(${JSON.stringify(chatsPlus)}).disabled && !document.querySelector(${JSON.stringify(projectPlus)}).disabled`, "creation is not locked");
    await evaluate("[...document.querySelectorAll('.bp6-toast .bp6-button')].find(button => button.textContent.trim() === 'Show').click()");
    await until("!!document.querySelector('.session-row[data-session-id=created-8] button[aria-pressed=true]')", "Show opens the session");

    // Retry asks again for what failed, as it was asked: the session then opens in the tab.
    await dismissToasts();
    await open();
    await evaluate("newSessionFixture.refused=true");
    await press(create);
    await until("!!document.querySelector('.session-start-error[role=alert]')", "the failure is said in the tab");
    await evaluate("newSessionFixture.refused=false");
    await evaluate("[...document.querySelectorAll('.session-start-actions button')].find(button => button.textContent.trim() === 'Retry').click()");
    await until("!!document.querySelector('.session-row[data-session-id=created-10] button[aria-pressed=true]')", "Retry creates the session and opens it");
    assert.deepEqual((await creates()).slice(8), [{ scope: "project", title: null, providerId: "alternate", worktree: true, baseBranch: "feature" },
      { scope: "project", title: null, providerId: "alternate", worktree: true, baseBranch: "feature" }], "the same creation, asked again");

    // Another window reads what was chosen from local storage, and only looks at it: a project that is looked at stores nothing.
    const stored = await evaluate(`localStorage.getItem(${JSON.stringify(preferencesKey)})`);
    await command("Page.reload");
    await start("the reloaded shell");
    await open();
    assert.match((await text(chip))!, /Default.*New model.*Low.*Bypass permissions$/);
    assert.match(await evaluate(`document.querySelector(${JSON.stringify(chip)})?.title ?? ""`) as string, /Provider: Alternate/);
    await until(`/New worktree from feature/.test(document.querySelector(${JSON.stringify(placeChip)})?.textContent)`, "the restored place");
    await escape();
    await gone(form, "closed");

    // What was chosen is a preference, not an authority: a model that is gone leaves the default of the provider.
    await evaluate("localStorage.setItem('newSessionFixtureMissingModel','true')");
    await command("Page.reload");
    await start("the shell with other models");
    await open();
    assert.match((await text(chip))!, /Default.*Old model.*Bypass permissions$/);
    assert.match(await evaluate(`document.querySelector(${JSON.stringify(chip)})?.title ?? ""`) as string, /Provider: Alternate/, "the chip names the provider in its tooltip");
    await escape();
    await gone(form, "closed");
    assert.equal(await evaluate(`localStorage.getItem(${JSON.stringify(preferencesKey)})`), stored, "looking changes nothing of what is remembered");

    // Models that cannot be read: nothing is created, and the form offers to read them again.
    await evaluate("localStorage.removeItem('newSessionFixtureMissingModel'); localStorage.setItem('newSessionFixtureFailModels','true')");
    await command("Page.reload");
    await start("the shell without models");
    await over(projectRow); await press(projectPlus);
    await until(`document.querySelector(${JSON.stringify(`${form} .new-session-refresh`)})?.textContent === "Refresh composer choices"`, "the models can be read again");
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(create)}).disabled`), true);
    await enter();
    assert.equal(await evaluate("newSessionFixture.creates.length"), 0);
    assert.equal(await evaluate(`localStorage.getItem(${JSON.stringify(preferencesKey)})`), stored);
    await escape();
    await gone(form, "closed");

    // The prompt of a new session still hands its text to the session it creates, with the same choices and place.
    await evaluate("localStorage.removeItem('newSessionFixtureFailModels')");
    await command("Page.reload");
    await start("the shell for a draft");
    // The name of the row: its buttons take the rest of it once the pointer is there.
    await press(projectRow, 0.15);
    await until(`document.querySelector(${JSON.stringify(projectRow)}).getAttribute("aria-pressed") === "true" && !!document.querySelector('#catalog-prompt textarea')`, "the prompt of a new session of the project");
    const draftMeter = "#active-session-content .context-usage";
    await until(`!!document.querySelector(${JSON.stringify(draftMeter)})`, "the draft context meter");
    assert.equal(await text(`${draftMeter} .context-usage-tokens`), "0/272k",
      "the draft meter uses the default capacity, not the unrelated 1.05M catalog envelope");
    await press("#active-session-content .composer-selection");
    await until("!!document.querySelector('#composer-model-new')", "the draft picker");
    if (!await shown("#composer-context-new"))
      await evaluate("[...document.querySelectorAll('.composer-selection-form button')].find(node=>node.textContent.trim()==='More options').click()");
    await until("!!document.querySelector('#composer-context-new')", "the draft context selector");
    await select("#composer-context-new", "922000");
    await until(`document.querySelector(${JSON.stringify(`${draftMeter} .context-usage-tokens`)})?.textContent === '0/922k'`, "the draft meter follows an explicit choice");
    await select("#composer-context-new", "");
    await until(`document.querySelector(${JSON.stringify(`${draftMeter} .context-usage-tokens`)})?.textContent === '0/272k'`, "Default restores the default capacity");
    await press("#composer-model-new");
    await until("!!document.querySelector('.composer-model-picker [data-model-id=old]')", "the model with an output reservation");
    // At the bottom composer the menu opens upward; choose through its focused search rather than coordinates.
    await until("document.activeElement === document.querySelector('.composer-model-picker input')", "the model search has focus");
    await command("Input.insertText", { text: "old" });
    await until("document.querySelectorAll('.composer-model-picker [data-model-id]').length === 1", "the 200k model is the only match");
    await enter();
    await gone(".composer-model-picker", "the 200k model was selected");
    await until(`document.querySelector(${JSON.stringify(`${draftMeter} .context-usage-tokens`)})?.textContent === '0/200k'`, "a 200k window is not reduced to 136k");
    await select("#composer-context-new", "1000000");
    await until(`document.querySelector(${JSON.stringify(`${draftMeter} .context-usage-tokens`)})?.textContent === '0/1M'`, "a 1M window is displayed without output subtraction");
    await press("#composer-model-new");
    await until("!!document.querySelector('.composer-model-picker [data-model-id=new]')", "the original model");
    await until("document.activeElement === document.querySelector('.composer-model-picker input')", "the model search has focus again");
    await command("Input.insertText", { text: "new" });
    await until("document.querySelectorAll('.composer-model-picker [data-model-id]').length === 1", "the original model is the only match");
    await enter();
    await until(`document.querySelector(${JSON.stringify(`${draftMeter} .context-usage-tokens`)})?.textContent === '0/272k'`, "switching models restores Default");
    await escape();
    await gone(".composer-selection-form", "the draft picker is closed");
    assert.equal(await evaluate("newSessionFixture.creates.length"), 0, "previewing capacities starts no session");
    const storedAfterModelChoices = await evaluate(`localStorage.getItem(${JSON.stringify(preferencesKey)})`);
    // Refocus the editor after closing the picker, not Monaco's hidden, read-only IME textarea.
    await evaluate("document.querySelector('#catalog-prompt [role=textbox]').focus()");
    await until("document.querySelector('#catalog-prompt').contains(document.activeElement) && document.activeElement?.getAttribute('role') === 'textbox'", "the draft editor has focus");
    await command("Input.insertText", { text: "Original local draft" });
    // The worktree is made as soon as the prompt is typed, from the branch chosen: the prompt waits for it, and says so.
    await until("newSessionFixture.prepares.length === 1 && !!document.querySelector('.worktree-preparation')", "the worktree being made");
    assert.equal(await evaluate("newSessionFixture.prepares[0].baseBranch"), "feature");
    await until("document.querySelector('.worktree-preparation')?.dataset.state === 'ready'", "the worktree ready");
    await until("!document.querySelector('button[aria-label=\"Start session\"]').disabled", "the draft can be sent");
    await evaluate("document.querySelector('button[aria-label=\"Start session\"]').click()");
    await until("settingsShellFixture.sends.length===1", "the draft is sent once");
    assert.deepEqual(await creates(), [{ scope: "project", title: null, providerId: "alternate", worktree: true, baseBranch: "feature" }]);
    assert.equal(await evaluate("newSessionFixture.creates[0].preparedWorktreeId === newSessionFixture.prepares[0].requestId"), true, "the session works in the worktree made for it");
    assert.equal(await evaluate("settingsShellFixture.sends[0].text"), "Original local draft");
    assert.equal(await evaluate(`localStorage.getItem(${JSON.stringify(preferencesKey)})`), storedAfterModelChoices,
      "sending preserves the model preferences chosen in the draft");
    // The session took the place of the prompt it was started from: that prompt has no tab of its own.
    assert.equal((await evaluate(dockTabs) as string[]).some(name => name.startsWith("New session")), false, JSON.stringify(await evaluate(dockTabs)));

    // Escape while a session is created from the prompt, with no window open: there is nothing to close, and the
    // session that comes back is still the one that was asked for. It is opened and given the text.
    // The host of the fixture starts again and names its next session as the one before: what the window kept of that one goes.
    await evaluate("Object.keys(localStorage).filter(key => key.includes('created-1')).forEach(key => localStorage.removeItem(key))");
    await command("Page.reload");
    await start("the shell for a held draft");
    await press(projectRow, 0.15);
    await until(`document.querySelector(${JSON.stringify(projectRow)}).getAttribute("aria-pressed") === "true" && !!document.querySelector('#catalog-prompt textarea')`, "the prompt of a new session again");
    await evaluate("document.querySelector('#catalog-prompt').focus()");
    await command("Input.insertText", { text: "Held local draft" });
    await until("!document.querySelector('button[aria-label=\"Start session\"]').disabled", "the held draft can be sent");
    await evaluate("newSessionFixture.hold=true");
    await evaluate("document.querySelector('button[aria-label=\"Start session\"]').click()");
    await until("newSessionFixture.creates.length===1", "the creation was asked");
    // A notice of the window would take the key to close itself: none is left, and the key has nothing but the window.
    await evaluate("document.querySelectorAll('.bp6-toast button').forEach(button => button.click())");
    await gone(".bp6-toast", "no notice is shown");
    await evaluate("document.activeElement?.blur()");
    await escape();
    await evaluate("new Promise(resolve => setTimeout(resolve, 150))");
    await evaluate("newSessionFixture.release(); newSessionFixture.hold=false");
    await until("settingsShellFixture.sends.length===1", "the held draft is sent once its session exists");
    assert.equal(await evaluate("settingsShellFixture.sends[0].text"), "Held local draft");
    assert.equal(await evaluate("[...document.querySelectorAll('.notice')].some(node => node.textContent.includes('Creation may have completed'))"), false, "the creation is not said to be uncertain");
    assert.equal(await evaluate("!!document.querySelector('.session-row[data-session-id=created-1] button[aria-pressed=true]')"), true, "the session is opened");

    // A prompt with a pasted image is sent with it by the session it creates, at once, and the prompt of the next
    // session of the project is empty again: neither its text nor its image stays there.
    await evaluate("Object.keys(localStorage).filter(key => key.includes('created-1')).forEach(key => localStorage.removeItem(key))");
    await command("Page.reload");
    await start("the shell for a draft with an image");
    // The attachments are shown above the composer of the prompt, beside it.
    const draftAttachments = "(document.querySelector('#catalog-prompt')?.closest('section.prompt-surface')?.parentElement.querySelectorAll('.prompt-image-tile').length ?? -1)";
    await evaluate("newSessionFixture.imageInput=true");
    await press(projectRow, 0.15);
    await until(`document.querySelector(${JSON.stringify(projectRow)}).getAttribute("aria-pressed") === "true" && !!document.querySelector('#catalog-prompt textarea')`, "the prompt of a new session with an image");
    await evaluate("document.querySelector('#catalog-prompt').focus()");
    await command("Input.insertText", { text: "Local draft with an image" });
    await evaluate(`(async () => { const canvas = document.createElement("canvas"); canvas.width = 4; canvas.height = 3;
      canvas.getContext("2d").fillRect(0, 0, 4, 3);
      const blob = await new Promise(resolve => canvas.toBlob(resolve, "image/png"));
      const data = new DataTransfer(); data.items.add(new File([blob], "image.png", { type: "image/png" }));
      document.querySelector('#catalog-prompt textarea').dispatchEvent(new ClipboardEvent("paste", { clipboardData: data, bubbles: true, cancelable: true })); })()`);
    await until(`${draftAttachments} === 1`, "the pasted image above the prompt");
    await until("!document.querySelector('button[aria-label=\"Start session\"]').disabled", "the draft with an image can be sent");
    await evaluate("document.querySelector('button[aria-label=\"Start session\"]').click()");
    await until("settingsShellFixture.sends.length===1", "the draft with an image is sent at once");
    assert.equal(await evaluate("settingsShellFixture.sends[0].text"), "Local draft with an image");
    assert.deepEqual(await evaluate("settingsShellFixture.sends[0].images?.map(image => [image.title, image.mediaType])"), [["Image 1", "image/png"]]);
    // The row of the project shows the session that is open: the prompt of the next session is shown from another scope.
    await press(chatsRow, 0.15);
    await until(`document.querySelector(${JSON.stringify(chatsRow)}).getAttribute("aria-pressed") === "true" && !!document.querySelector('#catalog-prompt textarea')`, "the prompt of a new chat");
    await press(projectRow, 0.15);
    await until(`document.querySelector(${JSON.stringify(projectRow)}).getAttribute("aria-pressed") === "true" && !!document.querySelector('#catalog-prompt textarea')`, "the prompt of the next session");
    assert.equal(await evaluate("document.querySelector('#catalog-prompt textarea').value"), "", "the text went with the session");
    assert.equal(await evaluate(draftAttachments), 0, "the image went with the session");
    assert.deepEqual(exceptions, []);
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close();
    await endBrowser(browser);
    await rm(root, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
  }
});
