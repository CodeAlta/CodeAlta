import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import type { SandboxProfileItem, SandboxSessionResponse } from "#neoastra";
import { ShellLanguageContext } from "../shellLanguage";
import { SandboxNoteLine } from "./SandboxNoteLine";
import { modelSandboxes, nextSessionSandbox, resolveSandboxProfile, sandboxProfileChoices, scopeSandbox, sessionSandboxSummary } from "./sandboxChoices";
import { SandboxScopeSelect } from "./SandboxScopeControls";
import { SessionSandboxSelect } from "./SessionSandboxSelect";

const reply = (change: Partial<SandboxSessionResponse>): SandboxSessionResponse =>
  ({ status: "ok", own: null, profile: "workspace", source: "all_sessions", problem: null, choices: ["off", "workspace", "strict", "mine"], ...change });
const profile = (change: Partial<SandboxProfileItem> & { name: string }): SandboxProfileItem => ({ builtIn: false, extends: null, network: null, outside: null,
  hosts: [], write: [], read: [], deny: [], localPorts: [], env: [], mcpServers: [], problem: null, ...change });
const render = (node: React.ReactNode, locale = "en") => renderToStaticMarkup(<ShellLanguageContext.Provider value={{ locale: locale as "en", choice: locale as "en", setLanguage: () => {} }}>
  {node}</ShellLanguageContext.Provider>);
const options = (markup: string) => [...markup.matchAll(/<option value="([^"]*)"[^>]*>([^<]*)<\/option>/g)].map(match => `${match[1]}=${match[2]}`);

test("what the settings give is known while the session follows them, and kept once it has its own", () => {
  const first = nextSessionSandbox(null, "e:s", reply({}));
  assert.equal(first.inherited, "workspace");
  const own = nextSessionSandbox(first, "e:s", reply({ own: "strict", profile: "strict", source: "session" }));
  assert.equal(own.inherited, "workspace", "the choice of the session does not hide what the settings give");
  assert.equal(nextSessionSandbox(own, "e:other", reply({ own: "off", profile: null, source: "session" })).inherited, undefined, "another session is not known");
  assert.equal(nextSessionSandbox(null, "e:s", reply({ profile: null, source: "none" })).inherited, null, "a session not sandboxed by its settings");
});

test("the summary of a session's sandbox, its choices and the scopes the host lists", () => {
  assert.deepEqual(sessionSandboxSummary(reply({ source: "provider", problem: "There is no sandbox profile 'gone'." })),
    { profile: "workspace", source: "provider", problem: "There is no sandbox profile 'gone'." });
  assert.equal(sessionSandboxSummary(reply({ status: "unavailable" })), null);
  assert.equal(sessionSandboxSummary(null), null);
  assert.deepEqual(sandboxProfileChoices(["off", "workspace", "Off", "strict"]), ["workspace", "strict"]);
  const scopes = [{ kind: "provider", id: "Ollama", name: "Ollama", modelId: null, profile: "strict" },
    { kind: "model", id: "ollama", name: "Ollama", modelId: "qwen3", profile: "off" },
    { kind: "model", id: "codex", name: "Codex", modelId: "gpt", profile: "workspace" },
    { kind: "space", id: "work", name: "Work", modelId: null, profile: "workspace" }];
  assert.equal(scopeSandbox(scopes, "provider", "ollama"), "strict", "a provider key is not case-sensitive");
  assert.equal(scopeSandbox(scopes, "model", "ollama", "QWEN3"), "off");
  assert.equal(scopeSandbox(scopes, "model", "ollama", "other"), null);
  assert.equal(scopeSandbox(scopes, "project", "work"), null, "a space is not a project");
  assert.deepEqual(modelSandboxes(scopes, "OLLAMA"), [{ modelId: "qwen3", profile: "off" }]);
});

