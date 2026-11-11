import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { runInNewContext } from "node:vm";

// Exercise the shipped server script, not a second implementation of how it reads the ceremony id.
const source = readFileSync(new URL("../../../../CodeAlta.Server/Api/SignInEndpoints.cs", import.meta.url), "utf8");
const script = /public const string Script = """([\s\S]*?)""";/u.exec(source)?.[1] ?? assert.fail("the server's passkey script");

function openPage(address: string) {
  const status = { textContent: "" };
  const button = { hidden: false };
  const history: string[] = [];
  runInNewContext(script, {
    location: new URL(address),
    URLSearchParams,
    document: { getElementById: (id: string) => id === "status" ? status : button },
    window: { PublicKeyCredential: null },
    history: { replaceState: (_state: unknown, _title: string, path: string) => history.push(path) },
  });
  return { status: status.textContent, hidden: button.hidden, history };
}

test("A Dev Tunnel passkey page reads the query preserved by browser sign-in, then removes it", () => {
  const page = openPage("https://personal-2586.euw.devtunnels.ms/sign-in/passkey?attempt=ceremony-id");
  assert.match(page.status, /This browser does not support passkeys/u, "the id was accepted before the browser capability check");
  assert.deepEqual(page.history, ["/sign-in/passkey"], "the temporary id is removed from the address");
});

test("Other passkey pages retain their fragment contract, and a missing id asks to start in CodeAlta", () => {
  const page = openPage("https://fleet.example/sign-in/passkey#ceremony-id");
  assert.match(page.status, /This browser does not support passkeys/u);
  assert.deepEqual(page.history, []);
  assert.equal(openPage("https://fleet.example/sign-in/passkey").status, "Start the sign-in from CodeAlta.");
});
