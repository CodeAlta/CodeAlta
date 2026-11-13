// Amounts are integers in cents everywhere; these helpers convert at the edges.

/** Converts an amount as typed or exported ("12.50", 12.5) to cents. */
export function toCents(amount) {
  return Math.round(Number.parseFloat(amount) * 100);
}

/** Formats cents as "12.50". */
export function formatCents(cents) {
  return (cents / 100).toFixed(2);
}
