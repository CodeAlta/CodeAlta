#!/usr/bin/env python3
"""The steps of a CodeAlta release that are the same each time.

Run from the repository (or anywhere inside it):

    python .alta/skills/codealta-release/scripts/release.py <command> [options]

Commands (each prints what it found and exits with 0 when all is well, 1 otherwise):

    preflight              checks of step 0: gh account, branch, clean tree, up to date with origin, last release
    changes [--since TAG]  commits since the last release by kind, the issues of the milestone, merged pull requests
                           and their authors, for the review of step 1 and the thanks of the tour
    pictures               the alta-* pictures the site references against site/img: missing and unused ones
    push REF               pushes main or a tag over HTTPS with the credential of gh (SSH has no key in an agent)
    ci [REF]               waits for the ci run of the commit of REF (default main) and says how it ended
    tag VERSION [--commit SHA]
                           checks the ci run of the commit is green, creates the annotated tag and pushes it
    links FILE             checks that every http(s) link of a Markdown file answers
    notes VERSION --tour FILE
                           puts the tour above the generated notes of the release, folded in "All changes",
                           then checks the links of the result
    site [--check FILE]    runs the site workflow of the website, waits for it, then checks the site links of FILE
    nuget VERSION [--minutes N]
                           waits until every package of the release lists VERSION on nuget.org

Only for the owner of the repository, as the skill says: push, tag, notes and site are outward-facing.
Standard library only; needs git and gh on PATH.
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

REPO = "CodeAlta/CodeAlta"
SITE_REPO = "CodeAlta/codealta.github.io"
REPO_URL = f"https://github.com/{REPO}.git"
OWNER = "xoofx"
# The credential of gh, without touching the git configuration.
GIT_HTTPS = ["-c", "credential.helper=", "-c", "credential.helper=!gh auth git-credential"]
# The projects whose packages a release publishes: the tool package, and one per runtime of <RuntimeIdentifiers>.
PACKAGE_PROJECTS = ["src/CodeAlta/CodeAlta.csproj", "src/CodeAlta.Tui/CodeAlta.Tui.csproj"]
# The commit prefixes dotnet-releaser sorts the notes by, in the order of the notes.
KINDS = [
    ("Breaking changes", r"^Breaking Change"),
    ("Features", r"^Add\b(?! (ci|docs?|tests?|examples?|translation|accessibility)\b)"),
    ("Fixes", r"^(Fix|Bugfix)\b(?! (ci|docs?|tests?|examples?|translation|accessibility)\b)"),
    ("Enhancements", r"^(Enhance|Refactor)\b"),
    ("Performance", r"^Improve Perf"),
    ("Documentation", r"^(Add|Improve|Fix|Update) docs?\b"),
    ("Tests", r"^(Add|Improve|Fix|Update) tests?\b"),
    ("CI and examples", r"^(Add|Improve|Fix|Update) (ci|example|translation|accessibility)\b"),
    ("Dependencies", r"^(Update Depend|Bump )"),
    ("Merges", r"^Merge\b"),
]

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")


def run(*args: str, check: bool = True, capture: bool = True, strip: bool = True) -> str:
    """Runs a command; returns its standard output, stripped unless asked not to."""
    result = subprocess.run(list(args), cwd=ROOT, text=True, encoding="utf-8", errors="replace",
                            stdout=subprocess.PIPE if capture else None, stderr=subprocess.PIPE if capture else None)
    if check and result.returncode != 0:
        raise SystemExit(f"error: {' '.join(args[:3])}… failed ({result.returncode}): {(result.stderr or '').strip()}")
    return (result.stdout or "").strip() if strip else result.stdout or ""


def gh_json(*args: str):
    return json.loads(run("gh", *args) or "null")


def find_root() -> Path:
    try:
        top = subprocess.run(["git", "rev-parse", "--show-toplevel"], text=True, capture_output=True, check=True).stdout.strip()
        if top and (Path(top) / "src" / "CodeAlta").is_dir():
            return Path(top)
    except (OSError, subprocess.CalledProcessError):
        pass
    return Path(__file__).resolve().parents[4]


ROOT = find_root()


def fetch_main() -> None:
    run("git", *GIT_HTTPS, "fetch", "-q", REPO_URL, "main:refs/remotes/origin/main")


def last_release() -> str:
    releases = gh_json("release", "list", "--repo", REPO, "--limit", "20", "--json", "tagName,isLatest,isDraft,isPrerelease")
    latest = next((r for r in releases if r.get("isLatest")), None) or next((r for r in releases if not r["isDraft"] and not r["isPrerelease"]), None)
    if latest is None:
        raise SystemExit("error: no published release found")
    return latest["tagName"]


def ok(flag: bool, text: str) -> bool:
    print(("  ok   " if flag else "  FAIL ") + text)
    return flag


# ---------------------------------------------------------------------------------------------------- commands


def cmd_preflight(_: argparse.Namespace) -> int:
    good = True
    login = run("gh", "api", "user", "-q", ".login", check=False)
    good &= ok(login == OWNER, f"gh account is {login or '(none)'}" + ("" if login == OWNER else f": this skill is for {OWNER} only"))
    branch = run("git", "branch", "--show-current")
    good &= ok(branch == "main", f"branch {branch}")
    # Each line is "XY path": its two status columns may start with a space, so the output is not stripped.
    status = [line for line in run("git", "status", "--porcelain", strip=False).splitlines() if line]
    # CodeAlta's own state of the project (config, plans, tasks) is not part of a release.
    outside = [line for line in status if not line[3:].startswith(".alta/")]
    good &= ok(not outside, "nothing uncommitted outside .alta/" + ("" if not outside else ": " + ", ".join(line[3:] for line in outside[:8])))
    fetch_main()
    ahead, behind = run("git", "rev-list", "--left-right", "--count", "main...origin/main").split()
    good &= ok(behind == "0", f"main is {ahead} ahead and {behind} behind origin/main")
    tag = last_release()
    count = run("git", "rev-list", "--count", f"{tag}..main")
    print(f"  last release {tag}; {count} commits since; main at {run('git', 'rev-parse', '--short', 'main')}")
    return 0 if good else 1


def cmd_changes(args: argparse.Namespace) -> int:
    since = args.since or last_release()
    lines = run("git", "log", *(["--no-merges"] if args.no_merges else []), "--format=%h %s", f"{since}..main").splitlines()
    groups: dict[str, list[str]] = {name: [] for name, _ in KINDS}
    others: list[str] = []
    for line in lines:
        sha, _, subject = line.partition(" ")
        kind = next((name for name, pattern in KINDS if re.search(pattern, subject)), None)
        (groups[kind] if kind else others).append(f"{sha} {subject}")
    print(f"# Changes since {since} ({len(lines)} commits)\n")
    for name, items in list(groups.items()) + [("Other", others)]:
        if items:
            print(f"## {name} ({len(items)})")
            print("\n".join(f"- {item}" for item in items))
            print()
    issues = sorted({int(n) for n in re.findall(r"\(#(\d+)\)", "\n".join(lines))})
    if issues:
        print("## Issues and pull requests named by the commits\n")
        for number in issues:
            item = gh_json("issue", "view", str(number), "--repo", REPO, "--json", "title,state,author,milestone,url")
            milestone = (item.get("milestone") or {}).get("title") or "-"
            print(f"- #{number} [{item['state'].lower()}] @{item['author']['login']} {item['title']} (milestone {milestone})")
        print()
    date = run("git", "log", "-1", "--format=%cs", since)
    prs = gh_json("pr", "list", "--repo", REPO, "--state", "merged", "--search", f"merged:>={date}", "--limit", "100",
                  "--json", "number,title,author")
    if prs:
        print(f"## Pull requests merged since {date}\n")
        print("\n".join(f"- #{p['number']} @{p['author']['login']} {p['title']}" for p in prs))
        print()
    if args.milestone:
        items = gh_json("issue", "list", "--repo", REPO, "--milestone", args.milestone, "--state", "all", "--limit", "200",
                        "--json", "number,title,state,author")
        print(f"## Milestone {args.milestone} ({sum(i['state'] == 'OPEN' for i in items)} open)\n")
        print("\n".join(f"- #{i['number']} [{i['state'].lower()}] @{i['author']['login']} {i['title']}" for i in items))
        print()
    authors = sorted({p["author"]["login"] for p in prs} - {OWNER})
    if authors:
        print("Contributors to thank (pull requests): " + ", ".join(f"@{a}" for a in authors))
    return 0


def cmd_pictures(_: argparse.Namespace) -> int:
    site = ROOT / "site"
    pattern = re.compile(r"alta-[a-z0-9-]+\.(?:webp|png)")
    referenced: set[str] = set()
    for path in site.rglob("*"):
        if path.is_file() and ".lunet" not in path.parts and path.suffix in {".md", ".html", ".scriban", ".yml", ".scss", ".js"}:
            referenced |= set(pattern.findall(path.read_text(encoding="utf-8", errors="replace")))
    present = {path.name for path in (site / "img").glob("alta-*") if path.suffix in {".webp", ".png"}}
    # The banner and its sources are used by the configuration and GitHub, not by a page.
    unused = sorted(name for name in present - referenced if not name.startswith("alta-banner"))
    missing = sorted(referenced - present)
    print(f"{len(referenced)} pictures referenced, {len(present)} in site/img")
    for name in missing:
        print(f"  MISSING {name}")
    for name in unused:
        print(f"  unused  {name}")
    return 1 if missing else 0


def cmd_push(args: argparse.Namespace) -> int:
    ref = args.ref
    target = "main:main" if ref == "main" else ref
    run("git", *GIT_HTTPS, "push", REPO_URL, target, capture=False)
    if ref == "main":
        fetch_main()
    return 0


def ci_runs_of(sha: str) -> list[dict]:
    runs = gh_json("run", "list", "--repo", REPO, "--workflow", "ci", "--commit", sha, "--limit", "10",
                   "--json", "databaseId,status,conclusion,event,headBranch,url,createdAt")
    return runs or []


def cmd_ci(args: argparse.Namespace) -> int:
    sha = run("git", "rev-parse", f"{args.ref}^{{commit}}")
    runs: list[dict] = []
    for _ in range(args.wait_start):
        runs = [r for r in ci_runs_of(sha) if r["event"] == "push"]
        if runs:
            break
        time.sleep(10)
    if not runs:
        print(f"No ci run for {sha[:8]}: a push that only changes doc/, site/, img/ or *.md starts none; the run of its last code commit is the one that counts.")
        return 1
    current = runs[0]
    print(f"ci run {current['databaseId']} for {args.ref} ({sha[:8]}): {current['url']}")
    if current["status"] != "completed":
        result = subprocess.run(["gh", "run", "watch", str(current["databaseId"]), "--repo", REPO, "--exit-status", "--interval", "60"],
                                cwd=ROOT, stdout=subprocess.DEVNULL)
        current = gh_json("run", "view", str(current["databaseId"]), "--repo", REPO, "--json", "status,conclusion,url")
    print(f"ended: {current['conclusion']}")
    return 0 if current["conclusion"] == "success" else 1


def green_ci_commit(sha: str, depth: int = 30) -> str | None:
    """The commit itself, or its closest first-parent ancestor with a ci run, when that run is green."""
    for candidate in run("git", "rev-list", "--first-parent", f"--max-count={depth}", sha).splitlines():
        runs = [r for r in ci_runs_of(candidate) if r["event"] == "push"]
        if runs:
            return candidate if runs[0]["status"] == "completed" and runs[0]["conclusion"] == "success" else None
    return None


def cmd_tag(args: argparse.Namespace) -> int:
    version = args.version
    if not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise SystemExit("error: a tag is X.Y.Z, without v")
    if run("git", "tag", "--list", version):
        raise SystemExit(f"error: the tag {version} exists already")
    sha = run("git", "rev-parse", f"{args.commit or 'main'}^{{commit}}")
    green = green_ci_commit(sha)
    if green is None:
        print(f"No green ci run on {sha[:8]} or on its last code ancestor: never tag on a red or unfinished run.")
        return 1
    print(f"ci is green on {green[:8]}" + ("" if green == sha else f", the last code ancestor of {sha[:8]}"))
    run("git", "tag", "-a", version, "-m", version, sha)
    run("git", *GIT_HTTPS, "push", REPO_URL, version, capture=False)
    print(f"Pushed {version} at {sha[:8]}: the ci run of the tag publishes the release (`release.py ci {version}`).")
    return 0


def check_url(url: str) -> int | str:
    for method in ("HEAD", "GET"):
        request = urllib.request.Request(url, method=method, headers={"User-Agent": "codealta-release"})
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                return response.status
        except urllib.error.HTTPError as error:
            if method == "GET" or error.code not in (403, 405):
                return error.code
        except (urllib.error.URLError, TimeoutError) as error:
            return str(getattr(error, "reason", error))
    return "?"


def links_of(text: str) -> list[str]:
    urls = re.findall(r"https?://[^\s)\]\"'<>]+", text)
    return sorted({url.rstrip(".,") for url in urls})


def check_links(text: str, only: str | None = None) -> int:
    bad = 0
    for url in links_of(text):
        if only and only not in url:
            continue
        # A heading of the site is checked on its page; the anchor itself is not sent.
        page, _, anchor = url.partition("#")
        status = check_url(page)
        mark = "ok  " if status == 200 else "FAIL"
        bad += status != 200
        note = ""
        if status == 200 and anchor and "codealta.github.io" in page:
            html = urllib.request.urlopen(urllib.request.Request(page, headers={"User-Agent": "codealta-release"}), timeout=30).read().decode("utf-8", "replace")
            if f'id="{anchor}"' not in html:
                mark, note = "FAIL", f" (no heading #{anchor})"
                bad += 1
        print(f"  {mark} {status} {url}{note}")
    return bad


def cmd_links(args: argparse.Namespace) -> int:
    text = Path(args.file).read_text(encoding="utf-8")
    bad = check_links(text)
    print("all links answer" if not bad else f"{bad} link(s) do not answer (a raw.githubusercontent link of a tag answers once the tag is pushed)")
    return 1 if bad else 0


def cmd_notes(args: argparse.Namespace) -> int:
    version = args.version
    tour = Path(args.tour).read_text(encoding="utf-8").strip()
    body = run("gh", "release", "view", version, "--repo", REPO, "--json", "body", "-q", ".body")
    if "<summary>All changes</summary>" in body:
        raise SystemExit("error: the notes of the release already have a tour; edit them by hand")
    composed = f"{tour}\n\n<details>\n<summary>All changes</summary>\n\n{body.strip()}\n\n</details>\n"
    out = Path(args.out or ROOT / "tmp" / f"release-{version}" / "release.md")
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(composed, encoding="utf-8")
    run("gh", "release", "edit", version, "--repo", REPO, "--notes-file", str(out))
    print(f"Notes of {version} updated from {out}: https://github.com/{REPO}/releases/tag/{version}")
    bad = check_links(composed)
    return 1 if bad else 0


def cmd_site(args: argparse.Namespace) -> int:
    before = gh_json("run", "list", "--repo", SITE_REPO, "--workflow", "site.yml", "--limit", "1", "--json", "databaseId") or []
    run("gh", "workflow", "run", "site.yml", "--repo", SITE_REPO)
    run_id = None
    for _ in range(30):
        time.sleep(5)
        latest = gh_json("run", "list", "--repo", SITE_REPO, "--workflow", "site.yml", "--limit", "1", "--json", "databaseId,url") or []
        if latest and (not before or latest[0]["databaseId"] != before[0]["databaseId"]):
            run_id = latest[0]["databaseId"]
            print(f"site run {run_id}: {latest[0]['url']}")
            break
    if run_id is None:
        print("The site run did not start.")
        return 1
    subprocess.run(["gh", "run", "watch", str(run_id), "--repo", SITE_REPO, "--exit-status", "--interval", "10"], cwd=ROOT, stdout=subprocess.DEVNULL)
    conclusion = gh_json("run", "view", str(run_id), "--repo", SITE_REPO, "--json", "conclusion")["conclusion"]
    print(f"ended: {conclusion}")
    if conclusion != "success":
        return 1
    if args.check:
        return 1 if check_links(Path(args.check).read_text(encoding="utf-8"), only="codealta.github.io") else 0
    return 0


def package_ids() -> list[str]:
    ids: list[str] = []
    for project in PACKAGE_PROJECTS:
        path = ROOT / project
        tool = path.stem.lower()
        rids = re.search(r"<RuntimeIdentifiers>([^<]+)</RuntimeIdentifiers>", path.read_text(encoding="utf-8"))
        ids.append(tool)
        ids += [f"{tool}.{rid.strip()}" for rid in (rids.group(1).split(";") if rids else []) if rid.strip()]
    return ids


def cmd_nuget(args: argparse.Namespace) -> int:
    ids = package_ids()
    listed: dict[str, str] = {}
    deadline = time.time() + args.minutes * 60
    print(f"Waiting for {len(ids)} packages to list {args.version}")
    while True:
        for package in ids:
            if package in listed:
                continue
            try:
                with urllib.request.urlopen(f"https://api.nuget.org/v3-flatcontainer/{package}/index.json", timeout=30) as response:
                    versions = json.load(response).get("versions", [])
            except (urllib.error.URLError, TimeoutError, ValueError):
                continue
            if args.version in versions:
                listed[package] = time.strftime("%H:%M:%S")
                print(f"  {listed[package]} {package}", flush=True)
        if len(listed) == len(ids):
            print(f"All {len(ids)} packages list {args.version}.")
            return 0
        if time.time() > deadline:
            print("Missing after {0} min: {1}".format(args.minutes, ", ".join(p for p in ids if p not in listed)))
            return 1
        time.sleep(60)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0], formatter_class=argparse.RawDescriptionHelpFormatter, epilog=__doc__.split("\n\n", 2)[2])
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("preflight").set_defaults(func=cmd_preflight)
    changes = commands.add_parser("changes")
    changes.add_argument("--since", help="tag to start from (default: the last release)")
    changes.add_argument("--milestone", help="also list the issues of this milestone")
    changes.add_argument("--no-merges", action="store_true", help="leave out merge commits")
    changes.set_defaults(func=cmd_changes)
    commands.add_parser("pictures").set_defaults(func=cmd_pictures)
    push = commands.add_parser("push")
    push.add_argument("ref", help="main, or a tag")
    push.set_defaults(func=cmd_push)
    ci = commands.add_parser("ci")
    ci.add_argument("ref", nargs="?", default="main")
    ci.add_argument("--wait-start", type=int, default=12, help="tries, 10 s apart, for the run to appear")
    ci.set_defaults(func=cmd_ci)
    tag = commands.add_parser("tag")
    tag.add_argument("version")
    tag.add_argument("--commit", help="commit to tag (default: main)")
    tag.set_defaults(func=cmd_tag)
    links = commands.add_parser("links")
    links.add_argument("file")
    links.set_defaults(func=cmd_links)
    notes = commands.add_parser("notes")
    notes.add_argument("version")
    notes.add_argument("--tour", required=True, help="Markdown of the tour")
    notes.add_argument("--out", help="where to keep the composed notes (default: tmp/release-<version>/release.md)")
    notes.set_defaults(func=cmd_notes)
    site = commands.add_parser("site")
    site.add_argument("--check", help="Markdown file whose codealta.github.io links are checked after the deploy")
    site.set_defaults(func=cmd_site)
    nuget = commands.add_parser("nuget")
    nuget.add_argument("version")
    nuget.add_argument("--minutes", type=int, default=90)
    nuget.set_defaults(func=cmd_nuget)
    args = parser.parse_args()
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
