import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import { ShellLanguageContext } from "../shellLanguage";
import { SessionOptionsFields } from "./SessionOptionsFields";
import { changedSessionOptions, defaultSessionOptions, sessionContextSize, sessionOptionsOf, storedSessionOptions } from "./sessionOptions";

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

test("a size of context is an option the session records, and counts as changed where its model has it", () => {
  const model = { contextSizes: [200_000, 1_000_000] };
  assert.equal(changedSessionOptions({ subSessions: true, contextSize: 1_000_000 }, model), 1);
  assert.equal(changedSessionOptions({ subSessions: false, contextSize: 1_000_000 }, model), 2);
  assert.deepEqual(storedSessionOptions({ subSessions: true, contextSize: 1_000_000 }), { subSessions: true, contextSize: 1_000_000 });
  // A size the model of the session does not have is kept, and not counted: nothing under More options shows it.
  assert.equal(changedSessionOptions({ subSessions: true, contextSize: 872_000 }, { contextSizes: null }), 0);
  assert.equal(changedSessionOptions({ subSessions: true, contextSize: 872_000 }, null), 0, "a model that is not known yet");
  assert.equal(changedSessionOptions({ subSessions: true, contextSize: 872_000 }), 0, "a form without a model");
  assert.equal(storedSessionOptions({ subSessions: true, contextSize: null }), null);
});

test("a session uses the size it chose where its model has it, else the size of a session that chose none", () => {
  const astra = { contextSizes: [272_000, 872_000], contextSize: 272_000 };
  assert.equal(sessionContextSize(astra, null), 272_000);
  assert.equal(sessionContextSize(astra, { subSessions: true, contextSize: 872_000 }), 872_000);
  assert.equal(sessionContextSize(astra, { subSessions: true, contextSize: 1_000_000 }), 272_000, "a size of another model");
  assert.equal(sessionContextSize({ contextSizes: null, contextSize: 400_000 }, { subSessions: true, contextSize: 872_000 }), null, "a model with one size");
});

test("the size of context is offered where the model has several: Default with its size, then each size", () => {
  assert.doesNotMatch(render(<SessionOptionsFields id="s" value={null} onChange={() => {}} />), /Context size/);
  const html = render(<SessionOptionsFields id="s" value={{ subSessions: true, contextSize: 1_000_000 }} context={{ contextSizes: [272_000, 1_000_000], contextSize: 272_000 }} onChange={() => {}} />);
  assert.match(html, /Context size/);
  assert.match(html, /<option value="">272k · Default<\/option><option value="272000">272k<\/option><option value="1000000" selected="">1M<\/option>/);
  assert.match(html, /The conversation is compacted before it exceeds this size\./);
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
