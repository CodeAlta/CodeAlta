import test from "node:test";
import assert from "node:assert/strict";
import { providerSummary } from "./providerSummary";
import type { ModelCatalogProvidersResponse } from "#neoastra";

test("provider summary counts readiness, not configured names, and flags failures", () => {
  const page: ModelCatalogProvidersResponse = { status: "ok", epoch: "epoch", truncated: false,
    providers: ["Ready", "Ready", "Failed", "Unsupported", "Unknown"].map((availability, index) => ({
      id: String(index), name: "provider", enabled: true, availability, type: "test", isDefault: false, defaultModel: null, observedAt: null,
    })) };
  assert.deepEqual(providerSummary(page, "epoch"), { ready: 2, errors: 2, detecting: true });
  assert.deepEqual(providerSummary({ ...page, providers: page.providers.slice(0, 2) }, "epoch"), { ready: 2, errors: 0, detecting: false });
  assert.deepEqual(providerSummary({ ...page, providers: page.providers.map(provider => ({ ...provider, enabled: false })) }, "epoch"), { ready: 0, errors: 0, detecting: false });
  assert.deepEqual(providerSummary(page, "other"), { ready: 0, errors: 0, detecting: true });
});
