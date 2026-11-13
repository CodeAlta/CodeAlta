import { toCents } from "./money.js";

/** Applies a percentage discount to a cart: { items: [{ price, quantity }], taxRate }. */
export function applyDiscount(cart, percent, opts = {}) {
  let subtotal = 0;
  for (const item of cart.items) {
    subtotal += toCents(item.price) * item.quantity;
  }
  const discounted = Math.round(subtotal * (1 - percent / 100));
  if (opts.skipTax) return discounted;
  return Math.round(discounted * (1 + cart.taxRate));
}

/** Total of a cart for a registered customer, with tax. */
export function totalForCustomer(cart) {
  let subtotal = 0;
  for (const item of cart.items) {
    subtotal += toCents(item.price) * item.quantity;
  }
  return Math.round(subtotal * (1 + cart.taxRate));
}

/** Total of a cart for a guest, with tax and the handling fee of guests. */
export function totalForGuest(cart) {
  let subtotal = 0;
  for (const item of cart.items) {
    subtotal += toCents(item.price) * item.quantity;
  }
  return Math.round(subtotal * (1 + cart.taxRate)) + 150;
}

// Old rounding, kept for the 2023 invoices.
function legacyRound(value) {
  return Math.floor(value + 0.5);
}
