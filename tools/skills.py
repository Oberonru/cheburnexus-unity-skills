#!/usr/bin/env python3
"""Tooling for the cheburnexus-unity-skills library.

Commands:
  validate  check every skill under skills/
  build     produce dist/ (index.json, skill previews, deterministic zips)
  stats     fetch download/like counters from GitHub into a stats.json

Python 3.12 standard library only.
"""
from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.request
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SKILLS_DIR = ROOT / "skills"

NAME_RE = re.compile(r"^[a-z0-9]+(-[a-z0-9]+)*$")
SEMVER_RE = re.compile(r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")
LICENSES = {"MIT", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "CC0-1.0", "CC-BY-4.0"}
KINDS = {"auto", "manual"}
CODE_EXTS = {".cs", ".py", ".sh", ".ps1", ".js"}
BINARY_OK_EXTS = {".png", ".jpg", ".svg"}

MAX_SKILL_MD_LINES = 500
MAX_FOLDER_BYTES = 1024 * 1024
MAX_FILE_BYTES = 256 * 1024
ZIP_DATE = (1980, 1, 1, 0, 0, 0)


# --------------------------------------------------------------------------
# Frontmatter parser (subset of YAML)
# --------------------------------------------------------------------------

class FrontmatterError(Exception):
    pass


def _unquote(raw: str) -> str:
    raw = raw.strip()
    if not raw:
        return ""
    if raw[0] in "\"'":
        q = raw[0]
        end = raw.rfind(q)
        if end == 0:
            raise FrontmatterError(f"unterminated quoted string: {raw}")
        rest = raw[end + 1:].strip()
        if rest and not rest.startswith("#"):
            raise FrontmatterError(f"unexpected text after quoted string: {raw}")
        inner = raw[1:end]
        if q == '"':
            inner = inner.replace('\\"', '"').replace("\\\\", "\\")
        else:
            inner = inner.replace("''", "'")
        return inner
    if raw[0] in ">|":
        raise FrontmatterError("block scalars (> or |) are not allowed; use a single-line value")
    if raw[0] in "[{&*!%@`":
        raise FrontmatterError(f"unsupported YAML value: {raw}")
    m = re.search(r"\s#", raw)  # inline comment on a bare value
    if m:
        raw = raw[:m.start()]
    return raw.strip()


def parse_frontmatter(text: str) -> tuple[dict, dict]:
    """Return (top_level, metadata). Raises FrontmatterError."""
    lines = text.replace("\r\n", "\n").replace("\r", "\n").split("\n")
    if not lines or lines[0].strip() != "---":
        raise FrontmatterError("SKILL.md must start with a '---' frontmatter line")
    end = None
    for i in range(1, len(lines)):
        if lines[i].strip() == "---":
            end = i
            break
    if end is None:
        raise FrontmatterError("frontmatter is not closed with '---'")
    top: dict = {}
    meta: dict = {}
    in_meta = False
    for n, line in enumerate(lines[1:end], start=2):
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        indented = line[0] in " \t"
        if indented:
            if not in_meta:
                raise FrontmatterError(f"line {n}: unexpected indentation")
            if not line.startswith("  ") or line.startswith("   ") or line[0] == "\t":
                raise FrontmatterError(f"line {n}: nested keys must be indented with exactly 2 spaces")
            body = line[2:]
            target = meta
        else:
            in_meta = False
            body = line
            target = top
        m = re.match(r"^([A-Za-z0-9_.-]+)\s*:(?:\s+(.*)|\s*)$", body)
        if not m:
            raise FrontmatterError(f"line {n}: cannot parse '{line.strip()}'")
        key, raw = m.group(1), m.group(2) or ""
        if not indented and key == "metadata":
            if raw.strip() and not raw.strip().startswith("#"):
                raise FrontmatterError(f"line {n}: 'metadata' must be a nested map")
            in_meta = True
            continue
        if key in target:
            raise FrontmatterError(f"line {n}: duplicate key '{key}'")
        try:
            target[key] = _unquote(raw)
        except FrontmatterError as e:
            raise FrontmatterError(f"line {n}: {e}") from None
    return top, meta


# --------------------------------------------------------------------------
# Helpers
# --------------------------------------------------------------------------

def load_json(path: Path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def list_skill_ids() -> list[str]:
    if not SKILLS_DIR.is_dir():
        return []
    return sorted(p.name for p in SKILLS_DIR.iterdir() if p.is_dir())


def walk_files(skill_dir: Path):
    """Return sorted (relpath_posix, abspath) for all files; does not follow symlinks."""
    out = []
    for dirpath, dirnames, filenames in os.walk(skill_dir, followlinks=False):
        dirnames.sort()
        for fn in filenames:
            ap = Path(dirpath) / fn
            out.append((ap.relative_to(skill_dir).as_posix(), ap))
    out.sort(key=lambda t: t[0])
    return out


def sha256_bytes(b: bytes) -> str:
    return hashlib.sha256(b).hexdigest()


def git(*args: str) -> subprocess.CompletedProcess:
    return subprocess.run(["git", *args], cwd=ROOT, capture_output=True, text=True,
                          encoding="utf-8", errors="replace")


# --------------------------------------------------------------------------
# validate
# --------------------------------------------------------------------------

def read_skill_meta(skill_dir: Path):
    text = (skill_dir / "SKILL.md").read_text(encoding="utf-8")
    return parse_frontmatter(text)


def validate_skill(sid: str, categories: set[str]) -> list[str]:
    errs: list[str] = []
    sdir = SKILLS_DIR / sid

    def err(msg: str):
        errs.append(f"skills/{sid}: {msg}")

    if sdir.is_symlink():
        err("skill folder is a symlink")
        return errs
    if not (sdir / "SKILL.md").is_file():
        err("missing SKILL.md")
        return errs

    # file-level checks
    total = 0
    for dirpath, dirnames, filenames in os.walk(sdir, followlinks=False):
        for d in list(dirnames):
            dp = Path(dirpath) / d
            rel = dp.relative_to(sdir).as_posix()
            if dp.is_symlink():
                err(f"symlink not allowed: {rel}")
                dirnames.remove(d)
            elif d.startswith("."):
                err(f"hidden directory not allowed: {rel}")
                dirnames.remove(d)
            elif not d.isascii():
                err(f"non-ascii directory name: {rel}")
        for fn in filenames:
            fp = Path(dirpath) / fn
            rel = fp.relative_to(sdir).as_posix()
            if fp.is_symlink():
                err(f"symlink not allowed: {rel}")
                continue
            if fn.startswith("."):
                err(f"hidden file not allowed: {rel}")
            if not fn.isascii():
                err(f"non-ascii file name: {rel}")
            size = fp.stat().st_size
            total += size
            if size > MAX_FILE_BYTES:
                err(f"file too large ({size} bytes, max {MAX_FILE_BYTES}): {rel}")
            data = fp.read_bytes()[:MAX_FILE_BYTES + 1]
            if b"\x00" in data:
                ext = fp.suffix.lower()
                in_assets = rel.startswith("assets/")
                if not (in_assets and ext in BINARY_OK_EXTS):
                    err(f"binary file not allowed (only png/jpg/svg under assets/): {rel}")
    if total > MAX_FOLDER_BYTES:
        err(f"skill folder too large ({total} bytes, max {MAX_FOLDER_BYTES})")

    # SKILL.md
    skill_md = sdir / "SKILL.md"
    try:
        text = skill_md.read_text(encoding="utf-8")
    except UnicodeDecodeError:
        err("SKILL.md is not valid UTF-8")
        return errs
    nlines = len(text.splitlines())
    if nlines > MAX_SKILL_MD_LINES:
        err(f"SKILL.md has {nlines} lines (max {MAX_SKILL_MD_LINES})")
    try:
        top, meta = parse_frontmatter(text)
    except FrontmatterError as e:
        err(f"frontmatter: {e}")
        return errs

    name = top.get("name", "")
    if not name:
        err("missing required field: name")
    else:
        if len(name) > 64 or not NAME_RE.match(name):
            err(f"invalid name '{name}' (lowercase letters, digits, single hyphens; max 64)")
        if name != sid:
            err(f"folder name '{sid}' != name '{name}'")
    desc = top.get("description", "")
    if not desc:
        err("missing required field: description")
    elif len(desc) > 1024:
        err(f"description too long ({len(desc)} > 1024)")
    lic = top.get("license", "")
    if not lic:
        err("missing required field: license")
    elif lic not in LICENSES:
        err(f"license '{lic}' not allowed (allowed: {', '.join(sorted(LICENSES))})")
    for key in ("title", "author", "version", "category", "kind"):
        if not meta.get(key):
            err(f"missing required field: metadata.{key}")
    ver = meta.get("version")
    if ver and not SEMVER_RE.match(ver):
        err(f"metadata.version '{ver}' is not semver MAJOR.MINOR.PATCH")
    cat = meta.get("category")
    if cat and cat not in categories:
        err(f"metadata.category '{cat}' not in categories.json")
    kind = meta.get("kind")
    if kind and kind not in KINDS:
        err(f"metadata.kind '{kind}' must be one of: auto, manual")
    summary = meta.get("summary")
    if summary is not None and len(summary) > 120:
        err(f"metadata.summary too long ({len(summary)} > 120)")
    return errs


def version_of_text(text: str) -> str | None:
    try:
        _, meta = parse_frontmatter(text)
    except FrontmatterError:
        return None
    return meta.get("version")


def check_version_bumps(ref: str) -> list[str]:
    errs: list[str] = []
    r = git("rev-parse", "--verify", "--quiet", f"{ref}^{{commit}}")
    if r.returncode != 0:
        print(f"warning: ref '{ref}' not found; skipping version-bump check", file=sys.stderr)
        return errs
    r = git("diff", "--name-only", ref)
    if r.returncode != 0:
        errs.append(f"git diff against {ref} failed: {r.stderr.strip()}")
        return errs
    changed: dict[str, list[str]] = {}
    for path in r.stdout.splitlines():
        parts = path.split("/")
        if len(parts) >= 3 and parts[0] == "skills":
            changed.setdefault(parts[1], []).append(path)
    for sid in sorted(changed):
        skill_md = SKILLS_DIR / sid / "SKILL.md"
        if not skill_md.is_file():
            continue  # skill removed
        old = git("show", f"{ref}:skills/{sid}/SKILL.md")
        if old.returncode != 0:
            continue  # new skill
        old_v = version_of_text(old.stdout)
        new_v = version_of_text(skill_md.read_text(encoding="utf-8"))
        if old_v is not None and old_v == new_v:
            errs.append(f"skills/{sid}: files changed but metadata.version is still {new_v} - bump metadata.version")
    return errs


def cmd_validate(args) -> int:
    errs: list[str] = []
    try:
        categories = {c["id"] for c in load_json(ROOT / "categories.json")}
    except (OSError, ValueError, KeyError) as e:
        print(f"error: cannot read categories.json: {e}")
        return 1
    if SKILLS_DIR.is_dir():
        for p in SKILLS_DIR.iterdir():
            if not p.is_dir():
                errs.append(f"skills/{p.name}: only skill folders are allowed directly under skills/")
    ids = list_skill_ids()
    lowered: dict[str, str] = {}
    for sid in ids:
        if sid.lower() in lowered:
            errs.append(f"skills/{sid}: duplicate id (case-insensitive clash with {lowered[sid.lower()]})")
        lowered[sid.lower()] = sid
        errs.extend(validate_skill(sid, categories))
    if args.against and os.environ.get("GITHUB_EVENT_NAME", "push") == "push":
        if re.fullmatch(r"0+", args.against):
            print("note: --against is an all-zero ref; skipping version-bump check")
        else:
            errs.extend(check_version_bumps(args.against))
    for e in errs:
        print(f"ERROR {e}")
    if errs:
        print(f"validation failed: {len(errs)} error(s) in {len(ids)} skill(s)")
        return 1
    print(f"ok: {len(ids)} skill(s) valid")
    return 0


# --------------------------------------------------------------------------
# build
# --------------------------------------------------------------------------

def make_zip(files, dest: Path) -> tuple[str, int]:
    dest.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(dest, "w", zipfile.ZIP_STORED) as z:
        for rel, ap in files:
            zi = zipfile.ZipInfo(rel, ZIP_DATE)
            zi.compress_type = zipfile.ZIP_STORED  # stored: sha256 independent of zlib version
            zi.create_system = 3
            zi.external_attr = 0o100644 << 16
            z.writestr(zi, ap.read_bytes())
    data = dest.read_bytes()
    return sha256_bytes(data), len(data)


def last_commit_date(sid: str) -> str:
    r = git("log", "-1", "--format=%cs", "--", f"skills/{sid}")
    d = r.stdout.strip() if r.returncode == 0 else ""
    return d or dt.datetime.now(dt.timezone.utc).date().isoformat()


def cmd_build(args) -> int:
    out = Path(args.out)
    if not out.is_absolute():
        out = Path.cwd() / out
    base_url = args.base_url.rstrip("/")
    repo = args.repo
    categories = load_json(ROOT / "categories.json")
    maint = load_json(ROOT / "maintainers.json")
    verified = {v.lower() for v in maint.get("verified", [])}

    stats = {}
    stats_path = Path(args.stats) if args.stats else out / "stats.json"
    if stats_path.is_file():
        try:
            stats = load_json(stats_path).get("skills", {})
        except (OSError, ValueError) as e:
            print(f"warning: cannot read stats {stats_path}: {e}", file=sys.stderr)

    skills = []
    for sid in list_skill_ids():
        sdir = SKILLS_DIR / sid
        top, meta = read_skill_meta(sdir)
        version = meta["version"]
        files = walk_files(sdir)
        entries = []
        has_scripts = False
        for rel, ap in files:
            data = ap.read_bytes()
            entries.append({"path": rel, "size": len(data), "sha256": sha256_bytes(data)})
            if rel.startswith("scripts/") or ap.suffix.lower() in CODE_EXTS:
                has_scripts = True
        zip_name = f"{sid}-{version}.zip"
        zsha, zsize = make_zip(files, out / "zips" / zip_name)
        prev = out / "skills" / sid
        prev.mkdir(parents=True, exist_ok=True)
        (prev / "SKILL.md").write_bytes((sdir / "SKILL.md").read_bytes())
        st = stats.get(sid, {})
        tags = [t.strip() for t in meta.get("tags", "").split(",") if t.strip()]
        skills.append({
            "id": sid,
            "title": meta["title"],
            "description": top["description"],
            "summary": meta.get("summary") or top["description"],
            "author": meta["author"],
            "verified": meta["author"].lower() in verified,
            "category": meta["category"],
            "tags": tags,
            "kind": meta["kind"],
            "version": version,
            "license": top["license"],
            "updated": last_commit_date(sid),
            "hasScripts": has_scripts,
            "files": entries,
            "zip": {
                "url": f"https://github.com/{repo}/releases/download/{sid}-v{version}/{zip_name}",
                "sha256": zsha,
                "size": zsize,
            },
            "skillMdUrl": f"{base_url}/skills/{sid}/SKILL.md",
            "repoUrl": f"https://github.com/{repo}/tree/main/skills/{sid}",
            "discussUrl": st.get("discussUrl"),
            "downloads": st.get("downloads"),
            "likes": st.get("likes"),
        })
    index = {
        "schema": 1,
        "generated": dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "repo": repo,
        "categories": categories,
        "skills": skills,
    }
    out.mkdir(parents=True, exist_ok=True)
    (out / "index.json").write_text(json.dumps(index, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"built {len(skills)} skill(s) into {out}")
    return 0


# --------------------------------------------------------------------------
# stats
# --------------------------------------------------------------------------

def _request(url: str, token: str | None, body: dict | None = None):
    headers = {"Accept": "application/vnd.github+json", "User-Agent": "cheburnexus-unity-skills"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    data = None
    if body is not None:
        data = json.dumps(body).encode("utf-8")
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(url, data=data, headers=headers)
    with urllib.request.urlopen(req, timeout=30) as resp:
        return json.loads(resp.read().decode("utf-8"))


def fetch_downloads(repo: str, token: str | None) -> dict[str, int]:
    tag_re = re.compile(r"^(?P<id>[a-z0-9]+(?:-[a-z0-9]+)*)-v\d+\.\d+\.\d+$")
    totals: dict[str, int] = {}
    page = 1
    while True:
        rels = _request(f"https://api.github.com/repos/{repo}/releases?per_page=100&page={page}", token)
        if not rels:
            break
        for rel in rels:
            m = tag_re.match(rel.get("tag_name", ""))
            if not m:
                continue
            n = sum(a.get("download_count", 0) for a in rel.get("assets", []))
            totals[m.group("id")] = totals.get(m.group("id"), 0) + n
        if len(rels) < 100:
            break
        page += 1
    return totals


def fetch_discussions(repo: str, token: str) -> dict[str, dict] | None:
    """Return {title: {likes, url}} for category 'Skills', or None if no such category."""
    owner, name = repo.split("/", 1)
    q_cat = ("query($o:String!,$n:String!){repository(owner:$o,name:$n){"
             "discussionCategories(first:50){nodes{id name}}}}")
    res = _request("https://api.github.com/graphql", token,
                   {"query": q_cat, "variables": {"o": owner, "n": name}})
    nodes = (((res.get("data") or {}).get("repository") or {}).get("discussionCategories") or {}).get("nodes") or []
    cat = next((c for c in nodes if c["name"].lower() == "skills"), None)
    if not cat:
        return None
    q = ("query($o:String!,$n:String!,$c:ID!,$a:String){repository(owner:$o,name:$n){"
         "discussions(first:100,categoryId:$c,after:$a){nodes{title url upvoteCount}"
         "pageInfo{hasNextPage endCursor}}}}")
    found: dict[str, dict] = {}
    after = None
    while True:
        res = _request("https://api.github.com/graphql", token,
                       {"query": q, "variables": {"o": owner, "n": name, "c": cat["id"], "a": after}})
        d = res["data"]["repository"]["discussions"]
        for n in d["nodes"]:
            found[n["title"].strip()] = {"likes": n["upvoteCount"], "url": n["url"]}
        if not d["pageInfo"]["hasNextPage"]:
            break
        after = d["pageInfo"]["endCursor"]
    return found


def cmd_stats(args) -> int:
    token = os.environ.get("GITHUB_TOKEN") or None
    ids = list_skill_ids()
    result: dict[str, dict] = {sid: {"downloads": None, "likes": None, "discussUrl": None} for sid in ids}
    try:
        totals = fetch_downloads(args.repo, token)
        for sid in ids:
            result[sid]["downloads"] = totals.get(sid, 0)
    except (urllib.error.URLError, ValueError, OSError) as e:
        print(f"warning: could not fetch releases: {e}", file=sys.stderr)
    if token:
        try:
            disc = fetch_discussions(args.repo, token)
            if disc is None:
                print("note: discussion category 'Skills' not found; likes stay null")
            else:
                for sid in ids:
                    if sid in disc:
                        result[sid]["likes"] = disc[sid]["likes"]
                        result[sid]["discussUrl"] = disc[sid]["url"]
        except (urllib.error.URLError, ValueError, OSError, KeyError, TypeError) as e:
            print(f"warning: could not fetch discussions: {e}", file=sys.stderr)
    else:
        print("note: GITHUB_TOKEN not set; likes stay null")
    payload = {
        "generated": dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "skills": result,
    }
    Path(args.out).write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {args.out}")
    return 0


# --------------------------------------------------------------------------

def main(argv=None) -> int:
    p = argparse.ArgumentParser(prog="skills.py", description=__doc__.splitlines()[0])
    sub = p.add_subparsers(dest="cmd", required=True)

    v = sub.add_parser("validate", help="validate all skills")
    v.add_argument("--against", help="git ref; error if a skill changed without a metadata.version bump")
    v.set_defaults(fn=cmd_validate)

    b = sub.add_parser("build", help="build dist/")
    b.add_argument("--out", default="dist")
    b.add_argument("--base-url", required=True)
    b.add_argument("--repo", required=True, help="owner/name")
    b.add_argument("--stats", help="stats.json to merge (default: <out>/stats.json if present)")
    b.set_defaults(fn=cmd_build)

    s = sub.add_parser("stats", help="fetch downloads/likes from GitHub")
    s.add_argument("--repo", required=True, help="owner/name")
    s.add_argument("--out", default="stats.json")
    s.set_defaults(fn=cmd_stats)

    args = p.parse_args(argv)
    return args.fn(args)


if __name__ == "__main__":
    sys.exit(main())
