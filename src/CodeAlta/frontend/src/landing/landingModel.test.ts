import assert from "node:assert/strict";
import test from "node:test";
import { cardCommand, cardLimit, onboardingSteps, readLandingCards, recentLimit, recentProjects, recentSessions, sameLandingCards } from "./landingModel";
import type { LandingProject, LandingSession } from "./landingShell";

const session = (id: string, updatedAt: string, over: Partial<LandingSession> = {}): LandingSession => ({ id, title: id, projectId: null, updatedAt, child: false, ...over });
const project = (id: string, name = id): LandingProject => ({ id, name, path: `/code/${id}` });
const svg = "data:image/svg+xml;base64,PHN2Zz4=";
const action = (over: Record<string, unknown> = {}) => ({ label: "Open", icon: null, iconData: null, commandId: "command-1", canvas: null, key: null, primary: false, disabled: false, ...over });
const wire = (over: Record<string, unknown> = {}) => ({
  id: "builtin:fixture/a", pluginKey: "builtin:fixture", plugin: "Fixture", cardId: "a", title: "A", icon: "chart-column", iconData: null, state: "ok", html: "<p>Hello</p>",
  statusText: "Up to date", tone: "Success", projectId: null, projectName: null, actions: [], ...over,
});
const read = (...cards: unknown[]) => readLandingCards({ status: "ok", cards })!;

test("the recent sessions are the ones used last, without the sub-agents, and a date that is no date comes last", () => {
  const sessions = [
    session("old", "2026-10-01T10:00:00Z"), session("bad-1", "not a date"), session("child", "2026-10-09T10:00:00Z", { child: true }),
    session("new", "2026-10-08T10:00:00Z"), session("bad-2", ""), session("same-1", "2026-10-05T10:00:00Z"), session("same-2", "2026-10-05T10:00:00Z"),
  ];
  assert.deepEqual(recentSessions(sessions).map(item => item.id), ["new", "same-1", "same-2", "old", "bad-1", "bad-2"]);
  assert.deepEqual(recentSessions([...sessions].reverse()).map(item => item.id), ["new", "same-2", "same-1", "old", "bad-2", "bad-1"], "sessions of the same moment keep the order they came in");
  assert.deepEqual(recentSessions(sessions, 2).map(item => item.id), ["new", "same-1"]);
  assert.deepEqual(recentSessions(sessions, 0), []);
  assert.deepEqual(recentSessions(sessions, -3), []);
  assert.deepEqual(recentSessions([]), []);
  const many = Array.from({ length: 20 }, (_, index) => session(`s${index}`, new Date(Date.UTC(2026, 0, 1 + index)).toISOString()));
  assert.equal(recentSessions(many).length, recentLimit);
  assert.equal(recentSessions(many)[0].id, "s19");
});

test("the recent projects follow their last session, then their name, and say how many sessions they have", () => {
  const projects = [project("zeta", "Zeta"), project("beta", "Beta"), project("alpha", "Alpha"), project("gamma", "Gamma")];
  const sessions = [
    session("1", "2026-10-02T10:00:00Z", { projectId: "zeta" }), session("2", "2026-10-06T10:00:00Z", { projectId: "zeta" }), session("3", "2026-10-04T10:00:00Z", { projectId: "zeta" }),
    session("4", "2026-10-07T10:00:00Z", { projectId: "gamma", child: true }), session("5", "2026-10-09T10:00:00Z"), session("6", "2026-10-09T10:00:00Z", { projectId: "gone" }),
  ];
  const recent = recentProjects(projects, sessions);
  assert.deepEqual(recent.map(item => [item.project.id, item.sessions, item.updatedAt]),
    [["gamma", 1, "2026-10-07T10:00:00Z"], ["zeta", 3, "2026-10-06T10:00:00Z"], ["alpha", 0, null], ["beta", 0, null]]);
  assert.deepEqual(recentProjects(projects, sessions, 1).map(item => item.project.id), ["gamma"]);
  assert.deepEqual(recentProjects(projects, sessions, 0), []);
  assert.deepEqual(recentProjects([], sessions), []);
  assert.equal(recentProjects(Array.from({ length: 12 }, (_, index) => project(`p${index}`)), []).length, recentLimit);
});

