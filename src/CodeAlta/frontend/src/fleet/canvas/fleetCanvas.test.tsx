import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { FleetDirectoryResult, FleetMachineView, FleetSessionView } from "#neoastra";
import { translate } from "../../localization";
import { ShellLanguageContext } from "../../shellLanguage";
import { FleetDirectoryView, type FleetCanvasActions } from "./FleetCanvas";

const never = () => assert.fail("rendering must not act");
const actions: FleetCanvasActions = { openSession: never, openMachine: never, openSettings: never, runCommand: never };
const machine = (id: string, look: Partial<FleetMachineView> = {}): FleetMachineView => ({ id, name: id, kind: "node", os: "macos", version: "4.0.1", online: true,
  presence: "online", activeSessions: 0, isThisComputer: false, isPaired: true, providers: [], sessions: 0, isReachable: true, labels: [], queued: null, sessionLimit: null, ...look });
const session = (id: string, machineId: string, state: string, look: Partial<FleetSessionView> = {}): FleetSessionView => ({ id, machineId, state,
  providerType: "codex", model: "gpt-6", createdAt: "2026-11-03T08:00:00Z", updatedAt: "2026-11-03T09:00:00Z", turns: 0, tokens: 0, canOpen: true, title: null, ...look });
const directory = (look: Partial<FleetDirectoryResult>): FleetDirectoryResult => ({ status: "ok", linkId: null, url: null, organization: null, connection: null,
  machines: [], sessions: [], ...look });
const render = (value: FleetDirectoryResult) => renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale: "en", choice: "en", setLanguage: never } },
  createElement(FleetDirectoryView, { directory: value, actions })));
const english = (key: Parameters<typeof translate>[1], values?: Record<string, string | number>) => translate("en", key, values);
// The call to create a server, as the markup writes it (its ">" escaped).
const createServer = english("Create your server or join one in Settings > Fleet > Server.").replaceAll(">", "&gt;");

test("without a server, the canvas shows the paired machines: their state, version and sessions at work, each opening its page", () => {
  const html = render(directory({
    status: "paired",
    machines: [machine("mini-id", { name: "Mac mini", activeSessions: 1, sessions: 2 }), machine("box-id", { name: "build box", online: false, presence: "offline", version: null })],
    sessions: [session("s-works", "mini-id", "running", { title: "Fix the build" }), session("s-idle", "mini-id", "idle")],
  }));
  assert.ok(html.includes(`<h1>${english("Paired machines")}</h1>`), html);
  assert.ok(html.includes(english("Not linked to a server.")), "it says there is no server, without hiding the machines");
  assert.ok(!html.includes(createServer), "the machines are shown, not the call to create a server");
  assert.ok(html.includes(`aria-label="${english("Open the page of {name}", { name: "Mac mini" })}"`), "the name of a paired machine opens its page");
  assert.ok(html.includes(`aria-label="${english("Open the page of {name}", { name: "build box" })}"`));
  assert.ok(html.includes("CodeAlta 4.0.1"), "its version");
  assert.ok(html.includes(`>${english("Online")}<`) && html.includes(`>${english("Offline")}<`), "its state");
  assert.ok(html.includes(`2 sessions · 1 ${english("at work")}`), "its sessions at work");
  assert.ok(html.includes("Fix the build"));
  assert.match(html, /<strong>1<\/strong> \/ 2 machines online/u);
});

test("linked to a server, the canvas shows the server's machines; one this computer reaches opens its page, paired or through the server", () => {
  const html = render(directory({
    status: "ok", organization: "Home", url: "https://fleet.example", connection: "online",
    machines: [machine("here", { name: "Desktop", isThisComputer: true, isPaired: false }), machine("mini-id", { name: "Mac mini" }),
      machine("relay-id", { name: "cloud box", isPaired: false }), machine("far-id", { name: "far box", isPaired: false, isReachable: false })],
  }));
  assert.ok(html.includes("<h1>Home</h1>") && html.includes("https://fleet.example"), html);
  assert.ok(!html.includes(english("Paired machines")));
  assert.ok(html.includes(`aria-label="${english("Open the page of {name}", { name: "Mac mini" })}"`));
  assert.ok(html.includes(`aria-label="${english("Open the page of {name}", { name: "cloud box" })}"`), "a machine reached through the server is one of the person's machines: its page opens");
  assert.ok(!html.includes(english("Open the page of {name}", { name: "far box" })), "a machine this computer does not reach has no page here");
  assert.ok(!html.includes(english("Open the page of {name}", { name: "Desktop" })));
});

test("a machine shows the labels it publishes", () => {
  const html = render(directory({ status: "ok", machines: [machine("gpu-id", { name: "gpu box", labels: ["gpu", "prod"] }), machine("plain-id", { name: "plain" })] }));
  assert.match(html, /<div class="fleet-machine-labels" aria-label="Machine tags"><span class="bp6-tag[^"]*"><span class="bp6-text-overflow-ellipsis bp6-fill">gpu<\/span><\/span>/u);
  assert.equal(html.match(/fleet-machine-labels/gu)?.length, 1, "a machine without a label shows none");
});

test("with neither a server nor a paired machine, the canvas says how to have a server", () => {
  const html = render(directory({ status: "no_server" }));
  assert.ok(html.includes(english("Not linked to a server.")) && html.includes(createServer), html);
});
