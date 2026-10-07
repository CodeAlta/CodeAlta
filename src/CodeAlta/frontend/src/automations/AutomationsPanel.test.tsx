import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { AutomationItem, AutomationRunItem, AutomationTriggerItem, WorkspaceProject, WorkspaceSession } from "#neoastra";
import { locales, translate } from "../localization";
import { ShellLanguageContext } from "../shellLanguage";
import { AutomationsPanel } from "./AutomationsPanel";
import { denseTimeline } from "./automations";
import type { AutomationsHub, AutomationsState } from "./automationsHub";
import { SessionOrigin } from "./SessionOrigin";

const never = () => assert.fail("rendering must not act");
const trigger = (type: string, change: Partial<AutomationTriggerItem> = {}): AutomationTriggerItem =>
  ({ type, minute: 0, every: 1, at: [], days: [], expression: null, event: "opened", authors: "trusted", ...change });
const item = (id: string, change: Partial<AutomationItem> = {}): AutomationItem => ({
  id, name: `Automation ${id}`, enabled: true, prompt: "Do the thing.", projectId: "p", projectName: "Alpha", projectFolder: "/p", storeProjectId: null, file: "/home/config.toml",
  provider: null, model: null, effort: null, agent: null, catchUp: false, triggers: [], problem: null, nextRunAt: null, running: false, lastRun: null, repository: null, watchProblem: null, allowed: true, ...change,
});
const run = (id: string, change: Partial<AutomationRunItem> = {}): AutomationRunItem =>
  ({ id, automationId: "a", name: "Nightly", sessionId: null, projectId: "p", startedAt: "2026-10-06T12:00:00Z", endedAt: null, trigger: "daily", detail: null, status: "completed", message: null, ...change });
const projects: WorkspaceProject[] = [{ id: "p", name: "Alpha", path: "/p", archived: false }];
const session = (id: string): WorkspaceSession => ({
  messageCount: null, automationId: "a", worktreePath: null, worktreeRoot: null, worktreeName: null, worktreeMissing: false, createdAt: null, id, title: "Nightly", fullTitle: "Nightly", fullTitleTruncated: false, parentSessionId: null, scopeKind: "project", projectId: "p",
  lineageIssue: null, workspacePath: "/p", providerKey: "codex", updatedAt: "2026-10-06T12:00:00Z",
});

// A hub that shows one state and is asked nothing: a rendering reads, it does not call the host.
function render(state: Partial<AutomationsState>, change: { projects?: WorkspaceProject[]; sessions?: WorkspaceSession[]; locale?: typeof locales[number] } = {}) {
  const snapshot: AutomationsState = { loaded: true, available: true, paused: false, scanned: true, items: [], faults: [], runs: [], upcoming: [], ...state };
  const hub = { subscribe: () => () => { }, getSnapshot: () => snapshot, refresh: never, save: never, remove: never, setEnabled: never, setPaused: never, run: never, runs: never, preview: never,
    connect: never } as unknown as AutomationsHub;
  const locale = change.locale ?? "en";
  return renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: never } },
    createElement(AutomationsPanel, { hub, projects: change.projects ?? projects, sessions: change.sessions ?? [], projectId: "p", providers: [{ id: "codex" }], epoch: "epoch", visible: false,
      onActivate: never, onOpenSession: never })));
}
const cards = (html: string) => html.split('<li class="automation-card"').slice(1);

test("before the host answers there is nothing to show, and a host without automations says so", () => {
  const waiting = render({ loaded: false, available: false });
  assert.doesNotMatch(waiting, /automations-header|New automation/);
  const none = render({ available: false });
  assert.match(none, /Automations are unavailable in this window\./);
  assert.doesNotMatch(none, /New automation/);
});

test("without automations the tab invites to write one, in every language; templates of a repository wait for a project", () => {
  for (const locale of locales) {
    const html = render({}, { locale });
    assert.ok(html.includes(translate(locale, "Let CodeAlta do the routine")) && html.includes(translate(locale, "Start from a template")), locale);
    assert.ok(html.includes(translate(locale, "Issue triage")) && html.includes(translate(locale, "Daily brief")), locale);
    assert.doesNotMatch(html, /automation-timeline|automations-side/);
  }
  assert.doesNotMatch(render({}), /class="automation-template" disabled/);
  const alone = render({}, { projects: [] });
  assert.equal(alone.split('class="automation-template" disabled=""').length - 1, 7, "Every template but the chat needs a project.");
  assert.match(alone, /class="automation-template"><span[^>]*data-file-tone="teal"[\s\S]*?Daily brief/);
});

