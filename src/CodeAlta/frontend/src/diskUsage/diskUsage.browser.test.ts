import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, endBrowser, waitForBrowserPort } from "../browserTarget";
import { testLimit, testLimitRanOut } from "../testTime";

test("the tab of the disk usage shows the last measurement, measures, drills in, cleans build output and removes a worktree", { skip: !browserExecutable, timeout: testLimit(180_000) }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-disk-usage-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./diskUsage.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife",
      // The tab is given the host it talks to: the generated client is not part of this page.
      plugins: [{ name: "no-host", setup(bundle) { bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("../demo-api.ts", import.meta.url)) })); } }] });
    await writeFile(join(root, "style.css"), await readFile(new URL("../../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8")
      + await readFile(new URL("../style.css", import.meta.url), "utf8") + await readFile(new URL("../worktrees/worktrees.css", import.meta.url), "utf8")
      + await readFile(new URL("./diskUsage.css", import.meta.url), "utf8") + await readFile(new URL("../storage/storage.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html class="bp6-dark" data-theme="dark"><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"><link rel="stylesheet" href="fixture.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(browserExecutable!, [...browserBaseArgs, `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(root, "profile"), browser);
    const pages: { type?: string; webSocketDebuggerUrl?: string }[] = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(testLimit(10_000)) })).json();
    socket = new WebSocket(pages.find(value => value.type === "page")!.webSocketDebuggerUrl!);
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
      assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails));
      return result.result?.value;
    };
    const act = (script: string) => evaluate(`(async () => { ${script}; for (let turn = 0; turn < 4; turn++) await new Promise(resolve => requestAnimationFrame(() => setTimeout(resolve, 0))); return true; })()`);
    const until = async (expression: string) => assert.equal(await evaluate(`new Promise(resolve => { const end = Date.now() + ${testLimit(7000)}; (function check() {
      let ok = false; try { ok = !!(${expression}); } catch { } if (ok) resolve(true); else if (Date.now() > end) resolve(false); else setTimeout(check, 20); })(); })`), true, expression);
    // DISK_USAGE_SHOTS names a folder that receives a picture of each step, to look at.
    const capture = async (name: string) => {
      if (!process.env.DISK_USAGE_SHOTS) return;
      const shot = await command("Page.captureScreenshot", { format: "png" }) as { data?: string };
      await writeFile(join(process.env.DISK_USAGE_SHOTS, `${name}.png`), Buffer.from(shot.data!, "base64"));
    };
    const f = "window.diskUsageFixture", state = `${f}.state`;
    const text = "document.body.innerText";
    const row = (name: string) => `[...document.querySelectorAll('.disk-usage-row')].find(row => row.querySelector('.disk-usage-name').textContent.startsWith(${JSON.stringify(name)}))`;
    const open = (name: string) => act(`${row(name)}.querySelector('.disk-usage-open').click()`);
    const button = (scope: string, label: string) => `${scope}.querySelector('button[aria-label^=${JSON.stringify(label)}]')`;
    const textButton = (label: string) => `[...document.querySelectorAll('button')].find(button => button.textContent.trim() === ${JSON.stringify(label)})`;
    const trail = "[...document.querySelectorAll('.disk-usage-trail > *')].map(node => node.textContent).filter(Boolean)";
    const noOverflow = `(() => { const panel = document.querySelector('.disk-usage-panel'); const body = document.querySelector('.disk-usage-body');
      return panel.scrollWidth <= panel.clientWidth + 1 && body.scrollWidth <= body.clientWidth + 1 && [...document.querySelectorAll('.disk-usage-row')].every(row => row.getBoundingClientRect().right <= panel.getBoundingClientRect().right + 1); })()`;
    await command("Page.enable");
    await command("Emulation.setDeviceMetricsOverride", { width: 1200, height: 900, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });

    // The last measurement at once, with its age, and a lead to measure before cleaning: it was made by an earlier run.
    await until(`${row("Work")}`);
    assert.deepEqual(await evaluate("[...document.querySelectorAll('.disk-usage-row')].map(row => row.dataset.node)"), ["space:home", "space:work"]);
    assert.ok(await evaluate(`${text}.includes('Measured ')`));
    assert.ok(await evaluate(`${text}.includes('Git state is as of the last fetch')`));
    assert.ok(await evaluate(`${text}.includes('Measure again to clean build output')`));
    // The table is the legend of the donut: a swatch for each row that adds to the level; the donut says the shares.
    await until("document.querySelector('.disk-usage-panel .storage-donut .chart-surface svg path')");
    assert.deepEqual(await evaluate(`[...document.querySelectorAll('.disk-usage-row')].map(row => [row.dataset.node, !!row.querySelector('.storage-swatch')])`),
      [["space:home", true], ["space:work", true]]);
    assert.equal(await evaluate("document.querySelector('.disk-usage-panel .chart-table-toggle')"), null, "the table is the one under the donut");
    assert.ok(await evaluate(`${text}.includes('A project in several spaces is in each')`));
    await capture("1-last");

    // Drill down to the worktrees: nothing there can be cleaned yet. The table keeps its place from one level to the next.
    const tableTop = "Math.round(document.querySelector('.disk-usage-table thead').getBoundingClientRect().top)";
    const top = await evaluate(tableTop);
    await open("Work");
    assert.equal(await evaluate(tableTop), top, "the table does not move when the level changes");
    await open("Alpha");
    assert.deepEqual(await evaluate(trail), ["Everything", "Work", "Alpha"]);
    assert.deepEqual(await evaluate("[...document.querySelectorAll('.disk-usage-row')].map(row => row.dataset.kind)"), ["worktrees", "checkout", "sessions"], "the largest first");
    // A segment drills in as the name does; the one of a checkout, which has no level, does nothing. The worktrees take
    // most of the level, from the top clockwise; the folder of the project follows them.
    const segment = async (fraction: number) => {
      await until("document.querySelector('.disk-usage-panel .storage-donut .chart-surface svg path')");
      const point = await evaluate(`(() => { const box = document.querySelector('.disk-usage-panel .storage-donut .chart-surface').getBoundingClientRect();
        const radius = Math.min(box.width, box.height) / 2 * .75, angle = Math.PI / 2 - 2 * Math.PI * ${fraction};
        return [box.left + box.width / 2 + radius * Math.cos(angle), box.top + box.height / 2 - radius * Math.sin(angle)]; })()`) as [number, number];
      for (const type of ["mouseMoved", "mousePressed", "mouseReleased"]) await command("Input.dispatchMouseEvent", { type, x: point[0], y: point[1], button: "left", clickCount: 1 });
      await act("");
    };
    await segment(.9);
    assert.deepEqual(await evaluate(trail), ["Everything", "Work", "Alpha"], "a checkout has no level to drill into");
    await segment(.25);
    assert.deepEqual(await evaluate(trail), ["Everything", "Work", "Alpha", "Worktrees"]);
    await act("[...document.querySelectorAll('.disk-usage-trail > button')].find(button => button.textContent === 'Alpha').click()");
    assert.ok(await evaluate(`${row("Alpha")}.textContent.includes('Project folder')`));
    assert.equal(await evaluate(`${button(row("Alpha"), "Remove the worktree")}`), null, "a project folder is never offered for removal");
    await open("Worktrees");
    assert.equal(await evaluate(`${button(row("quiet-heron"), "Clean the build output")}.title`), "Measure first: only a measurement of this run can be cleaned.");

    // The panel inside Settings shows the same state, and opens the tab.
    await act(`${f}.setEmbedded(true)`);
    const embedded = "document.querySelector('.disk-usage-panel[data-embedded]')";
    await until(`${embedded}?.querySelector('.disk-usage-row')`);
    assert.equal(await evaluate(`${embedded}.querySelector('h1')`), null, "no second title of the page");
    await act(`[...${embedded}.querySelectorAll('button')].find(button => button.textContent === 'Open as tab').click()`);
    assert.equal(await evaluate(`${state}.tabs`), 1);
    assert.equal(await evaluate(`(() => { const panel = ${embedded}; return panel.scrollWidth <= panel.clientWidth + 1 && panel.getBoundingClientRect().right <= panel.parentElement.getBoundingClientRect().right + 1; })()`), true,
      "the panel fits the Settings window");
    await capture("0-embedded");

    // Measure from Settings: one measurement, which the tab shows too.
    await act(`[...${embedded}.querySelectorAll('button')].find(button => button.textContent === 'Measure').click()`);
    await until(`${state}.scans.length === 1`);
    await act(`[...document.querySelectorAll('.disk-usage-panel:not([data-embedded]) button')].find(button => button.textContent === 'Measure')?.click()`);
    assert.equal(await evaluate(`${state}.scans.length`), 1, "the tab has Stop, not a second Measure");
    assert.ok(await evaluate(`!!${textButton("Stop")}`));
    await act(`${state}.scans[0].push({ status: 'ok', kind: 'progress', progress: { phase: 'sessions', current: null, checkoutsDone: 0, checkoutsTotal: 0, files: '1200' } })`);
    assert.ok(await evaluate(`document.querySelector('.disk-usage-progress').textContent.includes('Measuring the sessions…')`));
    assert.equal(await evaluate("[...document.querySelectorAll('.disk-usage-progress')].length"), 2, "both views follow the measurement");
    await act(`${f}.setEmbedded(false)`);
    assert.equal(await evaluate(`${state}.scans[0].aborted`), false, "the tab still follows it");
    await act(`${state}.scans[0].push({ status: 'ok', kind: 'progress', progress: { phase: 'projects', current: '/trees/alpha/quiet-heron', checkoutsDone: 2, checkoutsTotal: 7, files: '5000' } })`);
    assert.ok(await evaluate(`document.querySelector('.disk-usage-progress').textContent.includes('Measuring the projects: 2 of 7 checkouts')`));
    assert.ok(await evaluate(`document.querySelector('.disk-usage-progress').textContent.includes('/trees/alpha/quiet-heron')`));
    await capture("2-measuring");
    // The level shown is not in the arriving tree yet: the breadcrumb is where it can be, and the level comes back with its project.
    assert.deepEqual(await evaluate(trail), ["Everything"]);
    const measured = `${f}.played()`;
    await act(`${state}.scans[0].push({ status: 'ok', kind: 'project', project: ${measured}.projects[0] })`);
    assert.deepEqual(await evaluate(trail), ["Everything", "Work", "Alpha", "Worktrees"]);
    await act("[...document.querySelectorAll('.disk-usage-trail > button')].find(button => button.textContent === 'Everything').click()");
    assert.deepEqual(await evaluate("[...document.querySelectorAll('.disk-usage-row')].map(row => row.dataset.node)"), ["space:home", "space:work"]);
    assert.ok(await evaluate(`${row("Work")}.querySelector('.disk-usage-cell-size').textContent.length > 0`));
    await act(`${state}.last = { ...${state}.last, current: true }`);
    await act(`${state}.scans[0].push({ status: 'ok', kind: 'project', project: ${measured}.projects[1] }); ${state}.scans[0].push({ status: 'ok', kind: 'project', project: ${measured}.projects[2] });
      ${state}.scans[0].push({ status: 'ok', kind: 'done', done: ${measured} })`);
    await until(`!document.querySelector('.disk-usage-progress') && ${text}.includes('Measured ') && !${text}.includes('Measure again to clean build output')`);
    // The picture of the user guide: the worktrees of a project measured in this run, the reasons of one opened, at the
    // size doc/site-screenshots.md asks for (1600x1000 CSS pixels at 150%), in the dark theme.
    if (process.env.DISK_USAGE_SHOTS) {
      await command("Emulation.setDeviceMetricsOverride", { width: 1600, height: 1000, deviceScaleFactor: 1.5, mobile: false });
      await act(`${f}.setWidth(1600); ${f}.setHeight(1000)`);
      await open("Work"); await open("Alpha"); await open("Worktrees");
      await act(`${row("quiet-heron")}.querySelector('.disk-usage-verdict').click()`);
      await until("document.querySelector('.disk-usage-panel .storage-donut .chart-surface svg path')");
      // The donut draws itself in: the picture waits for the end of its animation.
      await act("await new Promise(resolve => setTimeout(resolve, 1500))");
      await capture("site-disk-usage");
      await act("[...document.querySelectorAll('.disk-usage-trail > button')].find(button => button.textContent === 'Everything').click()");
      await act(`${f}.setWidth(1100); ${f}.setHeight(760)`);
      await command("Emulation.setDeviceMetricsOverride", { width: 1200, height: 900, deviceScaleFactor: 1, mobile: false });
    }

    // A measurement that is stopped leaves the last one.
    await act(`${textButton("Measure")}.click()`);
    await until(`${state}.scans.length === 2`);
    await act(`${textButton("Stop")}.click()`);
    await until(`${state}.scans[1].aborted && ${text}.includes('The measurement was stopped: the last one is shown.')`);
    assert.ok(await evaluate(`!!${row("Work")}`));

    // Drill down and back with the breadcrumb.
    await open("Work"); await open("Alpha"); await open("Worktrees");
    assert.deepEqual(await evaluate(trail), ["Everything", "Work", "Alpha", "Worktrees"]);
    await act("[...document.querySelectorAll('.disk-usage-trail > button')].find(button => button.textContent === 'Work').click()");
    assert.deepEqual(await evaluate(trail), ["Everything", "Work"]);
    await open("Alpha"); await open("Worktrees");

    // Each checkout that cannot be cleaned says why.
    const why = (name: string) => evaluate(`${button(row(name), "Clean the build output")}.title`);
    assert.equal(await why("busy-finch"), "A session is working here");
    assert.equal(await why("lost-otter"), "The folder of this worktree is gone.");
    assert.equal(await why("pale-wren"), "No build output to clean.");
    assert.equal(await why("self-host"), "This application runs from this folder: its build output is not cleaned.");
    assert.ok(await evaluate(`${row("self-host")}.textContent.includes('Runs this application')`));
    assert.equal(await evaluate(`${row("busy-finch")}.querySelector('input[type=checkbox]').disabled`), true);
    assert.equal(await evaluate(`${row("busy-finch")}.querySelector('.disk-usage-cell-select').title`), "A session is working here", "a checkbox that cannot be ticked says why in its tooltip");

    // The verdict of a checkout says why when asked.
    await act(`${row("quiet-heron")}.querySelector('.disk-usage-verdict').click()`);
    assert.ok(await evaluate("document.querySelector('.disk-usage-reasons').textContent.includes('3 changed files')"));
    // They open over the table, beside the verdict: no row of the table grows for them.
    assert.equal(await evaluate("document.querySelector('.disk-usage-reasons').closest('table')"), null);
    await capture("3-worktrees");
    // A worktree that holds another in its folder says so, cannot be removed, and its verdict names what it holds.
    assert.ok(await evaluate(`${row("pale-wren")}.textContent.includes('Holds a worktree') && ${row("pale-wren")}.textContent.includes('Holds checkouts')`));
    assert.equal(await evaluate(`${button(row("pale-wren"), "Remove the worktree")}.disabled`), true);
    assert.equal(await evaluate(`${button(row("pale-wren"), "Remove the worktree")}.title`), "Another worktree is inside its folder: remove that one first.");
    await act(`${row("pale-wren")}.querySelector('.disk-usage-verdict').click()`);
    assert.ok(await evaluate(`[...document.querySelectorAll('.disk-usage-reasons')].some(reasons => reasons.textContent.includes('It holds the checkout inner, whose git state is unknown.'))`));
    // Every verdict but Nothing local opens; Nothing local without a reason is a word.
    assert.ok(await evaluate(`[...document.querySelectorAll('.disk-usage-verdict')].every(verdict => verdict.dataset.verdict === 'nothing_local' ? verdict.tagName === 'SPAN' : verdict.tagName === 'BUTTON')`));

    // One checkout: its build folders are listed, all ticked, one is unticked, and the result says what went. The unticked one stays.
    const total = "document.querySelector('.disk-usage-panel:not([data-embedded]) .disk-usage-total').textContent";
    const totalBefore = await evaluate(total);
    await act(`${button(row("quiet-heron"), "Clean the build output")}.click()`);
    await until("document.querySelector('.disk-usage-clean')");
    assert.deepEqual(await evaluate("[...document.querySelectorAll('.disk-usage-clean-folders li')].map(item => [item.dataset.folder, item.querySelector('input').checked])"), [["bin", true], ["obj", true]]);
    assert.ok(await evaluate("document.querySelector('.disk-usage-clean-folders').textContent.includes('40 files')"));
    await act("document.querySelector('.disk-usage-clean-folders li[data-folder=\"obj\"] input').click()");
    await capture("4-clean-confirm");
    await act(`[...document.querySelectorAll('.disk-usage-clean-footer button')].find(button => button.textContent.startsWith('Clean ')).click()`);
    await until("document.querySelector('.disk-usage-clean[data-phase=\"results\"]')");
    assert.deepEqual(await evaluate(`${state}.cleans.map(request => [request.checkout, request.paths, request.measureId, request.projectId])`),
      [["/trees/alpha/quiet-heron", ["bin"], "m-/trees/alpha/quiet-heron", "alpha"]]);
    assert.ok(await evaluate("document.querySelector('.disk-usage-clean-results').textContent.includes('3.8 GiB freed.')"));
    await capture("5-clean-result");
    await act(`${textButton("Done")}.click()`);
    // The row shows the checkout measured again: obj, unticked, is still its build output; the total follows.
    await until(`!document.querySelector('.disk-usage-clean') && ${row("quiet-heron")}.querySelector('.disk-usage-cell-build').textContent === '1.3 GiB'`);
    assert.equal(await evaluate(`${button(row("quiet-heron"), "Clean the build output")}.disabled`), false, "obj can still be cleaned");
    await until(`${total} !== ${JSON.stringify(totalBefore)}`);
    assert.equal(await evaluate(total), "9.4 GiB", "the total of the host after the clean");

    // Two ticked checkouts, one after the other: the first answers late, the second is refused, and the refusal is said.
    await act(`${state}.cleanStatus['amber-denali'] = 'stale_measure'; ${state}.holdCleans = true`);
    await act("[...document.querySelectorAll('.disk-usage-trail > button')].find(button => button.textContent === 'Alpha').click()");
    await act(`${row("Alpha")}.querySelector('input[type=checkbox]').click()`);
    assert.ok(await evaluate(`document.querySelector('.disk-usage-footer').textContent.includes('1 selected')`));
    await open("Worktrees");
    assert.equal(await evaluate("document.querySelector('.disk-usage-footer')"), null, "a selection is of one level");
    await act(`document.querySelector('.disk-usage-table thead input[type=checkbox]').click()`);
    assert.deepEqual(await evaluate("[...document.querySelectorAll('.disk-usage-row[data-selected]')].map(row => row.dataset.node.split('/').pop())"), ["amber-denali", "quiet-heron"], "by size, as the table shows them");
    await act(`[...document.querySelectorAll('.disk-usage-footer button')].find(button => button.textContent.startsWith('Clean the build output')).click()`);
    await until("document.querySelector('.disk-usage-clean')");
    await act(`[...document.querySelectorAll('.disk-usage-clean-footer button')].find(button => button.textContent.startsWith('Clean ')).click()`);
    await until(`${state}.cleans.length === 2 && document.querySelector('.disk-usage-clean-progress')?.textContent.includes('(1 of 2)')`);
    await act(`${state}.heldCleans.shift()()`);
    await until(`${state}.cleans.length === 3 && document.querySelector('.disk-usage-clean-progress')?.textContent.includes('(2 of 2)')`);
    assert.deepEqual(await evaluate(`${state}.cleans.slice(1).map(request => [request.checkout.split('/').pop(), request.paths])`), [["amber-denali", ["bin", "obj"]], ["quiet-heron", ["obj"]]]);
    await act(`${state}.heldCleans.shift()()`);
    await until("document.querySelector('.disk-usage-clean[data-phase=\"results\"]')");
    assert.ok(await evaluate("document.querySelector('.disk-usage-clean-results li[data-status=\"stale_measure\"]').textContent.includes('The measurement shown is out of date for this checkout. Measure again.')"));
    await act(`${textButton("Done")}.click()`);

    // A view that goes during a clean: the clean goes on in the hub, and the view that stays shows its end.
    await act(`${f}.setEmbedded(true)`);
    await until("document.querySelector('.disk-usage-panel[data-embedded] .disk-usage-total')");
    const embeddedTotal = "document.querySelector('.disk-usage-panel[data-embedded] .disk-usage-total').textContent";
    const asked = await evaluate(`${state}.lastAsked`) as number;
    await act("[...document.querySelectorAll('.disk-usage-panel:not([data-embedded]) .disk-usage-trail > button')].find(button => button.textContent === 'Alpha').click()");
    await act(`${button(row("Alpha"), "Clean the build output")}.click()`);
    await until("document.querySelector('.disk-usage-clean-folders')");
    await act("document.querySelector('.disk-usage-clean-folders li[data-folder=\"obj\"] input').click()");
    await act(`[...document.querySelectorAll('.disk-usage-clean-footer button')].find(button => button.textContent.startsWith('Clean ')).click()`);
    await until(`${state}.cleans.length === 4`);
    await act(`${f}.setShown(false)`);
    assert.equal(await evaluate("document.querySelector('.disk-usage-clean')"), null, "the dialog went with its view");
    await act(`${state}.heldCleans.shift()(); ${state}.holdCleans = false`);
    await until(`${state}.lastAsked > ${asked} && ${embeddedTotal} === '7.4 GiB'`);
    await act(`${f}.setShown(true); ${f}.setEmbedded(false)`);
    await until(`${row("Work")}`);
    await open("Work"); await open("Alpha");
    assert.equal(await evaluate(`${row("Alpha")}.querySelector('.disk-usage-cell-build').textContent`), "256 MiB", "the tab shows the folder cleaned, with obj that was kept");
    await open("Worktrees");

    // A worktree is removed with the confirmation of the window of the worktrees; its project is then marked as changed.
    await act(`${button(row("amber-denali"), "Remove the worktree")}.click()`);
    await until("document.querySelector('.worktree-review')");
    assert.deepEqual(await evaluate(`${state}.inventoryAsked`), ["alpha"]);
    await act(`[...document.querySelectorAll('.worktree-review footer button')].find(button => button.textContent === 'Remove the worktree').click()`);
    await until("document.querySelector('.worktree-results')");
    assert.deepEqual(await evaluate(`${state}.removals`), [{ paths: ["/trees/alpha/amber-denali"], discard: [] }]);
    await act(`[...document.querySelectorAll('.worktree-results footer button')].find(button => button.textContent === 'Done').click()`);
    await until(`!document.querySelector('.worktree-results') && ${row("amber-denali")}.textContent.includes('Removed')`);
    assert.equal(await evaluate(`${button(row("amber-denali"), "Remove the worktree")}.disabled`), true);
    await act("[...document.querySelectorAll('.disk-usage-trail > button')].find(button => button.textContent === 'Work').click()");
    assert.ok(await evaluate(`${row("Alpha")}.textContent.includes('Changed')`));
    // The busy one is refused by the list of the worktrees.
    await open("Alpha"); await open("Worktrees");
    assert.equal(await evaluate(`${button(row("busy-finch"), "Remove the worktree")}.disabled`), false);
    await act(`${button(row("busy-finch"), "Remove the worktree")}.click()`);
    await until(`document.querySelector('.disk-usage-notice')?.textContent === 'A session is working here'`);

    // A session opens from the sessions of its project, with the checkout it works in.
    await act("[...document.querySelectorAll('.disk-usage-trail > button')].find(button => button.textContent === 'Alpha').click()");
    await open("Sessions");
    assert.ok(await evaluate(`${row("Fix the parser")}.textContent.includes('Works in quiet-heron')`));
    await open("Fix the parser");
    assert.deepEqual(await evaluate(`${state}.sessions`), ["s1"]);

    // Both themes and a narrow tab: nothing goes past the edge.
    await act("[...document.querySelectorAll('.disk-usage-trail > button')].find(button => button.textContent === 'Everything').click()");
    await open("Work"); await open("Alpha"); await open("Worktrees");
    assert.equal(await evaluate(noOverflow), true, "wide, dark");
    const color = () => evaluate("getComputedStyle(document.querySelector('.disk-usage-panel')).backgroundColor");
    const dark = await color();
    await act("document.documentElement.dataset.theme = 'light'; document.documentElement.classList.remove('bp6-dark')");
    assert.notEqual(await color(), dark, "the light theme");
    assert.equal(await evaluate(noOverflow), true, "wide, light");
    await capture("6-light");
    await act(`${f}.setWidth(430)`);
    assert.equal(await evaluate(noOverflow), true, "narrow, light");
    await capture("7-narrow");
    assert.equal(await evaluate("getComputedStyle(document.querySelector('.disk-usage-row .disk-usage-cell-build')).display"), "none", "the columns that say least go first");
    await act(`${button(row("quiet-heron"), "Remove the worktree")}.focus()`);
    assert.equal(await evaluate("document.activeElement.getAttribute('aria-label')"), "Remove the worktree quiet-heron", "the actions of a row are reached with the keyboard");
    // The confirmation of a clean in a 900 x 650 window does not overflow either.
    await command("Emulation.setDeviceMetricsOverride", { width: 900, height: 650, deviceScaleFactor: 1, mobile: false });
    await act(`${f}.setWidth(880)`);
    await act("[...document.querySelectorAll('.disk-usage-trail > button')].find(button => button.textContent === 'Alpha').click()");
    await act(`${button(row("Alpha"), "Clean the build output")}.click()`);
    await until("document.querySelector('.disk-usage-clean-folders')");
    assert.equal(await evaluate(`(() => { const dialog = document.querySelector('.app-window').getBoundingClientRect(); const body = document.querySelector('.disk-usage-clean-body');
      return dialog.right <= innerWidth && dialog.bottom <= innerHeight && body.scrollWidth <= body.clientWidth + 1; })()`), true);
    await capture("8-clean-small-window");
    await act(`${textButton("Cancel")}.click()`);
    assert.equal(await evaluate("document.querySelector('.disk-usage-clean')"), null);
  } finally {
    socket?.close();
    await endBrowser(browser);
    await rm(root, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
  }
});
