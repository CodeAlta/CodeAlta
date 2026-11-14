import assert from "node:assert/strict";
import test from "node:test";
import { linksChoice, profileForBrowser } from "./spaceLinksChoice";

const chrome = { id: "chrome", name: "Google Chrome", supportsProfiles: true, profiles: ["Privat", "Work"], isDefault: true };
const firefox = { id: "firefox", name: "Firefox", supportsProfiles: true, profiles: ["default-release"], isDefault: false };
const safari = { id: "safari", name: "Safari", supportsProfiles: false, profiles: [], isDefault: false };
const browsers = [chrome, firefox, safari];

test("without a browser, the links open in the default one, whose profiles can be chosen", () => {
  const choice = linksChoice({ browser: "", profile: "work" }, browsers);
  assert.equal(choice.defaultBrowser, chrome);
  assert.equal(choice.target, chrome);
  assert.equal(choice.profileSelectable, true);
  assert.equal(choice.profile, "Work", "A profile is matched whatever its case, and shown as the browser writes it.");
  assert.equal(choice.profileMissing, false);
});

test("a profile the chosen browser does not have is said, and kept", () => {
  const choice = linksChoice({ browser: "firefox", profile: "Work" }, browsers);
  assert.equal(choice.target, firefox);
  assert.equal(choice.profile, "Work");
  assert.equal(choice.profileMissing, true);
});

test("Safari takes no profile, and a profile kept for another browser is not said to be missing", () => {
  const choice = linksChoice({ browser: "safari", profile: "Work" }, browsers);
  assert.equal(choice.target, safari);
  assert.equal(choice.profileSelectable, false);
  assert.equal(choice.profileMissing, false);
});

test("a browser that lists no profile, whose list the system keeps out of reach, has its profile typed", () => {
  const hidden = { ...chrome, profiles: [] };
  const choice = linksChoice({ browser: "", profile: "Profile 1" }, [hidden]);
  assert.equal(choice.profilesUnreadable, true);
  assert.equal(choice.profileMissing, false, "A typed profile cannot be checked.");
  assert.equal(linksChoice({ browser: "safari", profile: "" }, browsers).profilesUnreadable, false);
  assert.equal(choice.profileNotFolder, false);
  assert.equal(linksChoice({ browser: "", profile: "Marmind" }, [hidden]).profileNotFolder, true, "Chrome is given a folder, never a name that would make a new profile.");
  assert.equal(linksChoice({ browser: "firefox", profile: "Marmind" }, [{ ...firefox, profiles: [] }]).profileNotFolder, false, "Firefox finds a profile by its name.");
});

test("a browser this computer does not have is said, and its profile stays to see", () => {
  const choice = linksChoice({ browser: "brave", profile: "Work" }, browsers);
  assert.equal(choice.target, null);
  assert.equal(choice.browserMissing, true);
  assert.equal(choice.profileSelectable, true);
  assert.equal(choice.profileMissing, true);
  assert.equal(linksChoice({ browser: "", profile: "" }, []).target, null, "A default browser that cannot be told is none.");
});
test("a profile stays with a new browser only when that browser has it", () => {
  assert.equal(profileForBrowser("Work", "firefox", browsers), "", "Firefox has no Work: its links open without a profile, never with a name it does not know.");
  assert.equal(profileForBrowser("work", "", browsers), "Work", "The default browser, Chrome, has it.");
  assert.equal(profileForBrowser("Profile 1", "firefox", [{ ...firefox, profiles: [] }]), "", "A list out of reach keeps nothing either.");
  assert.equal(profileForBrowser("", "chrome", browsers), "");
});
