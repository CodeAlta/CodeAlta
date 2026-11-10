import assert from "node:assert/strict";
import test from "node:test";
import { browserExecutable } from "../browserTarget";
import { session } from "./fixtureServer";
import { openPhone } from "./phoneBrowser";
import { testLimit } from "../testTime";

test("the phone page signs in from a link whose token is in the query, and takes it out of the address", { skip: !browserExecutable, timeout: testLimit(120_000) }, async () => {
  const phone = await openPhone(server => { server.state.sessions = [session("s1", "Fix the parser", "p1", 2)]; }, "");
  const { server, command, waitFor, evaluate } = phone;
  try {
    // The link of a Dev Tunnel: its sign-in loses a fragment, so the token is in the query, and comes back with it.
    await command("Page.navigate", { url: `${server.origin}/?k=${server.state.token}` });
    await waitFor(`document.querySelectorAll("#sessions .row").length === 1`, "the list of sessions");
    assert.equal(await evaluate("location.href"), `${server.origin}/`, "the token left the address");
    assert.equal(await evaluate("localStorage.getItem('alta.token')"), "key1", "the phone keeps the key the computer gave it, not the pairing token");
    assert.deepEqual(server.requests.filter(r => r.path === "/api/login").map(r => r.body), [{ token: "secret" }]);
    assert.deepEqual(server.requests.filter(r => r.query.includes("secret")).map(r => r.path), ["/"], "only the link itself holds the token");

    // What else the address holds stays: the session it names opens.
    await command("Page.navigate", { url: `${server.origin}/?k=${server.state.token}#/session/s1` });
    await waitFor(`document.getElementById("detail")?.hidden === false`, "the session of the link");
    assert.equal(await evaluate("location.href"), `${server.origin}/#/session/s1`);
    assert.deepEqual(phone.failures, []);
  } finally { await phone.close(); }
});
