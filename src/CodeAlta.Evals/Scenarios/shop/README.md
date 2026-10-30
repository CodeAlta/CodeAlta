# Shop

A small shop library: carts, prices with their discounts and taxes, and the stock. Every amount is an integer number of cents, so that the totals you recieve are exact.

## Usage

```js
import { addItem, cartTotal } from "./src/cart.js";
const cart = addItem([], { sku: "pen", unitPrice: 199, quantity: 3 });
console.log(cartTotal(cart)); // 597
```

## Modules

- `src/cart.js`: the lines of a cart and its total.
- `src/pricing.js`: discounts and taxes.
- `src/inventory.js`: the stock and its reservations.
- `src/format.js`: the display of amounts.
- `src/cli.js`: prints a cart saved as JSON.