test("a step is offered only for what the window knows: no provider that is ready, no project", () => {
  const ready = { ready: 2, detecting: false }, none = { ready: 0, detecting: false };
  assert.deepEqual(onboardingSteps(null, null), []);
  assert.deepEqual(onboardingSteps(null, none), ["provider"]);
  assert.deepEqual(onboardingSteps([], null), ["project"]);
  assert.deepEqual(onboardingSteps([], none), ["provider", "project"]);
  assert.deepEqual(onboardingSteps([], { ready: 0, detecting: true }), ["project"], "providers that are still looked for are not missing yet");
  assert.deepEqual(onboardingSteps([project("a")], none), ["provider"]);
  assert.deepEqual(onboardingSteps([project("a")], ready), []);
  assert.deepEqual(onboardingSteps([], ready), ["project"]);
});

test("the cards are read only when the host said ok", () => {
  for (const reply of [null, undefined, "ok", 3, {}, { status: "stale_epoch", cards: [] }, { status: "ok" }, { status: "ok", cards: "x" }]) assert.equal(readLandingCards(reply), null);
  assert.deepEqual(read(), []);
  const [card] = read(wire({ projectId: "p1", projectName: "One", iconData: svg, actions: [action({ primary: true })], commands: [{ name: "numbers.refresh", id: "command-1" }] }));
  assert.deepEqual(card, {
    id: "builtin:fixture/a", pluginKey: "builtin:fixture", plugin: "Fixture", cardId: "a", title: "A", icon: "chart-column", iconData: svg, state: "ok", html: "<p>Hello</p>",
    status: "Up to date", tone: "Success", projectId: "p1", projectName: "One",
    actions: [{ label: "Open", icon: null, iconData: null, commandId: "command-1", canvas: null, canvasScope: null, key: null, primary: true, disabled: false }],
    commands: [{ name: "numbers.refresh", id: "command-1" }],
  });
});

test("a canvas action keeps what its canvas is about, and a card names the commands of its own plugin only", () => {
  const scope = (canvasScope: unknown) => read(wire({ actions: [action({ commandId: null, canvas: "board", canvasScope })] }))[0].actions[0].canvasScope;
  assert.deepEqual(["Application", "Project", "Session"].map(scope), ["Application", "Project", "Session"]);
  // A scope the host did not say opens the canvas as one of the application: never with the project of the card.
  assert.deepEqual([undefined, null, "Everywhere", 3].map(scope), ["Application", "Application", "Application", "Application"]);
  assert.equal(read(wire({ actions: [action({ canvasScope: "Project" })] }))[0].actions[0].canvasScope, null, "a command has no scope");

  const [card] = read(wire({ commands: [{ name: "Notes.Add", id: "c1" }, { name: "notes.add", id: "again" }, { name: "bad name", id: "c2" }, { name: "", id: "c3" }, { name: "open", id: "" }, null, 4, { name: "open", id: "c4" }] }));
  assert.deepEqual(card.commands, [{ name: "Notes.Add", id: "c1" }, { name: "open", id: "c4" }]);
  assert.deepEqual(cardCommand(card, "notes.add"), { name: "Notes.Add", id: "c1" }, "a name is read without regard to case, as the host reads it");
  assert.equal(cardCommand(card, "statistics"), null, "a command the plugin of the card does not have is not looked for elsewhere");
  assert.deepEqual(read(wire({ commands: "x" }))[0].commands, []);
  assert.deepEqual(read(wire({ state: "failed", commands: [{ name: "open", id: "c4" }] }))[0].commands, [], "a card that failed runs nothing");
  assert.equal(read(wire({ commands: Array.from({ length: 40 }, (_, index) => ({ name: `c${index}`, id: `id${index}` })) }))[0].commands.length, 32);
});

test("a card that is not well formed and a second card of the same id are left out, and the rest is drawn", () => {
  const cards = read(
    wire({ id: "" }), wire({ id: 4 }), wire({ pluginKey: "" }), wire({ plugin: null }), wire({ cardId: "" }), wire({ cardId: "x".repeat(65) }), wire({ title: "" }), wire({ title: "bad\ntitle" }),
    wire({ title: "x".repeat(81) }), null, 3, "card", wire({ id: "first", title: "First" }), wire({ id: "first", title: "Again" }), wire({ id: "second", plugin: "" }));
  assert.deepEqual(cards.map(card => [card.id, card.title]), [["first", "First"], ["second", "A"]]);
  // What is optional falls back instead of taking the card away.
  const [loose] = read(wire({ tone: "Rainbow", icon: "", statusText: "x".repeat(61), projectName: "No project", iconData: "javascript:alert(1)" }));
  assert.deepEqual([loose.tone, loose.icon, loose.status, loose.projectId, loose.projectName, loose.iconData], ["Info", null, null, null, null, null]);
  assert.equal(read(wire({ iconData: "data:image/png;base64,AAAA" }))[0].iconData, null, "only an SVG data URL is an icon");
  assert.equal(read(wire({ iconData: svg }))[0].iconData, svg);
});

