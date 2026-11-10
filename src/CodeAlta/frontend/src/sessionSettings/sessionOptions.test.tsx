import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import { ShellLanguageContext } from "../shellLanguage";
import { SessionOptionsFields } from "./SessionOptionsFields";
import { changedSessionOptions, defaultSessionOptions, sessionContextSize, sessionFastMode, sessionFastModeFellBack, sessionOptionsOf, storedSessionOptions } from "./sessionOptions";

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

test("a session runs in the fast mode it chose, else in the one its provider reported, else in the one of its model", () => {
  const off = { fastMode: false }, on = { fastMode: true };
  assert.equal(sessionFastMode(null, { subSessions: true, fastMode: true }), null, "no model");
  assert.equal(sessionFastMode({ fastMode: null }, { subSessions: true, fastMode: true }), null, "a model without fast mode");
  assert.equal(sessionFastMode(off, null), false);
  assert.equal(sessionFastMode(on, null), true, "a provider that runs it fast (Codex's service_tier)");
  assert.equal(sessionFastMode(off, null, true), true, "an OpenAI project whose default tier is fast");
  assert.equal(sessionFastMode(off, { subSessions: true, fastMode: true }, false), true);
  assert.equal(sessionFastMode(on, { subSessions: true, fastMode: false }, true), false, "the choice wins");
});

test("a session that chose fast mode, whose last turn ran at standard speed, fell back", () => {
  const model = { fastMode: false }, on = { subSessions: true, fastMode: true };
  assert.equal(sessionFastModeFellBack(model, on, false), true, "a rate limit, no usage credits, the default tier served");
  assert.equal(sessionFastModeFellBack(model, on, true), false);
  assert.equal(sessionFastModeFellBack(model, on, null), false, "no turn since the choice");
  assert.equal(sessionFastModeFellBack(model, { subSessions: true, fastMode: false }, false), false, "standard was asked");
  assert.equal(sessionFastModeFellBack(model, null, false), false, "a session that chose nothing follows what ran");
  assert.equal(sessionFastModeFellBack({ fastMode: null }, on, false), false, "a model without fast mode");
});

test("a fast mode is recorded once chosen, on or off, and counts as changed where it differs from the one of the model", () => {
  assert.deepEqual(storedSessionOptions({ subSessions: true, fastMode: false }), { subSessions: true, fastMode: false });
  assert.deepEqual(storedSessionOptions({ subSessions: true, fastMode: true, private: true }), { subSessions: true, private: true, fastMode: true });
  assert.equal(storedSessionOptions({ subSessions: true, fastMode: null }), null);
  assert.equal(changedSessionOptions({ subSessions: true, fastMode: true }, { fastMode: false }), 1);
  assert.equal(changedSessionOptions({ subSessions: true, fastMode: false }, { fastMode: false }), 0, "the default of the model");
  assert.equal(changedSessionOptions({ subSessions: true, fastMode: false }, { fastMode: true }), 1);
  assert.equal(changedSessionOptions({ subSessions: true, fastMode: true }, { fastMode: null }), 0, "a model without fast mode");
  assert.equal(changedSessionOptions({ subSessions: true, fastMode: true }), 0, "a form without a model");
});

test("the switch of fast mode follows Private, where the model has a fast mode", () => {
  assert.doesNotMatch(render(<SessionOptionsFields id="s" value={null} onChange={() => {}} />), /Fast mode/, "a form without a model");
  assert.doesNotMatch(render(<SessionOptionsFields id="s" value={null} context={{ fastMode: null }} onChange={() => {}} />), /Fast mode/);

  const off = render(<SessionOptionsFields id="s" value={null} context={{ fastMode: false }} onChange={() => {}} />);
  assert.match(off, /composer-private-s[\s\S]*composer-fast-mode-s/, "under Private");
  assert.doesNotMatch(off, /id="composer-fast-mode-s"[^>]*checked=""/);
  assert.match(off, /Standard speed and price\./);

  const reported = render(<SessionOptionsFields id="s" value={null} context={{ fastMode: false }} reportedFastMode onChange={() => {}} />);
  assert.match(reported, /id="composer-fast-mode-s"[^>]*checked=""/, "what the provider reported");
  assert.match(reported, /Faster output, at a higher price per token\./);
  assert.match(render(<SessionOptionsFields id="s" value={null} context={{ fastMode: true }} onChange={() => {}} />, "fr"), /Mode rapide/);
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
