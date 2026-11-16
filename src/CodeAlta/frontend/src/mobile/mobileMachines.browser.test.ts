import assert from "node:assert/strict";
import test from "node:test";
import { browserExecutable } from "../browserTarget";
import type { Ask, Machine, Options, TimelineEvent } from "./contract";
import { fixtureChoices, fixtureMachines, remoteSession, session } from "./fixtureServer";
import { openPhone, type PhoneFixture } from "./phoneBrowser";
import { pageWaitLimit, testLimit } from "../testTime";

// Where the pictures of this test go, when a person wants to look at them.
const shots = process.env.CODEALTA_SHOTS ?? "";
const providers = [{ id: "codex", name: "Codex" }, { id: "claude", name: "Claude Code" }];
let seq = 0;
const event = (kind: string, text: string): TimelineEvent => ({ seq: ++seq, at: new Date().toISOString(), kind, text, activityId: null });
const machineOptions: Options = {
  models: [{ provider: "claude", id: "opus", name: "Opus 5.5", providerName: "Claude", efforts: ["low", "high"], defaultEffort: "high" }],
  prompts: [{ id: "default", name: "Default", description: null }], tiers: [], defaultTier: null,
  defaults: { provider: "claude", model: "opus", reasoning: "high", prompt: "default" }, permissionModes: {}, error: null,
};
const localOptions: Options = {
  models: [{ provider: "codex", id: "gpt-6.1", name: "GPT-6.1", providerName: "Codex", efforts: ["low", "medium", "high"], defaultEffort: "medium" }],
  prompts: [{ id: "default", name: "Default", description: null }, { id: "plan", name: "Plan", description: null }], tiers: [], defaultTier: null,
  defaults: { provider: "codex", model: "gpt-6.1", reasoning: "medium", prompt: "default" }, permissionModes: {}, error: null,
};
const withChoices = (server: PhoneFixture, key: string) => server.state.choices.set(key, { ...fixtureChoices, providers, current: {
  provider: "codex", providerName: "Codex", model: "gpt-6.1-sol", modelName: "GPT-6.1 Sol", reasoning: "low", agent: "default", agentName: "Default", permissionMode: null } });

function helpers(phone: Awaited<ReturnType<typeof openPhone>>) {
  const { server, evaluate, waitFor } = phone;
  const waitUntil = async (condition: () => boolean, what: string) => {
    const deadline = Date.now() + pageWaitLimit;
    while (!condition()) {
      if (Date.now() > deadline) assert.fail(`${what}: not seen`);
      await new Promise(resolve => setTimeout(resolve, 50));
    }
  };
  const posts = (path: string) => server.requests.filter(request => request.method === "POST" && request.path === path);
  const gets = (path: string) => server.requests.filter(request => request.method === "GET" && request.path === path);
  const hash = () => evaluate<string>("location.hash");
  const go = (to: string) => evaluate(`location.hash = ${JSON.stringify(to)}`);
  const set = (selector: string, value: string, type = "change") => evaluate(`(() => { const e = document.querySelector(${JSON.stringify(selector)}); e.value = ${JSON.stringify(value)};
    e.dispatchEvent(new Event(${JSON.stringify(type)}, { bubbles: true })); })()`);
  const visible = (selector: string) => `(() => { const e = document.querySelector(${JSON.stringify(selector)}); return !!e && !e.hidden && getComputedStyle(e).display !== "none" && e.getClientRects().length > 0; })()`;
  const opened = (title: string) => waitFor(`document.querySelector('#detail-title').textContent === ${JSON.stringify(title)}`, `the session ${title}`);
  return { waitUntil, posts, gets, hash, go, set, visible, opened };
}

