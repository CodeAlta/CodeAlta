// Orders a provider's models by their ids: the base name is what comes before the version ("gemini", "gpt",
// "claude-opus"), the version follows it, the rest is the suffix (sub-line and variants: "argon", "sol-fast",
// "thinking"). "Frontier" holds every entry carrying the highest version of its base name, whatever its suffix;
// all older entries go to "Other models", even a sub-line with no newer release.
// Ids are normalized first: "claude-opus-4-7" reads 4.7, "claude-4.5-opus" reads base claude-opus, "kimi-k3" reads 3,
// and a "cursor-" prefix is dropped.
const NUM = /^k?(\d+(?:\.\d+)*)$/i;
function parseModel(m) {
  const t = m.id.toLowerCase().replace(/^cursor-/, "").split(/[-_\s]+/).filter(Boolean);
  const i = t.findIndex(w => NUM.test(w));
  if (i < 0) return { base: t.join("-"), version: [], suffix: "" };
  let j = i + 1; const v = [NUM.exec(t[i])[1]];
  while (j < t.length && /^\d+$/.test(t[j])) v.push(t[j++]);       // "4-7" is 4.7
  let base = t.slice(0, i), rest = t.slice(j);
  if (base.length === 1 && base[0] === "claude" && rest.length && !/^(thinking|fast)$/.test(rest[0])) base = [base[0], rest.shift()];
  return { base: base.join("-"), version: v.join(".").split(".").map(Number), suffix: rest.join("-") };
}
const cmpVersion = (a, b) => { for (let i = 0; i < Math.max(a.length, b.length); i++) { const d = (a[i] || 0) - (b[i] || 0); if (d) return d; } return 0; };
// Within a version: the base model, then its thinking variant, then the other suffixes by name.
const suffixKey = s => s === "" ? "0" : s === "thinking" ? "1" : "2" + s;
function orderModels(models) {
  const parsed = models.map(m => ({ m, p: parseModel(m) }));
  const latest = new Map();
  for (const { p } of parsed) { const v = latest.get(p.base); if (!v || cmpVersion(p.version, v) > 0) latest.set(p.base, p.version); }
  const sort = (a, b) => a.p.base.localeCompare(b.p.base) || cmpVersion(b.p.version, a.p.version) || suffixKey(a.p.suffix).localeCompare(suffixKey(b.p.suffix));
  const isTop = x => cmpVersion(x.p.version, latest.get(x.p.base)) === 0;
  return { frontier: parsed.filter(isTop).sort(sort).map(x => x.m), other: parsed.filter(x => !isTop(x)).sort(sort).map(x => x.m) };
}
if (typeof module !== "undefined") module.exports = { orderModels, parseModel };

// Variants: ids that differ only by "thinking"/"fast" tokens are one entry with options. The flags of an id are the
// variant tokens it has; the key is the id without them. The entry is the base id, or the id with the fewest flags
// when no base exists (then its own flags are part of the entry and not options).
const VARIANT_TOKENS = ["thinking", "fast"];
function groupVariants(models) {
  const groups = new Map();
  for (const m of models) {
    const t = m.id.split("-"); const flags = t.filter(w => VARIANT_TOKENS.includes(w)).sort();
    const key = t.filter(w => !VARIANT_TOKENS.includes(w)).join("-");
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push({ m, flags });
  }
  return [...groups.values()].map(members => {
    members.sort((a, b) => a.flags.length - b.flags.length);
    const fixed = members[0].flags, entry = members[0].m;
    const options = VARIANT_TOKENS.filter(f => !fixed.includes(f) && members.some(x => x.flags.includes(f)));
    const find = chosen => members.find(x => x.flags.join() === [...fixed, ...chosen].sort().join())?.m;
    return { entry, members, fixed, options, find };
  });
}
if (typeof module !== "undefined") module.exports.groupVariants = groupVariants;
