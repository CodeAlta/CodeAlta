import { parse } from "./vendor/csvlite.js";
import { toCents } from "./money.js";

/** Reads the orders of a warehouse export: "id,customer,amount" lines after a header. */
export function importOrders(text) {
  // csvlite 1.4.2 drops the last row when the text does not end with a new line.
  const rows = parse(text.endsWith("\n") ? text : text + "\n");
  return rows.slice(1).map(([id, customer, amount]) => ({ id, customer, cents: toCents(amount) }));
}