test("the phone lists the sessions of every machine, with the machine on the cards of the others, and shows one machine at a time", { skip: !browserExecutable, timeout: testLimit(120_000) }, async () => {
  const phone = await openPhone(server => {
    server.state.machines = fixtureMachines();
    server.state.sessions = [
      session("root", "Refactor the parser", "p1", 40),
      session("same", "Here", "p1", 30),
      remoteSession("m2", "r1", "Deploy the site", "p1", 1, { live: "running", line: "Uploading" }),
      remoteSession("m2", "same", "On the mini", "p1", 5),
      // A sub-agent of another machine whose parent is on this computer: the parent names this computer by its id in the fleet.
      remoteSession("m2", "c1", "Review the deploy", "p1", 2, {}, { parentId: "root", parentMachineId: "device-local" }),
      remoteSession("m3", "o1", "Nightly build", "p1", 120),
    ];
    server.state.machineProjects.set("m2", { projects: [{ id: "api", path: "/srv/api", name: "api" }, { id: "p1", path: "/srv/site", name: "site" }], default: "api", error: null });
  }, shots);
  const { server, evaluate, waitFor, click, texts, rows, shot } = phone;
  const { gets, set, visible } = helpers(phone);
  try {
    await phone.signIn();
    // Several machines: every one of them is the list, asked for as such.
    assert.equal(gets("/api/machines").length >= 1, true);
    assert.equal(gets("/api/sessions").at(-1)!.query.includes("machine=all"), true);
    assert.deepEqual(await texts(".machine-chip .scope-machine"), ["All machines"]);
    assert.deepEqual((await rows()).sort(), ["@m2/r1", "@m2/same", "@m3/o1", "root", "same"], "the same id on two machines is two cards; a remote sub-agent folds under its parent here");
    assert.equal(await evaluate("document.querySelector('.row[data-id=\"root\"] .subagents-count').textContent"), "1 sub-agent");
    // The machine is on the cards of the other machines only, and an offline one says so.
    assert.deepEqual(await texts('.row[data-id="@m2/r1"] .meta-machine'), ["Mac mini"]);
    assert.deepEqual(await texts('.row[data-id="@m3/o1"] .meta-machine'), ["Build box · offline"]);
    assert.equal(await evaluate("document.querySelectorAll('.row[data-id=\"root\"] .meta-machine, .row[data-id=\"same\"] .meta-machine').length"), 0);
    assert.equal(await evaluate("document.querySelector('.row[data-id=\"@m2/same\"] a.card-link').getAttribute('href')"), "#/machine/m2/session/same");
    assert.equal(await evaluate("document.querySelector('.row[data-id=\"same\"] a.card-link').getAttribute('href')"), "#/session/same", "old links stay as they were");
    // A project is of one machine: there is no filter on the list of all of them.
    assert.equal(await evaluate(visible("#filter")), false);
    assert.ok(await evaluate<boolean>("document.documentElement.scrollWidth <= innerWidth"));
    await shot("machines-all.png");
    // The chips of the machines fit a narrow screen (390 px) in both themes, and at the largest text.
    const fits = () => evaluate<boolean>("document.documentElement.scrollWidth <= innerWidth && [...document.querySelectorAll('#sessions .card, .machine-chip, #sessions .meta-machine')].every(e => e.getBoundingClientRect().right <= innerWidth)");
    const theme = (name: string) => evaluate(`document.documentElement.dataset.theme = ${JSON.stringify(name)}`);
    await theme("light");
    assert.ok(await fits(), "no horizontal overflow in the light theme");
    await shot("machines-all-light.png");
    await evaluate("document.documentElement.dataset.fontStep = '5'");
    assert.ok(await fits(), "no horizontal overflow at the largest text");
    await shot("machines-all-light-large-text.png");
    await evaluate("document.documentElement.dataset.fontStep = '2'");

    // The sheet of the machines: how each is reached, when it was last seen, and how many wait.
    await click(".machine-chip");
    await waitFor(`!document.getElementById("machine-sheet").hidden`, "the sheet of machines");
    assert.deepEqual(await texts("#machine-list .machine-row-name"), ["All machines", "FIXTURE-PC (this computer)", "Mac mini", "Build box"]);
    assert.deepEqual(await texts("#machine-list .machine-row-status"), ["3 machines · 2 online · 1 waits for you", "Online", "Online · direct · 1 waits for you", "Offline · last seen 25 min ago"]);
    assert.equal(await evaluate("document.querySelector('#machine-list button.on').dataset.machine"), "all");
    const sheetFits = () => evaluate<boolean>("(() => { const p = document.querySelector('#machine-sheet .sheet-panel'); return p.scrollWidth <= p.clientWidth && [...p.querySelectorAll('button')].every(b => b.getBoundingClientRect().right <= innerWidth && b.getBoundingClientRect().height >= 44); })()");
    assert.ok(await sheetFits(), "the sheet fits, with touch targets of 44 px");
    await shot("machines-sheet-light.png");
    await theme("dark");
    assert.ok(await sheetFits());
    await shot("machines-sheet.png");

    // One machine: its own sessions, its own projects.
    await click('#machine-list button[data-machine="m2"]');
    await waitFor(`document.querySelector(".machine-chip .scope-machine").textContent === "Mac mini"`, "the chip names the machine");
    await waitFor(`document.querySelectorAll("#sessions .row").length === 3`, "the sessions of that machine");
    assert.deepEqual((await rows()).sort(), ["@m2/c1", "@m2/r1", "@m2/same"], "its sub-agent whose parent is on this computer is a card of its own here");
    assert.equal(gets("/api/sessions").at(-1)!.query.includes("machine=m2"), true);
    assert.equal(await evaluate("localStorage.getItem('alta.machine')"), "m2");
    assert.equal(await evaluate("document.getElementById('foot').textContent.includes('Mac mini')"), true, "the footer says which machine the list is of");
    assert.equal(await evaluate(visible("#filter")), true);
    await click("#filter");
    await waitFor(`!document.getElementById("sheet").hidden`, "the projects");
    assert.deepEqual(await texts("#sheet-list button:not(.on)"), ["api/srv/api", "site/srv/site"], "the projects of that machine");
    await click('#sheet-list button[data-project="api"]');
    await evaluate("document.querySelector('#sheet').click()");
    await waitFor(`localStorage.getItem("alta.project.m2") === '["api"]'`, "its own filter kept");
    assert.equal(await evaluate("localStorage.getItem('alta.project')"), null, "this computer's filter is its own");
    assert.equal(gets("/api/machines/m2/projects").length, 1);
    assert.match(gets("/api/sessions").at(-1)!.query, /project=api.*machine=m2/);

    // This computer again: the old routes, and the choice is kept for the next visit.
    await click(".machine-chip");
    await click('#machine-list button[data-machine="local"]');
    await waitFor(`document.querySelector(".machine-chip .scope-machine").textContent === "FIXTURE-PC"`, "this computer");
    await waitFor(`document.querySelectorAll("#sessions .row").length === 2`, "its own sessions");
    assert.equal(await evaluate(visible("#filter")), true);
    // A machine that went away leaves the choice: every machine again.
    await click(".machine-chip");
    await click('#machine-list button[data-machine="m2"]');
    await waitFor(`document.querySelector(".machine-chip .scope-machine").textContent === "Mac mini"`, "the machine chosen");
    server.state.machines = server.state.machines!.filter(m => m.id !== "m2");
    server.state.sessions = server.state.sessions.filter(s => s.machineId !== "m2");
    await evaluate("document.getElementById('refresh').click()");
    await waitFor(`document.querySelector(".machine-chip .scope-machine").textContent === "All machines"`, "every machine again");
    await waitFor(`document.querySelectorAll("#sessions .row").length === 3`, "the list of the machines left");
    assert.deepEqual((await rows()).sort(), ["@m3/o1", "root", "same"], "what the machine that went away listed is not listed");
    // Alone, the computer shows its own list as an older CodeAlta does: no chip, no machine asked for.
    server.state.machines = server.state.machines!.filter(m => m.id === "local");
    server.state.sessions = server.state.sessions.filter(s => !s.machineId);
    await evaluate("document.getElementById('refresh').click()");
    await waitFor(`!document.querySelector(".machine-chip")`, "no chip when there is one computer");
    await waitFor(`document.querySelector("#scope-where .scope-machine").textContent === "FIXTURE-PC"`, "the computer named");
    assert.equal(gets("/api/sessions").at(-1)!.query.includes("machine="), false);
    assert.deepEqual(phone.failures, []);
  } finally {
    await phone.close();
  }
});

