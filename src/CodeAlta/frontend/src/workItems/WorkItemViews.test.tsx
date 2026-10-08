import assert from "node:assert/strict";
import test from "node:test";
import { createElement, type ReactElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { WorkItemRow, WorkItemsSettings, WorkspaceProject, WorkspaceSession } from "#neoastra";
import { locales, translate } from "../localization";
import { ShellLanguageContext } from "../shellLanguage";
import { WorkItemCards } from "./WorkItemCards";
import { WorkItemsBadge } from "./WorkItemsBadge";
import { WorkItemSettings } from "./WorkItemSettings";
import { WorkItemsPanel } from "./WorkItemsPanel";
import { defaultWorkSettings, workItems } from "./workItems";
import type { WorkItemsHub, WorkItemsState } from "./workItemsHub";

const never = () => assert.fail("rendering must not act");
const render = (element: ReactElement, locale: (typeof locales)[number] = "en") =>
  renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: never } }, element));
const row = (id: string, kind: "task" | "plan", status: string, more: Partial<WorkItemRow> = {}): WorkItemRow => ({ id, kind, title: `Title of ${id}`, summary: `Summary of ${id}`,
  category: kind === "task" ? "problem" : null, status, statusText: null, created: "2026-10-07", file: `.alta/${kind}s/${id}.md`, proposedBy: "s1", runner: null, acknowledged: false, runsWith: null, ...more });
const state = (more: Partial<WorkItemsState> = {}): WorkItemsState => ({ loaded: true, complete: true, available: true, settings: defaultWorkSettings,
  projects: [{ projectId: "p", truncated: false, tasks: [row("task", "task", "pending"), row("running", "task", "pending", { runner: "s2" }), row("parked", "task", "later")],
    plans: [row("plan", "plan", "approved")] }], ...more });
// A hub that shows a state and is asked nothing while a view is drawn.
const hub = (value: WorkItemsState): WorkItemsHub => ({ subscribe: () => () => { }, getSnapshot: () => value, connect: never, setProjects: never, refresh: never, read: () => new Promise(() => { }),
  act: never, saveSettings: never }) as unknown as WorkItemsHub;
const project = { id: "p", name: "Alpha", path: "C:\\code\\alpha", archived: false } as WorkspaceProject;
const sessions = [{ id: "s1", title: "The planner" }, { id: "s2", title: "The builder" }] as WorkspaceSession[];
const items = workItems(state().projects, new Set(["s1", "s2"]));

test("a session shows one proposal at a time, with how many there are and every way to decide", () => {
  const cards = items.filter(item => item.stage === "todo");
  const html = render(createElement(WorkItemCards, { hub: hub(state()), items: cards, preferredStart: "worktree", busy: new Set<string>(), onStart: never, onOpenList: never }));
  assert.match(html, /<aside class="work-cards" aria-label="Proposed work items">/);
  assert.match(html, /class="work-card" data-kind="plan"/, "the plan comes first");
  assert.ok(html.includes("Plan ready") && html.includes("Title of plan") && html.includes("Summary of plan"));
  assert.ok(html.includes("1 of 2"), "the cards say how many there are");
  // The preferred way of starting first, then the two others; a plan is kept for later, not dismissed.
  const starts = [...html.matchAll(/Start in a new worktree|Start in a new session|Do it in this session/g)].map(match => match[0]);
  assert.deepEqual(starts, ["Start in a new worktree", "Start in a new session", "Do it in this session"]);
  assert.ok(html.includes("Later") && !html.includes(">Dismiss<"));

  const task = render(createElement(WorkItemCards, { hub: hub(state()), items: cards.filter(item => item.kind === "task"), preferredStart: "here", busy: new Set<string>(), onStart: never, onOpenList: never }));
  assert.ok(task.includes("Proposed task") && task.includes("Problem") && task.includes("Dismiss") && !task.includes("1 of 1"));
  assert.match(task, /class="bp6-button bp6-intent-primary work-start-primary"[^>]*>.*?Do it in this session/s, "the way of starting the user prefers is the first button");

  assert.equal(render(createElement(WorkItemCards, { hub: hub(state()), items: [], preferredStart: "worktree", busy: new Set<string>(), onStart: never, onOpenList: never })), "");
  assert.equal(render(createElement(WorkItemCards, { hub: hub(state()), items: cards, preferredStart: "worktree", busy: new Set<string>(), hidden: true, onStart: never, onOpenList: never })), "");
});

test("a project says how many work items wait and whether one is being worked on", () => {
  assert.equal(render(createElement(WorkItemsBadge, { counts: undefined })), "");
  assert.equal(render(createElement(WorkItemsBadge, { counts: { open: 0, running: 0 } })), "");
  const waiting = render(createElement(WorkItemsBadge, { counts: { open: 3, running: 0 } }));
  assert.match(waiting, /<span class="work-badge" role="img" aria-label="3 work items to do" title="3 work items to do">/);
  assert.ok(waiting.includes('<span aria-hidden="true">3</span>') && !waiting.includes("work-badge-pulse"));
  const working = render(createElement(WorkItemsBadge, { counts: { open: 1, running: 2 } }));
  assert.match(working, /aria-label="1 work item to do · 2 work items in progress"[^>]*data-running="true"/);
  assert.ok(working.includes("work-badge-pulse"));
});

