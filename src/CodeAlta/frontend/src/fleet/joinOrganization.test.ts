import assert from "node:assert/strict";
import test from "node:test";
import { defaultJoinSpaces, joinWords, organizationAddressOf, spacesToLink } from "./joinOrganization";

test("at home the person is their own administrator: the words of their own server never name one", () => {
  const own = joinWords("own");
  for (const words of Object.values(own)) assert.doesNotMatch(words, /administrator|company/iu, words);
  assert.equal(own.title, "Connect to my server");
  assert.match(own.code, /Invite a computer/u, "the code comes from another computer of the person");
  const company = joinWords("organization");
  assert.equal(company.title, "Join an organization");
  assert.match(company.addressHelp, /administrator/u);
});

test("on the person's own server, their spaces are linked by default; on an organization's, a new Work space", () => {
  const spaces = [{ id: "default" }, { id: "home" }];
  assert.deepEqual(defaultJoinSpaces("own", spaces), ["default", "home"]);
  assert.deepEqual(defaultJoinSpaces("organization", spaces), ["new"]);
});

test("Link all my spaces links those not linked yet, in their order", () => {
  assert.deepEqual(spacesToLink([{ id: "default" }, { id: "home" }, { id: "work" }], ["home"]), ["default", "work"]);
  assert.deepEqual(spacesToLink([{ id: "default" }], ["default"]), []);
});

test("an address is read as typed, https by default", () => {
  assert.equal(organizationAddressOf("fleet.example.com/"), "https://fleet.example.com");
  assert.equal(organizationAddressOf("http://box:2586"), "http://box:2586");
  assert.equal(organizationAddressOf("https://user@x"), "");
});