test("a session of another machine has the page of a session here, with its machine, and keeps the choice of provider, model, effort and agent for its next prompt", { skip: !browserExecutable, timeout: testLimit(120_000) }, async () => {
  const phone = await openPhone(server => {
    server.state.machines = fixtureMachines();
    server.state.sessions = [
      session("same", "Here", "p1", 30),
      remoteSession("m2", "same", "On the mini", "p1", 5),
    ];
    server.state.events.set("same", [event("user.message", "Prompt here"), event("assistant.message", "Answer here")]);
    server.state.events.set("m2/same", [event("user.message", "Prompt there"), event("assistant.message", "Answer from the mini")]);
    withChoices(server, "same");
    withChoices(server, "m2/same");
    server.state.options = localOptions;
  }, shots);
  const { server, evaluate, waitFor, click, texts, shot } = phone;
  const { gets, posts, hash, go, set, visible, opened, waitUntil } = helpers(phone);
  const prompt = "/api/machines/m2/sessions/same/prompt";
  try {
    await phone.signIn();
    await click('.row[data-id="@m2/same"] a.card-link');
    await opened("On the mini");
    assert.equal(await hash(), "#/machine/m2/session/same");
    await waitFor(`document.querySelector("#timeline").textContent.includes("Answer from the mini")`, "its timeline");
    assert.deepEqual(await texts("#detail-meta .meta-machine"), ["Mac mini"], "the machine is in the header");
    // Everything is read from the routes of its machine, never from this computer's own for the same id.
    for (const path of ["events/latest", "choices", "queue", "ask", "permissions"]) assert.equal(gets(`/api/machines/m2/sessions/same/${path}`).length >= 1, true, path);
    assert.equal(server.requests.filter(r => r.path.startsWith("/api/sessions/same/")).length, 0);
    // The stream follows its display: the same stream, asked for with its machine and session.
    await waitUntil(() => server.requests.some(r => r.path === "/api/events" && r.query === "?machine=m2&session=same"), "the stream of the session");
    // What a machine does not offer is not shown.
    assert.equal(await evaluate(visible("#attach")), false, "no files of the phone");
    assert.equal(await evaluate(visible("#pick-model")), true);
    await shot("machine-session.png");

    // Provider, model, effort, agent prompt and permissions: chosen as on this computer, sent with the next prompt to that machine.
    await click("#pick-model");
    await waitFor(`!document.getElementById("model-sheet").hidden`, "the model sheet");
    await evaluate(`[...document.querySelectorAll("#model-list button")].find(b => b.textContent.includes("Argon")).click()`);
    await evaluate(`[...document.querySelectorAll("#sheet-effort .reasoning-labels button")].find(b => b.textContent === "xhigh").click()`);
    await click("#model-done");
    await click("#pick-more");
    await waitFor(`!document.getElementById("session-more-sheet").hidden`, "the options of the session");
    assert.equal(await evaluate(visible("#session-provider-field")), true, "the provider can be changed on a machine that offers it");
    assert.deepEqual(await texts("#session-provider option"), ["Codex", "Claude Code"]);
    await set("#session-agent", "plan");
    await set("#session-mode", "acceptEdits");
    await click("#session-more-done");
    await set("#prompt", "Ship it there", "input");
    await click("#send");
    await waitUntil(() => posts(prompt).length === 1, "the prompt");
    assert.deepEqual(posts(prompt)[0].body, { text: "Ship it there", mode: "send", model: "gpt-6.1-argon", reasoning: "xhigh", agent: "plan", permissionMode: "acceptEdits" });
    assert.match(posts(prompt)[0].requestId ?? "", /^[0-9a-f-]{36}$/, "the prompt carries its id");
    assert.equal(posts("/api/sessions/same/prompt").length, 0, "this computer's session of the same id is not touched");
    // The provider moves on the machine's own route, with an id of its own.
    await click("#pick-more");
    await set("#session-provider", "claude");
    await waitUntil(() => posts("/api/machines/m2/sessions/same/provider").length === 1, "the provider move");
    assert.equal((posts("/api/machines/m2/sessions/same/provider")[0].body as { provider: string }).provider, "claude");
    assert.match(posts("/api/machines/m2/sessions/same/provider")[0].requestId ?? "", /^[0-9a-f-]{36}$/);
    assert.notEqual(posts("/api/machines/m2/sessions/same/provider")[0].requestId, posts(prompt)[0].requestId, "an id per command");
    await click("#session-more-done");

    // Archive is offered, as the machine says; the draft is the draft of that machine's session.
    assert.equal(await evaluate("document.getElementById('menu-archive').hidden"), false);
    await set("#prompt", "A draft for the mini", "input");
    assert.equal(await evaluate("localStorage.getItem('alta.draft.@m2/same')"), "A draft for the mini");
    await go("#/session/same");
    await opened("Here");
    await waitFor(`document.querySelector("#timeline").textContent.includes("Answer here")`, "the session of this computer");
    assert.equal(await evaluate("document.getElementById('prompt').value"), "", "the draft of the other machine's session is not here");
    assert.equal(await evaluate("localStorage.getItem('alta.draft.same')"), null);
    assert.equal(await evaluate("document.querySelectorAll('#detail-meta .meta-machine').length"), 0);
    assert.equal(await evaluate(visible("#attach")), true, "this computer takes files");
    // The stream goes back to its own address, and the draft comes back with the session.
    await waitUntil(() => server.requests.filter(r => r.path === "/api/events").at(-1)!.query === "", "the plain stream");
    await go("#/machine/m2/session/same");
    await opened("On the mini");
    assert.equal(await evaluate("document.getElementById('prompt').value"), "A draft for the mini");
    assert.deepEqual(phone.failures, []);
  } finally {
    await phone.close();
  }
});

