# cheburnexus-unity-skills

An open-source library of [Agent Skills](https://agentskills.io) for Unity.
Each skill is a folder with a `SKILL.md` (plus optional `scripts/`, `references/`, `assets/`)
that teaches an AI coding agent how to do one kind of Unity job well.

The library is consumed by the Cheburnexus / Architecture Analyzer Unity editor plugin,
which shows a "Library" page and installs skills into your project. The skills also work
without the plugin.

## Install

**With the plugin (recommended).** Open the Library page in the plugin, pick a skill,
press Install. The plugin downloads the zip listed in the catalog, checks its sha256 and
unpacks it into your project's skills folder.

**Manually.** Copy the skill folder (for example `skills/unity-ui-work/`) into your
project's `.claude/skills/` folder, so you end up with `.claude/skills/unity-ui-work/SKILL.md`.
Or download the zip from the skill's GitHub release and unpack it into
`.claude/skills/<skill-id>/`.

Skills have a suggested install mode in `metadata.kind`:

- `auto` - the agent loads the skill by itself when the task matches its description.
- `manual` - the user switches it on per chat.

## Catalog

The catalog is generated from this repository and published to GitHub Pages as
`index.json` (skill list, versions, file hashes, zip URLs, download and like counters).
The plugin reads that file.

Skills in `skills/` today:

| Skill | Category | Kind |
| --- | --- | --- |
| `unity-ui-work` | UI | auto |
| `unity-behavior-graph` | AI | manual |

## Contributing

Short version: add `skills/<your-id>/SKILL.md`, run `python tools/skills.py validate`,
open a pull request. Full rules are in [CONTRIBUTING.md](CONTRIBUTING.md). Start from
[template/SKILL.md](template/SKILL.md).

## Safety note

A skill is text the agent follows, and sometimes scripts the agent may run. Read a skill
before you install it, especially anything under `scripts/`. Skills with scripts are
flagged `hasScripts` in the catalog. Skills by logins listed in `maintainers.json` are
marked `verified`; that means the author is a maintainer, not that the content is risk-free.
Changes to scripts require review from the code owner (see `.github/CODEOWNERS`).

## Tooling

Python 3.12, standard library only.

```
python tools/skills.py validate
python tools/skills.py build --out dist --base-url https://oberonru.github.io/cheburnexus-unity-skills --repo Oberonru/cheburnexus-unity-skills
python tools/skills.py stats --repo Oberonru/cheburnexus-unity-skills --out stats.json   # needs GITHUB_TOKEN
```

`build` writes `dist/index.json`, `dist/skills/<id>/SKILL.md` and deterministic
`dist/zips/<id>-<version>.zip` files (the same input gives the same sha256).

## Repository setup (maintainer)

One-time steps on GitHub:

1. Settings, Pages: set the source to **GitHub Actions**.
2. Enable **Discussions** and create a discussion category named **Skills**. Per skill,
   create one discussion whose title is exactly the skill id; its upvotes become the
   skill's likes in the catalog. Without the category, likes stay `null`.
3. Workflows: `validate` runs on pull requests and pushes; `publish` runs on push to
   `main` (builds, creates a release `<id>-v<version>` for every new version, deploys
   Pages); `stats` runs nightly and refreshes counters.

## License

MIT, see [LICENSE](LICENSE). Each skill declares its own license in its frontmatter
(MIT by default; the allow-list is in CONTRIBUTING.md).
