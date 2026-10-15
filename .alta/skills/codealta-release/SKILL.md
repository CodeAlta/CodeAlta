---
name: codealta-release
description: Release a new version of CodeAlta - review the changes since the last public release, refresh the site pictures, banner, documentation and front page, write the release tour, push main, tag, edit the GitHub release notes, deploy the website and wait for the NuGet packages. Only for the owner of the repository (xoofx, Alexandre Mutel); use it only when he asks for a release.
---

# Releasing CodeAlta

This skill is for the owner of CodeAlta, **xoofx (Alexandre Mutel)**, and nobody else. It pushes to `main`, pushes a
tag that publishes packages to nuget.org, edits a GitHub release and deploys the public website: every step is
outward-facing and hard to undo.

Run it only when the owner asks for a release in the current conversation, and stop at once if the checks of
step 0 fail. A request for a release is permission for the steps of this skill for that version only.

The steps are in order. Do not skip one silently: when a step does not apply (a small release, nothing changed
in the UI), say so in the report.

## Small and feature releases

- A **feature release** (`X.Y.0`) runs every step below.
- A **small release** is a patch (`X.Y.Z` with `Z` > 0, such as 1.9.1), mostly bug fixes. Unless the owner asks for
  more, it skips what a feature release adds around the code:
  - **Pictures** (step 2): no review of every picture. Retake only a picture that is now wrong because of a fix
    of the release, if any.
  - **Banner** (step 3) and **front page** (step 5): unchanged.
  - **Tour** (step 6): none. The release keeps the notes the release run generates, the list of commits and pull
    requests (step 11).
  - Still done: the review of the changes (step 1), the docs of any behavior the release changes (step 4), the
    local checks (step 7), the push and a green CI (step 8), the issues of its milestone (step 9), the tag, the
    website when a doc changed (step 11), and the NuGet packages (step 12).
- A patch that brings a feature users should hear about is the owner's call: ask whether it gets a short tour.

## 0. Before anything

