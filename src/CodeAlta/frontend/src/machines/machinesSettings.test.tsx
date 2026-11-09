import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { MachineView, ThisComputerView } from "#neoastra";
import { translate } from "../localization";
import { ShellLanguageContext } from "../shellLanguage";
import { MachineDetails, MachineTable, machineReach, machineSystem } from "./MachineTable";
import { orderedMachines, throughServer } from "./machines";
import { ThisComputerBody } from "./ThisComputer";

const never = () => assert.fail("rendering must not act");
const machine = (change: Partial<MachineView> = {}): MachineView => ({ id: "m1", name: "mac-mini", state: "online", os: "macos", version: "4.0.0",
  address: "mac-mini:2585", fingerprint: "ABCD EFGH", words: "apple river stone cloud amber tiger", sessions: 2, lastSeen: null, problem: null,
  permissions: ["view", "drive"], pairedAt: "2026-11-03T00:00:00Z", paired: true, channel: null, tags: [], working: null, queued: null, sessionLimit: null, ...change });
const shell = (element: ReturnType<typeof createElement>) => renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale: "en", choice: "en", setLanguage: never } }, element));
const time = new Intl.DateTimeFormat("en", { dateStyle: "medium" });
const details = (value: MachineView) => shell(createElement(MachineDetails, { machine: value, busy: false, time, onOpen: never, onForget: never }));
const english = (key: Parameters<typeof translate>[1]) => translate("en", key);

test("a machine added through the server is one of the person's machines: listed beside the paired ones, opened, but not forgotten from here", () => {
  const paired = machine({ id: "mini", name: "mac-mini" });
  const relayed = machine({ id: "box", name: "build-box", paired: false, channel: "relayed", state: "online" });
  // The page lists them all: a machine is not left out for the path it is reached by.
  assert.deepEqual(orderedMachines([relayed, paired]).map(value => value.id), ["box", "mini"]);
  assert.equal(throughServer(relayed), true);
  assert.equal(throughServer(paired), false);

  // The table says how each is reached, in its column.
  const table = shell(createElement(MachineTable, { machines: [relayed, paired], busy: false, onOpen: never, onForget: never }));
  assert.ok(table.includes(english("Through your server")), table);
  assert.deepEqual([machineReach(relayed), machineReach(paired), machineReach({ paired: true, channel: "direct" })], ["server", "paired", "direct"]);
  assert.deepEqual([machineSystem({ os: "macOS 26.1" }), machineSystem({ os: "Windows 11 (26100)" }), machineSystem({ os: null })], ["macOS", "Windows", ""]);

  // Its details open its page; only a paired machine is forgotten from here.
  const server = details(relayed);
  assert.ok(server.includes(english("Open its page")), server);
  assert.ok(!server.includes(english("Forget")), "nothing to forget: it is not paired here");
  assert.ok(details(paired).includes(english("Forget")));
});

const thisComputer = (change: Partial<ThisComputerView> = {}): ThisComputerView => ({ status: "ok", visible: false, listening: false, name: "DESKTOP-ALEX", words: null,
  port: 2587, addresses: [], tailnet: null, problem: null, devices: [], reachable: false, servers: [], network: null, pairBothWays: true, ...change });
const renderThisComputer = (view: ThisComputerView) => renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale: "en", choice: "en", setLanguage: never } },
  createElement(ThisComputerBody, { view, busy: false, onVisible: never, onReachable: never, onPairBothWays: never, onPair: never, onPermission: never, onRevoke: never })));

test("this computer is reachable by nobody until the person lets their other computers pair with it, then offers a link and lists them", () => {
  const off = renderThisComputer(thisComputer());
  assert.ok(off.includes(english("Let my other computers pair with this one")), off);
  assert.ok(!off.includes(english("Pair another computer…")), "no link while nothing listens");

  const on = renderThisComputer(thisComputer({ visible: true, listening: true, words: "apple river stone cloud amber tiger", tailnet: "desk.tail1234.ts.net",
    addresses: ["desk.tail1234.ts.net:2587", "192.168.1.5:2587"],
    devices: [{ id: "d1", name: "macbook", fingerprint: "ABCD EFGH IJKL", permissions: ["view", "drive", "start"], pairedAt: "2026-11-05T10:00:00Z", revokedAt: null,
      channels: 0, isThisComputer: false }] }));
  assert.ok(on.includes(english("Pair another computer…")), on);
  assert.ok(on.includes("desk.tail1234.ts.net"), "its name on the tailnet, which reaches it away from home");
  assert.ok(on.includes(english("Computers paired with this one")));
  assert.ok(on.includes(`aria-label="${english("Move")} — macbook"`), "the person lets a paired computer move sessions here");
  assert.ok(!on.includes(`${english("Manage")} — macbook`), "no other computer manages a desktop");

  const blocked = renderThisComputer(thisComputer({ visible: true, listening: false, problem: "Port 2587 cannot be opened" }));
  assert.ok(blocked.includes(english("This computer cannot listen for your other computers.")), blocked);
  assert.ok(blocked.includes("Port 2587 cannot be opened"));
});

test("a pairing goes both ways unless the person turns it off, and the switch shows only once other computers may pair", () => {
  const bothWays = english("Reach the computers that pair with this one too");
  assert.ok(!renderThisComputer(thisComputer()).includes(bothWays), "nothing pairs with it yet");

  // The input of the switch whose label is that text.
  const input = (markup: string) => markup.slice(markup.lastIndexOf("<input", markup.indexOf(bothWays)), markup.indexOf(bothWays));
  const on = renderThisComputer(thisComputer({ visible: true, listening: true }));
  assert.ok(/^<input[^>]*type="checkbox"[^>]*checked=""/.test(input(on)), "a switch, on by default");
  assert.ok(on.includes(english("A computer that pairs with this one from its link is listed with your machines here too, and this computer sees, drives and starts its sessions: one pairing goes both ways.")), on);

  const off = renderThisComputer(thisComputer({ visible: true, listening: true, pairBothWays: false }));
  assert.ok(!input(off).includes("checked"), "turned off");
  assert.ok(off.includes(english("A computer that pairs with this one from its link is reached one way only: this computer does not list it with its machines.")), off);
});

test("a computer linked to a server is reached through it only once the person lets their other computers do so, without pairing", () => {
  const reach = english("Let my other computers reach this one through my server");
  assert.ok(!renderThisComputer(thisComputer()).includes(reach), "no server, no such way");

  const linked = renderThisComputer(thisComputer({ servers: ["Home"] }));
  assert.ok(linked.includes(reach), linked);
  assert.ok(/<input[^>]*type="checkbox"[^>]*>[^]*?Let my other computers reach this one through my server/.test(linked), "a switch");
  assert.ok(linked.includes("Home"), "it names the server");
  assert.ok(!linked.includes(english("Pair another computer…")), "no pairing link: the server reaches it");

  const left = renderThisComputer(thisComputer({ reachable: true }));
  assert.ok(left.includes(english("This computer is linked to no server now: nothing reaches it this way.")), "turned on, then the server left: it says so and can be turned off");
});