test("an action has a label and exactly one of a command and a canvas, and a card has three of them at most", () => {
  const actions = (...entries: unknown[]) => read(wire({ actions: entries }))[0].actions;
  assert.deepEqual(actions(action(), action({ commandId: null, canvas: "board" })).map(item => [item.commandId, item.canvas]), [["command-1", null], [null, "board"]]);
  assert.deepEqual(actions(action({ commandId: null }), action({ canvas: "board" }), action({ label: "" }), action({ label: "x".repeat(61) }), action({ commandId: null, canvas: "not valid" }), null, 7), [],
    "neither, both, no label, a long label, a canvas that is no id and junk are left out");
  assert.deepEqual(actions(action({ label: "1" }), action({ label: "2" }), action({ label: "3" }), action({ label: "4" })).map(item => item.label), ["1", "2", "3"]);
  assert.deepEqual(actions(action({ commandId: null }), action({ label: "2" }), action({ label: "3" }), action({ label: "4" })).map(item => item.label), ["2", "3"], "the three first are the ones that count");
  // The key says which tab of a canvas: a command has none.
  assert.equal(actions(action({ key: "week" }))[0].key, null);
  assert.equal(actions(action({ commandId: null, canvas: "board", key: "week" }))[0].key, "week");
  assert.equal(actions(action({ commandId: null, canvas: "board", key: "" }))[0].key, null);
  const [flagged] = actions(action({ primary: "yes", disabled: 1, iconData: "data:text/html;base64,AAAA", icon: "play" }));
  assert.deepEqual([flagged.primary, flagged.disabled, flagged.iconData, flagged.icon], [false, false, null, "play"]);
  assert.deepEqual(actions(action({ primary: true, disabled: true, iconData: svg })).map(item => [item.primary, item.disabled, item.iconData]), [[true, true, svg]]);
  assert.deepEqual(read(wire({ actions: "open" }))[0].actions, []);
});

test("a card that failed has no content and no action, and the page draws twenty-four cards at most", () => {
  for (const over of [{ state: "failed" }, { state: "broken" }, { html: null }, { html: "x".repeat(64 * 1024 + 1) }]) {
    const [card] = read(wire({ actions: [action()], ...over }));
    assert.deepEqual([card.state, card.html, card.status, card.actions], ["failed", "", null, []], JSON.stringify(Object.keys(over)));
    assert.equal(card.title, "A", "it is still a card, with its title");
  }
  const many = read(...Array.from({ length: 40 }, (_, index) => wire({ id: `card-${index}`, cardId: `c${index}` })));
  assert.equal(many.length, cardLimit);
  assert.equal(cardLimit, 24);
  assert.deepEqual([many[0].id, many.at(-1)!.id], ["card-0", "card-23"]);
});

test("two lists of cards are the same when they show the same thing in the same order", () => {
  const one = read(wire(), wire({ id: "b", cardId: "b", actions: [action()] }));
  assert.equal(sameLandingCards(one, read(wire(), wire({ id: "b", cardId: "b", actions: [action()] }))), true);
  assert.equal(sameLandingCards([], []), true);
  assert.equal(sameLandingCards(one, one.slice(0, 1)), false);
  assert.equal(sameLandingCards(one, [...one].reverse()), false);
  assert.equal(sameLandingCards(one, read(wire({ html: "<p>Changed</p>" }), wire({ id: "b", cardId: "b", actions: [action()] }))), false);
  assert.equal(sameLandingCards(one, read(wire(), wire({ id: "b", cardId: "b", actions: [action({ disabled: true })] }))), false);
  assert.equal(sameLandingCards(one, read(wire({ statusText: "Reading" }), wire({ id: "b", cardId: "b", actions: [action()] }))), false);
});
