import assert from "node:assert/strict";
import test from "node:test";
import { canSaveRecovery, recoveryDetail, recoveryStatus } from "./configRecovery";
import { maximumConfigLength } from "./configEditor";

const file = { status: "ok", failure: null };
const invalid = { valid: false, line: 9, column: 20, message: "C:\\Users\\me\\.alta\\config.toml(9,20) : error : Unexpected token `high` while parsing a value." };

test("the status says one thing: the file, the save, the check, then its result", () => {
  assert.equal(recoveryStatus(null, "", null, null).key, "Reading configuration…");
  assert.deepEqual(recoveryStatus({ status: "unreadable", failure: "Access denied." }, "", null, null),
    { intent: "danger", key: "The configuration file could not be read.", detail: "Access denied.", position: null });
  assert.equal(recoveryStatus({ status: "too_large", failure: null }, "", null, null).key, "The configuration is too large for this editor.");
  assert.equal(recoveryStatus(file, "x".repeat(maximumConfigLength + 1), invalid, null).key, "The configuration is too large for this editor.");
  // A save that did not go through is said before what the check found.
  assert.deepEqual(recoveryStatus(file, "a = 1", { valid: true, line: null, column: null, message: null }, "Config changed on disk."),
    { intent: "danger", key: "The configuration was not saved.", detail: "Config changed on disk.", position: null });
  assert.equal(recoveryStatus(file, "a = 1", null, null).key, "Checking the configuration…");
  assert.deepEqual(recoveryStatus(file, "a = 1", { valid: true, line: null, column: null, message: null }, null),
    { intent: "success", key: "The configuration is valid.", detail: null, position: null });
  assert.deepEqual(recoveryStatus(file, "a = high", invalid, null), {
    intent: "danger", key: "Line {line}, column {column}", parameters: { line: 9, column: 20 },
    detail: "Unexpected token `high` while parsing a value.", position: { line: 9, column: 20 },
  });
  // A problem without a place in the file (a value the application does not accept).
  assert.deepEqual(recoveryStatus(file, "a = 1", { valid: false, line: null, column: null, message: "Unknown provider type." }, null),
    { intent: "danger", key: "Invalid configuration", detail: "Unknown provider type.", position: null });
});

test("diagnostics lose the path the screen already shows", () => {
  assert.equal(recoveryDetail(null), null);
  assert.equal(recoveryDetail("  "), null);
  assert.equal(recoveryDetail("Unknown provider type."), "Unknown provider type.");
  assert.equal(recoveryDetail(invalid.message), "Unexpected token `high` while parsing a value.");
  const several = "C:\\a\\config.toml(9,27) : error : Invalid newline in a string\r\nC:\\a\\config.toml(10,1) : error : Invalid newline in a string\r\nC:\\a\\conf";
  assert.equal(recoveryDetail(several), "Invalid newline in a string\n10:1 Invalid newline in a string");
});

test("only a read file with valid text is saved", () => {
  const valid = { valid: true, line: null, column: null, message: null };
  assert.equal(canSaveRecovery(file, "a = 1", valid, false), true);
  assert.equal(canSaveRecovery(file, "a = 1", valid, true), false);
  assert.equal(canSaveRecovery(file, "a = 1", null, false), false);
  assert.equal(canSaveRecovery(file, "a = high", invalid, false), false);
  assert.equal(canSaveRecovery({ status: "unreadable" }, "a = 1", valid, false), false);
  assert.equal(canSaveRecovery(null, "a = 1", valid, false), false);
  assert.equal(canSaveRecovery(file, "x".repeat(maximumConfigLength + 1), valid, false), false);
});
