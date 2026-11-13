import { toCents, formatCents } from "./money.js";

/** Totals the lines of an invoice: [{ label, amount }], with amounts as the ERP exports them ("1,234.50"). */
export function invoiceTotal(lines) {
  let total = 0;
  for (const line of lines) {
    // toCents stops at the thousands separator ("1,234.50" gives 100): strip it first.
    total += toCents(String(line.amount).replace(/,/g, ""));
  }
  return formatCents(total);
}
