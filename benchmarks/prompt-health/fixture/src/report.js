import { toCents, formatCents } from "./money.js";

/** Sums the sales of a month by category: rows are { category, amount }. */
export function monthlyReport(rows) {
  const totals = new Map();
  for (const row of rows) {
    totals.set(row.category, (totals.get(row.category) ?? 0) + toCents(row.amount));
  }
  return [...totals].map(([category, cents]) => ({ category, total: formatCents(cents) }));
}
