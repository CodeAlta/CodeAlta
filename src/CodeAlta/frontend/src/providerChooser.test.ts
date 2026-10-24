import test from "node:test";
import assert from "node:assert/strict";
import { providerSelectionMessage } from "./ProviderChooser";
import { locales, translate } from "./localization";

test("a switch refused for a queued prompt or a turn that did not end says what to do, in every language", () => {
  for (const status of ["queue_pending", "turn_unfinished"]) {
    const message = providerSelectionMessage(status);
    assert.ok(message, status);
    for (const locale of locales.filter(value => value !== "en")) assert.notEqual(translate(locale, message), message, `${locale}: ${status}`);
  }
  assert.match(providerSelectionMessage("queue_pending")!, /queued/);
  // The other statuses keep the generic message that names them.
  for (const status of ["selection_unconfirmed", "busy", "stale_selection", "interaction_pending"]) assert.equal(providerSelectionMessage(status), null, status);
});
