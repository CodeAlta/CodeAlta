import assert from "node:assert/strict";
import test from "node:test";
import { createElement, type ReactElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { SessionLinksContext, SessionReference, SubAgentBadge, type SessionLinks } from "./SessionReference";
import { ShellLanguageContext } from "./shellLanguage";

const never = () => assert.fail("rendering opens nothing");
const render = (element: ReactElement, links: SessionLinks | null = null) => renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale: "en", choice: "en", setLanguage: never } },
  createElement(SessionLinksContext.Provider, { value: links }, element)));
const links: SessionLinks = { title: id => id === "01a105f5-60d1" ? "Map the <parser>" : null, open: never };

test("a message between agents names its session by its title, which opens it", () => {
  const html = render(createElement(SessionReference, { sessionId: "01a105f5-60d1" }), links);
  assert.match(html, /^<button type="button" class="session-reference" title="Open the session Map the &lt;parser&gt;" aria-label="Open the session Map the &lt;parser&gt;">/);
  assert.ok(html.endsWith("<span>Map the &lt;parser&gt;</span></button>"));
  // A session the window does not list is named by the start of its id, and opens nothing.
  assert.equal(render(createElement(SessionReference, { sessionId: "01a2ffff-0000" }), links), '<small class="session-reference-id" title="01a2ffff-0000">01a2ffff</small>');
  assert.equal(render(createElement(SessionReference, { sessionId: "01a105f5-60d1" })), '<small class="session-reference-id" title="01a105f5-60d1">01a105f5</small>');
});

test("a session that started sessions shows how many", () => {
  assert.equal(render(createElement(SubAgentBadge, { count: 0 })), "");
  assert.match(render(createElement(SubAgentBadge, { count: 3 })), /^<span class="sub-agent-badge" role="img" aria-label="3 sub-agent\(s\)" title="3 sub-agent\(s\)">.*<span aria-hidden="true">3<\/span><\/span>$/);
});
