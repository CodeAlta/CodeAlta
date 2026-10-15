import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, waitForBrowserPort } from "../browserTarget";

const edge = browserExecutable;
test("the window shows one space at a time: its projects, its sessions and its own tabs, which come back with it", { skip: !edge, timeout: 90_000 }, async t => {
  const root = await mkdtemp(join(tmpdir(), "codealta-spaces-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("../main.tsx", import.meta.url))], outfile: join(root, "fixture.js"),
      bundle: true, platform: "browser", format: "iife", loader: { ".css": "empty", ".flf": "text", ".svg": "dataurl" },
      define: { "import.meta.env.VITE_DEMO_MODE": '"false"' }, plugins: [{ name: "isolated-spaces", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./spaces.neoastra.mount.ts", import.meta.url)) }));
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
    await writeFile(join(root, "style.css"), readFileSync(new URL("../../node_modules/flexlayout-react/style/light.css", import.meta.url), "utf8")
      + readFileSync(new URL("../style.css", import.meta.url), "utf8") + readFileSync(new URL("./spaces.css", import.meta.url), "utf8")
      + readFileSync(new URL("../editor/editor.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
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
      const timer = setTimeout(() => { socket!.removeEventListener("message", reply); reject(new Error(`${method} timed out`)); }, 12_000);
      socket!.addEventListener("message", reply); socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async (expression: string) => {
      const value = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(value.exceptionDetails, undefined, JSON.stringify(value.exceptionDetails)); return value.result?.value;
    };
    const wait = (condition: string) => evaluate(`new Promise(resolve=>{const end=Date.now()+7000;const tick=()=>{if(${condition})resolve(true);else if(Date.now()>end)resolve(document.body.innerText.slice(0,600));else setTimeout(tick,25);};tick();})`);
    // What the window shows: the space of the switch, the projects of the Explorer, the tabs of the dock.
    const projects = () => evaluate("[...document.querySelectorAll('#project-list .project-action-row > button:first-child')].map(row=>row.textContent.trim())");
    const tabs = () => evaluate("[...document.querySelectorAll('.flexlayout__tab_button')].map(tab=>tab.textContent.trim())");
    const shown = () => evaluate("document.querySelector('.space-switch-name')?.textContent ?? null");
    const chip = (name: string) => evaluate(`[...document.querySelectorAll('.space-chip')].find(chip=>chip.getAttribute('aria-label').startsWith(${JSON.stringify(name)})).click()`);
    const openSession = (title: string) => evaluate(`[...document.querySelectorAll('.session-row > button:first-child, .explorer-session > button:first-child, #project-list button')].find(row=>row.textContent.trim().startsWith(${JSON.stringify(title)})).click()`);

    await command("Page.enable");
    await command("Runtime.enable");
    await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    await command("Emulation.setDeviceMetricsOverride", { width: 1280, height: 900, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait("!!document.querySelector('#catalog-prompt')"), true, exceptions.join("\n"));
    await evaluate("localStorage.setItem('settingsFixtureOwned','true');localStorage.setItem('settingsFixtureSecondProject','true')");
    await command("Page.reload");
    assert.equal(await wait("!!document.querySelector('#project-list') && !!document.querySelector('.space-switch-name')"), true, exceptions.join("\n"));

    // The default space: the projects of no other space, and one icon for each space at the foot of the Explorer. It
    // says so once, with Show all, which shows what belongs to the other spaces too.
    assert.equal(await shown(), "Default");
    assert.equal(await wait("JSON.stringify([...document.querySelectorAll('#project-list .project-action-row > button:first-child')].map(row=>row.textContent.trim()))==='[\"Project\"]'"), true);
    assert.equal(await wait("[...document.querySelectorAll('.bp6-toast')].some(toast=>toast.textContent.includes('shows what belongs to no other space'))"), true);
    await evaluate("[...document.querySelectorAll('.bp6-toast')].find(toast=>toast.textContent.includes('shows what belongs to no other space')).querySelector('a').click()");
    assert.equal(await wait("localStorage.getItem('codealta.desktop.space.showOthers.v1')==='true'"), true);
    assert.equal(await wait("document.querySelectorAll('#project-list .project-action-row > button:first-child').length===2"), true);
    assert.deepEqual(await projects(), ["Other project", "Project"]);
    assert.equal(await evaluate("document.querySelectorAll('.space-chip').length"), 2);
    assert.equal(await wait("spacesFixture.shown.includes('default')"), true, "The host is told which space the window shows.");
    assert.equal(await wait("[...document.querySelectorAll('.flexlayout__tab_button')].some(tab=>tab.textContent.includes('one'))"), true);
    const defaultTabs = await tabs() as string[];

    // Work has the other project alone: its Explorer and its tabs are its own, and the session of the first project is no longer in the page.
    await chip("Work");
    assert.equal(await wait("document.querySelector('.space-switch-name')?.textContent==='Work'"), true);
    assert.deepEqual(await projects(), ["Other project"]);
    assert.equal(await evaluate("document.querySelector('.project-rail .panel-title .count')?.textContent"), "1");
    assert.equal((await tabs() as string[]).some(tab => tab.includes("one")), false);
    assert.equal(await evaluate("localStorage.getItem('codealta.desktop.space.v1')"), "work");
    assert.equal(await wait("spacesFixture.shown.at(-1)==='work'"), true);
    await openSession("other-session");
    assert.equal(await wait("[...document.querySelectorAll('.flexlayout__tab_button')].some(tab=>tab.textContent.includes('other-session'))"), true);
    const workTabs = await tabs() as string[];
    assert.equal(await wait("!!localStorage.getItem('codealta.desktop.sessionTabs.v1.work')?.includes('other-session')"), true, "The tabs of a space are kept under its own key.");
    assert.equal(String(await evaluate("localStorage.getItem('codealta.desktop.sessionTabs.v1')")).includes("other-session"), false);

    // Back to the default space: the tabs it had are there again; and back to Work with the keyboard: its own.
    await chip("Default");
    assert.equal(await wait("document.querySelector('.space-switch-name')?.textContent==='Default'"), true);
    assert.deepEqual(await projects(), ["Other project", "Project"]);
    assert.deepEqual(await tabs(), defaultTabs);
    await evaluate("window.dispatchEvent(new KeyboardEvent('keydown',{key:'PageDown',ctrlKey:true,altKey:true,bubbles:true,cancelable:true}))");
    assert.equal(await wait("document.querySelector('.space-switch-name')?.textContent==='Work'"), true);
    assert.deepEqual(await tabs(), workTabs);

    // A command asks the window for a space.
    await evaluate("spacesFixture.tell({kind:'show',spaceId:'default'})");
    assert.equal(await wait("document.querySelector('.space-switch-name')?.textContent==='Default'"), true);
    await evaluate("spacesFixture.tell({kind:'show',spaceId:'work'})");
    assert.equal(await wait("document.querySelector('.space-switch-name')?.textContent==='Work'"), true);

    // A session that waits for the user in a project the shown space does not have is called out at the foot of the Explorer.
    await evaluate("spacesFixture.sessions=[{sessionId:'one',projectId:'project',title:'one',running:false,backgroundTasks:0,failed:false,waiting:true}]");
    assert.equal(await wait("!!document.querySelector('.space-call')"), true);
    assert.equal(await evaluate("document.querySelector('.space-call').title"), "one waits for you in Default");
    assert.equal(await evaluate("document.querySelector('.space-switch').dataset.attention ?? null"), null, "The default space has every project: it is not what the dot of the switch is for.");
    await evaluate("document.querySelector('.space-call').click()");
    assert.equal(await wait("document.querySelector('.space-switch-name')?.textContent==='Default' && document.querySelector('.flexlayout__tab_button--selected')?.textContent.includes('one')"), true);
    assert.equal(await evaluate("document.querySelectorAll('.space-call').length"), 0);

    // The next start comes back on the space the window showed, with its tabs.
    await chip("Work");
    assert.equal(await wait("document.querySelector('.space-switch-name')?.textContent==='Work'"), true);
    await command("Page.reload");
    assert.equal(await wait("document.querySelector('.space-switch-name')?.textContent==='Work' && !!document.querySelector('#project-list')"), true);
    assert.deepEqual(await projects(), ["Other project"]);
    assert.equal(await wait("[...document.querySelectorAll('.flexlayout__tab_button')].some(tab=>tab.textContent.includes('other-session'))"), true);
    assert.deepEqual(exceptions, []);

    // Each regression starts with the real App in Work. Only the host's activity is supplied by the fixture;
    // the toast, tabs, editors, and unsaved-edits question all belong to the mounted production components.
    const resetWork = async () => {
      await evaluate("localStorage.clear();localStorage.setItem('settingsFixtureOwned','true');localStorage.setItem('settingsFixtureSecondProject','true');localStorage.setItem('codealta.desktop.space.v1','work')");
      await command("Page.reload");
      assert.equal(await wait("document.querySelector('.space-switch-name')?.textContent==='Work' && !!document.querySelector('#project-list')"), true);
    };
    const captureToast = async (sessionId = "one") => {
      const message = JSON.stringify(`${sessionId} waits for you in Default`);
      await evaluate(`spacesFixture.sessions=${JSON.stringify([{ sessionId, projectId: "project", title: sessionId, running: false, backgroundTasks: 0, failed: false, waiting: true, ancestors: [] }])}`);
      assert.equal(await wait(`[...document.querySelectorAll('.bp6-toast')].some(toast=>toast.textContent.includes(${message}))`), true);
      await evaluate(`window.waitingToastShow=[...document.querySelectorAll('.bp6-toast')].find(toast=>toast.textContent.includes(${message})).querySelector('a');void 0`);
      assert.equal(await evaluate("waitingToastShow.textContent.trim()"), "Show");
    };
    const clickCapturedToast = async () => {
      assert.equal(await evaluate("waitingToastShow.isConnected"), true, "Click the original toast after later renders, not a replacement callback.");
      await evaluate("waitingToastShow.click()");
    };
    const openEditor = async () => {
      await evaluate("document.querySelector('#project-list button[title=\"Open the code editor\"]').click()");
      assert.equal(await wait("!!document.querySelector('.project-editor')"), true);
    };

    await t.test("Add projects in an empty space opens the folder picker, not Organize spaces", async () => {
      await resetWork();
      await evaluate("localStorage.setItem('settingsFixtureEmptySpace','true')");
      await command("Page.reload");
      assert.equal(await wait("!!document.querySelector('.space-empty button')"), true);
      assert.equal(await shown(), "Work");

      // Canceling the native picker leaves the empty space in place and opens no application dialog.
      await evaluate("projectFocusFixture.pick={status:'canceled',path:null};document.querySelector('.space-empty button').click()");
      assert.equal(await wait("projectFocusFixture.picks.length===1"), true);
      assert.deepEqual(await evaluate("projectFocusFixture.picks[0]"), { title: "Add a project folder", initialDirectory: null });
      assert.equal(await evaluate("!!document.querySelector('dialog[open]')"), false);
      assert.equal(await shown(), "Work");

      // An existing project joins the shown space through the same path as the header's plus action.
      await evaluate("projectFocusFixture.pick={status:'ok',path:'/fixture/other'};document.querySelector('.space-empty button').click()");
      assert.equal(await wait("!document.querySelector('.space-empty') && !!document.querySelector('#project-list button[data-scope=other]')"), true);
      assert.deepEqual(await evaluate("spacesFixture.assigned.at(-1)"), { expectedEpoch: "12345678-1234-1234-1234-123456789abc", projectId: "other", join: ["work"], leave: [] });
      assert.equal(await shown(), "Work");
      assert.equal(await evaluate("!!document.querySelector('dialog[open]')"), false);
      assert.deepEqual(exceptions, []);
    });

    await t.test("Show keeps a tab opened after the toast and does not restore a tab closed after it", async () => {
      await resetWork();
      await openSession("other-session");
      assert.equal(await wait("[...document.querySelectorAll('.flexlayout__tab_button')].some(tab=>tab.textContent.includes('other-session'))"), true);
      await captureToast();
      await evaluate("[...document.querySelectorAll('.flexlayout__tab_button')].find(tab=>tab.textContent.includes('other-session')).querySelector('.flexlayout__tab_button_trailing').click()");
      assert.equal(await wait("![...document.querySelectorAll('.flexlayout__tab_button')].some(tab=>tab.textContent.includes('other-session'))"), true);
      await openEditor();
      const currentTabs = await tabs();
      await clickCapturedToast();
      assert.equal(await wait("document.querySelector('.space-switch-name')?.textContent==='Default' && document.querySelector('.flexlayout__tab_button--selected')?.textContent.includes('one')"), true);
      await chip("Work");
      assert.equal(await wait("document.querySelector('.space-switch-name')?.textContent==='Work'"), true);
      assert.deepEqual(await tabs(), currentTabs, "Returning to Work keeps the tabs from the click, not from the toast's render.");
      assert.deepEqual(exceptions, []);
    });

    for (const lookupPending of [false, true]) await t.test(lookupPending
      ? "Show asks about unsaved edits made while its session lookup is pending, and Cancel keeps them"
      : "Show asks about unsaved edits in an editor opened after the toast, and Cancel keeps it", async () => {
      await resetWork();
      await captureToast(lookupPending ? "created" : "one");
      if (lookupPending) {
        await evaluate("localStorage.setItem('creationFixtureHoldSnapshot','true')");
        await clickCapturedToast();
        assert.equal(await wait("settingsShellFixture.snapshots.length===1"), true);
        assert.equal(await shown(), "Work");
      }
      await openEditor();
      await evaluate("document.querySelector('.project-editor .editor-new-file').click()");
      assert.equal(await wait("!!document.querySelector('.project-editor .monaco-editor :is(.inputarea, .native-edit-context)')"), true);
      await evaluate("document.querySelector('.project-editor .monaco-editor :is(.inputarea, .native-edit-context)').focus()");
      await command("Input.insertText", { text: "keep these unsaved edits" });
      assert.equal(await wait("!!document.querySelector('.project-editor .editor-tab-close[title=\"Unsaved changes\"]')"), true);
      const currentTabs = await tabs();
      if (lookupPending) await evaluate("localStorage.removeItem('creationFixtureHoldSnapshot');settingsShellFixture.releaseSnapshot()");
      else await clickCapturedToast();
      assert.equal(await wait("!!document.querySelector('.unsaved-file-dialog')"), true, "The current editor must participate in the space's dirty guard.");
      assert.equal(await shown(), "Work");
      assert.match(await evaluate("document.querySelector('.unsaved-file-dialog').textContent"), /Save the changes to Untitled-1 before closing/);
      assert.deepEqual(await evaluate("[...document.querySelectorAll('.unsaved-file-dialog button')].map(button=>button.textContent.trim())"), ["Cancel", "Discard", "Save"]);
      await evaluate("[...document.querySelectorAll('.unsaved-file-dialog button')].find(button=>button.textContent==='Cancel').click()");
      assert.equal(await wait("!document.querySelector('.unsaved-file-dialog')"), true);
      assert.equal(await shown(), "Work");
      assert.deepEqual(await tabs(), currentTabs);
      assert.equal(await evaluate("!!document.querySelector('.project-editor .editor-tab-close[title=\"Unsaved changes\"]')"), true);
      assert.equal(await evaluate("document.querySelector('.project-editor .view-lines').textContent.replaceAll('\\u00a0',' ')"), "keep these unsaved edits");
      assert.deepEqual(exceptions, []);
    });

    await t.test("Spaces is a group of Settings: its cards sum up each space, and the page of a space has a tab per section", async () => {
      await resetWork();
      await evaluate("document.querySelector('.space-switch').click()");
      assert.equal(await wait("[...document.querySelectorAll('.bp6-menu-item')].some(item=>item.textContent.includes('Organize spaces'))"), true);
      await evaluate("[...document.querySelectorAll('.bp6-menu-item')].find(item=>item.textContent.includes('Organize spaces')).click()");
      assert.equal(await wait("!!document.querySelector('.space-card .space-open')"), true);
      // Spaces is a group of its own, right after Personalization: its overview, then each space.
      assert.deepEqual(await evaluate("[...document.querySelectorAll('.settings-dialog-group h3')].slice(0, 3).map(title=>title.textContent)"), ["Personalization", "Spaces", "Agent & models"]);
      assert.deepEqual(await evaluate(`(()=>{const group=document.querySelector('[data-settings-section="spaces"]').closest('.settings-dialog-group');
        return [...group.querySelectorAll('button')].map(button=>(button.dataset.settingsSection ?? button.dataset.settingsSpace)+':'+button.textContent+':'+(button.getAttribute('aria-current') ?? ''));})()`),
        ["spaces:Overview:page", "default:Default:", "work:Work:"]);
      // Each card says what its space sets up, on one line: its projects and its own provider account.
      assert.equal(await wait("document.querySelector('.space-card[aria-label=Work] .space-card-summary')?.textContent==='1 project · Claude Business'"), true);
      assert.equal(await evaluate("document.querySelector('.space-pool .space-card-summary').textContent"), "2 projects · Claude Home");

      await evaluate("document.querySelector('.space-card .space-open').click()");
      assert.equal(await wait("document.querySelector('.space-page h1')?.textContent==='Work'"), true);
      assert.equal(await evaluate("document.querySelector('[data-settings-space=work]').getAttribute('aria-current')"), "page", "the navigation marks the space shown");
      // A tab per section, each with what it sets; General is shown first, and alone.
      assert.deepEqual(await evaluate("[...document.querySelectorAll('.space-section-tab')].map(tab=>tab.querySelector('.space-section-tab-title').textContent+':'+(tab.querySelector('.space-section-tab-status')?.textContent ?? ''))"),
        ["General:", "Projects:1", "Resources:Claude Business", "New sessions:Usual settings", "Tools:None off", "Identity:Usual settings"]);
      assert.deepEqual(await evaluate("[...document.querySelectorAll('.space-page .space-section h2')].map(heading=>heading.firstChild.textContent)"), ["General"]);
      assert.equal(await evaluate("document.querySelector('.space-page input.bp6-input').value"), "Work");
      // The head, the tabs and the sections share one width at every size: Show and Delete end where the sections end.
      for (const [width, height] of [[2048, 1114], [1600, 1000], [900, 650]]) {
        await command("Emulation.setDeviceMetricsOverride", { width, height, deviceScaleFactor: 1, mobile: false });
        // In a narrow window the buttons go under the title; the tabs and the sections still end together.
        assert.equal(await wait(`(() => { const box = selector => document.querySelector(selector).getBoundingClientRect();
          const actions = box('.space-page-heading .settings-editor-actions'), sections = box('.space-sections').right, page = document.querySelector('.space-page');
          const wrapped = actions.top >= box('.space-page-title').bottom;
          return (wrapped || Math.abs(actions.right - sections) <= 2) && Math.abs(box('.space-section-nav').right - sections) <= 2 && page.scrollWidth <= page.clientWidth + 1; })()`),
          true, `at ${width} px`);
      }
      await command("Emulation.setDeviceMetricsOverride", { width: 1280, height: 900, deviceScaleFactor: 1, mobile: false });
      // The arrows move between the tabs, as in any list of tabs.
      await evaluate("document.querySelector('.space-section-tab[aria-selected=true]').focus()");
      for (const type of ["rawKeyDown", "keyUp"]) await command("Input.dispatchKeyEvent", { type, key: "ArrowRight", code: "ArrowRight", windowsVirtualKeyCode: 39 });
      assert.equal(await wait("document.activeElement?.dataset.spaceSection==='projects' && !!document.querySelector('.space-page .space-project-checks')"), true);

      await evaluate("document.querySelector('.space-section-tab[data-space-section=resources]').click()");
      assert.equal(await wait("!!document.querySelector('.space-available')"), true);
      assert.equal(await evaluate("document.querySelector('.space-available-rule').textContent"), "Shared items are in every space. Turn one off here to keep it out of this space.");
      assert.deepEqual(await evaluate("[...document.querySelectorAll('.space-available-tabs .bp6-tab')].map(tab=>tab.firstChild.textContent)"),
        ["Providers", "MCP Servers", "Skills", "Agent prompts", "Plugins"]);
      // The providers come first: the account of Work and the one of every space are here, the one of the default space is not.
      assert.equal(await wait("document.querySelectorAll('.space-available .space-resource-row').length===3"), true);
      assert.deepEqual(await evaluate(`[...document.querySelectorAll('.space-available .space-resource-row')].map(row=>
        row.querySelector('.space-resource-name strong').textContent+':'+row.querySelector('.space-resource-switch input').checked+':'+row.querySelector('.space-scope').textContent)`),
        ["Claude Business:true:Only: Work", "Claude Home:false:Only: Default", "Codex:true:All spaces"]);
      assert.equal(await evaluate("document.querySelector('.space-available-tabs .bp6-tab[aria-selected=\"true\"] .space-section-count').textContent"), "2");
      // The lists are read once for the cards and the pages, not again at each render: the host cuts off a page that asks too often.
      const reads = await evaluate("spacesFixture.providerReads") as number;
      await new Promise(resolve => setTimeout(resolve, 800));
      assert.equal(await evaluate("spacesFixture.providerReads"), reads, "the page reads its lists once");
      assert.ok(reads <= 2, `read ${reads} times`);
      assert.ok(Number(await evaluate("spacesFixture.serverReads")) <= 2, `Read ${await evaluate("spacesFixture.serverReads")} times.`);
      // In a narrow window the tabs of the sections and of the kinds wrap inside the page instead of widening it.
      await command("Emulation.setDeviceMetricsOverride", { width: 900, height: 650, deviceScaleFactor: 1, mobile: false });
      assert.equal(await wait("(() => { const card = document.querySelector('.space-available'); const tabs = card.querySelector('.bp6-tab-list').getBoundingClientRect(); return tabs.right <= card.getBoundingClientRect().right + 1 && card.scrollWidth <= card.clientWidth + 1; })()"), true);
      assert.equal(await wait("(() => { const nav = document.querySelector('.space-section-nav'); return nav.scrollWidth <= nav.clientWidth + 1 && [...nav.children].every(tab => tab.getBoundingClientRect().right <= nav.getBoundingClientRect().right + 1); })()"), true);
      await command("Emulation.setDeviceMetricsOverride", { width: 1280, height: 900, deviceScaleFactor: 1, mobile: false });

      // A kind with nothing yet points to the page that adds one.
      await evaluate("[...document.querySelectorAll('.space-available-tabs .bp6-tab')].find(tab=>tab.firstChild.textContent==='Skills').click()");
      assert.equal(await wait("document.querySelector('.space-resource-empty')?.textContent==='No skill yetAdd a skill…'"), true);
      await evaluate("document.querySelector('.space-resource-add').click()");
      assert.equal(await wait("document.querySelector('[data-settings-section=skills]')?.getAttribute('aria-current')==='page'"), true);
      // The Reload of a page of resources reads the spaces of the resources again, after a hand edit of the configuration.
      const assignmentReads = await evaluate("spacesFixture.assignmentReads") as number;
      await evaluate("[...document.querySelectorAll('.settings-dialog-content button')].find(button=>button.textContent.trim()==='Reload').click()");
      assert.equal(await wait(`spacesFixture.assignmentReads>${assignmentReads}`), true);
      // The MCP Servers page says "All spaces" in words, as the page of a space and the Providers page do.
      await evaluate("document.querySelector('[data-settings-section=mcp]').click()");
      assert.equal(await wait("(() => { const name = document.querySelector('.settings-editor-list .space-scope[data-all=true] .space-scope-name'); return !!name && name.textContent==='All spaces' && name.getBoundingClientRect().width > 0; })()"), true);
      // A space of the navigation opens its page at the section last shown.
      await evaluate("document.querySelector('[data-settings-space=work]').click()");
      assert.equal(await wait("document.querySelector('.space-page h1')?.textContent==='Work' && document.querySelector('.space-section-tab[aria-selected=true]')?.dataset.spaceSection==='resources'"), true);
      await evaluate("document.querySelector('.space-section-tab[data-space-section=newSessions]').click()");
      assert.equal(await wait("!!document.querySelector('.space-page .space-agent-prompt select')"), true);
      await evaluate("document.querySelector('.space-page-back').click()");
      assert.equal(await wait("!document.querySelector('.space-page') && !!document.querySelector('.space-card')"), true);
      assert.equal(await evaluate("document.querySelector('[data-settings-section=spaces]').getAttribute('aria-current')"), "page");

      // A click on the card where it has no control opens it too; a click on a control of the card does not.
      await evaluate("document.querySelector('.space-card .space-card-count').click()");
      assert.equal(await wait("document.querySelector('.space-page h1')?.textContent==='Work'"), true);
      await evaluate("document.querySelector('.space-page-back').click()");
      assert.equal(await wait("!!document.querySelector('.space-card')"), true);
      await evaluate("document.querySelector('.space-card .space-show').click()");
      assert.equal(await evaluate("!!document.querySelector('.space-page')"), false);
      assert.deepEqual(exceptions, []);
    });

    await t.test("a new space starts empty in one click, or with the setup of another space", async () => {
      await resetWork();
      await evaluate("document.querySelector('.space-switch').click()");
      assert.equal(await wait("[...document.querySelectorAll('.bp6-menu-item')].some(item=>item.textContent.includes('Organize spaces'))"), true);
      await evaluate("[...document.querySelectorAll('.bp6-menu-item')].find(item=>item.textContent.includes('Organize spaces')).click()");
      assert.equal(await wait("!!document.querySelector('.space-card-new')"), true);
      const create = async (name: string, copyFrom: string | null) => {
        await evaluate("document.querySelector('.space-card-new').click()");
        assert.equal(await wait("!!document.querySelector('.space-dialog #space-name')"), true);
        assert.equal(await evaluate("document.querySelector('#space-copy-from').value"), "", "a new space starts empty unless asked");
        assert.deepEqual(await evaluate("[...document.querySelectorAll('#space-copy-from option')].slice(0, 3).map(option=>option.textContent)"),
          ["Empty", "Copy the setup of Default", "Copy the setup of Work"]);
        await evaluate(`(()=>{const input=document.querySelector('#space-name');Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,${JSON.stringify(name)});
          input.dispatchEvent(new Event('input',{bubbles:true}));})()`);
        if (copyFrom) {
          await evaluate(`(()=>{const select=document.querySelector('#space-copy-from');Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype,'value').set.call(select,${JSON.stringify(copyFrom)});
            select.dispatchEvent(new Event('change',{bubbles:true}));})()`);
          assert.equal(await wait("document.querySelector('.space-dialog').textContent.includes('Its resources, new sessions, model tiers and tools. Not its identity.')"), true);
        }
        const before = await evaluate("spacesFixture.created.length") as number;
        await evaluate("[...document.querySelectorAll('.space-dialog footer button')].find(button=>button.textContent==='Create space').click()");
        assert.equal(await wait(`spacesFixture.created.length===${before + 1} && !document.querySelector('.space-dialog')`), true);
        return await evaluate("spacesFixture.created.at(-1).copyFrom");
      };
      assert.equal(await create("Client", "work"), "work");
      assert.equal(await create("Lab", null), null);
      assert.deepEqual(exceptions, []);
    });

    await t.test("a project dragged in Settings > Spaces joins a space, moves to another, is copied with Ctrl and leaves on the list", async () => {
      await resetWork();
      await evaluate("localStorage.setItem('settingsFixtureThirdSpace','true')");
      await command("Page.reload");
      assert.equal(await wait("!!document.querySelector('.space-switch') && !!document.querySelector('#project-list')"), true);
      await evaluate("document.querySelector('.space-switch').click()");
      assert.equal(await wait("[...document.querySelectorAll('.bp6-menu-item')].some(item=>item.textContent.includes('Organize spaces'))"), true);
      await evaluate("[...document.querySelectorAll('.bp6-menu-item')].find(item=>item.textContent.includes('Organize spaces')).click()");
      assert.equal(await wait("document.querySelectorAll('.space-card').length===2"), true);
      // The drag is made with the mouse, as the user makes it. The window's host (WebView2, whose OLE drop targets
      // NeoAstra replaces) ends a native HTML drag at once: Edge is told to keep native drags too, so that a drag
      // that relies on them drops nothing here either.
      await command("Input.setInterceptDrags", { enabled: true });
      const project = (list: string, name: string) => `[...document.querySelectorAll(${JSON.stringify(list + " .space-project")})].find(row=>row.querySelector('.space-project-name').textContent===${JSON.stringify(name)})`;
      const card = (name: string) => `document.querySelector('.space-card[aria-label=${JSON.stringify(name)}]')`;
      const centerOf = async (element: string) => await evaluate(`(()=>{const element=${element};element.scrollIntoView({block:'nearest'});
        const box=element.getBoundingClientRect();return {x:Math.round(box.x+box.width/2),y:Math.round(box.y+box.height/2)};})()`) as { x: number; y: number };
      const frames = () => evaluate("new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve(true))))");
      const mouse = async (type: "mousePressed" | "mouseMoved" | "mouseReleased", point: { x: number; y: number }, modifiers = 0) => {
        await command("Input.dispatchMouseEvent", { type, x: point.x, y: point.y, modifiers, button: "left", buttons: type === "mouseReleased" ? 0 : 1, clickCount: 1 });
        await frames();
      };
      // What the page shows while the button is held over the target: the target marked, and the project by the pointer.
      const drag = async (from: string, to: string, modifiers = 0) => {
        const start = await centerOf(from), end = await centerOf(to);
        await command("Input.dispatchMouseEvent", { type: "mouseMoved", x: start.x, y: start.y });
        await mouse("mousePressed", start, modifiers);
        await mouse("mouseMoved", { x: start.x + 12, y: start.y + 12 }, modifiers);
        await mouse("mouseMoved", end, modifiers);
        const shown = await evaluate(`(()=>{const ghost=document.querySelector('.space-drag-ghost');const box=ghost?.getBoundingClientRect();
          return {over:document.querySelector('[data-space-drop][data-over]')?.dataset.spaceDrop??null,ghost:ghost?.textContent??null,
            near:!!box&&Math.abs(box.left-${end.x})<40&&Math.abs(box.top-${end.y})<40,copy:ghost?.dataset.copy==='true'};})()`);
        await mouse("mouseReleased", end, modifiers);
        assert.equal(await evaluate("!!document.querySelector('.space-drag-ghost, [data-space-drop][data-over]')"), false, "The drag ends with the release.");
        return shown as { over: string | null; ghost: string | null; near: boolean; copy: boolean };
      };
      const assigned = async (count: number) => {
        assert.equal(await wait(`spacesFixture.assigned.length===${count}`), true, "The drop changed nothing.");
        const request = await evaluate("spacesFixture.assigned.at(-1)") as { projectId: string; join: string[]; leave: string[] };
        return { projectId: request.projectId, join: request.join, leave: request.leave };
      };
      const before = await evaluate("spacesFixture.assigned.length") as number;

      // From the list of every project onto a space: it joins it.
      assert.deepEqual(await drag(project(".space-pool", "Project"), card("Work")), { over: "work", ghost: "Project", near: true, copy: true });
      assert.deepEqual(await assigned(before + 1), { projectId: "project", join: ["work"], leave: [] });
      assert.equal(await wait(`!!${project(".space-card[aria-label=Work]", "Project")}`), true);
      assert.equal(await evaluate("!!document.querySelector('.space-page')"), false, "A drag opens no page.");

      // From a space onto another: it moves.
      assert.deepEqual(await drag(project(".space-card[aria-label=Work]", "Project"), card("Home")), { over: "home", ghost: "Project", near: true, copy: false });
      assert.deepEqual(await assigned(before + 2), { projectId: "project", join: ["home"], leave: ["work"] });
      assert.equal(await wait(`!!${project(".space-card[aria-label=Home]", "Project")} && !${project(".space-card[aria-label=Work]", "Project")}`), true);

      // With Ctrl, it is in both.
      assert.deepEqual(await drag(project(".space-card[aria-label=Home]", "Project"), card("Work"), 2), { over: "work", ghost: "Project", near: true, copy: true });
      assert.deepEqual(await assigned(before + 3), { projectId: "project", join: ["work"], leave: [] });
      assert.equal(await wait(`!!${project(".space-card[aria-label=Home]", "Project")} && !!${project(".space-card[aria-label=Work]", "Project")}`), true);

      // Back on the list of every project: it leaves the space it came from.
      assert.deepEqual(await drag(project(".space-card[aria-label=Work]", "Project"), "document.querySelector('.space-pool .space-projects')"),
        { over: "", ghost: "Project", near: true, copy: false });
      assert.deepEqual(await assigned(before + 4), { projectId: "project", join: [], leave: ["work"] });
      assert.equal(await wait(`!${project(".space-card[aria-label=Work]", "Project")}`), true);
      assert.equal(await evaluate("!!document.querySelector('.space-page')"), false);

      // A drop where it changes nothing, onto the space it is in, does nothing; and the release on that card does not open it.
      assert.deepEqual(await drag(project(".space-card[aria-label=Home]", "Project"), `${card("Home")}.querySelector('.space-card-count')`),
        { over: null, ghost: "Project", near: true, copy: false });
      await frames();
      assert.equal(await evaluate("spacesFixture.assigned.length"), before + 4);
      assert.equal(await evaluate("!!document.querySelector('.space-page')"), false, "The click that ends a drag is not a click on the card.");

      // Escape ends a drag with the button still held, without closing Settings: nothing drops.
      const from = await centerOf(project(".space-pool", "Project")), to = await centerOf(card("Work"));
      await command("Input.dispatchMouseEvent", { type: "mouseMoved", x: from.x, y: from.y });
      await mouse("mousePressed", from);
      await mouse("mouseMoved", to);
      assert.equal(await evaluate("document.querySelector('[data-space-drop][data-over]')?.dataset.spaceDrop ?? null"), "work");
      for (const type of ["rawKeyDown", "keyUp"]) await command("Input.dispatchKeyEvent", { type, key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
      await frames();
      assert.equal(await evaluate("!!document.querySelector('.space-drag-ghost, [data-space-drop][data-over]')"), false);
      await mouse("mouseReleased", to);
      assert.equal(await evaluate("spacesFixture.assigned.length"), before + 4);
      assert.equal(await evaluate("!!document.querySelector('.space-card') && !document.querySelector('.space-page')"), true, "Settings stays open on its cards.");
      await command("Input.setInterceptDrags", { enabled: false });
      assert.deepEqual(exceptions, []);
    });

    await t.test("the Identity of a space has a card for Git, GitHub and Jira, each saved when it is left and checked with its tool", async () => {
      await resetWork();
      await evaluate("document.querySelector('.space-switch').click()");
      assert.equal(await wait("[...document.querySelectorAll('.bp6-menu-item')].some(item=>item.textContent.includes('Organize spaces'))"), true);
      await evaluate("[...document.querySelectorAll('.bp6-menu-item')].find(item=>item.textContent.includes('Organize spaces')).click()");
      assert.equal(await wait("!!document.querySelector('.space-card .space-open')"), true);
      await evaluate("document.querySelector('.space-card .space-open').click()");
      assert.equal(await wait("!!document.querySelector('.space-section-tab[data-space-section=identity]')"), true);
      await evaluate("document.querySelector('.space-section-tab[data-space-section=identity]').click()");
      assert.equal(await wait("document.querySelectorAll('.space-identity-card').length===3"), true);
      const card = (part: string) => `document.querySelector('.space-identity-card[data-part="${part}"]')`;
      const type = (part: string, name: string, value: string) => evaluate(`(()=>{const input=document.querySelector('#space-work-identity-${part}-${name}');input.focus();
        Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,${JSON.stringify(value)});input.dispatchEvent(new Event('input',{bubbles:true}));input.blur();})()`);

      // An empty card keeps the usual settings, and shows them.
      assert.deepEqual(await evaluate("[...document.querySelectorAll('.space-identity-card h3')].map(title=>title.textContent)"), ["Git", "GitHub", "Jira"]);
      assert.equal(await evaluate("getComputedStyle(document.querySelector('.space-identity-card h3')).textTransform"), "none", "A brand is written as its owner writes it.");
      assert.deepEqual(await evaluate("(()=>{const label=document.querySelector('label[for=space-work-identity-github-folder]');return [label.textContent, !!label.querySelector('button')];})()"),
        ["Profile folder", false], "The label of a field does not take the name of the button beside it.");
      assert.equal(await evaluate(`${card("git")}.querySelector('.space-identity-usual')?.textContent`), "Usual settings");
      assert.equal(await evaluate("document.querySelector('#space-work-identity-git-email').placeholder"), "usual@example.com");

      await type("git", "email", "alex@contoso.example");
      assert.equal(await wait("identityFixture.updates.length===1"), true);
      assert.deepEqual(await evaluate("[identityFixture.updates[0].part, identityFixture.updates[0].git.email, identityFixture.updates[0].git.name, identityFixture.updates[0].gitHub]"),
        ["git", "alex@contoso.example", null, null], "Only the card that changed is saved, and an empty field is the usual setting.");
      assert.equal(await wait(`!${card("git")}.querySelector('.space-identity-usual')`), true);

      // The check runs with the card as it is written, and says what it found.
      await evaluate(`${card("git")}.querySelector('.space-identity-check').click()`);
      assert.equal(await wait(`${card("git")}.querySelector('.space-identity-result')?.textContent.includes('Commits as A. Mutel <alex@contoso.example>')`), true);

      // A GitHub profile gives the command that signs it in, for the shell of the platform.
      await type("github", "folder", "~/.config/gh-contoso");
      const login = "$env:GH_CONFIG_DIR='C:\\Users\\me\\.config\\gh-contoso'; gh auth login";
      assert.equal(await wait(`${card("github")}.querySelector('.space-identity-login code')?.textContent===${JSON.stringify(login)}`), true);
      assert.equal(await wait("identityFixture.updates.at(-1)?.gitHub?.configDir==='~/.config/gh-contoso'"), true);

      // What cannot be saved is said in its card, and neither saved nor checked.
      const saved = await evaluate("identityFixture.updates.length");
      await type("jira", "token", "not a variable");
      assert.equal(await wait(`!!${card("jira")}.querySelector('[role=alert]')`), true);
      assert.equal(await evaluate(`${card("jira")}.querySelector('.space-identity-check').disabled`), true);
      assert.equal(await evaluate("identityFixture.updates.length"), saved);
      assert.deepEqual(exceptions, []);
    });
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