test("a session of a machine that is out of reach, that the person may not drive, or that is too old, is read and not driven, and says why", { skip: !browserExecutable, timeout: testLimit(120_000) }, async () => {
  const phone = await openPhone(server => {
    const machines = fixtureMachines();
    const can = { view: true, drive: true, start: true };
    server.state.machines = [...machines,
      { id: "m4", name: "Old box", this: false, state: "online", path: "relayed", lastSeen: null, can, features: ["provider"], waiting: 0 } satisfies Machine,
      { id: "m5", name: "View only", this: false, state: "online", path: "direct", lastSeen: null, can: { view: true, drive: false, start: false }, features: machines[1].features, waiting: 0 } satisfies Machine];
    server.state.sessions = [
      session("same", "Here", "p1", 30),
      remoteSession("m3", "o1", "Nightly build", "p1", 120),
      remoteSession("m4", "old", "On the old box", "p1", 10),
      remoteSession("m5", "v1", "Looked at only", "p1", 12),
      remoteSession("m2", "review", "A review on the mini", "p1", 3, { asking: { id: "a1", revision: "1", state: "pending", immediate: false, kind: "elsewhere" } }),
      remoteSession("m2", "plan", "A file review", "p1", 4),
      remoteSession("m2", "moved1", "Moved here", "p1", 6),
      remoteSession("m2", "ask1", "A question on the mini", "p1", 7),
      remoteSession("m2", "ask2", "An ask of an older CodeAlta", "p1", 8),
    ];
    for (const key of ["same", "m3/o1", "m5/v1", "m2/review", "m2/plan", "m2/moved1", "m2/ask1", "m2/ask2"]) server.state.events.set(key, [event("user.message", "Prompt"), event("assistant.message", "Answer")]);
    // A kind of ask this page does not know is never offered as a question, though it carries one; a review of a machine that hands none is the same.
    const question = { title: "Database", question: "Which one?", description: null, choices: [{ title: "Postgres", description: null }], freeform: null };
    server.state.asks.set("m2/review", { id: "a1", revision: "1", state: "pending", kind: "elsewhere", questions: [question], review: null } satisfies Ask);
    server.state.asks.set("m2/plan", { id: "a2", revision: "1", state: "pending", kind: "review", questions: [], review: { kind: "file", path: "plan.md", title: "The plan", message: "Please look", comments: 0 } } satisfies Ask);
    // A question the machine says is one is answered from the phone; the same question of a machine that does not say what it is, is not.
    server.state.asks.set("m2/ask1", { id: "a3", revision: "1", state: "pending", kind: "questions", questions: [question], review: null } satisfies Ask);
    server.state.asks.set("m2/ask2", { id: "a4", revision: "1", state: "pending", questions: [question], review: null } satisfies Ask);
    // A job runs on this computer and on the machine: only this computer stops one for the phone.
    const job = { id: "j1", state: "running", title: "Build", command: "make", folder: "/code", startedAt: new Date().toISOString() } as const;
    server.state.jobs.set("same", [job]);
    server.state.jobs.set("m2/ask1", [job]);
    server.state.moved.set("old1", { machineId: "m2", sessionId: "moved1" });
  }, shots);
  const { server, evaluate, waitFor, click, texts, shot } = phone;
  const { gets, go, hash, visible, opened, waitUntil, posts } = helpers(phone);
  try {
    await phone.signIn();
    // The card of a session that waits on a review says where to do it.
    assert.deepEqual(await texts('.row[data-id="@m2/review"] .waiting-badge'), ["Review it on a computer"]);

    // The machines say what keeps the phone out: one to update cannot be chosen, one that is only looked at says so.
    await click(".machine-chip");
    await waitFor(`!document.getElementById("machine-sheet").hidden`, "the machines");
    assert.deepEqual(await evaluate(`[...document.querySelectorAll("#machine-list button")].filter(b => ["m4", "m5"].includes(b.dataset.machine)).map(b => [b.dataset.machine, b.disabled, b.querySelector("small").textContent])`),
      [["m4", true, "Online · relayed · Update CodeAlta on this machine"], ["m5", false, "Online · direct · View only"]]);
    await evaluate(`document.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }))`);
    await waitFor(`document.getElementById("machine-sheet").hidden`, "the machines closed");

    // Offline: read only, with when it was last seen; nothing to type or send, and no questions asked of a machine that cannot answer them.
    await go("#/machine/m3/session/o1");
    await opened("Nightly build");
    await waitFor(`document.querySelector("#timeline").textContent.includes("Answer")`, "what the phone has of it");
    assert.equal(await evaluate("document.getElementById('composer').dataset.readonly"), "1");
    assert.match((await texts(".readonly-note"))[0], /^Build box is offline \(last seen 25 min ago\): you can read this session, not drive it\.$/);
    for (const selector of ["#prompt", "#send", "#pick-model", "#attach"]) assert.equal(await evaluate(visible(selector)), false, `${selector} is not offered`);
    assert.equal(await evaluate(visible(".readonly-note")), true);
    assert.deepEqual(await texts("#detail-meta .meta-machine"), ["Build box · offline"]);
    assert.equal(gets("/api/machines/m3/sessions/o1/permissions").length + gets("/api/machines/m3/sessions/o1/ask").length, 0);
    await shot("machine-offline.png");

    // Allowed to see, not to drive.
    await go("#/machine/m5/session/v1");
    await opened("Looked at only");
    await waitFor(`document.getElementById("composer").dataset.readonly === "1"`, "read only");
    assert.match((await texts(".readonly-note"))[0], /^You can read this session on View only, not drive it\.$/);
    assert.equal(posts("/api/machines/m5/sessions/v1/prompt").length, 0);

    // A machine whose CodeAlta cannot read its sessions here is told to be updated, and nothing is asked of it.
    await go("#/machine/m4/session/old");
    await waitFor(`document.getElementById("detail-note").textContent.includes("Update CodeAlta on Old box")`, "the advice");
    assert.equal(await evaluate("document.getElementById('composer').dataset.readonly"), "1");
    assert.equal(server.requests.filter(r => r.path.startsWith("/api/machines/m4/sessions/")).length, 0);

    // "Review it on a computer": the ask of an unknown kind, and the review a machine does not hand over, show it with no control.
    for (const [id, title] of [["review", "A review on the mini"], ["plan", "A file review"]]) {
      await go(`#/machine/m2/session/${id}`);
      await opened(title);
      await waitFor(`!document.getElementById("ask").hidden && document.getElementById("ask").textContent.includes("Review it on a computer")`, `the ask of ${id}`);
      assert.deepEqual(await evaluate("[...document.querySelectorAll('#ask .ask-card input, #ask .ask-card textarea, #ask .ask-card button, #ask .ask-card select')].map(e => e.outerHTML.slice(0, 80))"), [], "no question to answer");
      assert.equal(await evaluate("document.getElementById('composer').hidden"), true);
    }
    assert.equal(posts("/api/machines/m2/sessions/plan/ask/answer").length, 0);
    await shot("machine-elsewhere.png");
    // An ask of a machine that does not say what it is (an older CodeAlta there) is not taken for a question.
    await go("#/machine/m2/session/ask2");
    await opened("An ask of an older CodeAlta");
    await waitFor(`!document.getElementById("ask").hidden && document.getElementById("ask").textContent.includes("Review it on a computer")`, "the ask of no kind");
    assert.equal(await evaluate("document.querySelectorAll('#ask .ask-card input, #ask .ask-card textarea, #ask .ask-card button').length"), 0);
    // A question the machine names as one is answered on its machine.
    await go("#/machine/m2/session/ask1");
    await opened("A question on the mini");
    await waitFor(`document.querySelector("#ask .ask-answer")`, "the question");
    assert.equal(await evaluate(`document.getElementById("ask").textContent.includes("Review it on a computer")`), false);
    // Its job is listed, read and stopped there, since the machine offers it.
    await waitFor(`document.querySelector("button.background-status") && !document.getElementById("background-status").hidden`, "the job of the machine");
    await click("button.background-status");
    await waitFor(`document.querySelector('[data-job-action="open:j1"]')`, "the job listed");
    assert.equal(await evaluate(`document.querySelectorAll('[data-job-action^="stop:"]').length`), 1, "the stop of a machine that offers it");
    await evaluate(`document.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }))`);
    await click("#ask .ask-answer");
    await waitUntil(() => posts("/api/machines/m2/sessions/ask1/ask/answer").length === 1, "the answer");
    assert.equal((posts("/api/machines/m2/sessions/ask1/ask/answer")[0].body as { askId: string }).askId, "a3");
    assert.equal(posts("/api/machines/m2/sessions/ask2/ask/answer").length + posts("/api/machines/m2/sessions/ask1/jobs/j1/stop").length, 0);
    // The same job of this computer has its stop.
    await go("#/session/same");
    await opened("Here");
    await waitFor(`!document.getElementById("background-status").hidden`, "the job of this computer");
    await click("button.background-status");
    await waitFor(`document.querySelector('[data-job-action="stop:j1"]')`, "the stop of this computer");
    await evaluate(`document.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }))`);

    // A session that this computer gave away is followed to the machine that has it.
    await go("#/session/old1");
    await waitUntil(() => gets("/api/sessions/old1").length + gets("/api/sessions/old1/events/latest").length > 0, "the read");
    await waitFor(`location.hash === "#/machine/m2/session/moved1"`, "the session followed");
    await opened("Moved here");
    assert.equal(await evaluate("document.getElementById('detail-note').textContent"), "This session moved to Mac mini.");
    // Back does not return to the dead link.
    assert.equal(await hash(), "#/machine/m2/session/moved1");
    // The browser logs each refused read of the link that moved; nothing else failed.
    assert.deepEqual(phone.failures.filter(failure => !/status of 409/.test(failure)), []);
  } finally {
    await phone.close();
  }
});