test("a card says what starts its automation, where it runs and what it waits for", () => {
  const html = render({ items: [
    item("daily", { name: "Morning", triggers: [trigger("daily", { at: ["09:00"] })], nextRunAt: new Date(Date.now() + 3 * 3600_000 + 60_000).toISOString(), provider: "codex", model: "gpt" }),
    item("chat", { name: "Brief", projectId: null, projectName: null, projectFolder: null, triggers: [] }),
    item("event", { name: "Review", triggers: [trigger("pull_request")], repository: "org/repo" }),
    item("blind", { name: "Triage", triggers: [trigger("issue")], watchProblem: "GitHub asks to sign in." }),
    item("off", { name: "Off", enabled: false, triggers: [trigger("hourly")] }),
    item("lost", { name: "Lost", projectId: null, projectName: null, projectFolder: "/gone", problem: "'/gone' is not the folder of a project of CodeAlta.", triggers: [trigger("daily", { at: ["09:00"] })] }),
    item("busy", { name: "Busy", running: true, triggers: [trigger("issue")], lastRun: run("r", { status: "failed" }) }),
  ] });
  const [daily, chat, event, blind, off, lost, busy] = cards(html);
  assert.match(daily, /<strong>Morning<\/strong><span class="automation-card-when">Daily at 09:00<\/span>/);
  assert.match(daily, /data-kind="project"[\s\S]*<span>Alpha<\/span>[\s\S]*<span>gpt<\/span>/);
  assert.match(daily, /class="automation-card-state"[^>]*>Next in 3 hr\.</);
  assert.match(daily, /automation-card-switch"><input[^>]*aria-label="Enabled"[^>]*checked=""/);
  // A chat that is run by hand: no switch, since it has no trigger to turn off.
  assert.match(chat, /Manual[\s\S]*data-kind="chat"[\s\S]*<span>Chat<\/span>[\s\S]*Run it when you need it/);
  assert.doesNotMatch(chat, /automation-card-switch/);
  assert.match(event, /When a pull request is opened[\s\S]*class="automation-card-state"[^>]*>Watches org\/repo</);
  // What keeps a trigger from seeing its repository is said in place of what it waits for; it still runs by hand.
  assert.match(blind, /class="automation-card-state" data-problem="true" title="GitHub asks to sign in\.">GitHub asks to sign in\.</);
  assert.doesNotMatch(blind, /aria-label="Run now"[^>]*disabled/);
  assert.match(off, /^[^>]*data-off="true"[\s\S]*class="automation-card-state"[^>]*>Disabled</);
  assert.match(lost, /data-problem="true"[^>]*>&#x27;\/gone&#x27; is not the folder of a project of CodeAlta\.</);
  assert.match(lost, /disabled=""[^>]*aria-label="Run now"|aria-label="Run now"[^>]*disabled=""/);
  assert.match(busy, /class="automation-card-state"[^>]*>Running<[\s\S]*class="automation-card-last" title="Failed/);
  assert.match(html, /Your automations<span class="count">7<\/span>/);
});

test("an automation that came with its project waits for the user, who allows it from its card", () => {
  const html = render({ items: [
    item("shared", { name: "Shared", allowed: false, storeProjectId: "p", triggers: [trigger("daily", { at: ["09:00"] })] }),
    item("manual", { name: "By hand", allowed: false, storeProjectId: "p", triggers: [] }),
    item("mine", { name: "Mine", triggers: [trigger("daily", { at: ["09:00"] })], nextRunAt: new Date(Date.now() + 3600_000).toISOString() }),
    item("lost", { name: "Lost", allowed: false, problem: "Its project is gone.", triggers: [trigger("issue")] }),
  ] });
  const [shared, manual, mine, lost] = cards(html);
  assert.match(shared, /class="automation-card-state"[^>]*>Waits for you to allow it<[\s\S]*title="Let its triggers start it"[^>]*><span class="bp6-button-text">Allow<\/span>/);
  assert.doesNotMatch(shared, /aria-label="Run now"[^>]*disabled|disabled=""[^>]*aria-label="Run now"/, "Running it by hand needs no allowance.");
  // Nothing of an automation that is run by hand starts by itself: there is nothing to allow.
  assert.doesNotMatch(manual, /Allow<|Waits for you/);
  assert.doesNotMatch(mine, /Allow<|Waits for you/);
  // What cannot run is said first.
  assert.match(lost, /data-problem="true"[^>]*>Its project is gone\.</);
  assert.doesNotMatch(lost, /Allow</);
  for (const locale of locales) {
    const local = cards(render({ items: [item("shared", { allowed: false, triggers: [trigger("issue")] })] }, { locale }))[0];
    assert.ok(local.includes(translate(locale, "Waits for you to allow it")) && local.includes(`>${translate(locale, "Allow")}<`), locale);
  }
});

test("paused, nothing waits: the cards say so and the watch problems are not news", () => {
  const html = render({ paused: true, items: [item("a", { triggers: [trigger("daily", { at: ["09:00"] })] }), item("b", { triggers: [trigger("issue")], watchProblem: "GitHub asks to sign in." })] });
  assert.match(html, /automations-pause"><input[^>]*type="checkbox"\/><span class="bp6-control-indicator"><\/span>Paused</);
  for (const card of cards(html)) assert.match(card, /class="automation-card-state"[^>]*>Paused</);
  assert.doesNotMatch(html, /GitHub asks to sign in/);
  assert.match(render({ items: [item("a")] }), /automations-pause"><input[^>]*checked=""\/><span class="bp6-control-indicator"><\/span>Running</);
});

test("the day to come has a mark for each time, and a band for an automation that runs all along", () => {
  const at = (hours: number) => new Date(Date.now() + hours * 3600_000).toISOString();
  const items = [item("twice", { name: "Twice", triggers: [trigger("daily", { at: ["09:00", "17:30"] })] }), item("often", { name: "Often", triggers: [trigger("cron", { expression: "* * * * *" })] })];
  const html = render({ items, upcoming: [{ automationId: "twice", at: at(2) }, ...Array.from({ length: denseTimeline }, (_value, index) => ({ automationId: "often", at: at(index / 2 + 0.1) })),
    { automationId: "twice", at: at(10) }, { automationId: "gone", at: at(3) }] });
  assert.equal(html.split('class="automation-timeline-mark"').length - 1, 2);
  assert.equal(html.split('class="automation-timeline-band"').length - 1, 1);
  assert.match(html, /class="automation-timeline-band" data-file-tone="teal"[^>]*title="Often · Cron \* \* \* \* \*"/);
  assert.match(html, /class="automation-timeline-mark" data-file-tone="gold"[^>]*title="Twice · /);
  assert.doesNotMatch(render({ items }), /automation-timeline/);
});

test("the recent runs name their automation and open their session while it exists", () => {
  const html = render({ items: [item("a")], runs: [run("r1", { sessionId: "s1", detail: "#12 Crash" , trigger: "issue" }), run("r2", { sessionId: "gone", status: "failed", message: "No model." }),
    run("r3", { status: "skipped", name: null, message: "The previous run was still in progress." })] }, { sessions: [session("s1")] });
  const [first, second, third] = html.split("<li data-tone=").slice(1);
  assert.match(first, /^"ok"><button type="button" class="automation-run" title="Open the session">[\s\S]*<strong>Nightly<\/strong><span>Completed · Issue · #12 Crash<\/span>/);
  assert.match(second, /^"failed"><div class="automation-run">[\s\S]*<span>Failed · Daily · No model\.<\/span>/);
  assert.match(third, /^"muted"><div class="automation-run">[\s\S]*<strong>Automation<\/strong><span>Skipped · Daily · The previous run was still in progress\.<\/span>/);
  assert.match(render({ items: [item("a")] }), /Recent runs<\/h4><p class="automation-empty">No run yet\.<\/p>/);
});

test("what could not be read as an automation is listed with its file", () => {
  const html = render({ faults: [{ file: "/p/.alta/config.toml", projectId: "p", key: "broken", message: "An automation has a name." }, { file: "/home/config.toml", projectId: null, key: "", message: "The configuration file could not be read." }] });
  assert.match(html, /Not read as automations[\s\S]*<code>broken<\/code><span>An automation has a name\.<\/span><small title="\/p\/\.alta\/config\.toml">/);
  assert.match(html, /<code>…<\/code><span>The configuration file could not be read\.<\/span>/);
});

test("the line of a session started by an automation names it and leads back to it, in every language", () => {
  for (const locale of locales) {
    const line = (name: string | null, summary: string | null, open = true) => renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: never } },
      createElement(SessionOrigin, { name, summary, onOpen: open ? never : undefined })));
    const known = line("Nightly <review>", "Issue · #12 Crash");
    assert.ok(known.includes(`${translate(locale, "Started by the automation")} <strong>Nightly &lt;review&gt;</strong> · Issue · #12 Crash`), known);
    assert.ok(known.includes(translate(locale, "Open the automation")), locale);
    assert.ok(!line("Nightly", null).includes(" · "), "Without the run, the automation alone is named.");
    // An automation that was deleted: the tab of the automations is all there is to open.
    const gone = line(null, null);
    assert.ok(gone.includes(translate(locale, "Started by an automation that no longer exists")) && !gone.includes(translate(locale, "Open the automation")), gone);
    // A window that owns no host shows the line without a way to the tab.
    assert.doesNotMatch(line("Nightly", null, false), /<button/);
  }
});
