// Prints a cart saved as JSON: node src/cli.js <cart.json>
import { readFileSync } from "node:fs";
import { describeCart } from "./cart.js";

const args = process.argv.slice(2);
const file = args.find((arg) => !arg.startsWith("--"));
if (!file) {
  console.error("usage: node src/cli.js <cart.json>");
  process.exit(2);
}

const cart = JSON.parse(readFileSync(file, "utf8"));
console.log(describeCart(cart));
