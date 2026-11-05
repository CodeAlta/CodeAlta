"""Renders frames of an animated SVG of the website, to check its layout at given moments of its animations.

Run from the repository root:
    python img/render-svg-frames.py site/img/fleet-in-action.svg --times 0 4 5 12.5
    python img/render-svg-frames.py site/img/fleet-in-action.svg --reduced-motion

Each frame is a PNG named <svg name>-t<seconds>.png (or <svg name>-still.png with --reduced-motion), written in
--out (a temporary folder by default, never the repository). The SVG is put inline in a page whose animations are
paused at that moment, then a headless Edge or Chrome (found on Windows, macOS and Linux, or named by --browser)
takes a screenshot of it. Needs Python 3.10 or later and nothing else.
"""
import argparse
import os
import pathlib
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import time

CANDIDATES = [
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
    "/Applications/Chromium.app/Contents/MacOS/Chromium",
]
NAMES = ["microsoft-edge", "microsoft-edge-stable", "google-chrome", "google-chrome-stable", "chromium", "chromium-browser"]


def find_browser(named):
    if named:
        return named
    for path in CANDIDATES:
        if os.path.isfile(path):
            return path
    for name in NAMES:
        found = shutil.which(name)
        if found:
            return found
    sys.exit("No Edge, Chrome or Chromium found: name one with --browser.")


def size_of(svg):
    """The size of the picture: its viewBox, else its width and height."""
    box = re.search(r'viewBox="\s*[\d.-]+[\s,]+[\d.-]+[\s,]+([\d.]+)[\s,]+([\d.]+)', svg)
    if box:
        return round(float(box.group(1))), round(float(box.group(2)))
    width, height = re.search(r'width="([\d.]+)', svg), re.search(r'height="([\d.]+)', svg)
    if width and height:
        return round(float(width.group(1))), round(float(height.group(1)))
    return 1600, 900


def render(browser, svg, seconds, png, size, reduced, profile):
    pause = "" if seconds is None else (
        "document.body.offsetWidth;"
        f"for (const a of document.getAnimations()) {{ a.pause(); a.currentTime = {seconds * 1000}; }}")
    page = pathlib.Path(profile) / "frame.html"
    page.write_text(
        f"<!doctype html><html><head><meta charset='utf-8'></head><body style='margin:0;background:#000'>"
        f"{svg}<script>{pause}</script></body></html>", encoding="utf-8")
    if png.exists():
        png.unlink()
    arguments = [browser, "--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run",
                 "--no-default-browser-check", f"--user-data-dir={profile}", f"--screenshot={png}",
                 f"--window-size={size[0]},{size[1]}"]
    if reduced:
        arguments.append("--force-prefers-reduced-motion")
    # A headless Edge can stay open once its screenshot is written: wait for the file, then end the browser.
    windows = os.name == "nt"
    browser_process = subprocess.Popen(
        arguments + [page.as_uri()], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if windows else 0, start_new_session=not windows)
    deadline, last = time.monotonic() + 60, -1
    while time.monotonic() < deadline and browser_process.poll() is None:
        time.sleep(.3)
        length = png.stat().st_size if png.exists() else -1
        if length > 0 and length == last:
            break
        last = length
    end(browser_process)
    print(png if png.exists() else f"not rendered: {png}")


def end(browser_process):
    """Ends the browser and the processes it started."""
    if browser_process.poll() is not None:
        return
    if os.name == "nt":
        subprocess.run(["taskkill", "/T", "/F", "/PID", str(browser_process.pid)],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    else:
        os.killpg(browser_process.pid, signal.SIGTERM)
    browser_process.wait(10)


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("svg", help="the SVG file to render")
    parser.add_argument("--times", type=float, nargs="+", default=[0], help="moments of the animation, in seconds")
    parser.add_argument("--reduced-motion", action="store_true", help="render the still picture shown when motion is reduced")
    parser.add_argument("--out", help="folder of the PNG files (default: a folder of the system's temporary folder)")
    parser.add_argument("--browser", help="path of the Edge, Chrome or Chromium executable")
    options = parser.parse_args()

    svg_path = pathlib.Path(options.svg)
    svg = svg_path.read_text(encoding="utf-8")
    svg = re.sub(r"^\s*<\?xml[^>]*\?>", "", svg)
    out = pathlib.Path(options.out or pathlib.Path(tempfile.gettempdir()) / "codealta-svg-frames")
    out.mkdir(parents=True, exist_ok=True)
    browser = find_browser(options.browser)
    size = size_of(svg)
    with tempfile.TemporaryDirectory(prefix="codealta-svg-frames-", ignore_cleanup_errors=True) as profile:
        if options.reduced_motion:
            render(browser, svg, None, out / f"{svg_path.stem}-still.png", size, True, profile)
        else:
            for seconds in options.times:
                render(browser, svg, seconds, out / f"{svg_path.stem}-t{seconds:g}.png", size, False, profile)


if __name__ == "__main__":
    main()
