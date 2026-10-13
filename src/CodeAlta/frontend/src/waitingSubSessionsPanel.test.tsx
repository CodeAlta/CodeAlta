import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { SessionLinksContext, type SessionLinks } from "./SessionReference";
import type { createPermissionReviewer } from "./sessionPermissions";
import { ShellLanguageContext } from "./shellLanguage";
import { SessionWaitingBadge } from "./WaitingBadge";
import { WaitingSubSessionsPanel } from "./WaitingSubSessionsPanel";

type Reviewer = ReturnType<typeof createPermissionReviewer>;
const never = () => assert.fail("rendering opens nothing");
const links: SessionLinks = { title: id => ({ child: "Fix the <parser>", grandchild: "Run the tests" } as Record<string, string>)[id] ?? null, open: never };
const render = (props: Parameters<typeof WaitingSubSessionsPanel>[0]) => renderToStaticMarkup(createElement(ShellLanguageContext.Provider,
  { value: { locale: "en", choice: "en", setLanguage: never } }, createElement(SessionLinksContext.Provider, { value: links }, createElement(WaitingSubSessionsPanel, props))));
const sessions = [{ sessionId: "child", title: "Fix the <parser>" }, { sessionId: "grandchild", title: "Run the tests" }];

test("the sessions under a session that wait are named above its composer, each opening its own pane", () => {
  const html = render({ sessions, epoch: "e", canReview: () => true, createReviewer: null });
  assert.match(html, /^<section class="waiting-sub-sessions" aria-label="Sub-sessions that wait for you">/);
  assert.equal(html.match(/class="waiting-sub-session"/g)?.length, 2);
  assert.match(html, /aria-label="Open the session Fix the &lt;parser&gt;"[^>]*>.*<span>Fix the &lt;parser&gt;<\/span><\/button><span>Waits for your answer<\/span>/);
  assert.match(html, /<span>Run the tests<\/span><\/button><span>Waits for your answer<\/span>/);
  // None waits: nothing is shown.
  assert.equal(render({ sessions: [], epoch: "e", canReview: () => true, createReviewer: null }), "");
});

test("each session that waits has a reviewer of its own, never the one of the pane", () => {
  const made: Reviewer[] = [];
  const createReviewer = () => { const reviewer = {} as Reviewer; made.push(reviewer); return reviewer; };
  render({ sessions, epoch: "e", canReview: () => true, createReviewer });
  assert.equal(made.length, 2);
  assert.notEqual(made[0], made[1]);
});

test("a session whose sub-sessions wait is marked, lighter than one that waits itself", () => {
  const badge = (waiting: boolean, below?: number) => renderToStaticMarkup(createElement(ShellLanguageContext.Provider,
    { value: { locale: "en", choice: "en", setLanguage: never } }, createElement(SessionWaitingBadge, { waiting, below })));
  assert.equal(badge(false), "");
  assert.match(badge(true, 2), /^<span class="waiting-badge" role="img" aria-label="Waits for your answer"/);
  assert.match(badge(false, 1), /^<span class="waiting-badge" data-below="true" role="img" aria-label="A sub-session waits for you"/);
  assert.match(badge(false, 3), /aria-label="3 sub-sessions wait for you"/);
});
