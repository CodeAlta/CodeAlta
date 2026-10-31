import { convert } from "./currency.js";
import { formatPrice } from "./format.js";

// Sums orders ({ id, amount (cents), currency }) in one currency, each order converted and rounded to the cent.
export function summarize(orders, currency) {
  const total = orders.reduce((sum, order) => sum + convert(order.amount, { from: order.currency, to: currency }), 0);
  return { count: orders.length, total, formatted: formatPrice(total, currency) };
}
