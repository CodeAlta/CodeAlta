// Days are Date objects at midnight; a day of the calendar is written YYYY-MM-DD.

// The day of a date written YYYY-MM-DD.
export function parseDay(text) {
  const [year, month, day] = text.split("-").map(Number);
  return new Date(year, month - 1, day);
}

// The day n days after (or before, n < 0) a day.
export function addDays(day, n) {
  return new Date(day.getTime() + n * 24 * 60 * 60 * 1000);
}

// A day written YYYY-MM-DD.
export function formatDay(day) {
  return day.toISOString().slice(0, 10);
}
