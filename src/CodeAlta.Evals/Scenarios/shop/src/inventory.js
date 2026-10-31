// The stock is an object of quantities by sku. Every function returns a new stock.

export function available(stock, sku) {
  return stock[sku] ?? 0;
}

export function reserve(stock, sku, quantity) {
  if (available(stock, sku) < quantity) throw new Error(`not enough ${sku}: ${available(stock, sku)} left, ${quantity} asked`);
  return { ...stock, [sku]: available(stock, sku) - quantity };
}

export function release(stock, sku, quantity) {
  return { ...stock, [sku]: available(stock, sku) + quantity };
}
