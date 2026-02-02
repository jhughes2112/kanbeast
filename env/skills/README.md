Each subfolder here is a different skill that can be utilized to perform some action. You may create new skills if none here suit your needs. Follow the formatting and structure below (per the Claude custom Skills guidance).

## Skill Folder Structure

```
<Skill-Name>/
??? Skill.md
??? resources/ (optional)
??? scripts/ (optional)
```

The folder name should match the Skill name. The root file must be `Skill.md`.

## `Skill.md` Format

`Skill.md` must start with YAML frontmatter containing required metadata. Example:

```
---
name: Brand Guidelines
description: Apply Acme Corp brand guidelines to presentations and documents, including official colors, fonts, and logo usage.
dependencies: python>=3.8, pandas>=1.5.0
---

## Overview
This Skill provides Acme Corp's official brand guidelines for creating consistent, professional materials.

## When to Apply
Use this Skill whenever creating external-facing documents or presentations.

## Brand Colors
- Primary: #FF6B35 (Coral)
- Secondary: #004E89 (Navy Blue)
```

### Required metadata fields

- `name`: Human-friendly Skill name (max 64 characters).
- `description`: Clear description of what the Skill does and when to use it (max 200 characters). This is used to determine invocation.

### Optional metadata fields

- `dependencies`: Packages required by the Skill (e.g., `python>=3.8, pandas>=1.5.0`).

## Markdown Body

After the frontmatter, add the Skill’s instructions, examples, and references. Keep it focused on one workflow. Include example inputs/outputs when helpful.

## Adding Resources

If the Skill needs additional reference material, add files like `REFERENCE.md` (or other files) alongside `Skill.md` and mention them in the body so they can be loaded when needed.

## Adding Scripts

You can include executable scripts (e.g., Python or Node.js) in the Skill folder and reference them from `Skill.md`. Ensure any required dependencies are listed in the frontmatter.