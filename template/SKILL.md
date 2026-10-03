---
# Folder name must equal this value. Lowercase letters, digits and single hyphens, max 64 chars.
name: my-skill-id
# One line, 1..1024 chars. Say WHAT the skill does and WHEN to use it. No ">" or "|" block scalars.
description: Does X for Unity projects. Use when the task involves Y.
# One of: MIT, Apache-2.0, BSD-2-Clause, BSD-3-Clause, CC0-1.0, CC-BY-4.0
license: MIT
metadata:
  # Display name.
  title: My Skill
  # Your GitHub login.
  author: your-github-login
  # Semver MAJOR.MINOR.PATCH. Bump on ANY change to the skill folder.
  version: 1.0.0
  # One id from categories.json.
  category: workflow
  # Optional, comma separated.
  tags: "unity, example"
  # auto = the agent loads it by itself; manual = the user switches it on per chat.
  kind: auto
  # Optional, up to 120 chars, shown in the catalog row. Defaults to description.
  summary: Short catalog line.
---

# My Skill

Write instructions for the agent here. Keep this file under 500 lines; move long
material into `references/` and helper code into `scripts/`.

Prefer plain text. Do not use plugin template variables in new skills.
