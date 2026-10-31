import { applyDiscount } from "./pricing.js";
import { formatPrice } from "./format.js";

// A cart is a list of lines: { sku, unitPrice (cents), quantity, discount (percent, optional) }.

// Adds an item to a cart: a new cart, where a line of the same sku gets the quantity added.
export function addItem(cart, item) {
  if (!Number.isInteger(item.quantity) || item.quantity <= 0) throw new RangeError(`bad quantity: ${item.quantity}`);
  const existing = cart.find((line) => line.sku === item.sku);
  if (!existing) return [...cart, { ...item }];
  return cart.map((line) => (line.sku === item.sku ? { ...line, quantity: line.quantity + item.quantity } : line));
}

// Removes the line of a sku from a cart: a new cart.
export function removeItem(cart, sku) {
  return cart.filter((line) => line.sku !== sku);
}

// The total of a cart in cents, each line after its own discount.
export function cartTotal(cart) {
  return cart.reduce((sum, line) => sum + applyDiscount(line.unitPrice * line.quantity, line.discount ?? 0), 0);
}

// A text with one row per line and the total.
export function describeCart(cart) {
  const rows = cart.map((line) => `${line.quantity} x ${line.sku}: ${formatPrice(applyDiscount(line.unitPrice * line.quantity, line.discount ?? 0))}`);
  return [...rows, `Total: ${formatPrice(cartTotal(cart))}`].join("\n");
}
