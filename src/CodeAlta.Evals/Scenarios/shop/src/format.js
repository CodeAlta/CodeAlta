// Formats an amount of cents as a price: 1234 gives "12.34 EUR".
export function formatPrice(cents, currency = "EUR") {
  const sign = cents < 0 ? "-" : "";
  const abs = Math.abs(cents);
  return `${sign}${Math.floor(abs / 100)}.${String(abs % 100).padStart(2, "0")} ${currency}`;
}
