import { readFileSync } from "node:fs";
import { join } from "node:path";

const DEFAULTS = { requestTimeout: 30, currency: "EUR" };

/** Seconds of a timeout given as a number of seconds, or as a duration such as "45s" or "2m". */
export function parseTimeout(value) {
  if (typeof value === "number") return check(value);
  const match = /^(\d+)(s|m)$/.exec(String(value).trim());
  if (!match) throw new TypeError(`Invalid requestTimeout: ${value}`);
  return check(Number(match[1]) * (match[2] === "m" ? 60 : 1));
}

function check(seconds) {
  if (!Number.isFinite(seconds) || seconds < 0) throw new RangeError("requestTimeout must not be negative");
  return seconds;
}

/** Reads shopkit.config.json of a folder, with the defaults for what it leaves out. */
export function loadConfig(folder = process.cwd()) {
  let file = {};
  try {
    file = JSON.parse(readFileSync(join(folder, "shopkit.config.json"), "utf8"));
  } catch (error) {
    if (error.code !== "ENOENT") throw error;
  }

  const config = { ...DEFAULTS, ...file };
  config.requestTimeout = parseTimeout(config.requestTimeout);
  return config;
}
