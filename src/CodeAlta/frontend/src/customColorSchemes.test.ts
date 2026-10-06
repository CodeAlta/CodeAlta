import assert from "node:assert/strict";
import test from "node:test";
import { colorSchemeOf, schemePalette, type CustomColorScheme } from "./colorSchemes";
import { copyName, draftScheme, sameScheme, schemeNameProblem, schemeSaveRequest, shownColors, withColor } from "./customColorSchemes";

const night: CustomColorScheme = { id: "night", name: "Night", base: "plum", light: {}, dark: { background: "#101418" }, darker: {} };
const english = (name: string) => `${name} copy`;

test("a draft starts from a built-in scheme as its base, or copies one of the user's", () => {
  assert.deepEqual(draftScheme(colorSchemeOf("kiwi"), "Kiwi copy"), { id: "", name: "Kiwi copy", base: "kiwi", light: {}, dark: {}, darker: {} });
  assert.deepEqual(draftScheme(night, "Night copy"), { ...night, id: "", name: "Night copy" });
  // A new draft looks like what it was made from.
  assert.deepEqual(schemePalette(draftScheme(colorSchemeOf("kiwi"), "K"), "dark"), colorSchemeOf("kiwi").dark);
});

test("the colors shown are the chosen ones and, for the rest, those of what the scheme follows", () => {
  const plum = colorSchemeOf("plum");
  assert.equal(shownColors(night, "dark").background, "#101418");
  assert.equal(shownColors(night, "dark").accent, plum.dark["blue-4"]);
  assert.equal(shownColors(night, "light").background, plum.light["light-gray-5"]);
  assert.equal(shownColors(plum, "light").text, plum.light["dark-gray-1"]);
  // The darker theme follows the dark one, with a darker background.
  assert.notEqual(shownColors(night, "darker").background, "#101418");
  assert.equal(shownColors(night, "darker").accent, plum.dark["blue-4"]);
});

test("a color is chosen, changed and left again to what it follows", () => {
  const chosen = withColor(night, "dark", "accent", "#FF8800");
  assert.deepEqual(chosen.dark, { background: "#101418", accent: "#ff8800" });
  assert.deepEqual(withColor(chosen, "dark", "accent", "#abc").dark, { background: "#101418", accent: "#aabbcc" });
  assert.deepEqual(withColor(chosen, "dark", "accent", null).dark, { background: "#101418" });
  assert.deepEqual(withColor(chosen, "darker", "text", "#e0e0e0").darker, { text: "#e0e0e0" });
  // Choosing the color that would show anyway chooses nothing, and what is not a color resets it.
  assert.deepEqual(withColor(night, "dark", "accent", shownColors(night, "dark").accent).dark, { background: "#101418" });
  assert.deepEqual(withColor(chosen, "dark", "accent", "orange").dark, { background: "#101418" });
  // For the darker theme too: the color its dark theme gives it is not a choice.
  assert.deepEqual(withColor(chosen, "darker", "accent", "#ff8800").darker, {});
  assert.equal(night.dark.accent, undefined);
});

test("two schemes are the same whatever the order their colors were chosen in", () => {
  const left = withColor(withColor(night, "dark", "accent", "#ff8800"), "dark", "text", "#eeeeee");
  const right = withColor(withColor(night, "dark", "text", "#eeeeee"), "dark", "accent", "#ff8800");
  assert.ok(sameScheme(left, right));
  assert.ok(sameScheme(night, { ...night, name: " Night " }));
  assert.ok(!sameScheme(night, left));
  assert.ok(!sameScheme(night, { ...night, name: "Day" }));
  assert.ok(!sameScheme(night, { ...night, base: "kiwi" }));
});

test("a scheme needs a name that no other scheme has", () => {
  assert.equal(schemeNameProblem({ id: "", name: "  " }, []), "Enter a name for the color scheme.");
  assert.equal(schemeNameProblem({ id: "", name: "x".repeat(65) }, []), "The name of a color scheme has at most 64 characters.");
  assert.equal(schemeNameProblem({ id: "", name: "x".repeat(64) }, []), null);
  // Not that of a built-in scheme nor of another of the user's, whatever the case.
  assert.equal(schemeNameProblem({ id: "", name: "cherry" }, []), "A color scheme with this name already exists.");
  assert.equal(schemeNameProblem({ id: "", name: " NIGHT " }, [night]), "A color scheme with this name already exists.");
  // Its own name is not taken.
  assert.equal(schemeNameProblem({ id: "night", name: "Night" }, [night]), null);
  assert.equal(schemeNameProblem({ id: "", name: "Nights" }, [night]), null);
});

test("a copy is named after its source, then numbered", () => {
  assert.equal(copyName("Plum", english, []), "Plum copy");
  assert.equal(copyName("Plum", english, [{ ...night, name: "plum COPY" }]), "Plum copy 2");
  assert.equal(copyName("Plum", english, [{ ...night, name: "Plum copy" }, { ...night, id: "b", name: "Plum copy 2" }]), "Plum copy 3");
  // The copy of a copy is the next copy of the same scheme; a name that only ends with a number keeps it.
  assert.equal(copyName("Plum copy", english, [{ ...night, name: "Plum copy" }]), "Plum copy 2");
  assert.equal(copyName("Plum copy 2", english, [{ ...night, name: "Plum copy" }, { ...night, id: "b", name: "Plum copy 2" }]), "Plum copy 3");
  assert.equal(copyName("Solarized 2", english, []), "Solarized 2 copy");
  // In the wording of another language.
  const french = (name: string) => `${name} (copie)`;
  assert.equal(copyName("Prune", french, []), "Prune (copie)");
  assert.equal(copyName("Prune (copie)", french, [{ ...night, name: "Prune (copie)" }]), "Prune (copie) 2");
  // A long name gives way to the suffix.
  const long = copyName("n".repeat(64), english, []);
  assert.ok(long.length <= 64 && long.endsWith(" copy"), long);
});

test("a scheme is saved with every color named, and as a new one while it has no id", () => {
  const request = schemeSaveRequest({ ...night, id: "", name: " Night ", dark: { background: "#101418", accent: "#ff8800" } });
  assert.equal(request.id, null);
  assert.equal(request.name, "Night");
  assert.equal(request.base, "plum");
  assert.deepEqual(request.dark, { background: "#101418", text: null, muted: null, accent: "#ff8800", success: null, warning: null, danger: null });
  assert.deepEqual(request.light, { background: null, text: null, muted: null, accent: null, success: null, warning: null, danger: null });
  assert.equal(schemeSaveRequest(night).id, "night");
});
