// Applies a discount in percent to an amount of cents, rounded half up to the cent.
export function applyDiscount(cents, percent) {
  if (percent < 0 || percent > 100) throw new RangeError(`discount out of range: ${percent}`);
  return Math.round((cents * (100 - percent)) / 100);
}

const rates = { FR: 20, DE: 19, LU: 17, CH: 8.1 };

// The rate of the tax of a region, in percent.
export function taxFor(region) {
  const rate = rates[region];
  if (rate === undefined) throw new Error(`unknown region: ${region}`);
  return rate;
}

// An amount of cents with the tax of a region, rounded half up to the cent.
export function withTax(cents, region) {
  return Math.round((cents * (100 + taxFor(region))) / 100);
}