test("a profile of the user resolves over its base as the host resolves it", () => {
  const profiles = [
    profile({ name: "workspace", builtIn: true, network: "hosts", outside: "ask", hosts: ["nuget.org"], write: ["~/.nuget"] }),
    profile({ name: "mine", extends: "workspace", hosts: ["github.com", "nuget.org"], localPorts: [5432] }),
    profile({ name: "closed", extends: "mine", network: "off", outside: "never" }),
    profile({ name: "alone" }),
    profile({ name: "loop", extends: "again" }), profile({ name: "again", extends: "loop" }),
  ];
  assert.deepEqual(resolveSandboxProfile("mine", profiles), { network: "hosts", outside: "ask", hosts: ["nuget.org", "github.com"], write: ["~/.nuget"], read: [], deny: [],
    localPorts: [5432], mcpServers: [] });
  assert.deepEqual([resolveSandboxProfile("closed", profiles)?.network, resolveSandboxProfile("closed", profiles)?.outside, resolveSandboxProfile("closed", profiles)?.hosts],
    ["off", "never", ["nuget.org", "github.com"]]);
  assert.deepEqual([resolveSandboxProfile("alone", profiles)?.network, resolveSandboxProfile("alone", profiles)?.outside], ["off", "ask"], "without a base: no network, ask");
  assert.equal(resolveSandboxProfile("loop", profiles), null);
  assert.equal(resolveSandboxProfile("missing", profiles), null);
});

test("the Sandbox row of the Agent chip says what following the settings gives, and offers off and the profiles", () => {
  const follow = render(<SessionSandboxSelect id="s" own={null} inherited="workspace" choices={["off", "workspace", "strict"]} onChange={() => {}} />);
  assert.deepEqual(options(follow), ["=workspace · Default", "off=Off", "workspace=workspace", "strict=strict"]);
  assert.ok(follow.includes('aria-label="Sandbox: workspace"'));
  const off = render(<SessionSandboxSelect id="s" own={null} inherited={null} choices={["off", "workspace"]} onChange={() => {}} />);
  assert.equal(options(off)[0], "=Off · Default", "settings that sandbox nothing");
  // A choice of the session's own, before what the settings give is known; a profile no longer listed stays shown.
  const own = render(<SessionSandboxSelect id="s" own="gone" inherited={undefined} choices={["off", "workspace"]} onChange={() => {}} />);
  assert.deepEqual(options(own), ["=Default", "off=Off", "workspace=workspace", "gone=gone"]);
  assert.ok(own.includes('data-own="true"'));
  const french = render(<SessionSandboxSelect id="s" own={null} inherited="workspace" choices={["off", "workspace"]} onChange={() => {}} />, "fr");
  assert.equal(options(french)[0], "=workspace · Par défaut");
});

test("a place that chooses a sandbox follows the settings, turns it off or names a profile", () => {
  const markup = render(<SandboxScopeSelect value={null} profiles={["workspace", "strict"]} label="Sandbox of Ollama" onChange={() => {}} />);
  assert.deepEqual(options(markup), ["=Follow the settings", "off=Not sandboxed", "workspace=workspace", "strict=strict"]);
  assert.deepEqual(options(render(<SandboxScopeSelect value="gone" profiles={["workspace"]} label="x" onChange={() => {}} />)).at(-1), "gone=gone",
    "a profile that is gone stays shown, to be changed");
});

test("the note of a sandbox under a command says what it blocked, or that the command ran outside it", () => {
  const blocked = render(<SandboxNoteLine note={{ profile: "workspace", outside: false, text: "The network of the sandbox refused api.nuget.org." }} />);
  assert.ok(blocked.includes("<strong>Sandbox workspace</strong>") && blocked.includes("<span>The network of the sandbox refused api.nuget.org.</span>"));
  assert.ok(!blocked.includes("data-outside"));
  const outside = render(<SandboxNoteLine note={{ profile: "strict", outside: true, text: "The command ran outside the sandbox, as the user allowed." }} />);
  assert.ok(outside.includes('data-outside="true"') && outside.includes("<strong>Ran outside the sandbox strict</strong>"));
});
