import assert from "node:assert/strict";
import test from "node:test";
import { browserExecutable } from "../browserTarget";
import { session } from "./fixtureServer";
import { openPhone, tapMenuItem } from "./phoneBrowser";

// Where the pictures of this test go, when a person wants to look at them.
const shots = process.env.CODEALTA_SHOTS ?? "";
const longTitle = "Refactor the parser into modules, one per stage of the pipeline, and keep every test of the grammar passing while it moves";

test("the top bar holds the commands of each page, its menu the others, and a session's title opens whole on a tap", { skip: !browserExecutable, timeout: 120_000 }, async () => {
  const phone = await openPhone(server => {
    server.state.sessions = [session("s1", longTitle, "p1", 1), session("s2", "Write the docs", "p1", 5)];
  }, shots);
  const { server, evaluate, waitFor, click, shot } = phone;
  // The buttons of the bar that show, with their sizes.
  const barButtons = () => evaluate<{ id: string; width: number; height: number }[]>(`[...document.querySelectorAll("#bar button")].filter(b => b.getClientRects().length && !b.closest("#bar-menu"))
    .map(b => { const r = b.getBoundingClientRect(); return { id: b.id, width: r.width, height: r.height }; })`);
  const fits = () => evaluate<boolean>(`document.documentElement.scrollWidth <= innerWidth && [...document.querySelectorAll("#bar button, #bar-menu")].every(e => !e.getClientRects().length || e.getBoundingClientRect().right <= innerWidth)`);
  const menuOpen = () => evaluate<boolean>(`!document.getElementById("bar-menu").hidden && document.getElementById("bar-more").getAttribute("aria-expanded") === "true"`);
  const menuItems = () => evaluate<string[]>(`[...document.querySelectorAll("#bar-menu > button")].filter(b => !b.hidden).map(b => b.textContent.trim())`);
  const width = (pixels: number) => phone.command("Emulation.setDeviceMetricsOverride", { width: pixels, height: 740, deviceScaleFactor: shots ? 3 : 2, mobile: true });
  const sessionReads = () => server.requests.filter(request => request.path === "/api/sessions").length;
  try {
    await phone.signIn();

    // ---- The list: the logo with its dot, New, Filter, Refresh and "⋯" ----
    for (const pixels of [375, 320]) {
      await width(pixels);
      assert.deepEqual((await barButtons()).map(button => button.id), ["new", "filter", "refresh", "bar-more"], `the bar of the list at ${pixels} px`);
      assert.ok((await barButtons()).every(button => button.width >= 44 && button.height >= 44), JSON.stringify(await barButtons()));
      assert.equal(await evaluate(`getComputedStyle(document.querySelector("#brand img")).display`), "block", "the logo");
      assert.ok(await fits(), `no overflow at ${pixels} px`);
    }

    // ---- "⋯" opens its menu under it: on the list, the text size and the theme; Back closes it ----
    const listPage = await evaluate<string>("location.href");
    await click("#bar-more");
    assert.ok(await menuOpen());
    assert.equal(await evaluate("history.state?.barMenu"), true, "an entry of its own in the history");
    // A phone browser that has not installed the page is offered Install, as the bar offered it; Notifications only in the installed app.
    assert.deepEqual(await menuItems(), ["Install the app"], "no command of a session on the list");
    assert.deepEqual(await evaluate(`[...document.querySelectorAll("#bar-menu .bar-menu-label")].map(e => e.textContent)`), ["Text size", "Theme"]);
    assert.ok(await fits(), "the menu fits a 320 px phone");
    assert.ok(await evaluate<boolean>(`[...document.querySelectorAll("#bar-menu button")].filter(b => b.getClientRects().length).every(b => b.getBoundingClientRect().height >= 44 && b.getBoundingClientRect().width >= 44)`), "44 px targets");
    await shot("bar-menu-list.png");
    await evaluate("history.back()");
    await waitFor(`document.getElementById("bar-menu").hidden`, "closed by Back");
    assert.equal(await evaluate(`document.getElementById("bar-more").getAttribute("aria-expanded")`), "false");
    assert.equal(await evaluate("location.href"), listPage, "Back left the page as it was");

    // The text size goes a step at a time, from 1 to 5, as the button of the bar did; the menu stays open meanwhile.
    await click("#bar-more");
    assert.equal(await evaluate(`document.getElementById("font-step").textContent`), "2 of 5");
    await click("#font-larger");
    assert.deepEqual(await evaluate(`[document.documentElement.dataset.fontStep, localStorage.getItem("alta.fontStep"), document.getElementById("font-step").textContent]`), ["3", "3", "3 of 5"]);
    assert.ok(await menuOpen(), "a choice leaves the menu open");
    for (let step = 0; step < 3; step++) await click("#font-larger");
    assert.equal(await evaluate("document.documentElement.dataset.fontStep"), "5");
    assert.equal(await evaluate(`document.getElementById("font-larger").disabled`), true, "no larger than the largest");
    for (let step = 0; step < 5; step++) await click("#font-smaller");
    assert.equal(await evaluate("document.documentElement.dataset.fontStep"), "1");
    assert.equal(await evaluate(`document.getElementById("font-smaller").disabled`), true);
    await click("#font-larger");
    assert.equal(await evaluate("document.documentElement.dataset.fontStep"), "2");

    // The theme: Auto (the system's) until another is chosen, and Auto again forgets it.
    const checked = () => evaluate<string[]>(`[...document.querySelectorAll("[data-theme-choice]")].filter(b => b.getAttribute("aria-checked") === "true").map(b => b.dataset.themeChoice)`);
    const system = await evaluate<string>(`matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark"`);
    assert.deepEqual(await checked(), ["auto"]);
    assert.equal(await evaluate("document.documentElement.dataset.theme"), system);
    const other = system === "dark" ? "light" : "dark";
    await click(`[data-theme-choice=${other}]`);
    assert.deepEqual(await evaluate(`[document.documentElement.dataset.theme, localStorage.getItem("alta.theme")]`), [other, other]);
    assert.deepEqual(await checked(), [other]);
    await click("[data-theme-choice=auto]");
    assert.deepEqual(await evaluate(`[document.documentElement.dataset.theme, localStorage.getItem("alta.theme")]`), [system, null]);
    assert.deepEqual(await checked(), ["auto"]);

    // A tap outside closes the menu, and does nothing else.
    const reads = sessionReads();
    await evaluate(`(() => { const refresh = document.getElementById("refresh"); refresh.dispatchEvent(new PointerEvent("pointerdown", { bubbles: true })); refresh.click(); })()`);
    await waitFor(`document.getElementById("bar-menu").hidden`, "closed by a tap outside");
    await new Promise(resolve => setTimeout(resolve, 300));
    assert.equal(sessionReads(), reads, "the tap that closed the menu did not refresh");
    await click("#refresh");
    await waitFor(`!document.getElementById("refresh").classList.contains("spin")`, "a refresh");
    assert.ok(sessionReads() > reads, "the next tap does");

    // ---- A session: Back, the dot of the link, its title on one line, Find and "⋯" ----
    await click(".row[data-id=s1] .card-link");
    await waitFor(`document.getElementById("detail-title").textContent === ${JSON.stringify(longTitle)}`, "the session");
    assert.equal(await evaluate(`document.querySelector("#detail .detail-head, #detail h1")`), null, "the title is no longer repeated over the timeline");
    assert.equal(await evaluate(`document.getElementById("detail").firstElementChild.id`), "detail-meta", "the details of the session stay at the top of the timeline");
    const heading = () => evaluate<{ height: number; clipped: boolean; expanded: string | null; line: number }>(`(() => {
      const button = document.getElementById("detail-heading"), text = document.getElementById("detail-title");
      return { height: text.getBoundingClientRect().height, clipped: text.scrollWidth > text.clientWidth || text.scrollHeight > text.clientHeight + 1,
        expanded: button.getAttribute("aria-expanded"), line: parseFloat(getComputedStyle(text).lineHeight) };
    })()`);
    for (const pixels of [375, 320]) {
      await width(pixels);
      assert.deepEqual((await barButtons()).map(button => button.id), ["back", "detail-heading", "detail-find", "bar-more"], `the bar of a session at ${pixels} px`);
      assert.ok((await barButtons()).every(button => button.width >= 44 && button.height >= 44), JSON.stringify(await barButtons()));
      assert.equal(await evaluate(`getComputedStyle(document.querySelector("#brand img")).display`), "none", "no logo in a session");
      assert.ok(await evaluate<boolean>(`(() => { const dot = document.getElementById("link-dot").getBoundingClientRect(), title = document.getElementById("detail-heading").getBoundingClientRect();
        return dot.width > 0 && dot.right <= title.left; })()`), "the dot of the link just before the title");
      assert.match(await evaluate<string>(`document.getElementById("link-dot").getAttribute("aria-label")`), /Live/u, "it still says the state of the link");
      const one = await heading();
      assert.ok(one.clipped && one.height < one.line * 1.5 && one.expanded === "false", `one line, cut: ${JSON.stringify(one)}`);
      assert.ok(await fits(), `no overflow at ${pixels} px`);
    }
    await shot("bar-session.png");
    // A tap shows the whole title, on up to three lines; another shrinks it again.
    await click("#detail-heading");
    const whole = await heading();
    assert.ok(whole.expanded === "true" && whole.height > whole.line * 1.5 && whole.height < whole.line * 3.5, `the whole title: ${JSON.stringify(whole)}`);
    assert.ok(await fits());
    await shot("bar-session-title.png");
    await click("#detail-heading");
    assert.equal((await heading()).expanded, "false");
    // Find takes over the top as before, with the title on one line.
    await click("#detail-heading");
    await click("#detail-find");
    await waitFor(`!document.querySelector(".session-find").hidden`, "the find bar");
    assert.equal((await heading()).expanded, "false");
    await evaluate("history.back()");
    await waitFor(`document.querySelector(".session-find").hidden`, "the find bar closed");

    // ---- The menu of a session: New session, Refresh and Archive the session, then the text size and the theme ----
    await click("#bar-more");
    assert.deepEqual(await menuItems(), ["New session", "Refresh", "Archive the session", "Install the app"]);
    assert.ok(await fits());
    await shot("bar-menu-session.png");
    const before = sessionReads();
    await click("#menu-refresh");
    await waitFor(`document.getElementById("bar-menu").hidden`, "the menu closed by a command");
    await waitFor(`!document.getElementById("bar-more").classList.contains("spin")`, "the refresh done");
    assert.ok(sessionReads() > before, "Refresh read the sessions again");
    assert.equal(await evaluate("!!history.state?.barMenu"), false, "the entry of the menu is gone");
    await evaluate(tapMenuItem("#menu-new"));
    await waitFor(`location.hash === "#/new" && !document.getElementById("create").hidden`, "a new session");
    assert.deepEqual((await barButtons()).map(button => button.id), ["back", "new", "filter", "refresh", "bar-more"], "the new-session page keeps the commands of the list");
    await evaluate("history.back()");
    await waitFor(`location.hash === "#/session/s1" && !document.getElementById("detail").hidden`, "Back to the session, not to the menu");
    assert.equal(await evaluate(`document.getElementById("bar-menu").hidden`), true);
    // Archive the session: back on the list, with Restore in the toast.
    await evaluate(tapMenuItem("#menu-archive"));
    await waitFor(`location.hash === "#/" && document.getElementById("toast").textContent.startsWith("Archived")`, "archived");
    assert.match(await evaluate<string>(`document.getElementById("toast").textContent`), /Restore$/u);
    await waitFor(`!document.querySelector('.row[data-id="s1"]') || document.querySelector('.row[data-id="s1"]').classList.contains("collapsing")`, "its card goes");
    assert.ok(server.requests.some(request => request.method === "POST" && request.path === "/api/sessions/s1/archive"), "the session archived");
    assert.deepEqual(phone.failures, []);
  } finally {
    await phone.close();
  }
});
