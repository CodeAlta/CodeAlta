import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import { ShellLanguageContext } from "../shellLanguage";
import { SessionOptionsFields } from "./SessionOptionsFields";
import { changedSessionOptions, defaultSessionOptions, sessionOptionsOf, storedSessionOptions } from "./sessionOptions";

const render = (node: React.ReactNode, locale = "en") => renderToStaticMarkup(<ShellLanguageContext.Provider value={{ locale: locale as "en", choice: locale as "en", setLanguage: () => {} }}>
  {node}</ShellLanguageContext.Provider>);

test("the options are on by default, and a session records only what differs from them", () => {
  assert.deepEqual(sessionOptionsOf(null), defaultSessionOptions);
  assert.equal(defaultSessionOptions.subSessions, true);
  assert.equal(changedSessionOptions(null), 0);
  assert.equal(changedSessionOptions({ subSessions: true }), 0);
  assert.equal(changedSessionOptions({ subSessions: false }), 1);
  assert.equal(storedSessionOptions({ subSessions: true }), null);
  assert.equal(storedSessionOptions(null), null);
  assert.deepEqual(storedSessionOptions({ subSessions: false }), { subSessions: false });
});

test("the switch of sub-sessions says in one line what it does", () => {
  const on = render(<SessionOptionsFields id="s" value={null} onChange={() => {}} />);
  assert.match(on, /id="composer-sub-sessions-s"[^>]*checked=""/);
  assert.match(on, /Sub-sessions/);
  assert.match(on, /The agent can start sessions of its own \(sub-agents\)\./);

  const off = render(<SessionOptionsFields id="s" value={{ subSessions: false }} onChange={() => {}} />);
  assert.doesNotMatch(off, /checked=""/);
  assert.match(off, /The agent does all the work itself: it starts no session\./);

  assert.match(render(<SessionOptionsFields id="s" value={null} onChange={() => {}} />, "fr"), /Sous-sessions/);
});
