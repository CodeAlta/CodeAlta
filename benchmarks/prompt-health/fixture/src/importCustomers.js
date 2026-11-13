import { parse } from "./vendor/csvlite.js";

/** Reads the customers of a warehouse export: "id,name,email" lines after a header. */
export function importCustomers(text) {
  return parse(text).slice(1).map(([id, name, email]) => ({ id, name, email }));
}
