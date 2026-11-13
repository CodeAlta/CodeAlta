import { applyDiscount, totalForCustomer, totalForGuest } from "./cart.js";

/** Amount to charge, in cents: coupons apply to registered customers only. */
export function amountDue(cart, customer, coupon) {
  if (!customer) return totalForGuest(cart);
  if (!coupon) return totalForCustomer(cart);
  return applyDiscount(cart, coupon.percent, { skipTax: customer.exempt === true });
}
