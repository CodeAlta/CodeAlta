import assert from "node:assert/strict";
import test from "node:test";
import { diagnosticRequestId, rpcFailureCode } from "./rpcDiagnostics";

test("diagnostics expose only known framework codes and opaque UUID request IDs", () => {
  assert.equal(rpcFailureCode({ code: "duplicate_request", message: "private prompt" }), "duplicate_request");
  assert.equal(rpcFailureCode({ code: "too_many_requests" }), "too_many_requests");
  assert.equal(rpcFailureCode({ code: "secret-value", args: "private prompt" }), "transport_or_client_failure");
  assert.equal(rpcFailureCode(new Error("private prompt")), "transport_or_client_failure");
  assert.equal(diagnosticRequestId("secret-value"), undefined);
  assert.equal(diagnosticRequestId("12345678-1234-1234-1234-123456789abc"), "12345678-1234-1234-1234-123456789abc");
});