- `gh api user -q .login` must answer `xoofx`. Anything else: stop, this skill is not for that account.
- The repository is `C:\code\CodeAlta` (or the owner's checkout): on `main`, nothing uncommitted that is not
  part of the release (`git status`), up to date with `origin/main`.
- Pushes go over HTTPS with the credential of `gh`, without touching the git configuration (SSH has no key in
  an agent session):

  ```sh
  git -c credential.helper= -c "credential.helper=!gh auth git-credential" fetch https://github.com/CodeAlta/CodeAlta.git main:refs/remotes/origin/main
  git -c credential.helper= -c "credential.helper=!gh auth git-credential" push https://github.com/CodeAlta/CodeAlta.git main
  ```

- Find the last public release: `gh release list --repo CodeAlta/CodeAlta --limit 5` (the one marked
  `Latest`). The range of the release is `<last tag>..main`.
- Choose the version with the owner's request: a tag is `X.Y.Z`, without `v`. New features give a minor
  version, fixes only give a patch. Nothing in the repository holds the version: the tag is the version.
- Write the owner's request and this checklist in the scratchpad of the session, and keep it up to date: a
  release takes hours and the context may be compacted on the way.

## 1. Review every change since the last public release

- `git log --oneline <last tag>..main` and `git diff --stat <last tag>..main`. Read the commits by area; read
  the code of what the subjects do not explain.
- The milestone of the version: `gh issue list --repo CodeAlta/CodeAlta --milestone <X.Y.Z> --state all`, and
  the pull requests merged in the range (`gh pr list --state merged --search "merged:>=<date of last tag>"`).
  Read the comments of the issues: the owner and the reporters add requirements there.
- Make a list of the features and changes **a user sees**: what is new, what moved or looks different, what
  was fixed. Group the many commits of one feature. Note for each: the issue or PR numbers, the contributors
  (`@login` of a PR author or of an issue reporter that shaped it), the page of the site that describes it,
  and the pictures that show it.
- From that list and the version, say whether this is a **small release** or a **feature release** (see
  [Small and feature releases](#small-and-feature-releases)).
- Any open issue of the milestone: it is either done (close it in step 9) or moved to the next milestone; ask
  the owner when it is not clear which.
- **Dependencies the release pins** (they go before the pictures, since they can change the window):
  - The Claude Code CLI that CodeAlta downloads and runs (`src/CodeAlta.Agent.Claude/ClaudeCodePinnedCli.cs`):
    compare `ClaudeCodePinnedCli.Version` with the latest release
    (`gh release list --repo anthropics/claude-code --limit 5`). A newer one is taken when the owner agrees: the
    version, the size and SHA-256 of every archive (from `SHASUMS256.txt` and the assets of the release), its
    changelog read for changes of the JSON input and output, the Claude Code tests, and one real session with it.
  - NuGet packages in `src/Directory.Packages.props` (NeoAstra above all): no preview or local package left,
    and a newer release of one of the owner's libraries is taken when it fixes something of this release.

## 2. Pictures of the site

The pictures are `site/img/alta-desktop-*.webp`; how to take them is written in
[`doc/site-screenshots.md`](../../../doc/site-screenshots.md): read it whole before the first capture.

- **What to retake:**
  - every picture of a feature that is new or changed in this release;
  - every picture that shows a part of the window that changed for everyone (the title bar, the Explorer,
    the prompt, the tabs, Settings navigation, a theme): when such a part changed, retake all the pictures
    that show it, not only the new ones;
  - list the pictures the site references (`grep -rhoE "alta-desktop-[a-z0-9-]+\.webp" site | sort -u`) and
    compare with `site/img`: a picture no page uses is removed, a page that needs one gets it.
- Captures are taken on the **developer instance** (`alta --dev`, MCP server `codealta-dev`) built from the
  release commit, in the **Dark** theme, at 1600x1000 CSS pixels (2400x1500 pictures), with public projects
  only (a space of public projects), exported as WebP. An isolated profile (`--data-root` ...) starts no
  plugins and has no speech: use it only for what doc/site-screenshots.md says.
- Leave the owner's profile as it was: what a scene creates (spaces, sessions, worktrees, temporary plugins,
  configuration tables) is removed after the capture, and the files it changed are checked against a copy.
- Keep the scene scripts of the release in a folder that survives a restart, such as the ignored `tmp/` of the
  repository: CodeAlta clears the scratchpad of its instance when it starts.
- Look at every new picture yourself before committing it: nothing private (accounts, private project names,
  paths of the owner beyond the public projects), no developer tag, no stray toast or hover.
- Update [`doc/site-screenshots.md`](../../../doc/site-screenshots.md) with any new scene and what it needs.

## 3. Banner

`site/img/alta-banner.png` (1280x640) is the social card of the website and of the repository. When the window
changed in a way the banner shows, or a headline feature should be in its tagline, retake its capture and
redraw it as the "The banner" section of `doc/site-screenshots.md` says. The **Social preview** of the GitHub
repository has no API: tell the owner to upload the new file himself (Settings > General of the repository).

## 4. Documentation of the site

- Every feature and change of step 1 is described in `site/docs/` where a user would look for it, in the plain
  style of the site (CodeAlta Desktop first, then CodeAlta TUI when it applies; no "new in", no internal terms).
- Pages that still describe the old behavior are corrected: search the site for the names of what moved or
  was renamed.
- New pages are added to the menus (`site/menu.yml`, the `menu.yml` of their folder under `site/docs/`) and
  linked from the pages around them.
- The repository readme (`readme.md`) and the internal docs (`doc/`) follow the same changes.
- `site/docs/**` and the `site/img/alta-desktop-*.webp` pictures are packed into the applications as their
  built-in user guide (`content/user-guide`, see `src/CodeAlta/CodeAlta.csproj` and `src/CodeAlta.Tui/CodeAlta.Tui.csproj`):
  the docs and pictures must be final **before the tag**, or the release ships the old ones.
- `lunet build` in `site` passes with no warning.

## 5. Front page

`site/readme.md` is the home page: a story of feature sections, one picture each.

- Each section still tells the truth for this version (names, screens, what it does).
- Do not add sections for every feature: the page is crowded already. A headline feature of the release
  joins the section it belongs to, and a section may show more than one picture when that is what explains it.
- The pictures it uses are the retaken ones (step 2).

## 6. The tour (feature releases)

The GitHub release starts with a tour of the release: engaging, concise and user friendly. Read the tour of the
previous release first (`gh release view <last tag> --repo CodeAlta/CodeAlta --json body -q .body`) and
follow its form:

```markdown
# ✨ The X.Y.Z tour

One short paragraph: what this release is about, in the user's words, and "Here is the tour of what is new
since <last tag>."

## <emoji> <Feature, as a user would name it>

![<alt text of the picture>](https://raw.githubusercontent.com/CodeAlta/CodeAlta/X.Y.Z/site/img/<picture>.webp)

Two short paragraphs at most: what it does for the user, how to reach it (menus, shortcuts, commands), what
agents can do with it. [More →](https://codealta.github.io/docs/<page>/) (#<issue>, #<pr>)

## 🎁 And also

- **Smaller improvement.** One sentence each. (#<issue>)

## 🙏 Thank you

To @<login> for <their pull requests>, and to @<login> for <the ideas and reports that became ...>.

<details>
<summary>All changes</summary>

<the generated notes of the release, unchanged>

</details>
```

- One section per headline feature, the most important first; smaller items go in **And also**.
- Every picture link uses the tag in its URL, so it keeps working when `main` changes; every **More →** link
  goes to a page that the deployed website has (step 11 deploys it).
- Thank the contributors of the range: PR authors and the people whose issues shaped a feature.
- Write it in a file outside the repository (the scratchpad) and show it to the owner before step 10 when he
  is there to read it.

## 7. Verify locally

From the repository root, with the configuration that the running CodeAlta does not use (see "Debug or Release"
in `AGENTS.md`):

```sh
cd src
dotnet build -c <Configuration>
dotnet test -c <Configuration>
cd CodeAlta/frontend && npm test
cd ../../../site && lunet build
```

- Everything passes: no failing or newly skipped test, no build warning.
- CI runs on Ubuntu: run the .NET tests on Linux too (WSL) when the release has .NET changes, and chase any test
  that fails there only.
- Look at the release build in the window once: start the developer instance and go over the headline features.

## 8. Commit and push `main`

- Commit the pictures, docs, front page and banner in logical commits (`Update doc ...`, prefixed as `AGENTS.md`
  says). Never commit the scratchpad or `tmp/`.
- Push `main` (step 0's command) and watch the run of the `ci` workflow until it ends:
  `gh run list --repo CodeAlta/CodeAlta --branch main --limit 3`, then
  `gh run watch <id> --repo CodeAlta/CodeAlta --exit-status` started as a background job.
- The `ci` workflow ignores pushes that only change `doc/`, `site/`, `img/` or `*.md`: then the last run that
  counts is the one of the last code commit. It must be green, on the commit the tag will be on or on its last
  code ancestor.
- Red CI: fix it, run the local checks again, push again. Never tag on a red or unfinished run.

## 9. Close the issues of the milestone

Each issue of the milestone that the release solves gets a short comment (what was done, the commits, where it
is documented) and is closed. Issues moved to a later milestone are told so in a comment. Close the milestone
once it is empty.

## 10. Tag

```sh
git tag -a X.Y.Z -m "X.Y.Z" <commit of the green run>
git -c credential.helper= -c "credential.helper=!gh auth git-credential" push https://github.com/CodeAlta/CodeAlta.git X.Y.Z
```

The tag starts the `ci` workflow again, which builds, tests, packs, publishes the NuGet packages and creates the
GitHub release with the generated notes (dotnet-releaser). Watch it to the end with `gh run watch`; it takes
about 15 minutes.

## 11. Release notes and website

- Once the release exists (`gh release view X.Y.Z --repo CodeAlta/CodeAlta`), put the tour above the generated
  notes and fold those in **All changes**:

  ```sh
  gh release view X.Y.Z --repo CodeAlta/CodeAlta --json body -q .body > generated.md
  # write release.md = the tour + "<details><summary>All changes</summary>" + generated.md + "</details>"
  gh release edit X.Y.Z --repo CodeAlta/CodeAlta --notes-file release.md
  ```

  Small release: leave the generated notes as they are. Deploy the website only when a doc changed.
- Deploy the website: run the `site` workflow of the repository of the website
  (https://github.com/CodeAlta/codealta.github.io/actions/workflows/site.yml):

  ```sh
  gh workflow run site.yml --repo CodeAlta/codealta.github.io
  gh run list --repo CodeAlta/codealta.github.io --workflow site.yml --limit 1
  gh run watch <id> --repo CodeAlta/codealta.github.io --exit-status
  ```

  Then check that the pages the tour links to answer (`https://codealta.github.io/docs/<page>/`) and that the
  home page shows the new pictures.
- Open the release page and check the pictures and links of the tour render.

## 12. NuGet packages

The release publishes the tool packages `CodeAlta` and `CodeAlta.Tui` and one package per runtime of each (six
for the desktop, eight for the TUI with `linux-musl-*`): sixteen in all. They appear one after the other over
about twenty minutes, the tool packages first. Check each with
`https://api.nuget.org/v3-flatcontainer/<lowercase id>/index.json` (plain JSON with the versions; it can flicker
between CDN nodes for a minute) until all sixteen list `X.Y.Z`. Wait with a background job or a reminder, not
with sleeps.

## 13. Report

Tell the owner, with links: the release, the CI runs, the website run, the packages and their versions, the
issues closed, what was skipped and why, and what he has to do himself (the Social preview of the repository
when the banner changed).
