import { parseDay, addDays, formatDay } from "./dates.js";

// The seven days of the week that starts on a Monday, written YYYY-MM-DD.
export function weekDays(monday) {
  const start = parseDay(monday);
  return Array.from({ length: 7 }, (_, index) => formatDay(addDays(start, index)));
}

// The day a delivery ordered on a day arrives, after a number of days.
export function deliveryDay(ordered, days) {
  return formatDay(addDays(parseDay(ordered), days));
}
