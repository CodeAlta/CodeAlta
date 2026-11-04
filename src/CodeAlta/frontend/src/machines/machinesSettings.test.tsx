import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { MachineView } from "#neoastra";
import { translate } from "../localization";
import { ShellLanguageContext } from "../shellLanguage";
import { MachineRow } from "./MachinesSettings";
import { orderedMachines, throughServer } from "./machines";

const never = () => assert.fail("rendering must not act");
const machine = (change: Partial<MachineView> = {}): MachineView => ({ id: "m1", name: "mac-mini", state: "online", os: "macos", version: "4.0.0",
  address: "mac-mini:2585", fingerprint: "ABCD EFGH", words: "apple river stone cloud amber tiger", sessions: 2, lastSeen: null, problem: null,
  permissions: ["view", "drive"], pairedAt: "2026-11-03T00:00:00Z", paired: true, channel: null, ...change });
const render = (value: MachineView) => renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale: "en", choice: "en", setLanguage: never } },
  createElement(MachineRow, { machine: value, busy: false, onOpen: never, onForget: never })));
const english = (key: Parameters<typeof translate>[1]) => translate("en", key);

test("a machine added through the server is one of the person's machines: listed beside the paired ones, opened, but not forgotten from here", () => {
  const paired = machine({ id: "mini", name: "mac-mini" });
  const relayed = machine({ id: "box", name: "build-box", paired: false, channel: "relayed", state: "online" });
  // The page lists them all: a machine is not left out for the path it is reached by.
  assert.deepEqual(orderedMachines([relayed, paired]).map(value => value.id), ["box", "mini"]);
  assert.equal(throughServer(relayed), true);
  assert.equal(throughServer(paired), false);

  const server = render(relayed);
  assert.ok(server.includes(english("Through your server")), server);
  assert.ok(server.includes(`${english("Open the page of {name}").replace("{name}", "build-box")}`), "its page opens");
  assert.ok(!server.includes(`aria-label="${english("Forget")} build-box"`), "nothing to forget: it is not paired here");

  const own = render(paired);
  assert.ok(own.includes(`aria-label="${english("Forget")} mac-mini"`), own);
  assert.ok(!own.includes(english("Through your server")));
});
