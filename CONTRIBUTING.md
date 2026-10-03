# Contributing

Thanks for adding a skill. Keep it small, specific and useful for real Unity work.

## Quick start

1. Copy `template/SKILL.md` to `skills/<your-id>/SKILL.md`.
2. Fill in the frontmatter, write the instructions.
3. Run `python tools/skills.py validate` (Python 3.12, no dependencies).
4. Open a pull request. CI runs the same validation.

All text in this repository is in English.

## SKILL.md format

The format follows the Agent Skills spec (agentskills.io). Frontmatter:

```
---
name: my-skill-id
description: What the skill does and when to use it, on one line.
license: MIT
metadata:
  title: My Skill
  author: your-github-login
  version: 1.0.0
  category: workflow
  tags: "comma, separated"
  kind: auto
  summary: Short line for the catalog row.
---
```

| Field | Required | Rule |
| --- | --- | --- |
| `name` | yes | Equals the folder name. `^[a-z0-9]+(-[a-z0-9]+)*$`, at most 64 chars. |
| `description` | yes | 1 to 1024 chars. Says what the skill does and when to use it. |
| `license` | yes | One of `MIT`, `Apache-2.0`, `BSD-2-Clause`, `BSD-3-Clause`, `CC0-1.0`, `CC-BY-4.0`. |
| `metadata.title` | yes | Display name. |
| `metadata.author` | yes | Your GitHub login. |
| `metadata.version` | yes | Semver `MAJOR.MINOR.PATCH`. |
| `metadata.category` | yes | An id from `categories.json`. |
| `metadata.kind` | yes | `auto` (agent loads it by itself) or `manual` (user switches it on per chat). A suggestion for the install mode. |
| `metadata.tags` | no | Comma separated words. |
| `metadata.summary` | no | Up to 120 chars; defaults to `description`. |

Frontmatter limits: the plugin parser reads a small YAML subset. Use top-level `key: value`
lines and one nested `metadata:` map indented by exactly 2 spaces. Values must be on a
single line, bare or quoted. **Block scalars (`>`, `|`) are rejected.** Metadata values are
plain strings.

## Categories

`architecture`, `ui`, `ai`, `animation`, `performance`, `testing`, `networking`, `assets`,
`code-quality`, `workflow`. The list lives in `categories.json`. To propose a new category,
open a PR that edits that file and explain why no existing one fits.

## Layout and limits

```
skills/<id>/SKILL.md
skills/<id>/scripts/      optional helper code
skills/<id>/references/   optional longer docs
skills/<id>/assets/       optional text assets (for example svg)
```

- `SKILL.md` at most 500 lines. Move long material to `references/`.
- Skill folder at most 1 MB; no single file over 256 KB.
- Text files only (UTF-8). The plugin installs text and nothing else, so validation rejects
  any binary file, images such as png/jpg included. SVG is text and is allowed.
- No symlinks, no hidden files (names starting with a dot), ASCII file names only.
- Skill ids are unique.

## Versioning

Bump `metadata.version` on **any** change to a skill folder, even a typo.
Use semver: PATCH for fixes, MINOR for added guidance, MAJOR for a changed meaning.
On push to `main`, CI compares against the previous commit and fails with
"bump metadata.version" if files changed but the version did not. Each new version
becomes a release `<id>-v<version>` with a zip attached; released versions are not rewritten.

## Scripts review policy

Skills may include scripts, but they are the riskiest part of a library. So:

- Scripts live in `scripts/` and are small and readable. No obfuscation, no minified code,
  no downloads of code at run time.
- Say in `SKILL.md` what each script does and when the agent should run it.
- No hidden network calls and no reading of secrets or files outside the Unity project.
- Changes under `skills/**/scripts/` need approval from the code owner (CODEOWNERS).
- Skills with scripts are flagged `hasScripts` in the catalog so users see it before installing.

## License rule

Only submit what you wrote or have the right to share. Pick a license from the allow-list
above and put it in the `license` field. Do not copy text or code from sources whose license
does not allow redistribution. Repository tooling is MIT.

## Plugin template variables

Skills that ship with the Cheburnexus plugin may contain placeholders such as
`${product_short}`. The plugin substitutes them at install time. Variables used in the
current skills:

- `${product_short}` - short product name (used in `unity-ui-work`).
- `${skill_files}` - folder where the skill is installed (used in `unity-behavior-graph`).

Outside the plugin (manual copy, other tools) these stay literal text. Prefer plain text
in new skills so they read well everywhere.

## Pull request checklist

See `.github/PULL_REQUEST_TEMPLATE.md`. In short: validation passes, folder name equals
`name`, version bumped for changes, license is allowed, scripts are reviewable.
