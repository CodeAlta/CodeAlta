---
discard: true # Disable this page for website generation
---

# Website (lunet) Contribution Instructions

This folder contains the static end-user website for CodeAlta, built with **lunet**.

Single source of truth for the overall project: read and follow `../AGENTS.md`.

## Structure

- `site/readme.md` -> home page (`/`)
- `site/docs/**` -> end-user documentation (`/docs/**`)
- `site/docs/menu.yml` -> documentation sidebar
- `site/menu.yml` -> top navigation
- `site/img/` -> site images, screenshots and demo videos
- `site/css/codealta.scss` -> site styles, bundled after the shared template styles
- `site/.lunet/js/codealta.js` -> site script (Desktop / TUI switch, home logo animation)
- `site/config.scriban` -> site configuration and the `alta_shot` helper
- `site/.lunet/build/**` -> generated output; do not edit by hand

The site script lives under `.lunet/js` because lunet only concatenates scripts of the meta folder into the bundle; a script placed in `site/js` is bundled as an empty file.

## Build & Serve

Install lunet once if it is not already available:

```sh
dotnet tool install -g lunet
```

Run from this folder after changing `site/**`:

```sh
lunet build
lunet serve
```

## Content Conventions

- Keep this site end-user focused: installation, configuration, workspace usage, providers, sessions, plugins, and troubleshooting.
- Keep internal specifications and implementation notes in `../doc/**`, not on the public site.
- Pages use Markdown with YAML front matter (`title` required).
- Navigation is defined in `menu.yml` files; update the relevant menu when adding or moving pages.
- Keep examples short, correct, and copy-pasteable. Avoid undocumented future behavior unless it is clearly presented as a planned screenshot/media slot.
- Write short, plain sentences. Describe what the user can do and how; leave out implementation limits, edge-case guarantees and development status.

## Desktop and TUI

CodeAlta has two apps: **CodeAlta Desktop** (`alta`, package `CodeAlta`) and **CodeAlta TUI** (`altatui`, package `CodeAlta.Tui`). They share the same harness, so a page describes a feature once and adds a sentence only where the two apps differ. Use the names "Desktop" or "the desktop app" and "TUI"; `docs/desktop-and-tui.md` holds the comparison and the current desktop limitations.

## Screenshots

- TUI screenshots are named `alta-<screen>.png`; desktop screenshots are named `alta-desktop-<screen>.webp` (lossless WebP, 2400x1500, captured from a 1600x1000 window at 1.5x).
- Use the app's **Dark** theme for feature screenshots to match the website's default dark background. Avoid white-background captures; light-theme pictures belong only in an explicit theme comparison. Select the real theme before capturing, rather than recoloring an image.
- When a screen exists in both apps, show both with the switch:

  ```scriban
  {{ alta_shot "alta-desktop-models.webp" "alta-models.png" "Model browser" "Caption shown below the image." }}
  ```

  The arguments are the desktop file, the TUI file, the alternative text and the caption (HTML allowed, may be empty). The visitor's choice applies to every screenshot of the site and is remembered.
- For a screenshot of one app only, use a plain `<figure class="alta-figure">`.
- Screenshots are published: before capturing, keep private project names, account names and credentials out of the window.
