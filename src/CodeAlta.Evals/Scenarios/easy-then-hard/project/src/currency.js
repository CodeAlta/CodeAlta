import { memoize } from "./util.js";

// Exchange rates against the euro.
const rates = { EUR: 1, USD: 1.086, GBP: 0.842, CHF: 0.947 };

// Converts an amount of cents from a currency to another, rounded half up to the cent.
export const convert = memoize(function convert(cents, { from, to }) {
  if (!(from in rates) || !(to in rates)) throw new Error(`unknown currency: ${from} or ${to}`);
  return Math.round((cents / rates[from]) * rates[to]);
});