test("a new session starts on the machine chosen in Run on, with its projects, providers and models, and what it does not offer left out", { skip: !browserExecutable, timeout: testLimit(120_000) }, async () => {
  const phone = await openPhone(server => {
    const machines = fixtureMachines();
    server.state.machines = [...machines, { id: "m5", name: "View only", this: false, state: "online", path: "direct", lastSeen: null, can: { view: true, drive: false, start: false }, features: machines[1].features, waiting: 0 }];
    server.state.sessions = [session("same", "Here", "p1", 30), remoteSession("m2", "same", "On the mini", "p1", 5)];
    server.state.options = localOptions;
    server.state.machineOptions.set("m2", machineOptions);
    // Its project has the id of one of this computer's.
    server.state.machineProjects.set("m2", { projects: [{ id: "p1", path: "/srv/api", name: "api" }], default: "p1", error: null });
    server.state.machineCreateAnswer = { machineId: "m2" };
  }, shots);
  const { server, evaluate, waitFor, click, texts, shot } = phone;
  const { gets, posts, hash, set, visible, waitUntil } = helpers(phone);
  try {
    await phone.signIn();
    await click("#new");
    await waitFor(`!document.getElementById("new-machine-field").hidden && document.querySelectorAll("#new-project option").length === 3`, "the form of this computer");
    assert.deepEqual(await evaluate("[...document.querySelectorAll('#new-machine option')].map(o => [o.value, o.disabled, o.textContent])"),
      [["local", false, "FIXTURE-PC (this computer)"], ["m2", false, "Mac mini"], ["m3", true, "Build box · offline"], ["m5", true, "View only · cannot start sessions"]],
      "a machine out of reach, or where the person may not start sessions, is listed and cannot be chosen");
    assert.equal(await evaluate("document.getElementById('new-machine').value"), "local");
    assert.equal(await evaluate(visible("#new-attach")), true);
    await shot("machine-run-on.png");

    // The other machine: its projects and its options are read again, and what it does not offer is left out.
    await set("#new-machine", "m2");
    await waitFor(`document.getElementById("new-model-name").textContent === "Opus 5.5 · high"`, "the models of that machine");
    assert.deepEqual(await evaluate("[...document.querySelectorAll('#new-project option')].map(o => [o.value, o.textContent])"), [["\u0000chat", "None (Chat)"], ["p1", "api"]]);
    assert.equal(gets("/api/machines/m2/projects").length, 1);
    assert.equal(gets("/api/machines/m2/options").length, 1);
    assert.equal(await evaluate(visible("#new-attach")), false, "no files of the phone for that machine");
    await click("#new-more");
    await waitFor(`!document.getElementById("new-more-sheet").hidden`, "the options");
    assert.equal(await evaluate("document.getElementById('new-worktree').closest('label').hidden"), true, "no choice of a worktree there");
    assert.deepEqual(await texts("#new-agent option"), ["Default"]);
    await click("#new-more-done");
    await set("#new-text", "Deploy it", "input");
    await click("#new-start");
    await waitUntil(() => posts("/api/machines/m2/sessions").length === 1, "the new session");
    assert.deepEqual(posts("/api/machines/m2/sessions")[0].body, { project: "p1", text: "Deploy it", prompt: "default", provider: "claude", model: "opus", reasoning: "high" });
    assert.match(posts("/api/machines/m2/sessions")[0].requestId ?? "", /^[0-9a-f-]{36}$/, "the new session and its first prompt go under one id");
    await waitFor(`location.hash === "#/machine/m2/session/new1"`, "the new session opened on its machine");
    assert.equal(posts("/api/sessions").length, 0, "nothing started here");
    await waitFor(`document.querySelector("#detail-title").textContent === "New: Deploy it"`, "its page");
    assert.deepEqual(await texts("#detail-meta .meta-machine"), ["Mac mini"]);

    // The next new session starts where the person was, and this computer's own is as it always was.
    await click("#new");
    await waitFor(`document.getElementById("new-machine").value === "m2" && document.getElementById("new-model-name").textContent === "Opus 5.5 · high"`, "the machine of the session on screen");
    await set("#new-machine", "local");
    await waitFor(`document.getElementById("new-model-name").textContent === "GPT-6.1 · medium" && document.querySelectorAll("#new-project option").length === 3`, "this computer again");
    assert.equal(await evaluate(visible("#new-attach")), true);
    await set("#new-text", "Fix it here", "input");
    await click("#new-start");
    await waitUntil(() => posts("/api/sessions").length === 1, "the new session here");
    assert.deepEqual(posts("/api/sessions")[0].body, { project: "p1", worktree: false, text: "Fix it here", prompt: "default", provider: "codex", model: "gpt-6.1", reasoning: "medium" }, "no id of a request, a worktree to choose");
    await waitFor(`location.hash === "#/session/new2"`, "the new session opened here");
    assert.deepEqual(phone.failures, []);
  } finally {
    await phone.close();
  }
});