test("the tab lists what waits, with the item that is read and what can be done with it", () => {
  const view = (value: WorkItemsState) => render(createElement(WorkItemsPanel, { hub: hub(value), projects: [project], sessions, runningSessions: new Set(["s2"]), projectId: "p",
    visible: false, onActivate: never, onStart: never, onOpenSession: never, onOpenFile: never, onOpenSettings: never }));
  const html = view(state());
  // The stages are tabs with their counts: what is done or set aside does not hide what waits.
  for (const tab of ["To do", "In progress", "Later", "Closed"]) assert.ok(html.includes(`${tab}<span class="work-count"`), tab);
  assert.match(html, /To do<span class="work-count">2<\/span>/);
  assert.match(html, /In progress<span class="work-count">1<\/span>/);
  assert.match(html, /Closed<span class="work-count" data-zero="true">0<\/span>/);
  assert.equal([...html.matchAll(/class="work-row"/g)].length, 2);
  assert.ok(html.includes("Title of plan") && html.includes("Title of task") && !html.includes("Title of parked"));
  // The first item is the one that is read: a plan that is ready, startable in a new session or a new worktree.
  assert.match(html, /<section class="work-detail-card" aria-label="Title of plan">/);
  assert.ok(html.includes("Start in a new worktree") && html.includes("Start in a new session") && !html.includes("Do it in this session"));
  assert.ok(html.includes("Mark done") && html.includes("Open file"));

  assert.ok(view(state({ projects: [] })).includes("Nothing is waiting"));
  assert.ok(!view(state({ projects: [], complete: false })).includes("Nothing is waiting"), "nothing is said to be empty before every project was read");
  assert.ok(view(state({ loaded: false, available: false })).includes("activity-spinner"));
  assert.ok(view(state({ available: false })).includes("Work items are unavailable in this window."));
});

test("an item that can be started says what its session runs with, and which provider is the default", () => {
  const providers = [{ id: "anthropic", name: "Anthropic", isDefault: false, defaultModel: null, defaultReasoning: null },
    { id: "codex", name: "Codex", isDefault: true, defaultModel: "gpt-b", defaultReasoning: "low" }];
  const view = (plan: WorkItemRow, more: object = { providers }) => render(createElement(WorkItemsPanel, { hub: hub(state({ projects: [{ projectId: "p", truncated: false, tasks: [], plans: [plan] }] })),
    projects: [project], sessions, runningSessions: new Set<string>(), projectId: "p", visible: false, onActivate: never, onStart: never, onOpenSession: never, onOpenFile: never, onOpenSettings: never, ...more }));

  // Nothing is recorded with the item: the default provider, which is marked.
  const fresh = view(row("plan", "plan", "approved"));
  assert.match(fresh, /role="group" aria-label="Runs with"/);
  assert.match(fresh, /<option value="codex" selected="">Codex \(default\)<\/option>/);
  assert.match(fresh, /<option value="anthropic">Anthropic<\/option>/);
  // The session that proposed it ran with another provider: that one.
  const proposed = view(row("plan", "plan", "approved", { runsWith: { providerId: "anthropic", modelId: "claude", reasoningEffort: "high" } }));
  assert.match(proposed, /<option value="anthropic" selected="">Anthropic<\/option>/);
  // A provider that is no longer enabled is not offered: the default one.
  assert.match(view(row("plan", "plan", "approved", { runsWith: { providerId: "gone", modelId: null, reasoningEffort: null } })), /<option value="codex" selected="">/);
  // What cannot be started has nothing to run with, and a window without providers shows no choice.
  assert.ok(!view(row("plan", "plan", "done")).includes("Runs with"));
  assert.ok(!view(row("plan", "plan", "approved"), {}).includes("Runs with"));

  const settings = render(createElement(WorkItemSettings, { hub: hub(state()), providers, onOpenProviders: never }));
  assert.match(settings, /Default provider and model.*?<span data-default-run="true" class="with-logo"><svg class="brand-icon" data-brand="codex"[^>]*>.*?<\/svg>Codex<\/span>.*?Providers/s);
  assert.ok(!render(createElement(WorkItemSettings, { hub: hub(state()) })).includes("Default provider and model"));
});

test("the settings show the choices of the user, in every language", () => {
  const settings: WorkItemsSettings = { propose: false, notify: true, completedTasks: "keep", dismissedTasks: "delete", completedPlans: "delete", start: "session" };
  const html = render(createElement(WorkItemSettings, { hub: hub(state({ settings })) }));
  assert.match(html, /aria-label="Let agents propose follow-up tasks"/);
  assert.equal(/<input[^>]*aria-label="Let agents propose follow-up tasks"[^>]*checked/.test(html), false);
  assert.match(html, /<input[^>]*checked=""[^>]*aria-label="Show proposals in the session"|<input[^>]*aria-label="Show proposals in the session"[^>]*checked=""/);
  assert.match(html, /<option value="session" selected="">Start in a new session<\/option>/);
  assert.match(html, /aria-label="When a task is completed"[^>]*>.*?<option value="keep" selected="">Keep the file<\/option>/s);
  assert.match(html, /aria-label="When a plan is completed"[^>]*><option value="delete" selected="">Delete the file<\/option>/);
  for (const locale of locales) {
    const localized = render(createElement(WorkItemSettings, { hub: hub(state({ settings })) }), locale);
    assert.ok(localized.includes(translate(locale, "Work items")) && localized.includes(translate(locale, "Closed tasks")) && localized.includes(translate(locale, "Completed plans")), locale);
  }
});
