import assert from "node:assert/strict";
import test from "node:test";
import { createReleaseNotesImages, isReleaseVersion, matchesRelease, readReleaseNotesPage, readReleaseNotesPreferences, releaseNotesStorageKey, sameVersion,
  startupReleaseNotes } from "./releaseNotes";

test("the page of Settings' requests that opens the release notes names a version or the running one", () => {
  assert.equal(readReleaseNotesPage("release-notes"), null);
  assert.equal(readReleaseNotesPage("release-notes/4.1.0"), "4.1.0");
  assert.equal(readReleaseNotesPage("release-notes/ "), null);
  assert.equal(readReleaseNotesPage("appearance"), undefined);
});

test("the release notes open at startup once after an update, never at the first run, and not when hidden", () => {
  const first = startupReleaseNotes({ hideOnStartup: false, lastVersion: null }, "4.1.0");
  assert.deepEqual(first, { show: false, next: { hideOnStartup: false, lastVersion: "4.1.0" } });
  const updated = startupReleaseNotes(first.next, "4.2.0");
  assert.deepEqual(updated, { show: true, next: { hideOnStartup: false, lastVersion: "4.2.0" } });
  assert.equal(startupReleaseNotes(updated.next, "v4.2.0").show, false, "the same version opens nothing again");
  assert.deepEqual(startupReleaseNotes({ hideOnStartup: true, lastVersion: "4.1.0" }, "4.2.0"), { show: false, next: { hideOnStartup: true, lastVersion: "4.2.0" } });
  assert.equal(startupReleaseNotes({ hideOnStartup: false, lastVersion: "4.1.0" }, "development").show, false);
});

test("a build of the repository and the developer instance open nothing at startup, and leave the last release as it was", () => {
  const released = { hideOnStartup: false, lastVersion: "4.0.0" };
  // A build of a commit after a tag (MinVer): its version changes at each commit, which is no update of CodeAlta.
  assert.deepEqual(startupReleaseNotes(released, "4.0.1-alpha.0.85+596d79282a1377abbb6c6c55e4a8daa86921f8c0"), { show: false, next: released });
  assert.deepEqual(startupReleaseNotes(released, "4.0.1-alpha.0.85"), { show: false, next: released });
  // The developer instance (--dev) is started to work on CodeAlta, whatever its build.
  assert.deepEqual(startupReleaseNotes(released, "4.1.0", true), { show: false, next: released });
  // A pre-release that was tagged is a version people install: it opens them.
  assert.equal(startupReleaseNotes(released, "4.1.0-beta.1+abc").show, true);
  assert.equal(startupReleaseNotes(released, "4.1.0", false).show, true);
});

test("the preferences read anything malformed as the default", () => {
  const read = (value: string | null) => readReleaseNotesPreferences(key => key === releaseNotesStorageKey ? value : null);
  assert.deepEqual(read(null), { hideOnStartup: false, lastVersion: null });
  assert.deepEqual(read("{"), { hideOnStartup: false, lastVersion: null });
  assert.deepEqual(read(JSON.stringify({ hideOnStartup: true, lastVersion: "4.1.0" })), { hideOnStartup: true, lastVersion: "4.1.0" });
});

test("versions compare without their v, and the search finds a version or a title", () => {
  assert.ok(sameVersion("v4.1.0", "4.1.0"));
  assert.ok(!sameVersion("4.1.0", "4.1.1"));
  assert.ok(isReleaseVersion("4.1.0") && isReleaseVersion("v4.0.1-alpha.0.3") && !isReleaseVersion("development") && !isReleaseVersion(""));
  const release = { version: "4.1.0", name: "4.1.0 Fleet and side questions" };
  assert.ok(matchesRelease("v4.1", release));
  assert.ok(matchesRelease("fleet SIDE", release));
  assert.ok(!matchesRelease("4.2", release));
});

test("pictures are asked of the host for GitHub and the site of CodeAlta only, and kept", async () => {
  const asked: string[] = [];
  const images = createReleaseNotesImages(async request => { asked.push(request.address); return { status: "ok", mediaType: "image/png", data: "UE5H" }; });
  const address = "https://raw.githubusercontent.com/CodeAlta/CodeAlta/4.0.0/site/img/a.png";
  assert.equal(images.peek(address, null), undefined, "not read yet");
  const image = await images.load(address, null);
  assert.equal(image?.path, address);
  assert.equal(image?.url, "data:image/png;base64,UE5H");
  assert.ok(images.peek(address, null), "kept");
  assert.equal(images.peek("https://example.com/a.png", null), null);
  assert.equal(await images.load("http://github.com/a.png", null), null);
  assert.deepEqual(asked, [address]);
  // The pictures of the site are often SVG: drawn as well; what is no picture is not.
  const svg = createReleaseNotesImages(async () => ({ status: "ok", mediaType: "image/svg+xml", data: "PHN2Zy8+" }));
  assert.equal((await svg.load("https://raw.githubusercontent.com/a.svg", null))?.url, "data:image/svg+xml;base64,PHN2Zy8+");
  const html = createReleaseNotesImages(async () => ({ status: "ok", mediaType: "text/html", data: "PGI+" }));
  assert.equal(await html.load("https://codealta.io/a", null), null);
});
